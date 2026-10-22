using EdgeGateway.Gateway.Configuration;
using EdgeGateway.Gateway.Storage;
using IoT.Contracts;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.Gateway.Acquisition;

// Runs one poller per configured device. Follows the configuration service: added devices start polling,
// removed or disabled ones leave with a DDEATH. Keeps the last device list if the service is unreachable.
public sealed class AcquisitionService(DaqSettings settings, ConfigClient config, Outbox outbox)
{
    private readonly Dictionary<string, (ModbusPoller Poller, CancellationTokenSource Stop, Task Task)> _pollers = [];

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
                }
                else
                {
                    if (!reachable) Console.WriteLine("Configuration service reachable again.");
                    reachable = true;
                    if (fetched.Version != version)
                    {
                        try
                        {
                            await ApplyAsync(fetched.Devices, cancellationToken);
                            Console.WriteLine($"Configuration v{fetched.Version}: {fetched.Devices.Length} device(s).");
                            version = fetched.Version;
                        }
                        catch (SqliteException error)
                        {
                            Console.WriteLine($"Configuration v{fetched.Version} not applied: {error.Message}. Retrying on the next refresh.");
                        }
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

    private async Task ApplyAsync(DeviceDefinition[] devices, CancellationToken cancellationToken)
    {
        var wanted = devices.Where(device => device.Enabled).ToDictionary(device => device.Id);
        foreach (var id in _pollers.Keys.ToArray())
        {
            if (wanted.TryGetValue(id, out var device) && _pollers[id].Poller.SameDefinition(device)) continue;
            var (_, stop, task) = _pollers[id];
            stop.Cancel();
            // Wait for the final in-flight sample before recording offline, so it cannot resurrect the device.
            try { await task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
            stop.Dispose();
            _pollers.Remove(id);
            Console.WriteLine($"Stopped polling {id} (configuration changed).");
        }
        // Reconcile persisted intake state too: disabled/deleted devices have no poller after a restart.
        // Keep the published state and queued readings intact; DDEATH follows them in the existing outbox order.
        foreach (var id in outbox.GetIntakeOnlineDeviceIds().Where(id => !wanted.ContainsKey(id)))
        {
            if (outbox.AcceptDeviceOffline(id)) Console.WriteLine($"{id} offline (not enabled in configuration).");
        }
        foreach (var device in wanted.Values.Where(device => !_pollers.ContainsKey(device.Id)))
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var poller = new ModbusPoller(device, settings, outbox);
            _pollers[device.Id] = (poller, stop, poller.RunAsync(stop.Token));
        }
    }
}
