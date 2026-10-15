namespace Device.Simulator;

public sealed class SimulatorSettings
{
    public string DeviceId { get; set; } = "fridge-01";
    public int GpioPin { get; set; } = 17;
    public int ReadingIntervalSeconds { get; set; } = 5;
    // Normal cabinet temperature, so several simulated fridges are easy to tell apart.
    public double BaseTemperature { get; set; } = 4.5;
    public string StateDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "state", "devices");
}

public sealed class EdgeBrokerSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1883;
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int KeepAliveSeconds { get; set; } = 30;
}
