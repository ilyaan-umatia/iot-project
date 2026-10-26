using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Storage;
using IoT.Contracts;

namespace EdgeGateway.Gateway.Acquisition;

// Runs one poller per configured device. Follows the configuration service: added devices start polling,
// removed or disabled ones leave with a DDEATH. Keeps the last device list if the service is unreachable.
public sealed class AcquisitionService(DaqSettings settings, ConfigClient config, Outbox outbox)
{
    private readonly Dictionary<string, (ModbusPoller Poller, CancellationTokenSource Stop, Task Task)> _pollers = [];
    private readonly object _gate = new();
    private long? _version;
    private bool _configReachable = true;

    public PollerStatus[] Statuses()
    {
        lock (_gate) return _pollers.Values.Select(item => item.Poller.Status).OrderBy(status => status.DeviceId).ToArray();
    }

    public (long? Version, bool Reachable) ConfigState
    {
        get { lock (_gate) return (_version, _configReachable); }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        long? version = null;
        var reachable = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var fetched = await config.TryFetchAsync(cancellationToken);
                if (fetched is null && version is null && config.LoadCache() is { } cached)
                {
                    Console.WriteLine($"Configuration service unreachable. Using cached configuration v{cached.Version}.");
                    fetched = cached;
                }
                if (fetched is null)
                {
                    if (reachable) Console.WriteLine(version is null
                        ? "Configuration service unreachable and no cached configuration yet. Retrying."
                        : "Configuration service unreachable. Continuing with the current device list.");
                    reachable = false;
                    lock (_gate) _configReachable = false;
                }
                else
                {
                    if (!reachable) Console.WriteLine("Configuration service reachable again.");
                    reachable = true;
                    lock (_gate) _configReachable = true;
                    if (fetched.Version != version)
                    {
                        Apply(fetched.Devices, cancellationToken);
                        Console.WriteLine($"Configuration v{fetched.Version}: {fetched.Devices.Length} device(s).");
                        version = fetched.Version;
                        lock (_gate) _version = version;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(settings.ConfigRefreshSeconds), cancellationToken);
            }
        }
        finally
        {
            foreach (var (_, stop, _) in _pollers.Values) stop.Cancel();
            await Task.WhenAll(_pollers.Values.Select(item => item.Task));
        }
    }

    private void Apply(DeviceDefinition[] devices, CancellationToken cancellationToken)
    {
        lock (_gate) ApplyLocked(devices, cancellationToken);
    }

    private void ApplyLocked(DeviceDefinition[] devices, CancellationToken cancellationToken)
    {
        var wanted = devices.Where(device => device.Enabled).ToDictionary(device => device.Id);
        foreach (var id in _pollers.Keys.ToArray())
        {
            if (wanted.TryGetValue(id, out var device) && _pollers[id].Poller.SameDefinition(device)) continue;
            _pollers[id].Stop.Cancel();
            _pollers[id].Task.Wait(TimeSpan.FromSeconds(5));
            _pollers.Remove(id);
            if (!wanted.ContainsKey(id))
            {
                outbox.AcceptDeviceOffline(id);
                Console.WriteLine($"Stopped polling {id} (removed from configuration).");
            }
        }
        foreach (var device in wanted.Values.Where(device => !_pollers.ContainsKey(device.Id)))
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var poller = new ModbusPoller(device, settings, outbox);
            _pollers[device.Id] = (poller, stop, poller.RunAsync(stop.Token));
        }
    }
}
