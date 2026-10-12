namespace Cloud.Forwarder.Configuration;

public sealed class ForwarderSettings
{
    public BrokerSettings Mqtt { get; set; } = new();
    public NodeSettings Sparkplug { get; set; } = new();
    public IoTHubSettings IoTHub { get; set; } = new();
}

public sealed class BrokerSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8883;
    public string ClientId { get; set; } = "gateway-01";
    public bool UseTls { get; set; } = true;
    public string BrokerCertificatePath { get; set; } = "certs/broker.crt";
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int KeepAliveSeconds { get; set; } = 60;
}

public sealed class NodeSettings
{
    public string GroupId { get; set; } = "warehouse-01";
    public string EdgeNodeId { get; set; } = "gateway-01";
}

public sealed class IoTHubSettings
{
    public string DeviceConnectionString { get; set; } = "";
}
