using System.Globalization;

namespace EdgeGateway.Gateway.Acquisition;

// Persists each device's last reading number, so a restart never reuses one.
public sealed class DeviceSequenceStore
{
    private readonly string _path;
    private ulong _last;

    public DeviceSequenceStore(string deviceId, string stateDirectory)
    {
        var directory = Path.Combine(stateDirectory, "sequences");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, deviceId + ".seq");
        _last = File.Exists(_path) ? ulong.Parse(File.ReadAllText(_path), CultureInfo.InvariantCulture) : 0;
    }

    public ulong Next()
    {
        var next = _last + 1;
        File.WriteAllText(_path + ".tmp", next.ToString(CultureInfo.InvariantCulture));
        File.Move(_path + ".tmp", _path, overwrite: true);
        _last = next;
        return next;
    }
}
