using IoT.Contracts;
using Org.Eclipse.Tahu.Protobuf;

namespace EdgeGateway.BrokerMqtt.Sparkplug;

public static class SparkplugPayloads
{
    public const string RebirthMetric = "Node Control/Rebirth";

    public static Payload NodeBirth(ulong birthDeathSequence, ulong uptimeSeconds = 0)
    {
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = new Payload { Timestamp = timestamp, Seq = 0 };

        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "bdSeq",
            Datatype = (uint)DataType.Int64,
            LongValue = birthDeathSequence,
            Timestamp = timestamp
        });

        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "UptimeSeconds",
            Datatype = (uint)DataType.Int64,
            LongValue = uptimeSeconds,
            Timestamp = timestamp
        });

        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = RebirthMetric,
            Datatype = (uint)DataType.Boolean,
            BooleanValue = false,
            Timestamp = timestamp
        });
        return payload;
    }
    public static Payload NodeData(ulong sequence, ulong uptimeSeconds)
    {
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = new Payload { Timestamp = timestamp, Seq = sequence };
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "UptimeSeconds",
            Timestamp = timestamp,
            LongValue = uptimeSeconds
        });
        return payload;
    }
    public static Payload NodeDeath(ulong birthDeathSequence)
    {
        // The Will is prepared before connecting, so its timestamp is not the death time.
        var payload = new Payload();
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "bdSeq",
            Datatype = (uint)DataType.Int64,
            LongValue = birthDeathSequence
        });
        return payload;
    }
    public static Payload DeviceReading(ulong sequence, double temperature, double humidity, int gpioPin, int gpioValue) =>
        DeviceReading(sequence, new IoT.Contracts.DeviceReading("", 0, DateTimeOffset.UtcNow,
            temperature, humidity, gpioPin, gpioValue), includeDeviceSeq: false);

    // Timestamps are the measurement time, so replayed readings keep their original time.
    public static Payload DeviceReading(ulong sequence, DeviceReading reading, bool historical = false,
        bool includeDeviceSeq = true)
    {
        var timestamp = (ulong)reading.Timestamp.ToUnixTimeMilliseconds();
        var payload = new Payload { Timestamp = timestamp, Seq = sequence };
        void Add(Payload.Types.Metric metric)
        {
            metric.Timestamp = timestamp;
            if (historical) metric.IsHistorical = true;
            payload.Metrics.Add(metric);
        }
        Add(new() { Name = "Temperature", Datatype = (uint)DataType.Double, DoubleValue = reading.Temperature });
        Add(new() { Name = "Humidity", Datatype = (uint)DataType.Double, DoubleValue = reading.Humidity });
        Add(new() { Name = "GpioPin", Datatype = (uint)DataType.Int32, IntValue = (uint)reading.GpioPin });
        Add(new() { Name = "GpioValue", Datatype = (uint)DataType.Int32, IntValue = (uint)reading.GpioValue });
        if (includeDeviceSeq)
            Add(new() { Name = "DeviceSeq", Datatype = (uint)DataType.Int64, LongValue = reading.DeviceSeq });
        return payload;
    }

    public static Payload DeviceDeath(ulong sequence) => new()
    {
        Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Seq = sequence
    };

    public static bool IsRebirthCommand(byte[] bytes)
    {
        var payload = Payload.Parser.ParseFrom(bytes);
        return payload.Metrics.Any(metric => metric.Name == RebirthMetric && metric.HasBooleanValue && metric.BooleanValue);
    }
}
