using System.Buffers;
using System.Text;
using System.Text.Json;
using EdgeGateway.DataAcquisition.Configuration;

using IoT.Contracts;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace EdgeGateway.DataAcquisition.Intake;

// Receives device messages from the edge broker and acknowledges each one only after the outbox commits it.
public sealed class DeviceSubscriber(EdgeBrokerSettings settings, string clientId, BrokerSender sender) : IDisposable
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    public bool IsConnected => _client.IsConnected;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var factory = new MqttClientFactory();
        // A persistent session makes the edge broker keep readings while the gateway is down.
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithClientId(clientId)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession(false)
            .WithTimeout(TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds))
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(settings.KeepAliveSeconds))
            .Build();
        _client.ApplicationMessageReceivedAsync += args => HandleAsync(args, cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!_client.IsConnected)
                    {
                        await _client.ConnectAsync(options, cancellationToken);
                        await _client.SubscribeAsync(factory.CreateSubscribeOptionsBuilder()
                            .WithTopicFilter(EdgeTopics.DataFilter, MqttQualityOfServiceLevel.AtLeastOnce)
                            .WithTopicFilter(EdgeTopics.StatusFilter, MqttQualityOfServiceLevel.AtLeastOnce)
                            .Build(), cancellationToken);
                        Console.WriteLine($"Intake subscribed on edge broker {settings.Host}:{settings.Port}.");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    Console.WriteLine($"Edge broker connection failed ({error.GetType().Name}). Retry in 2s.");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }
        finally
        {
            if (_client.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), timeout.Token); }
                catch (Exception error) { Console.WriteLine($"Intake disconnect failed ({error.GetType().Name})."); }
            }
        }
    }

    private async Task HandleAsync(MqttApplicationMessageReceivedEventArgs args, CancellationToken cancellationToken)
    {
        args.AutoAcknowledge = false;
        var topic = args.ApplicationMessage.Topic;
        var payload = args.ApplicationMessage.Payload.ToArray();
        try
        {
            if (topic.Split('/') is not ["edge", var deviceId, var kind])
                throw new ArgumentException("Unexpected topic.");
            if (kind == "data")
            {
                var reading = JsonSerializer.Deserialize<DeviceReading>(payload, EdgeTopics.Json)
                    ?? throw new ArgumentException("Empty reading.");
                if (reading.DeviceId != deviceId) throw new ArgumentException("Topic and payload device differ.");
                reading.Validate();
                await sender.SendAsync(new(deviceId, reading), cancellationToken);
                Console.WriteLine($"Forwarded {deviceId} #{reading.DeviceSeq} to EdgeGateway.BrokerMqtt.");
            }
            else if (kind == "status" && Encoding.UTF8.GetString(payload) == EdgeTopics.Offline)
            {
                await sender.SendAsync(new(deviceId, null), cancellationToken);
                Console.WriteLine($"Device offline: {deviceId}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception error) when (error is ArgumentException or JsonException)
        {
            // Invalid messages are acknowledged so they do not block the session.
            Console.WriteLine($"Rejected edge message on {topic}: {error.Message}");
        }
        catch (Exception error)
        {
            // Not committed: leave it unacknowledged and reconnect so the broker redelivers it.
            Console.WriteLine($"Could not buffer edge message ({error.GetType().Name}). Reconnecting.");
            _ = _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build());
            return;
        }
        await args.AcknowledgeAsync(CancellationToken.None);
    }

    public void Dispose() => _client.Dispose();
}
