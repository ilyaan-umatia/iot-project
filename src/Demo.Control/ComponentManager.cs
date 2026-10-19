using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Demo.Control;

public sealed record ComponentView(string Name, string Kind, bool Running, string Status, string[] Actions);
public sealed record FridgeState(bool DoorOpen, bool Spike, bool Paused);
public sealed record FridgeView(string Id, bool Running, int GpioPin, FridgeState Controls, long LastSent);
public sealed record FridgeDefinition(string Id, int GpioPin, double BaseTemperature);

// Starts, stops and crashes every demo component: the backend, the gateway, one simulator per fridge
// (each with its own MQTT connection) and two broker containers.
public sealed partial class ComponentManager : IDisposable
{
    public const string Backend = "backend", Gateway = "gateway", Publisher = "publisher", Broker = "mosquitto", EdgeBroker = "edge-broker";
    private static readonly Dictionary<string, string> Containers = new()
    {
        [Broker] = "dotnet-mqtt-demo-mosquitto-1",
        [EdgeBroker] = "dotnet-mqtt-demo-edge-mosquitto-1"
    };
    public static readonly FridgeDefinition[] Fridges =
    [
        new("fridge-01", 17, 4.5), new("fridge-02", 18, 3.5), new("fridge-03", 27, 5.5)
    ];

    private readonly string _workspace;
    private readonly string _state;
    private readonly Dictionary<string, ManagedProcess> _processes;
    private readonly Dictionary<string, string> _containerStatus = new() { [Broker] = "unknown", [EdgeBroker] = "unknown" };
    private readonly ConcurrentDictionary<string, long> _lastSent = new();
    private readonly ConcurrentDictionary<string, FridgeState> _controls = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private TaskCompletionSource _intakeSubscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string BackendUrl { get; }

    public ComponentManager(IConfiguration configuration)
    {
        _workspace = FindWorkspace();
        _state = Path.Combine(_workspace, "logs", "demo", "state");
        var logs = Path.Combine(_workspace, "logs", "demo");
        BackendUrl = (configuration["Backend:Url"] ?? "http://localhost:5114").TrimEnd('/');
        _processes = new()
        {
            [Backend] = new("Application.Api", _workspace, logs),
            [Gateway] = new("EdgeGateway.DataAcquisition", _workspace, logs),
            [Publisher] = new("EdgeGateway.BrokerMqtt", _workspace, logs)
        };
        _processes[Gateway].Line += line =>
        {
            if (line.StartsWith("Intake subscribed", StringComparison.Ordinal)) _intakeSubscribed.TrySetResult();
        };
        foreach (var fridge in Fridges)
        {
            var process = new ManagedProcess("Device.Simulator", _workspace, logs, fridge.Id);
            process.Line += line =>
            {
                if (SentPattern().Match(line) is { Success: true } match)
                    _lastSent[fridge.Id] = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            };
            _processes[fridge.Id] = process;
            _controls[fridge.Id] = new(false, false, false);
        }
    }

    public static bool IsFridge(string name) => Fridges.Any(fridge => fridge.Id == name);
    public long LastSent(string fridge) => _lastSent.GetValueOrDefault(fridge);
    public string[] Tail(string name, int count) => _processes.TryGetValue(name, out var process) ? process.Tail(count) : [];

    public FridgeView[] DescribeFridges() => Fridges.Select(fridge => new FridgeView(fridge.Id,
        _processes[fridge.Id].Running, fridge.GpioPin, _controls[fridge.Id], LastSent(fridge.Id))).ToArray();

    public ComponentView[] Describe()
    {
        var gateway = ReadGatewayStatus();
        return
        [
            .. Fridges.Select(fridge => View(fridge.Id, _controls[fridge.Id].Paused ? "running, readings paused" : null)),
            ContainerView(EdgeBroker),
            View(Gateway, null),
            View(Publisher, gateway is null ? null : $"upstream {gateway.Value.GetProperty("upstream").GetString()}"),
            ContainerView(Broker),
            View(Backend, null)
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
        string status;
        lock (_containerStatus) status = _containerStatus[name];
        var running = status == "running";
        return new(name, "container", running, status, running ? ["restart", "stop", "crash"] : ["start"]);
    }

    // The gateway writes this snapshot every second; an old file means the gateway is not running.
    public JsonElement? ReadGatewayStatus()
    {
        var path = Path.Combine(_state, "status.json");
        try
        {
            if (!_processes[Publisher].Running || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(4))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    public async Task<bool> BackendReachableAsync()
    {
        try { return (await _http.GetAsync(BackendUrl + "/health")).IsSuccessStatusCode; }
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
            return code == 0 ? $"{name}: {action} done." : throw new InvalidOperationException(output.Trim());
        }
        if (!_processes.TryGetValue(name, out var process)) throw new ArgumentException($"Unknown component {name}.");
        switch (action)
        {
            case "start":
                if (name == Gateway) _intakeSubscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (IsFridge(name)) _controls[name] = new(false, false, false);
                process.Start(Arguments(name));
                break;
            case "stop":
                // The backend has no stop command; ending it closes its connection and the broker publishes its Will.
                await process.StopAsync(acceptsStopCommand: name != Backend);
                break;
            case "crash":
                process.Crash();
                break;
            default:
                throw new ArgumentException($"Unknown action {action}.");
        }
        return $"{name}: {action} done.";
    }

    public string SendFridgeCommand(string fridge, string command)
    {
        if (!IsFridge(fridge)) throw new ArgumentException($"Unknown fridge {fridge}.");
        var text = command switch
        {
            "door-open" => "door open", "door-close" => "door close", "spike-on" => "spike on",
            "spike-off" => "spike off", "pause" => "pause", "resume" => "resume",
            _ => throw new ArgumentException($"Unknown fridge command {command}.")
        };
        if (!_processes[fridge].Running) throw new InvalidOperationException($"Start {fridge} first.");
        _processes[fridge].Send(text);
        _controls.AddOrUpdate(fridge, _ => new(false, false, false), (_, current) => command switch
        {
            "door-open" => current with { DoorOpen = true },
            "door-close" => current with { DoorOpen = false },
            "spike-on" => current with { Spike = true },
            "spike-off" => current with { Spike = false },
            "pause" => current with { Paused = true },
            _ => current with { Paused = false }
        });
        return $"{fridge}: {text}";
    }

    public void SendToRunningFridges(string command)
    {
        foreach (var fridge in Fridges)
            if (_processes[fridge.Id].Running) SendFridgeCommand(fridge.Id, command);
    }

    // Start in dependency order: the backend's STATE first, and the gateway's edge session before any device publishes.
    public async Task StartAllAsync()
    {
        var (code, output) = await RunAsync("docker", ["compose", "up", "-d", "--wait"]);
        if (code != 0) throw new InvalidOperationException(output.Trim());
        await RefreshContainersAsync();
        if (!_processes[Backend].Running) await ActAsync(Backend, "start");
        for (var attempt = 0; attempt < 60 && !await BackendReachableAsync(); attempt++) await Task.Delay(500);
        if (!_processes[Publisher].Running) await ActAsync(Publisher, "start");
        if (!_processes[Gateway].Running)
        {
            await ActAsync(Gateway, "start");
            await _intakeSubscribed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        foreach (var fridge in Fridges)
            if (!_processes[fridge.Id].Running) await ActAsync(fridge.Id, "start");
    }

    public async Task StopAllAsync()
    {
        await Task.WhenAll(Fridges.Select(fridge => ActAsync(fridge.Id, "stop")));
        await ActAsync(Gateway, "stop");
        await ActAsync(Publisher, "stop");
        await ActAsync(Backend, "stop");
    }

    // Fresh brokers and empty state, so the demo starts at reading #1 with nothing queued.
    public async Task ResetAsync()
    {
        foreach (var process in _processes.Values) process.Crash();
        var (code, output) = await RunAsync("docker", ["compose", "down", "-v"]);
        if (code != 0) throw new InvalidOperationException(output.Trim());
        if (Directory.Exists(_state)) Directory.Delete(_state, recursive: true);
        _lastSent.Clear();
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

    private IEnumerable<string> Arguments(string name)
    {
        if (name == Backend) return [$"--Storage:DatabasePath={Path.Combine(_state, "backend.db")}"];
        if (name == Gateway) return [];
        if (name == Publisher) return [$"--Gateway:DatabasePath={Path.Combine(_state, "outbox.db")}"];
        var fridge = Fridges.Single(item => item.Id == name);
        return
        [
            $"--Simulator:DeviceId={fridge.Id}", $"--Simulator:GpioPin={fridge.GpioPin}",
            $"--Simulator:BaseTemperature={fridge.BaseTemperature.ToString(CultureInfo.InvariantCulture)}",
            "--Simulator:ReadingIntervalSeconds=3", $"--Simulator:StateDirectory={Path.Combine(_state, "devices")}"
        ];
    }

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

    [GeneratedRegex(@"^Sent #(\d+):")]
    private static partial Regex SentPattern();

    public void Dispose()
    {
        foreach (var process in _processes.Values) process.Crash();
        _http.Dispose();
    }
}
