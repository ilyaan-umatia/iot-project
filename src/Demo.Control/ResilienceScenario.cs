namespace Demo.Control;

public sealed record ScenarioStep(string Label, string Explanation, string Status);
public sealed record ScenarioView(bool Running, ScenarioStep[] Steps, string? Result, bool? Passed);

// Breaks each component in turn, then checks that every reading data acquisition polled reached Mosquitto
// exactly once, as seen by the console's own persistent Sparkplug subscription.
public sealed class ResilienceScenario(ComponentManager components, SparkplugView view)
{
    private readonly object _gate = new();
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
            ("Start everything", "Broker, device registry and gateway in dependency order.",
                components.StartAllAsync, 10),
            ("Crash Mosquitto", "The gateway keeps polling and stores every reading in its SQLite outbox.",
                () => components.ActAsync(ComponentManager.Broker, "crash"), 15),
            ("Start Mosquitto", "The gateway reconnects, rebirths and replays the backlog as historical.",
                () => components.ActAsync(ComponentManager.Broker, "start"), 15),
            ("Crash gateway", "The broker publishes its NDEATH. No polling while it is down; the outbox survives on disk.",
                () => components.ActAsync(ComponentManager.Gateway, "crash"), 10),
            ("Restart gateway", "Polling resumes with numbering continued, while the publisher drains the saved outbox in order.",
                () => components.ActAsync(ComponentManager.Gateway, "start"), 15),
            ("Crash configuration service", "The gateway keeps polling from its cached device list.",
                () => components.ActAsync(ComponentManager.Configuration, "crash"), 10),
            ("Restart configuration service", "The gateway reconnects to the registry.",
                () => components.ActAsync(ComponentManager.Configuration, "start"), 10)
        ];
        lock (_gate) _steps = plan.Select(step => new ScenarioStep(step.Label, step.Explanation, "pending"))
            .Append(new("Verify", "Compare each registered device's polled readings with what arrived on Mosquitto.", "pending")).ToArray();
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
            // Polling never stops, so take the polled numbers now and give them time to reach Mosquitto.
            var polledNow = components.PolledSnapshot();
            if (polledNow.Count == 0) throw new InvalidOperationException("No device has been polled. Add a device first (Device registry tab).");
            await Task.Delay(TimeSpan.FromSeconds(8));
            var summaries = view.Summaries();
            var passed = true;
            var lines = new List<string>();
            foreach (var (id, polled) in polledNow.OrderBy(item => item.Key))
            {
                var device = summaries.FirstOrDefault(item => item.DeviceId == id);
                var last = device?.LastSeq ?? 0;
                var missing = device?.Missing ?? polled;
                var duplicates = device?.Duplicates ?? 0;
                passed &= last >= polled && missing == 0 && duplicates == 0;
                lines.Add($"{id}: polled #{polled}, on Mosquitto #{last}, missing {missing}, duplicates {duplicates}, historical {device?.Historical ?? 0}");
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
