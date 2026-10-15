using System.Buffers;
using System.Security.Cryptography.X509Certificates;
using Device.Gateway.Configuration;
using Device.Gateway.Sparkplug;
using Google.Protobuf;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;

namespace Device.Gateway.Connection;

public sealed class MqttConnection : IDisposable
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly ConnectionStateMachine _state = new();
    private TaskCompletionSource _disconnected = NewDisconnectSignal();
    private readonly MqttClientOptionsBuilder _optionsBuilder;
    private readonly BirthDeathSequenceStore _sequenceStore;
    private readonly string _deathTopic;
    public ulong BirthDeathSequence { get; private set; }
    public bool IsConnected => _client.IsConnected;
    public ConnectionState State => _state.Current;
    public event Func<string, byte[], Task>? MessageReceived;
    private readonly X509Certificate2? _trustedCertificate;

    public MqttConnection(MqttSettings settings, SparkplugSettings sparkplug)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithClientId(settings.ClientId)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession()
            .WithTimeout(TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds))
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(settings.KeepAliveSeconds));

        if (settings.UseTls)
        {
            var certificatePath = Path.Combine(AppContext.BaseDirectory, settings.BrokerCertificatePath);
            _trustedCertificate = X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath));
            var trustedCertificates = new X509Certificate2Collection(_trustedCertificate);

            // Trust our local broker certificate while keeping TLS verification enabled.
            builder.WithTlsOptions(new MqttClientTlsOptionsBuilder()
                .WithTrustChain(trustedCertificates)
                .Build());
        }

        _optionsBuilder = builder;
        _sequenceStore = new BirthDeathSequenceStore(settings.ClientId, settings.StateDirectory);
        _deathTopic = SparkplugTopics.NodeDeath(sparkplug);
        _client.DisconnectedAsync += _ =>
        {
            if (_state.TryMoveTo(ConnectionState.Disconnected))
            {
                Console.WriteLine("Broker disconnected.");
            }
            _disconnected.TrySetResult();
            return Task.CompletedTask;
        };
        _client.ApplicationMessageReceivedAsync += args =>
            MessageReceived?.Invoke(args.ApplicationMessage.Topic, args.ApplicationMessage.Payload.ToArray())
            ?? Task.CompletedTask;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (!_state.TryMoveTo(ConnectionState.Connecting))
            throw new InvalidOperationException("Gateway cannot connect in its current state.");

        try
        {
            _disconnected = NewDisconnectSignal();
            BirthDeathSequence = _sequenceStore.ReserveNext();
            var options = _optionsBuilder
                .WithWillTopic(_deathTopic)
                .WithWillPayload(SparkplugPayloads.NodeDeath(BirthDeathSequence).ToByteArray())
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithWillRetain(false)
                .Build();
            await _client.ConnectAsync(options, cancellationToken);
            Console.WriteLine($"Session bdSeq: {BirthDeathSequence}");
            if (!_state.TryMoveTo(ConnectionState.PublishingBirth))
                throw new InvalidOperationException("Connection closed before the birth announcement.");
        }
        catch
        {
            _state.TryMoveTo(ConnectionState.Disconnected);
            throw;
        }
    }

    private static TaskCompletionSource NewDisconnectSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForDisconnectAsync(CancellationToken cancellationToken) =>
        _disconnected.Task.WaitAsync(cancellationToken);

    public async Task PrepareRetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_client.IsConnected)
                await PublishDeathAndDisconnectAsync(cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // The connection dropped meanwhile; the broker publishes the Will instead.
            Console.WriteLine($"NDEATH not sent ({error.GetType().Name}).");
        }
        _state.TryMoveTo(ConnectionState.Disconnected);
        if (!_state.TryMoveTo(ConnectionState.RetryWaiting))
            throw new InvalidOperationException("Gateway cannot retry in its current state.");
    }


    public async Task SubscribeAsync(IEnumerable<string> topics, CancellationToken cancellationToken)
    {
        var builder = new MqttClientFactory().CreateSubscribeOptionsBuilder();
        foreach (var topic in topics) builder.WithTopicFilter(topic, MqttQualityOfServiceLevel.AtLeastOnce);
        await _client.SubscribeAsync(builder.Build(), cancellationToken);
    }

    public bool MarkOnline() => _state.TryMoveTo(ConnectionState.Online);

    public async Task PublishAsync(string topic, byte[] payload, CancellationToken cancellationToken,
        MqttQualityOfServiceLevel qualityOfService = MqttQualityOfServiceLevel.AtMostOnce)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(qualityOfService)
            .WithRetainFlag(false)
            .Build();

        await _client.PublishAsync(message, cancellationToken);
    }
    private async Task PublishDeathAndDisconnectAsync(CancellationToken cancellationToken)
    {
        await PublishAsync(_deathTopic, SparkplugPayloads.NodeDeath(BirthDeathSequence).ToByteArray(),
            cancellationToken, MqttQualityOfServiceLevel.AtLeastOnce);
        Console.WriteLine($"Published NDEATH: {_deathTopic} (bdSeq {BirthDeathSequence})");
        await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        _state.TryMoveTo(ConnectionState.Stopping);
        try
        {
            if (_client.IsConnected)
                await PublishDeathAndDisconnectAsync(cancellationToken);
        }
        catch (Exception error) when (error is MqttCommunicationException or IOException)
        {
            // A lost transport cannot send NDEATH; disposal closes it and the broker handles the Will.
            Console.WriteLine("Connection closed during shutdown.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("Disconnect timed out.");
        }
        finally
        {
            _state.TryMoveTo(ConnectionState.Stopped);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _trustedCertificate?.Dispose();
    }
}
