using System.Diagnostics;
using Device.Gateway.Configuration;
using Device.Gateway.Simulation;
using Device.Gateway.Sparkplug;
using Google.Protobuf;

namespace Device.Gateway.Connection;

public sealed class GatewayRunner(MqttConnection connection, GatewaySettings settings)
{
    private volatile bool _deviceOnline = true;
    public void SetDeviceOnline(bool online) => _deviceOnline = online;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private ulong UptimeSeconds => (ulong)Stopwatch.GetElapsedTime(_startedAt).TotalSeconds;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var mqtt = settings.Mqtt;
        if (mqtt.RetryDelaySeconds <= 0 || mqtt.MaxRetryDelaySeconds < mqtt.RetryDelaySeconds)
            throw new ArgumentException("Retry delays must be positive and the maximum must be at least the initial delay.");
        if (settings.Device.ReadingIntervalSeconds <= 0)
            throw new ArgumentException("Reading interval must be positive.");
        if (settings.Device.GpioPin < 0)
            throw new ArgumentException("GPIO pin must not be negative.");
        _ = SparkplugTopics.DeviceBirth(settings.Sparkplug, settings.Device.DeviceId);
        var topic = SparkplugTopics.NodeBirth(settings.Sparkplug);
        var delaySeconds = mqtt.RetryDelaySeconds;
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Console.WriteLine($"Connecting {mqtt.ClientId} to {mqtt.Host}:{mqtt.Port}...");
                await connection.ConnectAsync(cancellationToken);
                Console.WriteLine("Connected.");
                await connection.PublishAsync(topic,
                    SparkplugPayloads.NodeBirth(connection.BirthDeathSequence, UptimeSeconds).ToByteArray(), cancellationToken);
                Console.WriteLine($"Published NBIRTH: {topic}");
                if (!connection.MarkOnline())
                    throw new InvalidOperationException("Connection closed during the birth announcement.");
                attempt = 0;
                delaySeconds = mqtt.RetryDelaySeconds;
                await RunSessionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                Console.WriteLine($"Connection failed: {error.Message}");
            }
            cancellationToken.ThrowIfCancellationRequested();
            await connection.PrepareRetryAsync(cancellationToken);
            attempt++;
            Console.WriteLine($"Retry {attempt} in {delaySeconds}s.");
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            delaySeconds = (int)Math.Min((long)delaySeconds * 2, mqtt.MaxRetryDelaySeconds);
        }
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var disconnected = connection.WaitForDisconnectAsync(session.Token);
        var publishing = PublishReadingsAsync(session.Token, cancellationToken);
        try
        {
            if (await Task.WhenAny(disconnected, publishing) == publishing)
                await publishing;
            await disconnected;
        }
        finally
        {
            session.Cancel();
            try { await publishing; }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
            try { await disconnected; }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
        }
    }

    private async Task PublishReadingsAsync(CancellationToken cancellationToken, CancellationToken gatewayStopToken)
    {
        var sequence = new SparkplugSequence();
        var fridge = new FridgeSimulator(connection, settings, sequence);
        await fridge.InitializeAsync(_deviceOnline, cancellationToken);
        var nodeTopic = SparkplugTopics.NodeData(settings.Sparkplug);

        // One loop preserves the publish order across node and device messages.
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.Device.ReadingIntervalSeconds), cancellationToken);
                var current = sequence.Next();
                var uptime = UptimeSeconds;
                await connection.PublishAsync(nodeTopic,
                    SparkplugPayloads.NodeData(current, uptime).ToByteArray(), cancellationToken);
                Console.WriteLine($"NDATA {settings.Sparkplug.EdgeNodeId}: uptime={uptime}s (seq {current})");
                await fridge.UpdateAsync(_deviceOnline, cancellationToken);
            }
        }
        finally
        {
            if (gatewayStopToken.IsCancellationRequested && connection.IsConnected)
            {
                using var deviceStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await fridge.UpdateAsync(false, deviceStop.Token); }
                catch (Exception error)
                {
                    Console.WriteLine($"Device shutdown failed ({error.GetType().Name}).");
                }
            }
        }
    }
}
