namespace EdgeGateway.DataAcquisition.Configuration;

public sealed class EdgeBrokerSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1883;
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int KeepAliveSeconds { get; set; } = 30;
}
