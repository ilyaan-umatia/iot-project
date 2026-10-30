using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using EdgeGateway.Configuration;
using EdgeGateway.Gateway.Acquisition;
using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Storage;
using FluentModbus;
using IoT.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

internal static class InterfaceChecks
{
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private static ModbusInterface Tcp(string id, int port, string host = "127.0.0.1") =>
        new(id, ModbusInterface.Tcp, JsonSerializer.SerializeToElement(new TcpInterfaceSettings(host, port), EdgeTopics.Json));
    private static ModbusInterface Serial(string id) => new(id, ModbusInterface.Rtu,
        JsonSerializer.SerializeToElement(new SerialInterfaceSettings("COM99", 19200, 8, "Even", 1), EdgeTopics.Json));
    private static DeviceRegistration Device(string id, string connection, byte unit = 1, bool enabled = true) =>
        new(id, connection, enabled, unit, 1, null, "check", [new("Temperature", "holding", 0, 0.1, "°C", "int16")]);

    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "interface-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckStorage(Path.Combine(directory, "configuration.db"));
            CheckAtomicCreation(Path.Combine(directory, "atomic.db"));
            await CheckPollingAndCacheAsync(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("PASS: Shared interfaces, atomic device/interface creation, reference integrity, configuration-only serial, transaction rollback, persistence, TCP multi-unit polling, interface hot reload, reassignment and cached restart.");
    }

    private static void CheckAtomicCreation(string path)
    {
        using var store = new DeviceStore(path);
        Check(store.Save(Device("one", "tcp"), true, Tcp("tcp", 502)), "Atomic TCP creation failed.");
        Check(store.Snapshot().Version == 1 && store.Snapshot().Devices.Single().Port == 502, "Atomic create must bump the version once and resolve TCP settings.");
        Check(!store.Save(Device("one", "unused"), true, Tcp("unused", 502)) && store.FindInterface("unused") is null,
            "Duplicate device left an unused interface.");
        Reject<ConfigurationConflictException>(() => store.Save(Device("two", "tcp"), true, Tcp("tcp", 1502)));
        Check(store.Find("two") is null && store.FindInterface("tcp")!.TcpSettings().Port == 502, "Duplicate interface overwrote existing settings.");
        Reject<ArgumentException>(() => store.Save(Device("invalid", "unused") with { Metrics = [] }, true, Tcp("unused", 502)));
        Reject<ArgumentException>(() => store.Save(Device("mismatch", "different"), true, Tcp("unused", 502)));
        Reject<ArgumentException>(() => store.Save(Device("invalid", "unused"), true, Tcp("unused", 0)));
        Reject<ArgumentException>(() => store.Save(Device("serial-enabled", "serial"), true, Serial("serial")));
        Check(store.All().Length == 1 && store.AllInterfaces().Length == 1 && store.Snapshot().Version == 1,
            "Rejected creation persisted partial records or changed the version.");
        using var probe = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        probe.Open();
        using var command = probe.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailDevice BEFORE INSERT ON Devices BEGIN SELECT RAISE(ABORT, 'test device insert failure'); END";
        command.ExecuteNonQuery();
        Reject<SqliteException>(() => store.Save(Device("failed", "rollback"), true, Tcp("rollback", 502)));
        Check(store.FindInterface("rollback") is null && store.Find("failed") is null && store.Snapshot().Version == 1,
            "Device insert failure did not roll back the interface.");
        command.CommandText = "DROP TRIGGER FailDevice; CREATE TRIGGER FailVersion BEFORE UPDATE ON Meta BEGIN SELECT RAISE(ABORT, 'test version failure'); END";
        command.ExecuteNonQuery();
        Reject<SqliteException>(() => store.Save(Device("failed", "rollback"), true, Tcp("rollback", 502)));
        Check(store.FindInterface("rollback") is null && store.Find("failed") is null && store.Snapshot().Version == 1,
            "Version failure did not roll back both records.");
        command.CommandText = "DROP TRIGGER FailVersion";
        command.ExecuteNonQuery();
        Check(store.Save(Device("draft", "serial", enabled: false), true, Serial("serial")) && store.Snapshot().Devices.Length == 1,
            "Atomic serial draft entered the polling snapshot.");
        Reject<ArgumentException>(() => store.Save(Device("one", "new"), false, Tcp("new", 502)));
        // The same endpoint/settings are allowed under another interface ID; interface sharing is explicit.
        Check(store.Save(Device("three", "other"), true, Tcp("other", 502)), "Second atomic create failed.");
        using var reopened = new DeviceStore(path);
        Check(reopened.Find("three")?.InterfaceId == "other" && reopened.FindInterface("other") is not null,
            "Atomic creation did not persist across a new connection.");
    }

    private static void CheckStorage(string path)
    {
        using (var store = new DeviceStore(path))
        {
            Check(store.All().Length == 0 && store.AllInterfaces().Length == 0 && store.Snapshot().Version == 0, "Fresh registry is not empty.");
            Check(store.SaveInterface(Tcp("shared", 502), true), "Interface create failed.");
            Check(!store.SaveInterface(Tcp("shared", 502), true) && !store.SaveInterface(Tcp("missing", 502), false), "Interface create/update existence handling failed.");
            store.SaveInterface(Serial("serial"), true);
            store.Save(Device("one", "shared"), true);
            store.Save(Device("two", "shared", 2), true);
            store.Save(Device("draft", "serial", enabled: false), true);
            var version = store.Snapshot().Version;
            Check(store.Snapshot().Devices.Length == 2, "Serial draft entered the polling snapshot.");
            Reject<ConfigurationConflictException>(() => store.DeleteInterface("shared"));
            Reject<ConfigurationConflictException>(() => store.DeleteInterface("serial"));
            Reject<ConfigurationConflictException>(() => store.SaveInterface(Serial("shared"), false));
            Reject<ArgumentException>(() => store.Save(Device("bad-ref", "absent"), true));
            Reject<ArgumentException>(() => store.Save(Device("draft", "serial"), false));
            Reject<ArgumentException>(() => store.Save(Device("bad-unit", "serial", 0, false), true));
            Reject<ArgumentException>(() => store.Save(Device("bad-unit", "serial", 248, false), true));
            Reject<ArgumentException>(() => store.Save(Device("bad-metric", "shared") with { Metrics = [null!] }, true));
            foreach (var invalid in new[]
            {
                Tcp("bad", 0), Tcp("bad", 65536), Tcp("bad", 502, " "), Serial("bad") with { Type = "unknown" },
                Serial("bad") with { Settings = JsonSerializer.SerializeToElement(new { portName = "COM3", baudRate = 0, dataBits = 8, parity = "Even", stopBits = 1 }) },
                Serial("bad") with { Settings = JsonSerializer.SerializeToElement(new { portName = "COM3", baudRate = 19200, dataBits = 7, parity = "Even", stopBits = 1 }) },
                Serial("bad") with { Settings = JsonSerializer.SerializeToElement(new { portName = "COM3", baudRate = 19200, dataBits = 8, parity = "bad", stopBits = 1 }) },
                Serial("bad") with { Settings = JsonSerializer.SerializeToElement(new { portName = "COM3", baudRate = 19200, dataBits = 8, parity = "Even", stopBits = 3 }) },
                Tcp("bad", 502) with { Settings = JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port = 502, readTimeoutMs = 5000 }) },
                Tcp("bad", 502) with { Settings = JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port = "not-a-number" }) },
                Tcp("bad", 502) with { Settings = JsonSerializer.SerializeToElement(new { portName = "COM3" }) },
                Serial("bad") with { Settings = JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port = 502 }) },
                Tcp("bad", 502) with { Settings = JsonSerializer.SerializeToElement<object?>(null) }
            }) Reject<ArgumentException>(() => store.SaveInterface(invalid, true));
            Check(store.Snapshot().Version == version && store.FindInterface("shared")!.Type == ModbusInterface.Tcp,
                "Rejected writes changed the version or existing settings.");

            using var probe = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            probe.Open();
            void Sql(string sql) { using var command = probe.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            Sql("CREATE TRIGGER FailVersion BEFORE UPDATE ON Meta BEGIN SELECT RAISE(ABORT, 'test write failure'); END");
            Reject<SqliteException>(() => store.SaveInterface(Tcp("shared", 1502), false));
            Reject<SqliteException>(() => store.Save(Device("one", "shared") with { Description = "changed" }, false));
            Check(store.FindInterface("shared")!.TcpSettings().Port == 502 && store.Find("one")!.Description == "check"
                && store.Snapshot().Version == version, "A failed version update left partial configuration writes.");
            Sql("DROP TRIGGER FailVersion");
            Reject<SqliteException>(() => Sql("INSERT INTO Devices VALUES ('orphan','absent','{}')"));
            using (var command = probe.CreateCommand())
            {
                command.CommandText = "SELECT Data FROM Devices WHERE Id='one'";
                using var data = JsonDocument.Parse((string)command.ExecuteScalar()!);
                Check(!data.RootElement.TryGetProperty("host", out _) && !data.RootElement.TryGetProperty("interfaceId", out _)
                    && data.RootElement.GetProperty("metrics").GetArrayLength() == 1, "Device JSON duplicates connection settings or lost metrics.");
            }
            var before = store.Snapshot();
            store.SaveInterface(Tcp("shared", 1502), false);
            Check(store.Snapshot().Devices.All(d => d.Port == 1502) && before.Devices.All(d => d.Port == 502), "Shared update or immutable snapshot failed.");
            store.SaveInterface(Tcp("other", 2502), true);
            store.Save(Device("two", "other", 2), false);
            Check(store.Snapshot().Devices.Single(d => d.Id == "two").Port == 2502, "Device reassignment failed.");
            store.Delete("one");
            Check(store.FindInterface("shared") is not null && store.DeleteInterface("shared"), "Deleting a device deleted a reusable interface.");
            Check(!store.DeleteInterface("missing") && !store.Delete("missing"), "Missing deletes succeeded.");
            using var integrity = probe.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check";
            Check((string?)integrity.ExecuteScalar() == "ok", "Configuration database integrity check failed.");
            integrity.CommandText = "PRAGMA foreign_key_check";
            using var violations = integrity.ExecuteReader();
            Check(!violations.Read(), "Configuration has orphan references.");
        }
        using var restarted = new DeviceStore(path);
        Check(restarted.Find("two")!.InterfaceId == "other" && restarted.FindInterface("serial")!.Settings.GetProperty("portName").GetString() == "COM99",
            "Device/interface settings did not survive restart.");
    }

    private static async Task CheckPollingAndCacheAsync(string directory)
    {
        using var store = new DeviceStore(":memory:");
        using var first = new ModbusTcpServer();
        using var second = new ModbusTcpServer();
        using var other = new ModbusTcpServer();
        int Start(ModbusTcpServer server, short unit1, short unit2)
        {
            var port = FreePort();
            server.AddUnit(1); server.AddUnit(2);
            server.GetHoldingRegisters(1).SetBigEndian<short>(0, unit1);
            server.GetHoldingRegisters(2).SetBigEndian<short>(0, unit2);
            server.Start(new IPEndPoint(IPAddress.Loopback, port));
            return port;
        }
        var firstPort = Start(first, 45, 650);
        var secondPort = Start(second, 85, 900);
        var otherPort = Start(other, 330, 440);
        store.Save(Device("a", "shared"), true, Tcp("shared", firstPort));
        store.SaveInterface(Tcp("other", otherPort), true);
        store.SaveInterface(Serial("serial"), true);
        store.Save(Device("b", "shared", 2), true);
        store.Save(Device("c", "other"), true);
        store.Save(Device("draft", "serial", enabled: false), true);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        await using var api = builder.Build();
        var url = $"http://127.0.0.1:{FreePort()}";
        api.Urls.Add(url);
        var available = true;
        GatewayConfiguration? invalid = null;
        api.MapGet("/config", () => !Volatile.Read(ref available) ? Results.StatusCode(503) : Results.Ok(Volatile.Read(ref invalid) ?? store.Snapshot()));
        await api.StartAsync();
        try
        {
            var settings = new DaqSettings { StateDirectory = Path.Combine(directory, "daq"), ConfigUrl = url, ConfigRefreshSeconds = 1 };
            using var outbox = new Outbox(Path.Combine(directory, "outbox.db"));
            double? Last(string id) => outbox.PeekUnsent(10000).LastOrDefault(r => r.DeviceId == id && r.Reading is not null)?.Reading?.Metrics["Temperature"].Number;
            int Count(string id) => outbox.PeekUnsent(10000).Count(r => r.DeviceId == id);
            async Task Wait(Func<bool> condition, Task running, string reason)
            {
                var until = DateTime.UtcNow.AddSeconds(12);
                while (!condition())
                {
                    if (running.IsCompleted) await running;
                    if (DateTime.UtcNow > until) throw new Exception(reason);
                    await Task.Delay(50);
                }
            }
            async Task Run(Func<AcquisitionService, Task, Task> verify)
            {
                using var config = new ConfigClient(settings);
                var acquisition = new AcquisitionService(settings, config, outbox);
                using var stop = new CancellationTokenSource();
                var running = acquisition.RunAsync(stop.Token);
                try { await verify(acquisition, running); }
                finally { stop.Cancel(); try { await running; } catch (OperationCanceledException) { } }
            }
            await Run(async (acquisition, running) =>
            {
                await Wait(() => Last("a") == 4.5 && Last("b") == 65 && Last("c") == 33, running, "Shared TCP units did not return their own readings.");
                Check(acquisition.Statuses().Length == 3 && Count("draft") == 0, "Serial draft started polling.");
                var countOther = Count("c");
                store.SaveInterface(Tcp("shared", secondPort), false);
                await Wait(() => Last("a") == 8.5 && Last("b") == 90 && Count("c") > countOther, running, "Interface-only edit did not reload both devices or interrupted the unrelated device.");
                store.Save(Device("b", "other", 2), false);
                await Wait(() => Last("b") == 44, running, "Reassigned device still uses its former interface.");
                var cache = Path.Combine(settings.StateDirectory, "config-cache.json");
                await Wait(() => acquisition.ConfigState.Version == store.Snapshot().Version, running, "New configuration version not applied.");
                var validCache = File.ReadAllText(cache);
                Volatile.Write(ref invalid, new(store.Snapshot().Version + 1, [store.Snapshot().Devices[0] with { Host = "" }]));
                var before = Count("a");
                await Wait(() => !acquisition.ConfigState.Reachable && Count("a") > before, running, "Invalid config stopped polling instead of retaining usable configuration.");
                Check(File.ReadAllText(cache) == validCache, "Invalid configuration overwrote the working cache.");
                Volatile.Write(ref available, false);
                Volatile.Write(ref invalid, null);
            });
            var beforeRestart = Count("a");
            await Run(async (_, running) => await Wait(() => Count("a") > beforeRestart && Last("a") == 8.5 && Last("b") == 44,
                running, "API-down restart did not use the complete cached interface settings."));
            using var client = new ConfigClient(settings);
            var cachePath = Path.Combine(settings.StateDirectory, "config-cache.json");
            File.WriteAllText(cachePath, "{\"version\":1,\"devices\":[{\"id\":\"old\",\"enabled\":true,\"protocol\":\"ModbusTcp\",\"host\":\"127.0.0.1\",\"port\":502,\"unitId\":1,\"pollIntervalSeconds\":1,\"metrics\":[{\"name\":\"T\",\"register\":\"holding\",\"address\":0,\"scale\":1,\"unit\":\"\"}]}]}");
            Check(client.LoadCache() is null, "Old cache without interface references was accepted.");
        }
        finally { await api.StopAsync(); }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
