using System.Text.Json;
using IoT.Contracts;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.Configuration;

// Device metadata in SQLite. Every change bumps the version so data acquisition knows to reload.
public sealed class DeviceStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public DeviceStore(string databasePath)
    {
        if (databasePath != ":memory:")
        {
            databasePath = Path.GetFullPath(databasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        }
        _connection = new(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _connection.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Devices (Id TEXT PRIMARY KEY, Data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Meta (Key TEXT PRIMARY KEY, Value INTEGER NOT NULL);
            INSERT OR IGNORE INTO Meta (Key, Value) VALUES ('Version', 0);
            """);
    }

    public bool IsEmpty
    {
        get { lock (_gate) return Scalar("SELECT COUNT(*) FROM Devices") is 0L; }
    }

    public DeviceDefinition[] All()
    {
        lock (_gate)
        {
            using var command = Command("SELECT Data FROM Devices ORDER BY Id");
            using var reader = command.ExecuteReader();
            var devices = new List<DeviceDefinition>();
            while (reader.Read()) devices.Add(JsonSerializer.Deserialize<DeviceDefinition>(reader.GetString(0), EdgeTopics.Json)!);
            return devices.ToArray();
        }
    }

    public DeviceDefinition? Find(string id) => All().FirstOrDefault(device => device.Id == id);

    public GatewayConfiguration Snapshot()
    {
        lock (_gate)
            return new((long)Scalar("SELECT Value FROM Meta WHERE Key = 'Version'")!,
                All().Where(device => device.Enabled).ToArray());
    }

    // Returns false when creating a device whose ID already exists, or updating one that does not.
    public bool Save(DeviceDefinition device, bool create)
    {
        device.Validate();
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var exists = Scalar("SELECT 1 FROM Devices WHERE Id = $id", transaction, ("$id", device.Id)) is not null;
            if (exists == create) return false;
            Execute("INSERT INTO Devices (Id, Data) VALUES ($id, $data) ON CONFLICT(Id) DO UPDATE SET Data = excluded.Data",
                transaction, ("$id", device.Id), ("$data", JsonSerializer.Serialize(device, EdgeTopics.Json)));
            BumpVersion(transaction);
            transaction.Commit();
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = Command("DELETE FROM Devices WHERE Id = $id", transaction, ("$id", id));
            if (command.ExecuteNonQuery() == 0) return false;
            BumpVersion(transaction);
            transaction.Commit();
            return true;
        }
    }

    private void BumpVersion(SqliteTransaction transaction) =>
        Execute("UPDATE Meta SET Value = Value + 1 WHERE Key = 'Version'", transaction);

    private void Execute(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, transaction, values);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, transaction, values);
        return command.ExecuteScalar();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] values)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public void Dispose() => _connection.Dispose();
}
