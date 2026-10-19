using EdgeGateway.DataAcquisition.Configuration;
using EdgeGateway.DataAcquisition.Intake;
using IoT.Contracts;
using Microsoft.Extensions.Configuration;
using NetMQ;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json").AddCommandLine(args).Build();
    var edge = configuration.GetSection("Edge").Get<EdgeBrokerSettings>() ?? new();
    var transport = configuration.GetSection("NetMq").Get<NetMqSettings>() ?? new();
    var clientId = (configuration["Mqtt:ClientId"] ?? "gateway-01") + "-intake";
    using var intake = new DeviceSubscriber(edge, clientId, new BrokerSender(transport));
    _ = Task.Run(() =>
    {
        while (!stop.IsCancellationRequested)
        {
            var command = Console.ReadLine();
            if (command is null) return;
            if (command.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) { stop.Cancel(); return; }
        }
    });
    Console.WriteLine($"Gateway intake → {transport.Endpoint}. Type stop to exit.");
    await intake.RunAsync(stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
catch (Exception error)
{
    Console.Error.WriteLine($"Intake error: {error.Message}");
    Environment.ExitCode = 1;
}
finally { NetMQConfig.Cleanup(); }
