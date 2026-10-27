using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Demo.Control;

public sealed record ComponentView(string Name, string Kind, bool Running, string Status, string[] Actions);

// Starts, stops and crashes every demo component: the configuration service and the gateway
// as processes, and the Mosquitto broker as a container. Devices are external (registered in the configuration service).
public sealed partial class ComponentManager : IDisposable
{
    public const string Configuration = "configuration", Gateway = "gateway",
        Broker = "mosquitto";
    private static readonly Dictionary<string, string> Containers = new() { [Broker] = "dotnet-mqtt-demo-mosquitto-1" };

    private readonly string _workspace;
    private readonly string _state;
    private readonly Dictionary<string, ManagedProcess> _processes;
    private readonly Dictionary<string, string> _containerStatus = Containers.Keys.ToDictionary(name => name, _ => "unknown");
    private readonly ConcurrentDictionary<string, long> _lastPolled = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public string ConfigurationUrl { get; }

    public ActivityLog Activity { get; }

    public ComponentManager(IConfiguration configuration, ActivityLog activity)
    {
        _workspace = FindWorkspace();
        _state = Path.Combine(_workspace, "logs", "demo", "state");
        var logs = Path.Combine(_workspace, "logs", "demo");
        ConfigurationUrl = (configuration["Daq:ConfigUrl"] ?? "http://127.0.0.1:5300").TrimEnd('/');
        _processes = new()
        {
            [Configuration] = new("EdgeGateway.Configuration", _workspace, logs),
            [Gateway] = new("EdgeGateway.Gateway", _workspace, logs)
        };
        Activity = activity;
        _processes[Gateway].Line += activity.FromGateway;
        _processes[Gateway].Line += line =>
        {
            if (PolledPattern().Match(line) is { Success: true } match)
                _lastPolled[match.Groups[1].Value] = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        };
    }

    public long LastPolled(string device) => _lastPolled.GetValueOrDefault(device);
    public Dictionary<string, long> PolledSnapshot() => new(_lastPolled);
    public string[] Tail(string name, int count) => _processes.TryGetValue(name, out var process) ? process.Tail(count) : [];

    public ComponentView[] Describe()
    {
        var gateway = ReadGatewayStatus();
        return
        [
            View(Configuration, null),
            View(Gateway, gateway is null ? null : $"upstream {gateway.Value.GetProperty("upstream").GetString()}"),
            ContainerView(Broker)
        ];
    }

    private ComponentView View(string name, string? detail)
    {
        var running = _processes[name].Running;
        return new(name, "process", running, running ? detail ?? "running" : "stopped",
            running ? ["stop", "crash"] : ["start"]);
    }

    private ComponentView ContainerView(string name)
    {
        var running = IsContainerRunning(name);
        string status;
        lock (_containerStatus) status = _containerStatus[name];
        return new(name, "container", running, status, running ? ["restart", "stop", "crash"] : ["start"]);
    }

    private bool IsContainerRunning(string name)
    {
        lock (_containerStatus) return _containerStatus.GetValueOrDefault(name) == "running";
    }

    // The gateway writes this snapshot every second; an old file means it is not running.
    public JsonElement? ReadGatewayStatus()
    {
        var path = Path.Combine(_state, "status.json");
        try
        {
            if (!_processes[Gateway].Running || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(4))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    public Task<bool> ConfigurationReachableAsync() => ReachableAsync(ConfigurationUrl + "/health");

    private async Task<bool> ReachableAsync(string url)
    {
        try { return (await _http.GetAsync(url)).IsSuccessStatusCode; }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { return false; }
    }

    public async Task<string> ActAsync(string name, string action)
    {
        if (Containers.TryGetValue(name, out var container))
        {
            var command = action switch
            {
                "start" => "start", "stop" => "stop", "restart" => "restart", "crash" => "kill",
                _ => throw new ArgumentException($"Unknown action {action}.")
            };
            var (code, output) = await RunAsync("docker", [command, container]);
            await RefreshContainersAsync();
            if (code != 0) throw new InvalidOperationException(output.Trim());
            Activity.Add("console", "action", $"{name}: {action} (container).");
            return $"{name}: {action} done.";
        }
        if (!_processes.TryGetValue(name, out var process)) throw new ArgumentException($"Unknown component {name}.");
        switch (action)
        {
            case "start":
                process.Start(Arguments(name));
                break;
            case "stop":
                // The configuration service has no stop command; ending it closes its connections.
                await process.StopAsync(acceptsStopCommand: name is Gateway);
                break;
            case "crash":
                process.Crash();
                break;
            default:
                throw new ArgumentException($"Unknown action {action}.");
        }
        Activity.Add("console", "action", $"{name}: {action}.");
        return $"{name}: {action} done.";
    }

    // Dependency order: the broker, the device registry, then the gateway,
    // which reads its device list from the registry.
    public async Task StartAllAsync()
    {
        var (code, output) = await RunAsync("docker", ["compose", "up", "-d", "--build", "--wait", "--remove-orphans"]);
        if (code != 0) throw new InvalidOperationException(output.Trim());
        await RefreshContainersAsync();
        if (!_processes[Configuration].Running) await ActAsync(Configuration, "start");
        await WaitUntilAsync(ConfigurationReachableAsync);
        if (!_processes[Gateway].Running) await ActAsync(Gateway, "start");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> ready)
    {
        for (var attempt = 0; attempt < 60 && !await ready(); attempt++) await Task.Delay(500);
    }

    public async Task StopAllAsync()
    {
        await ActAsync(Gateway, "stop");
        await ActAsync(Configuration, "stop");
    }

    // Fresh broker and state: empty registry, empty outbox, nothing queued.
    public async Task ResetAsync()
    {
        foreach (var process in _processes.Values) process.Crash();
        var (code, output) = await RunAsync("docker", ["compose", "down", "-v", "--remove-orphans"]);
        if (code != 0) throw new InvalidOperationException(output.Trim());
        if (Directory.Exists(_state)) Directory.Delete(_state, recursive: true);
        _lastPolled.Clear();
        await StartAllAsync();
    }

    public async Task RefreshContainersAsync()
    {
        foreach (var (name, container) in Containers)
        {
            var (code, output) = await RunAsync("docker", ["inspect", "-f", "{{.State.Status}}", container]);
            lock (_containerStatus) _containerStatus[name] = code == 0 ? output.Trim() : "not created";
        }
    }

    private IEnumerable<string> Arguments(string name) => name switch
    {
        Configuration => [$"--Configuration:DatabasePath={Path.Combine(_state, "configuration.db")}"],
        // No backend in this flow: an empty HostId makes the gateway publish without waiting for a primary host.
        _ => [$"--Gateway:DatabasePath={Path.Combine(_state, "outbox.db")}", "--Sparkplug:HostId=",
            $"--Daq:StateDirectory={Path.Combine(_state, "daq")}", $"--Daq:ConfigUrl={ConfigurationUrl}"]
    };

    private async Task<(int Code, string Output)> RunAsync(string file, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = _workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "IoT.sln"))) return directory.FullName;
        throw new InvalidOperationException("Run the demo console from inside the project folder.");
    }

    [GeneratedRegex(@"^Polled (\S+) #(\d+):")]
    private static partial Regex PolledPattern();

    public void Dispose()
    {
        foreach (var process in _processes.Values) process.Crash();
        _http.Dispose();
    }
}
