using System.Buffers;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using IoT.Contracts;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using Org.Eclipse.Tahu.Protobuf;

namespace Demo.Control;

public sealed record WireMessage(long Id, DateTimeOffset At, string Topic, string Type, string Summary,
    long? Seq, bool Historical, bool Retained);

// The console's Sparkplug consumer on Mosquitto, alongside MQTTX. It uses a persistent session with QoS 1,
// so the broker keeps its messages while it is disconnected, and feeds both the message feed and the view.
public sealed class WireMonitor(IConfiguration configuration, SparkplugView view, ILogger<WireMonitor> logger) : BackgroundService
{
    private readonly Dictionary<string, LinkedList<WireMessage>> _messages = new() { ["upstream"] = new() };
    private readonly Dictionary<string, bool> _connected = new() { ["upstream"] = false };
    private long _nextId;

    public WireMessage[] Recent(string broker, int count = 120)
    {
        lock (_messages) return _messages[broker].Take(count).ToArray();
    }

    public bool Connected(string broker)
    {
        lock (_messages) return _connected[broker];
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mqtt = configuration.GetSection("Mqtt");
        var useTls = mqtt.GetValue("UseTls", true);
        var certificatePath = Path.Combine(AppContext.BaseDirectory, mqtt["BrokerCertificatePath"] ?? "certs/broker.crt");
        return WatchAsync("upstream", mqtt["Host"] ?? "127.0.0.1", mqtt.GetValue("Port", 8883),
            useTls && File.Exists(certificatePath) ? certificatePath : null, "spBv1.0/#", stoppingToken);
    }

    private async Task WatchAsync(string broker, string host, int port, string? certificatePath, string filter,
        CancellationToken stoppingToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        using var certificate = certificatePath is null ? null : X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath));
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId("demo-console-monitor")
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession(false)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(10));
        if (certificate is not null)
            builder.WithTlsOptions(new MqttClientTlsOptionsBuilder()
                .WithTrustChain(new X509Certificate2Collection(certificate)).Build());
        var options = builder.Build();
        client.ApplicationMessageReceivedAsync += args =>
        {
            var message = args.ApplicationMessage;
            Add(broker, message.Topic, message.Payload.ToArray(), message.Retain);
            return Task.CompletedTask;
        };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    await client.ConnectAsync(options, stoppingToken);
                    await client.SubscribeAsync(new MqttClientFactory().CreateSubscribeOptionsBuilder()
                        .WithTopicFilter(filter, MqttQualityOfServiceLevel.AtLeastOnce).Build(), stoppingToken);
                }
                lock (_messages) _connected[broker] = true;
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                lock (_messages) _connected[broker] = false;
                logger.LogDebug("Monitor for {Broker} not connected ({ErrorType}).", broker, error.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void Add(string broker, string topic, byte[] payload, bool retained)
    {
        string type, summary;
        long? seq = null;
        var historical = false;
        try
        {
            if (topic.StartsWith("spBv1.0/STATE/", StringComparison.Ordinal))
            {
                type = "STATE";
                summary = Encoding.UTF8.GetString(payload);
            }
            else
            {
                type = topic.Split('/') is { Length: >= 3 } parts ? parts[2] : "?";
                var decoded = Payload.Parser.ParseFrom(payload);
                view.Apply(topic, decoded);
                if (decoded.HasSeq) seq = (long)decoded.Seq;
                historical = decoded.Metrics.Any(metric => metric.IsHistorical);
                summary = string.Join(" · ", decoded.Metrics.Select(metric => $"{metric.Name}={Value(metric)}"));
                if (summary == "") summary = "(no metrics)";
            }
        }
        catch (Exception error) when (error is JsonException or Google.Protobuf.InvalidProtocolBufferException)
        {
            type = "?";
            summary = $"{payload.Length} bytes (not decodable)";
        }
        var item = new WireMessage(Interlocked.Increment(ref _nextId), DateTimeOffset.UtcNow, topic, type, summary,
            seq, historical, retained);
        lock (_messages)
        {
            var list = _messages[broker];
            list.AddFirst(item);
            while (list.Count > 200) list.RemoveLast();
        }
    }

    private static string Value(Payload.Types.Metric metric) =>
        metric.IsNull || SparkplugDecoder.QualityOf(metric) != MetricQuality.Good
            ? $"null ({SparkplugDecoder.QualityOf(metric) switch { MetricQuality.Good => MetricQuality.Bad, var quality => quality }})"
        : metric.HasDoubleValue ? metric.DoubleValue.ToString(CultureInfo.InvariantCulture)
        : metric.HasBooleanValue ? metric.BooleanValue.ToString().ToLowerInvariant()
        : metric.HasLongValue ? metric.LongValue.ToString(CultureInfo.InvariantCulture)
        : metric.HasIntValue ? metric.IntValue.ToString(CultureInfo.InvariantCulture)
        : "?";
}
