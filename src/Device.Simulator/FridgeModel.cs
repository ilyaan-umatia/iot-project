namespace Device.Simulator;

// Simple physical model: temperature and humidity drift toward a target set by the door and a fault spike.
public sealed class FridgeModel(double baseTemperature)
{
    private readonly object _gate = new();
    private readonly Random _random = new();
    private readonly double _baseTemperature = baseTemperature;
    private double _temperature = baseTemperature;
    private double _humidity = 65;

    public bool DoorOpen { get; private set; }
    public bool Spike { get; private set; }
    public bool Paused { get; private set; }

    // Returns a description of the change, or null for an unknown command.
    public string? Apply(string command)
    {
        lock (_gate)
        {
            switch (command)
            {
                case "door open": DoorOpen = true; return "Door opened.";
                case "door close": DoorOpen = false; return "Door closed.";
                case "spike on": Spike = true; return "Cooling fault: temperature rising.";
                case "spike off": Spike = false; return "Cooling restored.";
                case "pause": Paused = true; return "Readings paused.";
                case "resume": Paused = false; return "Readings resumed.";
                default: return null;
            }
        }
    }

    public (double Temperature, double Humidity, int GpioValue) Next()
    {
        lock (_gate)
        {
            var targetTemperature = Spike ? 12.5 : DoorOpen ? 10 : _baseTemperature;
            var targetHumidity = DoorOpen ? 78 : 65;
            _temperature += (targetTemperature - _temperature) * 0.35 + (_random.NextDouble() - 0.5) * 0.2;
            _humidity += (targetHumidity - _humidity) * 0.4 + (_random.NextDouble() - 0.5);
            return (Math.Round(_temperature, 1), Math.Round(Math.Clamp(_humidity, 0, 100)), DoorOpen ? 1 : 0);
        }
    }
}
