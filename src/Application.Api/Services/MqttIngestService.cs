using System.Buffers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Application.Api.Configuration;
using Google.Protobuf;
using IoT.Contracts;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using Org.Eclipse.Tahu.Protobuf;

namespace Application.Api.Services;

// Sparkplug Primary Host Application: announces STATE, stores every message, then acknowledges it.
public sealed class MqttIngestService(
    IOptions<BrokerSettings> brokerOptions,
    IOptions<PrimaryHostSettings> hostOptions,
    GpioMessageService messageService,
    ILogger<MqttIngestService> logger) : BackgroundService
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly SparkplugDecoder _decoder = new();
    private readonly Dictionary<string, DateTime> _rebirthRequests = [];
    private string _stateTopic = "";
    private long _sessionTimestamp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var broker = brokerOptions.Value;
        var host = hostOptions.Value;
        if (string.IsNullOrWhiteSpace(host.HostId) || host.HostId.IndexOfAny(['/', '+', '#']) >= 0 ||
            string.IsNullOrWhiteSpace(host.GroupId) || host.GroupId.IndexOfAny(['/', '+', '#']) >= 0)
            throw new InvalidOperationException("Sparkplug:GroupId and Sparkplug:HostId must be valid identifiers.");
        var stateTopic = _stateTopic = $"spBv1.0/STATE/{host.HostId}";
        var filter = $"spBv1.0/{host.GroupId}/#";
        using var certificate = broker.UseTls
            ? X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, broker.BrokerCertificatePath)))
            : null;
        var factory = new MqttClientFactory();
        _client.ApplicationMessageReceivedAsync += args => HandleAsync(args, stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_client.IsConnected)
                    {
                        // The Will and the online message share a timestamp, as Sparkplug requires.
                        var timestamp = _sessionTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var builder = new MqttClientOptionsBuilder()
                            .WithTcpServer(broker.Host, broker.Port)
                            .WithClientId(host.HostId)
                            .WithProtocolVersion(MqttProtocolVersion.V311)
                            .WithCleanSession(false)
                            .WithTimeout(TimeSpan.FromSeconds(broker.ConnectTimeoutSeconds))
                            .WithKeepAlivePeriod(TimeSpan.FromSeconds(broker.KeepAliveSeconds))
                            .WithWillTopic(stateTopic)
                            .WithWillPayload(State(false, timestamp))
                            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                            .WithWillRetain(true);
                        if (certificate is not null)
                            builder.WithTlsOptions(new MqttClientTlsOptionsBuilder()
                                .WithTrustChain(new X509Certificate2Collection(certificate)).Build());
                        await _client.ConnectAsync(builder.Build(), stoppingToken);
                        await _client.SubscribeAsync(factory.CreateSubscribeOptionsBuilder()
                            .WithTopicFilter(filter, MqttQualityOfServiceLevel.AtLeastOnce)
                            .WithTopicFilter(stateTopic, MqttQualityOfServiceLevel.AtLeastOnce).Build(), stoppingToken);
                        await PublishStateAsync(stateTopic, true, timestamp, stoppingToken);
                        logger.LogInformation("Primary host {HostId} online, subscribed to {Filter}.", host.HostId, filter);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    logger.LogWarning("Broker connection failed ({ErrorType}). Retry in 2s.", error.GetType().Name);
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
        }
        finally
        {
            if (_client.IsConnected)
            {
                // A clean disconnect suppresses the Will, so announce offline explicitly.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await PublishStateAsync(stateTopic, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), timeout.Token);
                    await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), timeout.Token);
                }
                catch (Exception error) { logger.LogWarning("Disconnect failed ({ErrorType}).", error.GetType().Name); }
            }
            _client.Dispose();
        }
    }

    private async Task HandleAsync(MqttApplicationMessageReceivedEventArgs args, CancellationToken stoppingToken)
    {
        args.AutoAcknowledge = false;
        var topic = args.ApplicationMessage.Topic;
        if (topic == _stateTopic)
        {
            // A late Will from an earlier connection can overwrite the retained STATE; restore it.
            if (IsOffline(args.ApplicationMessage.Payload.ToArray()))
            {
                logger.LogWarning("Stale offline STATE seen. Republishing online.");
                await PublishStateAsync(_stateTopic, true, _sessionTimestamp, stoppingToken);
            }
            await args.AcknowledgeAsync(stoppingToken);
            return;
        }
        if (topic.Split('/') is [_, _, "NCMD" or "DCMD", ..])
        {
            await args.AcknowledgeAsync(stoppingToken);
            return;
        }
        try
        {
            var item = _decoder.Decode(topic, args.ApplicationMessage.Payload.ToArray(), out var needsRebirth);
            if (messageService.Process(item))
                logger.LogInformation("Processed {Type}{Historical}: {GatewayId}/{DeviceId}",
                    item.MessageType, item.Historical ? " (historical)" : "", item.GatewayId, item.DeviceId);
            else
                logger.LogInformation("Saved {Type} to history without changing current state: {GatewayId}/{DeviceId}",
                    item.MessageType, item.GatewayId, item.DeviceId);
            if (needsRebirth) await RequestRebirthAsync(item.GroupId, item.GatewayId, stoppingToken);
        }
        catch (Exception error) when (error is ArgumentException or InvalidProtocolBufferException)
        {
            logger.LogWarning("Rejected {Topic}: {Reason}", topic, error.Message);
        }
        catch (Exception error)
        {
            // Not stored: leave it unacknowledged and reconnect so the broker redelivers it.
            logger.LogError("Could not store {Topic} ({ErrorType}). Reconnecting.", topic, error.GetType().Name);
            _ = _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build());
            return;
        }
        await args.AcknowledgeAsync(stoppingToken);
    }

    private async Task RequestRebirthAsync(string groupId, string edgeNodeId, CancellationToken cancellationToken)
    {
        var node = groupId + "/" + edgeNodeId;
        if (_rebirthRequests.TryGetValue(node, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(5)) return;
        _rebirthRequests[node] = DateTime.UtcNow;
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = new Payload { Timestamp = timestamp };
        payload.Metrics.Add(new Payload.Types.Metric
        {
            Name = "Node Control/Rebirth", Datatype = (uint)DataType.Boolean, BooleanValue = true, Timestamp = timestamp
        });
        // QoS 0 as Sparkplug specifies for commands; the request repeats if the node stays out of sync.
        await _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic($"spBv1.0/{groupId}/NCMD/{edgeNodeId}").WithPayload(payload.ToByteArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce).Build(), cancellationToken);
        logger.LogWarning("Out of sync with {Node}. Requested rebirth.", node);
    }

    private Task PublishStateAsync(string topic, bool online, long timestamp, CancellationToken cancellationToken) =>
        _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(State(online, timestamp))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(true).Build(),
            cancellationToken);

    private static bool IsOffline(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("online", out var online) && online.ValueKind == JsonValueKind.False;
        }
        catch (JsonException) { return false; }
    }

    private static byte[] State(bool online, long timestamp) =>
        JsonSerializer.SerializeToUtf8Bytes(new { online, timestamp });
}
