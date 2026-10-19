using EdgeGateway.BrokerMqtt.Configuration;
using EdgeGateway.BrokerMqtt.Connection;
using EdgeGateway.BrokerMqtt.Intake;
using EdgeGateway.BrokerMqtt.Storage;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};
try
{
    var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json").AddCommandLine(args).Build();
    var settings = configuration.Get<GatewaySettings>()
        ?? throw new InvalidOperationException("Could not read appsettings.json.");
    using var outbox = new Outbox(settings.Gateway.DatabasePath);
    using var connection = new MqttConnection(settings.Mqtt, settings.Sparkplug);
    var intake = new AcquisitionReceiver(settings.NetMq, outbox);
    var runner = new GatewayRunner(connection, outbox, settings);
    Console.WriteLine($"Outbox: {settings.Gateway.DatabasePath} ({outbox.CountUnsent()} unsent). Type stop to exit.");
    var input = Task.Run(() =>
    {
        while (!stop.IsCancellationRequested)
        {
            var command = Console.ReadLine()?.Trim().ToLowerInvariant();
            // Without console input (for example under the launcher) run until stopped.
            if (command is null) stop.Token.WaitHandle.WaitOne();
            if (command is null or "stop") break;
            if (command != "") Console.WriteLine("Commands: stop.");
        }
    });
    // Intake keeps buffering while the upstream connection is down.
    var receiving = intake.RunAsync(stop.Token);
    var running = runner.RunAsync(stop.Token);
    var reporting = WriteStatusAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.Gateway.DatabasePath))!,
        "status.json"), stop.Token);
    try
    {
        var completed = await Task.WhenAny(input, running, receiving);
        stop.Cancel();
        await completed;
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        Console.WriteLine("Stopping...");
    }
    finally
    {
        stop.Cancel();
        try
        {
            try { await Task.WhenAll(running, receiving, reporting); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        finally
        {
            using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await connection.DisconnectAsync(disconnectTimeout.Token);
        }
    }
    Console.WriteLine("Disconnected.");

    // A status snapshot for local tools such as the demo console; not part of the data path.
    async Task WriteStatusAsync(string path, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var json = JsonSerializer.Serialize(new
                {
                    updatedAt = DateTimeOffset.UtcNow, upstream = connection.State.ToString(),
                    hostOnline = runner.HostOnline,
                    unsent = outbox.CountUnsent(), bdSeq = connection.BirthDeathSequence,
                    lastPublished = runner.LastPublished
                });
                File.WriteAllText(path + ".tmp", json);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (IOException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }
}
catch (Exception error)
{
    Console.Error.WriteLine($"Gateway error: {error.Message}");
    Environment.ExitCode = 1;
}
finally { NetMQ.NetMQConfig.Cleanup(); }
