using System.Text.Json;

namespace IoT.Contracts;

// DeviceSeq is persisted by the device and never repeats, so it identifies a reading end to end.
public sealed record DeviceReading(string DeviceId, ulong DeviceSeq, DateTimeOffset Timestamp,
    double Temperature, double Humidity, int GpioPin, int GpioValue)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceId) || DeviceId.IndexOfAny(['/', '+', '#']) >= 0)
            throw new ArgumentException("Device ID must be nonempty and must not contain /, + or #.");
        if (DeviceSeq == 0 || DeviceSeq > long.MaxValue)
            throw new ArgumentException("Device sequence must be positive.");
        if (!double.IsFinite(Temperature) || !double.IsFinite(Humidity) || Humidity is < 0 or > 100)
            throw new ArgumentException("Invalid temperature or humidity.");
        if (GpioPin < 0 || GpioValue is not (0 or 1))
            throw new ArgumentException("Invalid GPIO reading.");
    }
}

public static class EdgeTopics
{
    public const string DataFilter = "edge/+/data";
    public const string StatusFilter = "edge/+/status";
    public const string Online = "online";
    public const string Offline = "offline";
    public static string Data(string deviceId) => $"edge/{deviceId}/data";
    public static string Status(string deviceId) => $"edge/{deviceId}/status";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
