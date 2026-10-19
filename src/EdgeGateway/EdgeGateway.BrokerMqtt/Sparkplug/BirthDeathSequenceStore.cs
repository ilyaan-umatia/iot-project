using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EdgeGateway.BrokerMqtt.Sparkplug;

public sealed class BirthDeathSequenceStore
{
    private readonly string _path;

    public BirthDeathSequenceStore(string clientId, string stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory) || !Path.IsPathFullyQualified(stateDirectory))
            throw new ArgumentException("Gateway state directory must be an absolute path.", nameof(stateDirectory));
        var filename = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId))) + ".txt";
        _path = Path.Combine(stateDirectory, filename);
        Directory.CreateDirectory(stateDirectory);
        // Preserve the counter from earlier versions when switching to the stable directory.
        var previousPath = Path.Combine(AppContext.BaseDirectory, "state", filename);
        if (!File.Exists(_path) && File.Exists(previousPath))
            File.Copy(previousPath, _path);
    }

    public ulong ReserveNext()
    {
        var next = File.Exists(_path)
            ? (byte)((byte.Parse(File.ReadAllText(_path), CultureInfo.InvariantCulture) + 1) % 256)
            : (byte)0;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", next.ToString(CultureInfo.InvariantCulture));
        File.Move(_path + ".tmp", _path, overwrite: true);
        return next;
    }
}
