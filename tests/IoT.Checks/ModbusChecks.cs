using System.Net;
using System.Net.Sockets;
using EdgeGateway.Gateway.Sparkplug;
using EdgeGateway.Gateway.Storage;
using EdgeGateway.Configuration;
using EdgeGateway.Gateway.Acquisition;
using EdgeGateway.Gateway.Configuration;
using FluentModbus;
using Google.Protobuf;
using IoT.Contracts;

internal static class ModbusChecks
{
    public static async Task RunAsync()
    {
        CheckRegistry();
        CheckMetricChangeRebirth();
        CheckFrames();
        CheckQuality();
        await CheckPollerAsync();
        Console.WriteLine("PASS: Device registry, DBIRTH on metric change, read frames, int16/uint16/int32/uint32/float32 decoding with word order, Good/Bad/Stale quality through Sparkplug, Modbus polling into the outbox with request-time stamps, Bad samples and offline detection.");
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }

    private static DeviceDefinition Fridge(string id, int port, bool enabled = true) => new(id, enabled,
        DeviceDefinition.ModbusTcp, "127.0.0.1", port, 1, 1, 17, "check",
        [new("Temperature", "holding", 0, 0.1, "Â°C"), new("Humidity", "holding", 1, 0.1, "%"), new("GpioValue", "coil", 0, 1, "door")]);

    private static void CheckRegistry()
    {
        using var store = new DeviceStore(":memory:");
        Check(store.Save(Fridge("fridge-a", 5021), create: true), "Create failed.");
        Check(!store.Save(Fridge("fridge-a", 5021), create: true), "Duplicate device was created.");
        var version = store.Snapshot().Version;
        Check(store.Save(Fridge("fridge-a", 5021, enabled: false), create: false), "Update failed.");
        var disabled = store.Snapshot();
        Check(disabled.Version > version && disabled.Devices.Length == 0, "Disabling did not bump the version or hide the device.");
        Check(store.Delete("fridge-a") && store.Snapshot().Version > disabled.Version, "Delete did not bump the version.");
        foreach (var bad in new[]
        {
            Fridge("bad id", 5021), Fridge("fridge-b", 0), Fridge("fridge-c", 5021) with { Metrics = [] },
            Fridge("fridge-d", 5021) with { Metrics = [new("Temperature", "register", 0, 1, "")] },
            Fridge("fridge-e", 5021) with { Metrics = [new("X", "holding", 0, 1, ""), new("X", "holding", 1, 1, "")] }
        })
        {
            try { store.Save(bad, create: true); throw new Exception($"Invalid device {bad.Id} was accepted."); }
            catch (ArgumentException) { }
        }
    }

    private static void CheckMetricChangeRebirth()
    {
        using var outbox = new Outbox(":memory:");
        var first = SparkplugPayloads.FridgeReading("fridge-a", 1, DateTimeOffset.UtcNow, 4.5, 65, 17, 0);
        outbox.AcceptReading(first);
        outbox.AcceptReading(first with { DeviceSeq = 2 });
        var changed = first with { DeviceSeq = 3, Metrics = new(first.Metrics) { ["Pressure"] = new(Number: 1.2) } };
        outbox.AcceptReading(changed);
        Check(outbox.PeekUnsent(10).Select(row => row.Kind).SequenceEqual(["DBIRTH", "DDATA", "DBIRTH"]),
            "A changed metric list did not trigger a new DBIRTH.");
    }

    private static void CheckFrames()
    {
        MetricDefinition Metric(string name, string register, ushort address, string? type = null) =>
            new(name, register, address, 1, "", type);
        var frames = ModbusFrames.Plan(
        [
            Metric("A", "holding", 0, "int16"), Metric("B", "holding", 1, "uint16"),
            Metric("C", "holding", 10, "float32"), Metric("D", "holding", 12, "uint32"),
            Metric("Far", "holding", 200, "int16"), Metric("Door", "coil", 0)
        ]);
        Check(frames.Length == 3
              && frames.Any(frame => frame is { Register: "holding", Start: 0, Count: 14 } && frame.Metrics.Length == 4)
              && frames.Any(frame => frame is { Register: "holding", Start: 200, Count: 1 })
              && frames.Any(frame => frame is { Register: "coil", Start: 0, Count: 1 }),
            "Read frames were not planned as expected: " + string.Join("; ", frames.Select(f => $"{f.Register} {f.Start}+{f.Count}")));
        var floatBits = (uint)BitConverter.SingleToInt32Bits(85.5f);
        ushort[] big = [(ushort)(floatBits >> 16), (ushort)floatBits];
        Check(ModbusFrames.Decode(big, "float32", "big") == 85.5, "float32 (big word order) decoded wrongly.");
        Check(ModbusFrames.Decode([big[1], big[0]], "float32", "little") == 85.5, "float32 (little word order) decoded wrongly.");
        Check(ModbusFrames.Decode([0xFFFF, 0xFFFE], "int32", "big") == -2, "int32 sign handled wrongly.");
        Check(ModbusFrames.Decode([0x0001, 0x1170], "uint32", "big") == 70_000, "uint32 decoded wrongly.");
        Check(ModbusFrames.Decode([0xFFE7], "int16", "big") == -25 && ModbusFrames.Decode([0xFFE7], "uint16", "big") == 65_511,
            "16-bit sign handled wrongly.");
        Check(ModbusFrames.DecodeBit([0b0000_0100], 2) == 1 && ModbusFrames.DecodeBit([0b0000_0100], 1) == 0, "Bit unpacking is wrong.");
        foreach (var bad in new[] { Metric("X", "coil", 0, "int16"), Metric("X", "holding", 0, "bool"), Metric("X", "holding", 65535, "uint32"),
                     new MetricDefinition("X", "holding", 0, 1, "", "float32", "middle") })
        {
            try { bad.Validate(); throw new Exception($"Invalid metric {bad} was accepted."); }
            catch (ArgumentException) { }
        }
    }

    private static void CheckQuality()
    {
        var reading = new DeviceReading("fridge-q", 7, DateTimeOffset.UtcNow, new()
        {
            ["Temperature"] = new(Number: 4.5), ["Humidity"] = new(Quality: MetricQuality.Bad),
            ["Power"] = new(Quality: MetricQuality.Stale)
        });
        reading.Validate();
        foreach (var bad in new MetricValue[] { new(Number: 1, Quality: MetricQuality.Bad), new(Quality: "odd"), new() })
        {
            try { (reading with { Metrics = new() { ["X"] = bad } }).Validate(); throw new Exception($"Invalid metric {bad} was accepted."); }
            catch (ArgumentException) { }
        }
        var payload = SparkplugPayloads.DeviceReading(1, reading);
        var humidity = payload.Metrics.Single(metric => metric.Name == "Humidity");
        Check(humidity.IsNull && SparkplugDecoder.QualityOf(humidity) == MetricQuality.Bad, "Bad quality was not encoded as a null metric with Quality 0.");
        Check(payload.Metrics.Single(metric => metric.Name == "Temperature").Properties is null, "A good metric should carry no quality property.");
        var decoder = new SparkplugDecoder();
        decoder.Decode("spBv1.0/g/NBIRTH/n", SparkplugPayloads.NodeBirth(1).ToByteArray());
        var decoded = decoder.Decode("spBv1.0/g/DBIRTH/n/fridge-q", payload.ToByteArray());
        Check(decoded.Metrics["Humidity"] is { Quality: MetricQuality.Bad, Number: null }
              && decoded.Metrics["Power"].Quality == MetricQuality.Stale && decoded.Metrics["Temperature"] is { Number: 4.5, IsGood: true },
            "Quality did not survive the Sparkplug round trip.");
    }

    private static async Task CheckPollerAsync()
    {
        var port = FreePort();
        var state = Path.Combine(Path.GetTempPath(), "daq-check-" + Guid.NewGuid().ToString("N"));
        using var server = new ModbusTcpServer();
        server.Start(new IPEndPoint(IPAddress.Loopback, port));
        SetSensorValues(server);
        using var outbox = new Outbox(":memory:");
        using var stop = new CancellationTokenSource();
        var settings = new DaqSettings { StateDirectory = state, FailedPollsBeforeOffline = 3 };
        var device = Fridge("fridge-a", port) with
        {
            Metrics =
            [
                new("Temperature", "holding", 0, 0.1, "Â°C", "int16"), new("Humidity", "holding", 1, 0.1, "%", "uint16"),
                new("CompressorPower", "holding", 10, 1, "W", "float32"), new("DoorOpenings", "holding", 12, 1, "", "uint32"),
                new("GpioValue", "coil", 0, 1, "door")
            ]
        };
        var startedAt = DateTimeOffset.UtcNow.AddMilliseconds(-1);
        var polling = new ModbusPoller(device, settings, outbox).RunAsync(stop.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            OutboxRow[] rows;
            while ((rows = outbox.PeekUnsent(10)).Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
            var reading = rows.FirstOrDefault()?.Reading;
            Check(rows.FirstOrDefault()?.Kind == "DBIRTH" && reading is { DeviceSeq: 1 }
                  && reading.Metrics["Temperature"].Number == -2.5 && reading.Metrics["Humidity"].Number == 65
                  && reading.Metrics["CompressorPower"].Number == 85.5 && reading.Metrics["DoorOpenings"].Number == 70_000
                  && reading.Metrics["GpioValue"].Number == 1 && reading.Metrics["GpioPin"].Number == 17,
                "Registers were not read, decoded and scaled correctly: " + string.Join(", ",
                    reading?.Metrics.Select(metric => $"{metric.Key}={metric.Value.Number}") ?? []));
            server.Stop();
            while (!outbox.PeekUnsent(100).Any(row => row.Kind == "DDEATH") && DateTime.UtcNow < deadline) await Task.Delay(100);
            var after = outbox.PeekUnsent(100);
            Check(after.Any(row => row is { Kind: "DDEATH", DeviceId: "fridge-a" }), "Three failed polls did not record the device offline.");
            // The failed polls before the death are Bad samples, numbered in sequence, so the gap is explained.
            var badSamples = after.TakeWhile(row => row.Kind != "DDEATH").Where(row => row.Reading?.Metrics["Temperature"].Quality == MetricQuality.Bad).ToArray();
            Check(badSamples.Length == settings.FailedPollsBeforeOffline - 1 && badSamples.All(row => row.Reading!.Metrics["GpioPin"].Number == 17),
                $"Expected {settings.FailedPollsBeforeOffline - 1} Bad samples before DDEATH, got {badSamples.Length}.");
            Check(reading!.Timestamp >= startedAt && reading.Timestamp <= rows[0].CreatedAt.AddMilliseconds(1), "The reading was not stamped at request time.");


        }
        finally
        {
            stop.Cancel();
            await polling;
            Directory.Delete(state, recursive: true);
        }
    }

    // Values as a sensor would leave them: -2.5 Â°C, 65.0 %, 85.5 W, 70000 door openings, door open.
    private static void SetSensorValues(ModbusTcpServer server)
    {
        server.AddUnit(1);
        var registers = server.GetHoldingRegisters(1);
        registers.SetBigEndian<short>(0, -25);
        registers.SetBigEndian<short>(1, 650);
        var power = BitConverter.SingleToInt32Bits(85.5f);
        registers.SetBigEndian<short>(10, (short)(power >> 16));
        registers.SetBigEndian<short>(11, (short)power);
        registers.SetBigEndian<short>(12, 1);
        registers.SetBigEndian<short>(13, 0x1170);
        server.GetCoils(1)[0] = 1;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
