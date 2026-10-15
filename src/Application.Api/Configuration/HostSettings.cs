namespace Application.Api.Configuration;

public sealed class BrokerSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8883;
    public bool UseTls { get; set; } = true;
    public string BrokerCertificatePath { get; set; } = "certs/broker.crt";
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int KeepAliveSeconds { get; set; } = 60;
}

public sealed class PrimaryHostSettings
{
    public string GroupId { get; set; } = "warehouse-01";
    // Also the MQTT client ID, so the broker keeps this host's persistent session.
    public string HostId { get; set; } = "iot-host";
}
