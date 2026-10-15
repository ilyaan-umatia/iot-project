using System.Text;
using System.Text.Json;
using Device.Simulator;
using IoT.Contracts;
using Microsoft.Extensions.Configuration;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

// Simulated fridge: publishes each reading to the edge broker and keeps it until the broker acknowledges it.
var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true).AddCommandLine(args).Build();
var settings = configuration.GetSection("Simulator").Get<SimulatorSettings>() ?? new();
var edge = configuration.GetSection("Edge").Get<EdgeBrokerSettings>() ?? new();
if (settings.ReadingIntervalSeconds <= 0) throw new ArgumentException("Reading interval must be positive.");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};

var fridge = new FridgeModel(settings.BaseTemperature);
// Console commands stand in for the physical world: opening the door, a cooling fault, a silent sensor.
_ = Task.Run(() =>
{
    while (!stop.IsCancellationRequested)
    {
        var command = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (command is null) return;
        if (command == "stop") { stop.Cancel(); return; }
        if (command == "") continue;
        Console.WriteLine(fridge.Apply(command)
            ?? "Commands: door open, door close, spike on, spike off, pause, resume, stop.");
    }
});

var sequence = new DeviceSequenceStore(settings.DeviceId, settings.StateDirectory);
var statusTopic = EdgeTopics.Status(settings.DeviceId);
using var client = new MqttClientFactory().CreateMqttClient();
var options = new MqttClientOptionsBuilder()
    .WithTcpServer(edge.Host, edge.Port)
    .WithClientId(settings.DeviceId)
    .WithProtocolVersion(MqttProtocolVersion.V311)
    .WithCleanSession()
    .WithTimeout(TimeSpan.FromSeconds(edge.ConnectTimeoutSeconds))
    .WithKeepAlivePeriod(TimeSpan.FromSeconds(edge.KeepAliveSeconds))
    .WithWillTopic(statusTopic)
    .WithWillPayload(Encoding.UTF8.GetBytes(EdgeTopics.Offline))
    .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
    .WithWillRetain(true)
    .Build();

Task PublishAsync(string topic, byte[] payload, bool retain, CancellationToken cancellationToken) =>
    client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(retain).Build(),
        cancellationToken);

var pending = new Queue<DeviceReading>();
Console.WriteLine($"Simulating {settings.DeviceId} every {settings.ReadingIntervalSeconds}s via {edge.Host}:{edge.Port}. Ctrl+C stops.");
using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.ReadingIntervalSeconds));
try
{
    do
    {
        if (!fridge.Paused)
        {
            var value = fridge.Next();
            pending.Enqueue(new(settings.DeviceId, sequence.Next(), DateTimeOffset.UtcNow,
                value.Temperature, value.Humidity, settings.GpioPin, value.GpioValue));
        }
        try
        {
            if (!client.IsConnected)
            {
                await client.ConnectAsync(options, stop.Token);
                await PublishAsync(statusTopic, Encoding.UTF8.GetBytes(EdgeTopics.Online), true, stop.Token);
                Console.WriteLine("Connected to edge broker.");
            }
            while (pending.TryPeek(out var reading))
            {
                await PublishAsync(EdgeTopics.Data(settings.DeviceId),
                    JsonSerializer.SerializeToUtf8Bytes(reading, EdgeTopics.Json), false, stop.Token);
                pending.Dequeue();
                Console.WriteLine($"Sent #{reading.DeviceSeq}: temperature={reading.Temperature}C, humidity={reading.Humidity}%, GPIO {reading.GpioPin}={reading.GpioValue}");
            }
        }
        catch (Exception error) when (!stop.IsCancellationRequested)
        {
            Console.WriteLine($"Edge broker unavailable ({error.GetType().Name}). {pending.Count} reading(s) queued.");
        }
    } while (await timer.WaitForNextTickAsync(stop.Token));
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
}
finally
{
    if (client.IsConnected)
    {
        // A clean disconnect suppresses the Will, so announce the shutdown explicitly.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await PublishAsync(statusTopic, Encoding.UTF8.GetBytes(EdgeTopics.Offline), true, timeout.Token);
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), timeout.Token);
        }
        catch (Exception error) { Console.WriteLine($"Disconnect failed ({error.GetType().Name})."); }
    }
    if (pending.Count > 0) Console.WriteLine($"Stopped with {pending.Count} unsent reading(s).");
}
