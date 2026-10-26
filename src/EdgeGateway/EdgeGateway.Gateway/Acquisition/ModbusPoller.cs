using System.Globalization;
using System.Net;
using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Storage;
using FluentModbus;
using IoT.Contracts;

namespace EdgeGateway.Gateway.Acquisition;

// A snapshot of one device's polling, for operators and the demo console: what is read, what came back, what failed.
public sealed record PollerStatus(
    string DeviceId, string State, string Endpoint, string[] Frames, long LastSeq, int Failures,
    DateTimeOffset? LastPollAt, DateTimeOffset? LastSuccessAt, string? LastError, RawFrame[] LastRaw,
    Dictionary<string, MetricValue> LastValues);

// The raw words (or bits) of one frame as the device returned them, before decoding.
public sealed record RawFrame(string Register, int Start, int[] Values);

// Polls one Modbus TCP device on its configured interval and commits each reading straight to the outbox,
// so a reading survives a gateway crash from the moment it is polled. After repeated failures it reports
// the device offline once; the next good poll brings it back.
public sealed class ModbusPoller(DeviceDefinition device, DaqSettings settings, Outbox outbox)
{
    private readonly DeviceSequenceStore _sequence = new(device.Id, settings.StateDirectory);
    private readonly ModbusFrame[] _frames = ModbusFrames.Plan(device.Metrics);
    private int _failures;
    private bool? _online;
    private List<RawFrame> _raw = [];
    private volatile PollerStatus? _status;

    // "connecting" until the first poll, then "polling", "failing" (errors, still counted online), or "offline".
    public PollerStatus Status => _status ??= Snapshot("connecting", 0, null, null, null, [], []);
    public string DeviceId => device.Id;

    private PollerStatus Snapshot(string state, long seq, DateTimeOffset? pollAt, DateTimeOffset? successAt, string? error,
        RawFrame[] raw, Dictionary<string, MetricValue> values) =>
        new(device.Id, state, $"{device.Host}:{device.Port} unit {device.UnitId}",
            _frames.Select(frame => $"{frame.Register} {frame.Start}+{frame.Count}").ToArray(),
            seq, _failures, pollAt, successAt, error, raw, values);

    public Task RunAsync(CancellationToken cancellationToken) => Task.Factory.StartNew(() => Run(cancellationToken),
        cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken cancellationToken)
    {
        using var client = new ModbusTcpClient { ConnectTimeout = 2000, ReadTimeout = 2000, WriteTimeout = 2000 };
        var interval = TimeSpan.FromSeconds(device.PollIntervalSeconds);
        Console.WriteLine($"Polling {device.Id} at {device.Host}:{device.Port} every {device.PollIntervalSeconds}s in " +
            string.Join(", ", _frames.Select(frame => $"{frame.Register} {frame.Start}..{frame.Start + frame.Count - 1}")) + ".");
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            // The source timestamp is when the device was asked, not when the reply was processed or published.
            var sampledAt = DateTimeOffset.UtcNow;
            try
            {
                if (!client.IsConnected)
                {
                    client.Connect(new IPEndPoint(Resolve(device.Host), device.Port), ModbusEndianness.BigEndian);
                    sampledAt = DateTimeOffset.UtcNow;
                }
                var metrics = Read(client);
                var reading = new DeviceReading(device.Id, _sequence.Next(), sampledAt, metrics);
                outbox.AcceptReading(reading);
                if (_online != true) Console.WriteLine($"{device.Id} online.");
                _online = true;
                _failures = 0;
                _status = Snapshot("polling", (long)reading.DeviceSeq, sampledAt, sampledAt, null, _raw.ToArray(), metrics);
                Console.WriteLine($"Polled {device.Id} #{reading.DeviceSeq}: " + string.Join(", ", metrics.Select(metric =>
                    $"{metric.Key}={metric.Value.Number?.ToString(CultureInfo.InvariantCulture) ?? metric.Value.Quality}")));
            }
            catch (Exception error)
            {
                try { client.Disconnect(); } catch (Exception) { }
                if (cancellationToken.IsCancellationRequested) break; // stopping mid-poll is not a device failure
                _failures++;
                Console.WriteLine($"Poll {device.Id} failed ({error.GetType().Name}: {error.Message}), {_failures} in a row.");
                // Until the device is declared offline, each failed poll is still a sample: every value at Bad quality,
                // so consumers see the problem immediately instead of an old value that looks current.
                if (_online == true && _failures < settings.FailedPollsBeforeOffline)
                    outbox.AcceptReading(new DeviceReading(device.Id, _sequence.Next(), sampledAt, BadSample()));
                if (_failures >= settings.FailedPollsBeforeOffline && _online != false)
                {
                    _online = false;
                    outbox.AcceptDeviceOffline(device.Id);
                    Console.WriteLine($"{device.Id} offline after {_failures} failed polls.");
                }
                // Keep the last good values and raw frames visible; the error says why they are not current.
                var previous = Status;
                _status = Snapshot(_online == false ? "offline" : "failing", previous.LastSeq, sampledAt, previous.LastSuccessAt,
                    $"{error.GetType().Name}: {error.Message}", previous.LastRaw, previous.LastValues);
            }
            var wait = interval - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero) cancellationToken.WaitHandle.WaitOne(wait);
        }
    }

    // One request per frame, then each metric is decoded from its slice of the frame.
    private Dictionary<string, MetricValue> Read(ModbusTcpClient client)
    {
        var metrics = new Dictionary<string, MetricValue>(StringComparer.Ordinal);
        var raw = new List<RawFrame>();
        int unit = device.UnitId;
        foreach (var frame in _frames)
        {
            if (frame.Register is "coil" or "discrete")
            {
                var bits = frame.Register == "coil"
                    ? client.ReadCoils(unit, frame.Start, frame.Count).ToArray()
                    : client.ReadDiscreteInputs(unit, frame.Start, frame.Count).ToArray();
                raw.Add(new(frame.Register, frame.Start,
                    Enumerable.Range(0, frame.Count).Select(index => (int)ModbusFrames.DecodeBit(bits, index)).ToArray()));
                foreach (var metric in frame.Metrics)
                    metrics[metric.Name] = new(Number: ModbusFrames.DecodeBit(bits, metric.Address - frame.Start) * metric.Scale);
                continue;
            }
            var words = frame.Register == "holding"
                ? client.ReadHoldingRegisters<ushort>(unit, frame.Start, frame.Count).ToArray()
                : client.ReadInputRegisters<ushort>(unit, frame.Start, frame.Count).ToArray();
            raw.Add(new(frame.Register, frame.Start, words.Select(word => (int)word).ToArray()));
            foreach (var metric in frame.Metrics)
            {
                var value = ModbusFrames.Decode(words.AsSpan(metric.Address - frame.Start, metric.Width), metric.Type, metric.Order);
                // A float register can hold NaN or infinity: report that one value as Bad, keep the rest of the reading.
                metrics[metric.Name] = double.IsFinite(value)
                    ? new(Number: Math.Round(value * metric.Scale, 6))
                    : new(Quality: MetricQuality.Bad);
            }
        }
        // The GPIO pin is configuration, published with the readings so consumers know which input it is.
        if (device.GpioPin is { } pin) metrics["GpioPin"] = new(Number: pin);
        _raw = raw;
        return metrics;
    }

    // Same metric list as a good reading (so no rebirth is needed), with no values.
    private Dictionary<string, MetricValue> BadSample()
    {
        var metrics = device.Metrics.ToDictionary(metric => metric.Name, _ => new MetricValue(Quality: MetricQuality.Bad),
            StringComparer.Ordinal);
        if (device.GpioPin is { } pin) metrics["GpioPin"] = new(Number: pin);
        return metrics;
    }

    private static IPAddress Resolve(string host) =>
        IPAddress.TryParse(host, out var address) ? address
            : Dns.GetHostAddresses(host).First(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

    public bool SameDefinition(DeviceDefinition other) =>
        System.Text.Json.JsonSerializer.Serialize(device) == System.Text.Json.JsonSerializer.Serialize(other);
}
