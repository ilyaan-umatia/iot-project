using System.Text.Json;
using IoT.Contracts;
using Microsoft.Data.Sqlite;

namespace Device.Gateway.Storage;

public sealed record OutboxRow(long Id, string Kind, string DeviceId, DeviceReading? Reading, DateTimeOffset CreatedAt);

// Every device message is committed here before it is acknowledged, and leaves only after the broker's PUBACK.
public sealed class Outbox : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _added = new(0, 1);

    public Outbox(string databasePath)
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
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS Outbox (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Kind TEXT NOT NULL,
                DeviceId TEXT NOT NULL,
                DeviceSeq INTEGER,
                Reading TEXT,
                CreatedAt INTEGER NOT NULL,
                SentAt INTEGER,
                UNIQUE (DeviceId, DeviceSeq)
            );
            CREATE INDEX IF NOT EXISTS IX_Outbox_Unsent ON Outbox (Id) WHERE SentAt IS NULL;
            CREATE TABLE IF NOT EXISTS Devices (
                DeviceId TEXT PRIMARY KEY,
                IntakeOnline INTEGER NOT NULL DEFAULT 0,
                PublishedOnline INTEGER NOT NULL DEFAULT 0,
                PublishedReading TEXT
            );
            """);
    }

    // Returns false for a reading the edge broker redelivered.
    public bool AcceptReading(DeviceReading reading)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            if (Scalar("SELECT 1 FROM Outbox WHERE DeviceId = $device AND DeviceSeq = $seq", transaction,
                    ("$device", reading.DeviceId), ("$seq", (long)reading.DeviceSeq)) is not null)
                return false;
            var online = Scalar("SELECT IntakeOnline FROM Devices WHERE DeviceId = $device", transaction,
                ("$device", reading.DeviceId)) is 1L;
            Insert(online ? "DDATA" : "DBIRTH", reading.DeviceId, reading, transaction);
            Execute("""
                INSERT INTO Devices (DeviceId, IntakeOnline) VALUES ($device, 1)
                ON CONFLICT(DeviceId) DO UPDATE SET IntakeOnline = 1
                """, transaction, ("$device", reading.DeviceId));
            transaction.Commit();
        }
        Signal();
        return true;
    }

    // Returns false when the device was already offline.
    public bool AcceptDeviceOffline(string deviceId)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            if (Scalar("SELECT IntakeOnline FROM Devices WHERE DeviceId = $device", transaction,
                    ("$device", deviceId)) is not 1L)
                return false;
            Insert("DDEATH", deviceId, null, transaction);
            Execute("UPDATE Devices SET IntakeOnline = 0 WHERE DeviceId = $device", transaction, ("$device", deviceId));
            transaction.Commit();
        }
        Signal();
        return true;
    }

    public OutboxRow[] PeekUnsent(int limit)
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT Id, Kind, DeviceId, Reading, CreatedAt FROM Outbox
                WHERE SentAt IS NULL ORDER BY Id LIMIT $limit
                """, null, ("$limit", limit));
            using var reader = command.ExecuteReader();
            var rows = new List<OutboxRow>();
            while (reader.Read())
                rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<DeviceReading>(reader.GetString(3), EdgeTopics.Json),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
            return rows.ToArray();
        }
    }

    // Also records what the backend now knows, so a new session can rebirth exactly those devices.
    public void MarkSent(OutboxRow row)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            Execute("UPDATE Outbox SET SentAt = $now WHERE Id = $id", transaction,
                ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$id", row.Id));
            if (row.Kind == "DDEATH")
                Execute("UPDATE Devices SET PublishedOnline = 0 WHERE DeviceId = $device", transaction, ("$device", row.DeviceId));
            else
                Execute("UPDATE Devices SET PublishedOnline = 1, PublishedReading = $reading WHERE DeviceId = $device",
                    transaction, ("$device", row.DeviceId), ("$reading", JsonSerializer.Serialize(row.Reading, EdgeTopics.Json)));
            transaction.Commit();
        }
    }

    public DeviceReading[] GetPublishedOnlineDevices()
    {
        lock (_gate)
        {
            using var command = Command("SELECT PublishedReading FROM Devices WHERE PublishedOnline = 1 ORDER BY DeviceId");
            using var reader = command.ExecuteReader();
            var readings = new List<DeviceReading>();
            while (reader.Read())
                readings.Add(JsonSerializer.Deserialize<DeviceReading>(reader.GetString(0), EdgeTopics.Json)!);
            return readings.ToArray();
        }
    }

    public long CountUnsent()
    {
        lock (_gate) return (long)Scalar("SELECT COUNT(*) FROM Outbox WHERE SentAt IS NULL")!;
    }

    // Sent rows are kept for a while so late redeliveries from the edge broker are still recognized.
    public void PruneSent(TimeSpan keep)
    {
        lock (_gate)
            Execute("DELETE FROM Outbox WHERE SentAt IS NOT NULL AND SentAt < $cutoff", null,
                ("$cutoff", DateTimeOffset.UtcNow.Subtract(keep).ToUnixTimeMilliseconds()));
    }

    public async Task WaitForRowsAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await _added.WaitAsync(timeout, cancellationToken);

    private void Signal()
    {
        try { _added.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void Insert(string kind, string deviceId, DeviceReading? reading, SqliteTransaction transaction) =>
        Execute("""
            INSERT INTO Outbox (Kind, DeviceId, DeviceSeq, Reading, CreatedAt)
            VALUES ($kind, $device, $seq, $reading, $now)
            """, transaction, ("$kind", kind), ("$device", deviceId),
            ("$seq", reading is null ? null : (long)reading.DeviceSeq),
            ("$reading", reading is null ? null : JsonSerializer.Serialize(reading, EdgeTopics.Json)),
            ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

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

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] values)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public void Dispose()
    {
        _connection.Dispose();
        _added.Dispose();
    }
}
