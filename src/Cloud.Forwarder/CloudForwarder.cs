using System.Buffers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cloud.Forwarder.Configuration;
using IoT.Contracts;
using Microsoft.Azure.Devices.Client;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace Cloud.Forwarder;

public sealed class CloudForwarder : BackgroundService
{
    private readonly IMqttClient _mqtt = new MqttClientFactory().CreateMqttClient();
    private readonly MqttClientOptions _options;
    private readonly X509Certificate2? _certificate;
    private readonly DeviceClient? _azure;
    private readonly string _filter;
    private readonly SparkplugDecoder _decoder = new();
    private readonly Channel<LifecycleEvent> _pending = Channel.CreateBounded<LifecycleEvent>(200);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public CloudForwarder(ForwarderSettings settings)
    {
        var connectionString = settings.IoTHub.DeviceConnectionString;
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var identity = IotHubConnectionStringBuilder.Create(connectionString).DeviceId;
            if (identity != settings.Sparkplug.EdgeNodeId)
                throw new ArgumentException("IoT Hub credentials must belong to the gateway's EdgeNodeId.");
            _azure = DeviceClient.CreateFromConnectionString(connectionString, TransportType.Mqtt_Tcp_Only);
            _azure.OperationTimeoutInMilliseconds = 15_000;
        }

        var mqtt = settings.Mqtt;
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(mqtt.Host, mqtt.Port)
            .WithClientId(mqtt.ClientId + "-cloud-forwarder")
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession()
            .WithTimeout(TimeSpan.FromSeconds(mqtt.ConnectTimeoutSeconds))
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(mqtt.KeepAliveSeconds));
        if (mqtt.UseTls)
        {
            _certificate = X509Certificate2.CreateFromPem(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, mqtt.BrokerCertificatePath)));
            builder.WithTlsOptions(new MqttClientTlsOptionsBuilder()
                .WithTrustChain(new X509Certificate2Collection(_certificate)).Build());
        }
        _options = builder.Build();
        _filter = $"spBv1.0/{settings.Sparkplug.GroupId}/+/{settings.Sparkplug.EdgeNodeId}/#";
        _mqtt.ApplicationMessageReceivedAsync += args =>
        {
            try
            {
                var item = _decoder.Decode(args.ApplicationMessage.Topic, args.ApplicationMessage.Payload.ToArray());
                if (!_pending.Writer.TryWrite(item))
                    Console.WriteLine($"Cloud buffer full. Dropped {item.MessageType}: {item.EventId}");
            }
            catch (Exception error) when (error is ArgumentException or Google.Protobuf.InvalidProtocolBufferException)
            {
                Console.WriteLine($"Rejected MQTT message: {error.Message}");
            }
            return Task.CompletedTask;
        };
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var forwarding = ForwardAsync(session.Token);
        var factory = new MqttClientFactory();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (forwarding.IsCompleted) await forwarding;
                try
                {
                    if (!_mqtt.IsConnected)
                    {
                        _decoder.Reset();
                        await _mqtt.ConnectAsync(_options, cancellationToken);
                        await _mqtt.SubscribeAsync(factory.CreateSubscribeOptionsBuilder()
                            .WithTopicFilter(_filter, MqttQualityOfServiceLevel.AtLeastOnce).Build(), cancellationToken);
                        Console.WriteLine($"Cloud forwarder subscribed: {_filter}");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    Console.WriteLine($"Forwarder broker connection failed ({error.GetType().Name}). Retry in 2s.");
                    // Retry the subscription on a fresh connection.
                    if (_mqtt.IsConnected)
                        await _mqtt.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }
        finally
        {
            session.Cancel();
            try { await forwarding; }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
            if (_mqtt.IsConnected)
            {
                using var disconnect = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _mqtt.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), disconnect.Token); }
                catch (Exception error) { Console.WriteLine($"Forwarder disconnect failed ({error.GetType().Name})."); }
            }
        }
    }

    private async Task ForwardAsync(CancellationToken cancellationToken)
    {
        if (_azure is null) Console.WriteLine("Azure forwarding disabled. IoT Hub credentials are blank.");
        await foreach (var item in _pending.Reader.ReadAllAsync(cancellationToken))
        {
            if (_azure is null) continue;
            while (true)
            {
                try
                {
                    using var message = new Message(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, JsonOptions)))
                    {
                        ContentType = "application/json", ContentEncoding = "utf-8", MessageId = item.EventId
                    };
                    await _azure.SendEventAsync(message, cancellationToken).WaitAsync(cancellationToken);
                    var session = item.MessageType == "NDEATH" ? $" (bdSeq {item.BdSeq})" : "";
                    Console.WriteLine($"Forwarded {item.MessageType}: {item.EventId}{session}");
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    Console.WriteLine($"Azure send failed ({error.GetType().Name}). Retry in 2s.");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _mqtt.Dispose();
        _azure?.Dispose();
        _certificate?.Dispose();
    }
}
