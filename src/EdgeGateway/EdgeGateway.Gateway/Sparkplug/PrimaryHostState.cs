using System.Text.Json;

namespace EdgeGateway.Gateway.Sparkplug;

// Tracks the Primary Host Application's STATE for one MQTT session.
public sealed class PrimaryHostState
{
    private readonly object _gate = new();
    private long _timestamp = -1;
    private bool _online;
    private TaskCompletionSource _cameOnline = NewSignal();
    private TaskCompletionSource _wentOffline = NewSignal();

    public bool IsOnline
    {
        get { lock (_gate) return _online; }
    }

    public Task CameOnline => _cameOnline.Task;
    public Task WentOffline => _wentOffline.Task;

    public void Reset()
    {
        lock (_gate)
        {
            _timestamp = -1;
            _online = false;
            _cameOnline = NewSignal();
            _wentOffline = NewSignal();
        }
    }

    public void Apply(byte[] payload)
    {
        bool online;
        long timestamp;
        try
        {
            using var document = JsonDocument.Parse(payload);
            online = document.RootElement.GetProperty("online").GetBoolean();
            timestamp = document.RootElement.GetProperty("timestamp").GetInt64();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Console.WriteLine("Ignored invalid STATE message.");
            return;
        }
        lock (_gate)
        {
            // An older STATE (for example a delayed Will) must not override a newer one.
            if (timestamp < _timestamp) return;
            _timestamp = timestamp;
            if (online)
            {
                _online = true;
                _cameOnline.TrySetResult();
            }
            else if (_online)
            {
                _online = false;
                _wentOffline.TrySetResult();
            }
        }
        Console.WriteLine($"Primary host is {(online ? "online" : "offline")}.");
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
