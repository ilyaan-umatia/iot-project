using Application.Api.Services;
using Cloud.Forwarder;
using Device.Gateway.Sparkplug;
using Google.Protobuf;
using IoT.Contracts;
using Org.Eclipse.Tahu.Protobuf;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var decoder = new SparkplugDecoder();
using var memoryStore = new TelemetryStore(":memory:");
var state = new GpioMessageService(memoryStore);
var prefix = "spBv1.0/warehouse-01/";
LifecycleEvent Send(string type, Payload payload)
{
    var topic = prefix + type + "/gateway-01" + (type.StartsWith('D') ? "/fridge-01" : "");
    var item = decoder.Decode(topic, payload.ToByteArray());
    var json = JsonSerializer.Serialize(item);
    state.Process(JsonSerializer.Deserialize<LifecycleEvent>(json)!);
    return item;
}
void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
void Reject(Action action)
{
    try { action(); } catch (ArgumentException) { return; }
    throw new Exception("Invalid event was accepted.");
}
Reject(() => Send("DDATA", SparkplugPayloads.DeviceReading(1, 5, 60, 17, 0)));
Send("NBIRTH", SparkplugPayloads.NodeBirth(7));
var birth = Send("DBIRTH", SparkplugPayloads.DeviceReading(1, 4.5, 65, 17, 0));
Check(!birth.Metrics.ContainsKey("DoorOpen"), "Gateway should send raw GPIO input.");
Check(state.GetDevices().Single() is { GpioPin: 17, GpioValue: 0, DoorOpen: false }, "GPIO low must mean door closed.");
Send("NDATA", SparkplugPayloads.NodeData(2, 5));
Send("DDATA", SparkplugPayloads.DeviceReading(3, 9.2, 67, 17, 1));
Check(state.GetDevices().Single() is { Online: true, Temperature: 9.2, HighTemperature: true, GpioPin: 17, GpioValue: 1, DoorOpen: true }, "Reading mismatch.");
state.Process(birth); // queue redelivery must not restore the older reading
Check(state.GetDevices().Single().Temperature == 9.2, "Duplicate overwrote latest reading.");
Send("DDEATH", SparkplugPayloads.DeviceDeath(4));
Check(!state.GetDevices().Single().Online && state.GetGateways().Single().Online, "Device outage affected gateway.");
Send("NDATA", SparkplugPayloads.NodeData(5, 10));
Send("DBIRTH", SparkplugPayloads.DeviceReading(6, 5, 69, 17, 0));
Check(state.GetDevices().Single().Online, "Device failed to recover.");
Check(!state.GetDevices().Single().DoorOpen, "GPIO low did not close the door on recovery.");
Reject(() => decoder.Decode(prefix + "NDEATH/gateway-01", SparkplugPayloads.NodeDeath(6).ToByteArray()));
Check(state.GetGateways().Single().Online, "Old death affected current session.");
Send("NDEATH", SparkplugPayloads.NodeDeath(7));
Check(!state.GetGateways().Single().Online && !state.GetDevices().Single().Online, "Node loss did not invalidate devices.");
Send("NBIRTH", SparkplugPayloads.NodeBirth(8, 12));
Send("DBIRTH", SparkplugPayloads.DeviceReading(1, 5, 69, 17, 0));
Reject(() => Send("NDATA", SparkplugPayloads.NodeData(3, 15)));
Reject(() => Send("NDATA", SparkplugPayloads.NodeData(4, 20)));
Send("NBIRTH", SparkplugPayloads.NodeBirth(9));
Send("DBIRTH", SparkplugPayloads.DeviceReading(1, 5, 69, 17, 0));
for (ulong seq = 2; seq <= 255; seq++) Send("NDATA", SparkplugPayloads.NodeData(seq, seq));
Send("NDATA", SparkplugPayloads.NodeData(0, 256));
Check(state.GetGateways().Single().UptimeSeconds == 256, "Sequence wrap failed.");
Reject(() => state.Process(birth with { EventId = "bad", GatewayId = "forged" }));
Reject(() => state.Process(birth with { EventId = "bad-humidity", Metrics = new() { ["Humidity"] = new(Number: 101) } }));
Reject(() => state.Process(birth with { EventId = "bad-gpio", Metrics = new() { ["GpioValue"] = new(Number: 2) } }));
Reject(() => state.Process(birth with { EventId = "bad-pin", Metrics = new() { ["GpioPin"] = new(Number: -1) } }));
Reject(() => state.Process(birth with { EventId = "fractional-pin", Metrics = new() { ["GpioPin"] = new(Number: 17.5) } }));
foreach (var metrics in new Dictionary<string, MetricValue>[]
{
    new() { ["Temperature"] = new(Boolean: true) },
    new() { ["Humidity"] = new() },
    new() { ["Temperature"] = new(Number: 5, Boolean: true) },
    new() { ["Temperature"] = new(Number: double.NaN) },
    new() { ["UptimeSeconds"] = new(Number: -1) },
    new() { [""] = new(Number: 1) }
})
{
    var invalidMetrics = new Dictionary<string, MetricValue>(birth.Metrics);
    foreach (var metric in metrics) invalidMetrics[metric.Key] = metric.Value;
    Reject(() => state.Process(birth with { EventId = Guid.NewGuid().ToString("N"), Metrics = invalidMetrics }));
}
Reject(() => state.Process(birth with { EventId = "partial-birth", Metrics = new() { ["Temperature"] = new(Number: 5) } }));
Reject(() => state.Process(new LifecycleEvent("node-device", "NDATA", "warehouse-01", "gateway-01",
    "fridge-01", prefix + "NDATA/gateway-01", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, null, new())));
Reject(() => state.Process(birth with { EventId = "wildcard", GroupId = "+", SourceTopic = "spBv1.0/+/DBIRTH/gateway-01/fridge-01" }));
Console.WriteLine("PASS: GPIO door mapping, binary decoding, JSON roundtrip, lifecycle/recovery, duplicates, stale death, sequence gaps/wrap and validation.");

var restartedDecoder = new SparkplugDecoder();
Reject(() => restartedDecoder.Decode(prefix + "NDATA/gateway-01", SparkplugPayloads.NodeData(2, 5).ToByteArray()));
var offlineWithoutBirth = restartedDecoder.Decode(prefix + "NDEATH/gateway-01", SparkplugPayloads.NodeDeath(9).ToByteArray());
Check(offlineWithoutBirth is { MessageType: "NDEATH", BdSeq: 9 }, "Independent forwarder lost death without a local birth cache.");
restartedDecoder.Decode(prefix + "NBIRTH/gateway-01", SparkplugPayloads.NodeBirth(10).ToByteArray());
restartedDecoder.Decode(prefix + "DBIRTH/gateway-01/fridge-01", SparkplugPayloads.DeviceReading(1, 5, 65, 17, 0).ToByteArray());
restartedDecoder.Reset();
Reject(() => restartedDecoder.Decode(prefix + "DDATA/gateway-01/fridge-01", SparkplugPayloads.DeviceReading(2, 5, 65, 17, 0).ToByteArray()));
restartedDecoder.Decode(prefix + "NBIRTH/gateway-01", SparkplugPayloads.NodeBirth(11).ToByteArray());
restartedDecoder.Decode(prefix + "DBIRTH/gateway-01/fridge-01", SparkplugPayloads.DeviceReading(1, 5, 65, 17, 0).ToByteArray());
Check(restartedDecoder.Decode(prefix + "DDATA/gateway-01/fridge-01", SparkplugPayloads.DeviceReading(2, 6, 65, 17, 0).ToByteArray()).Metrics["Temperature"].Number == 6,
    "Fresh births did not restore decoding after reconnect.");
Console.WriteLine("PASS: Independent forwarder cold start, death without birth cache and manual birth recovery after reset.");

var testDirectory = Path.Combine(Path.GetTempPath(), "iot-sqlite-checks-" + Guid.NewGuid().ToString("N"));
var databasePath = Path.Combine(testDirectory, "backend.db");
var time = DateTimeOffset.UtcNow;
LifecycleEvent Event(string type, int seconds, ulong? seq, ulong? bdSeq = null, double temperature = 5) =>
    new(Guid.NewGuid().ToString("N"), type, "warehouse-01", "gateway-01",
        type.StartsWith('D') ? "fridge-01" : null,
        prefix + type + "/gateway-01" + (type.StartsWith('D') ? "/fridge-01" : ""),
        time.AddSeconds(seconds), type == "NDEATH" ? null : time.AddSeconds(seconds), seq, bdSeq,
        type is "DBIRTH" or "DDATA" ? new()
        {
            ["Temperature"] = new(Number: temperature), ["Humidity"] = new(Number: 65),
            ["GpioPin"] = new(Number: 17), ["GpioValue"] = new(Number: 0)
        } : type == "NDATA" ? new() { ["UptimeSeconds"] = new(Number: seconds) } : new());

var firstBirth = Event("NBIRTH", 0, 0, 40);
var firstDevice = Event("DBIRTH", 1, 1);
var firstReading = Event("DDATA", 2, 2, temperature: 9.2);
try
{
    using (var firstStore = new TelemetryStore(databasePath))
    {
        var first = new GpioMessageService(firstStore);
        first.Process(firstBirth);
        first.Process(firstDevice);
        first.Process(firstReading);
    }
    using (var restoredStore = new TelemetryStore(databasePath))
    {
        var restored = new GpioMessageService(restoredStore);
        Check(restored.GetGateways().Single() is { Online: true, BdSeq: 40 }, "Gateway session was lost after restart.");
        Check(restored.GetDevice("fridge-01") is { Online: true, Temperature: 9.2 }, "Device state was lost after restart.");
        Check(restored.Process(Event("NDATA", 3, 3)), "Queued NDATA needs a new birth after restart.");
        Check(restored.Process(Event("NDATA", 3, 3) with { Metrics = new() }), "Empty node update was rejected.");
        Check(restored.GetGateways().Single().UptimeSeconds == 3, "Partial node update erased stored uptime.");
        Check(restored.Process(Event("DDATA", 4, 4, temperature: 6)), "Queued DDATA needs a new birth after restart.");
        restored.Process(firstReading);
        Check(restored.GetHistory("fridge-01").Length == 3, "Redelivery duplicated history after restart.");
        Check(restored.GetDevice("fridge-01")!.Temperature == 6, "Redelivery overwrote current data.");

        var older = Event("DDATA", 5, 5, temperature: 3) with { RecordedAt = time.AddSeconds(2) };
        Check(!restored.Process(older), "Older reading overwrote newer data.");
        Check(restored.GetDevice("fridge-01")!.Temperature == 6, "Timestamp guard did not preserve latest reading.");
        Check(restored.GetHistory("fridge-01").Any(row => row.Event.EventId == older.EventId && !row.AppliedToState),
            "Older reading was not kept in history.");
        var page = restored.GetHistory("fridge-01", 2);
        var nextPage = restored.GetHistory("fridge-01", 2, page.Last().Id);
        Check(page.Length == 2 && nextPage.Length == 2 && !page.Select(row => row.Id).Intersect(nextPage.Select(row => row.Id)).Any(),
            "History pagination skipped or duplicated readings.");
        Reject(() => restored.GetHistory("fridge-01", 501));
        Reject(() => restored.GetHistory("fridge-01", before: 0));

        // A failed history write must also roll back the current-state update.
        using var probe = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        probe.Open();
        using var trigger = probe.CreateCommand();
        trigger.CommandText = "CREATE TRIGGER FailHistory BEFORE INSERT ON Events WHEN NEW.EventId = 'fail-write' BEGIN SELECT RAISE(ABORT, 'test write failure'); END";
        trigger.ExecuteNonQuery();
        var failed = Event("DDATA", 6, 6, temperature: 10) with { EventId = "fail-write" };
        try { restored.Process(failed); throw new Exception("Write failure was ignored."); }
        catch (SqliteException) { }
        Check(restored.GetDevice("fridge-01")!.Temperature == 6 && restored.GetHistory("fridge-01").Length == 4,
            "Failed transaction left partial data.");
        trigger.CommandText = "DROP TRIGGER FailHistory";
        trigger.ExecuteNonQuery();
        Check(restored.Process(failed), "Failed write could not be retried.");
        Check(!restored.Process(Event("NDEATH", 7, null, 39)), "Old session death was applied.");
        Check(restored.GetGateways().Single().Online, "Old session death marked gateway offline.");
        restored.Process(Event("DDEATH", 8, 7));
        restored.Process(Event("NDEATH", 9, null, 40));
    }
    using (var lastStore = new TelemetryStore(databasePath))
    {
        var last = new GpioMessageService(lastStore);
        Check(!last.GetGateways().Single().Online && !last.GetDevices().Single().Online, "Offline state did not survive restart.");
        Check(last.GetHistory("fridge-01").Length == 6, "Full device history did not survive restart.");
        last.Process(Event("NBIRTH", 10, 0, 41));
        last.Process(Event("DBIRTH", 11, 1));
        Check(last.GetGateways().Single() is { Online: true, BdSeq: 41 } && last.GetDevices().Single().Online,
            "Fresh session failed after restoring offline state.");
        var missing = Event("DDATA", 12, 2) with
        {
            DeviceId = "fridge-02", SourceTopic = prefix + "DDATA/gateway-01/fridge-02"
        };
        Check(!last.Process(missing), "Unknown device was marked online without birth.");
        Check(last.GetHistory("fridge-02").Single().Event.EventId == missing.EventId,
            "Valid data without birth was lost instead of saved to history.");
        var bad = Event("DDATA", 13, 3) with { Metrics = new() { ["Humidity"] = new(Number: 101) } };
        Reject(() => last.Process(bad));
        Check(!last.GetHistory("fridge-01").Any(row => row.Event.EventId == bad.EventId), "Invalid data was committed.");
    }
    Console.WriteLine("PASS: SQLite restart recovery, complete history, persistent deduplication, timestamp checks, pagination, transaction rollback and missing births.");
}
finally
{
    foreach (var file in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        if (File.Exists(file)) File.Delete(file);
    Directory.Delete(testDirectory);
}

var counterDirectory = Path.Combine(Path.GetTempPath(), "gateway-counter-" + Guid.NewGuid().ToString("N"));
var counterClient = "check-" + Guid.NewGuid().ToString("N");
var counterFilename = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
    System.Text.Encoding.UTF8.GetBytes(counterClient))) + ".txt";
var previousCounter = Path.Combine(AppContext.BaseDirectory, "state", counterFilename);
try
{
    Directory.CreateDirectory(Path.GetDirectoryName(previousCounter)!);
    File.WriteAllText(previousCounter, "40");
    var counter = new BirthDeathSequenceStore(counterClient, counterDirectory);
    Check(counter.ReserveNext() == 41, "Existing gateway counter was reset during migration.");
    File.WriteAllText(previousCounter, "1");
    Check(new BirthDeathSequenceStore(counterClient, counterDirectory).ReserveNext() == 42,
        "Restart did not use the stable counter or overwrote it with the old file.");
    File.WriteAllText(Path.Combine(counterDirectory, counterFilename), "255");
    Check(counter.ReserveNext() == 0, "Gateway counter did not wrap at 255.");
    Check(new BirthDeathSequenceStore(counterClient + "-new", counterDirectory).ReserveNext() == 0,
        "New gateway counter did not start at zero.");
    Reject(() => new BirthDeathSequenceStore(counterClient, "relative-state"));
    Console.WriteLine("PASS: Stable gateway counter path, migration, restart, wrap and path validation.");
}
finally
{
    if (File.Exists(previousCounter)) File.Delete(previousCounter);
    if (Directory.Exists(counterDirectory))
    {
        foreach (var file in Directory.GetFiles(counterDirectory)) File.Delete(file);
        Directory.Delete(counterDirectory);
    }
}

using (var historyStore = new TelemetryStore(":memory:"))
{
    var history = new GpioMessageService(historyStore);
    history.Process(firstBirth);
    history.Process(firstDevice);
    for (var index = 0; index < 520; index++)
        history.Process(Event("DDATA", index + 2, (ulong)(index % 256), temperature: index));
    var firstPage = history.GetHistory("fridge-01", 500);
    var secondPage = history.GetHistory("fridge-01", 500, firstPage[^1].Id);
    Check(firstPage.Length == 500 && secondPage.Length == 21, "History beyond 500 records was truncated.");
    Check(firstPage.Concat(secondPage).Select(row => row.Event.EventId).Distinct().Count() == 521,
        "Full history pagination duplicated or lost events.");
    Check(firstPage[0].Event.Metrics["Temperature"].Number == 519, "History metrics did not roundtrip.");

    Parallel.For(0, 200, index =>
    {
        history.Process(Event("DDATA", index + 600, (ulong)(index % 256), temperature: index));
        history.GetDevices();
        history.GetHistory("fridge-01", 10);
    });
    var all = new List<Application.Api.Models.HistoryEntry>();
    long? cursor = null;
    while (true)
    {
        var page = history.GetHistory("fridge-01", 500, cursor);
        if (page.Length == 0) break;
        all.AddRange(page);
        cursor = page[^1].Id;
    }
    Check(all.Count == 721 && all.Select(row => row.Event.EventId).Distinct().Count() == 721,
        "Concurrent reads and writes lost or duplicated history.");
    Check(history.GetDevice("fridge-01")!.RecordedAt == time.AddSeconds(799),
        "Concurrent older events overwrote the latest device state.");
    Console.WriteLine("PASS: Complete history across 500-record pages and concurrent storage/retrieval.");
}
