using EdgeGateway.Gateway.Configuration;

namespace EdgeGateway.Gateway.Sparkplug;

public static class SparkplugTopics
{
    public static string NodeBirth(SparkplugSettings settings) => NodeTopic(settings, "NBIRTH");
    public static string NodeData(SparkplugSettings settings) => NodeTopic(settings, "NDATA");
    public static string NodeDeath(SparkplugSettings settings) => NodeTopic(settings, "NDEATH");
    public static string NodeCommand(SparkplugSettings settings) => NodeTopic(settings, "NCMD");

    public static string PrimaryHostState(string hostId)
    {
        ValidateIdentifier(hostId, nameof(hostId));
        return $"spBv1.0/STATE/{hostId}";
    }

    private static string NodeTopic(SparkplugSettings settings, string messageType)
    {
        ValidateIdentifier(settings.GroupId, nameof(settings.GroupId));
        ValidateIdentifier(settings.EdgeNodeId, nameof(settings.EdgeNodeId));
        return $"spBv1.0/{settings.GroupId}/{messageType}/{settings.EdgeNodeId}";
    }

    public static string DeviceBirth(SparkplugSettings settings, string deviceId) =>
        DeviceTopic(settings, deviceId, "DBIRTH");

    public static string DeviceData(SparkplugSettings settings, string deviceId) =>
        DeviceTopic(settings, deviceId, "DDATA");

    public static string DeviceDeath(SparkplugSettings settings, string deviceId) =>
        DeviceTopic(settings, deviceId, "DDEATH");

    private static string DeviceTopic(SparkplugSettings settings, string deviceId, string messageType)
    {
        ValidateIdentifier(deviceId, nameof(deviceId));
        return $"{NodeTopic(settings, messageType)}/{deviceId}";
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['/', '+', '#']) >= 0)
        {
            throw new ArgumentException($"{name} must be nonempty and must not contain /, + or #.");
        }
    }
}
