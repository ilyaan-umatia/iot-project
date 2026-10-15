using System.Globalization;

namespace Device.Simulator;

// Persists the last reading number so a restarted device never reuses one.
public sealed class DeviceSequenceStore
{
    private readonly string _path;
    private ulong _last;

    public DeviceSequenceStore(string deviceId, string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, deviceId + ".seq");
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
