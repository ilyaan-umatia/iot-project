namespace EdgeGateway.Gateway.Sparkplug;

public sealed class SparkplugSequence
{
    // NBIRTH uses 0; the session's single publishing loop shares the remaining numbers.
    private byte _next = 1;

    public ulong Next()
    {
        var current = _next;
        _next = (byte)((current + 1) % 256);
        return current;
    }
}
