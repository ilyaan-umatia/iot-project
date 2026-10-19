namespace IoT.Contracts;

// A null Reading means the device went offline. This is internal data, not Sparkplug.
public sealed record AcquisitionMessage(string DeviceId, DeviceReading? Reading)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceId) || DeviceId.IndexOfAny(['/', '+', '#']) >= 0)
            throw new ArgumentException("Invalid device ID.");
        Reading?.Validate();
        if (Reading is not null && Reading.DeviceId != DeviceId)
            throw new ArgumentException("Device and reading IDs differ.");
    }
}

public sealed class NetMqSettings
{
    public string Endpoint { get; set; } = "tcp://127.0.0.1:5555";
}
