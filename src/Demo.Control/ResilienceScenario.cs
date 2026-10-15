using System.Net.Http.Json;
using System.Text.Json;

namespace Demo.Control;

public sealed record ScenarioStep(string Label, string Explanation, string Status);
public sealed record ScenarioView(bool Running, ScenarioStep[] Steps, string? Result, bool? Passed);

// Breaks each component in turn, then checks the backend has every reading the fridge sent, exactly once.
public sealed class ResilienceScenario(ComponentManager components)
{
    private readonly object _gate = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly string Victim = ComponentManager.Fridges[1].Id;
    private ScenarioStep[] _steps = [];
    private string? _result;
    private bool? _passed;
    private bool _running;

    public ScenarioView Snapshot()
    {
        lock (_gate) return new(_running, _steps, _result, _passed);
    }

    public bool TryStart()
    {
        lock (_gate)
        {
            if (_running) return false;
            _running = true;
            _result = null;
            _passed = null;
        }
        _ = Task.Run(RunAsync);
        return true;
    }

    private async Task RunAsync()
    {
        (string Label, string Explanation, Func<Task> Action, int Seconds)[] plan =
        [
            ("Start everything", "Brokers, backend, gateway and fridge in dependency order.", components.StartAllAsync, 8),
            ("Crash backend", "Its Will sets STATE offline; the gateway ends its session and buffers.",
                () => components.ActAsync(ComponentManager.Backend, "crash"), 12),
            ("Restart backend", "STATE online; the gateway rebirths and replays the backlog as historical.",
                () => components.ActAsync(ComponentManager.Backend, "start"), 15),
            ("Crash gateway", "The broker publishes its NDEATH; the edge broker holds new readings.",
                () => components.ActAsync(ComponentManager.Gateway, "crash"), 10),
            ("Restart gateway", "The outbox and the edge session drain in order.",
                () => components.ActAsync(ComponentManager.Gateway, "start"), 15),
            ("Restart Mosquitto", "Sessions and queued messages survive on disk.",
                () => components.ActAsync(ComponentManager.Broker, "restart"), 15),
            ("Restart edge broker", "Every fridge queues its readings and resends them.",
                () => components.ActAsync(ComponentManager.EdgeBroker, "restart"), 15),
            ($"Crash {Victim}", "Its MQTT Will becomes a Sparkplug DDEATH; the other fridges carry on.",
                () => components.ActAsync(Victim, "crash"), 8),
            ($"Restart {Victim}", "DBIRTH, and numbering continues where it stopped.",
                () => components.ActAsync(Victim, "start"), 12),
            ("Pause readings and drain", "Let every queued message reach the backend.",
                () => { components.SendToRunningFridges("pause"); return Task.CompletedTask; }, 12)
        ];
        lock (_gate) _steps = plan.Select(step => new ScenarioStep(step.Label, step.Explanation, "pending"))
            .Append(new("Verify", "Compare each fridge's sent readings with what the backend stored.", "pending")).ToArray();
        try
        {
            for (var index = 0; index < plan.Length; index++)
            {
                SetStatus(index, "running");
                await plan[index].Action();
                await Task.Delay(TimeSpan.FromSeconds(plan[index].Seconds));
                SetStatus(index, "done");
            }
            SetStatus(plan.Length, "running");
            var summary = await _http.GetFromJsonAsync<JsonElement[]>(components.BackendUrl + "/events/summary")
                ?? throw new InvalidOperationException("No summary from the backend.");
            var passed = true;
            var lines = new List<string>();
            foreach (var fridge in ComponentManager.Fridges)
            {
                var sent = components.LastSent(fridge.Id);
                var device = summary.FirstOrDefault(item => item.GetProperty("deviceId").GetString() == fridge.Id);
                var found = device.ValueKind == JsonValueKind.Object;
                var last = found ? device.GetProperty("lastSeq").GetInt64() : 0;
                var missing = found ? device.GetProperty("missing").GetInt64() : sent;
                var duplicates = found ? device.GetProperty("duplicates").GetInt64() : 0;
                var historical = found ? device.GetProperty("historical").GetInt64() : 0;
                passed &= last >= sent && missing == 0 && duplicates == 0;
                lines.Add($"{fridge.Id}: sent #{sent}, stored #{last}, missing {missing}, duplicates {duplicates}, historical {historical}");
            }
            SetStatus(plan.Length, passed ? "done" : "failed");
            Finish(passed, string.Join(" · ", lines));
        }
        catch (Exception error)
        {
            lock (_gate)
                _steps = _steps.Select(step => step.Status == "running" ? step with { Status = "failed" } : step).ToArray();
            Finish(false, "Stopped: " + error.Message);
        }
        finally
        {
            try { components.SendToRunningFridges("resume"); } catch (InvalidOperationException) { }
        }
    }

    private void SetStatus(int index, string status)
    {
        lock (_gate) _steps[index] = _steps[index] with { Status = status };
    }

    private void Finish(bool passed, string result)
    {
        lock (_gate)
        {
            _passed = passed;
            _result = result;
            _running = false;
        }
    }
}
