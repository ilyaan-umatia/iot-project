namespace EdgeGateway.Gateway.Storage;

// Diagnostics must recover on the next tick if a Windows reader temporarily blocks replacement.
public static class GatewayStatusFile
{
    public static bool TryWrite(string path, string json)
    {
        try
        {
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
