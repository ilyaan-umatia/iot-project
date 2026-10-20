using System.Net.Http.Json;
using System.Text.Json;
using IoT.Contracts;

namespace EdgeGateway.Gateway.Configuration;

// Fetches the device list from the configuration service and keeps a local copy,
// so data acquisition keeps polling even when the configuration service is down.
public sealed class ConfigClient(DaqSettings settings) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string _cachePath = Path.Combine(settings.StateDirectory, "config-cache.json");

    public async Task<GatewayConfiguration?> TryFetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var configuration = await _http.GetFromJsonAsync<GatewayConfiguration>(
                settings.ConfigUrl.TrimEnd('/') + "/config", EdgeTopics.Json, cancellationToken);
            if (configuration is null) return null;
            Directory.CreateDirectory(settings.StateDirectory);
            await File.WriteAllTextAsync(_cachePath + ".tmp", JsonSerializer.Serialize(configuration, EdgeTopics.Json), cancellationToken);
            File.Move(_cachePath + ".tmp", _cachePath, overwrite: true);
            return configuration;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException
                                          && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public GatewayConfiguration? LoadCache()
    {
        try { return JsonSerializer.Deserialize<GatewayConfiguration>(File.ReadAllText(_cachePath), EdgeTopics.Json); }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    public void Dispose() => _http.Dispose();
}
