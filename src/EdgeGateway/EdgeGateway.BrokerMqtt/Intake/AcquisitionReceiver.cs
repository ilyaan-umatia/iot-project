using System.Text.Json;
using EdgeGateway.BrokerMqtt.Storage;
using IoT.Contracts;
using NetMQ;
using NetMQ.Sockets;

namespace EdgeGateway.BrokerMqtt.Intake;

public sealed class AcquisitionReceiver(NetMqSettings settings, Outbox outbox)
{
    public Task RunAsync(CancellationToken cancellationToken) => Task.Factory.StartNew(() =>
    {
        using var socket = new ResponseSocket();
        socket.Options.Linger = TimeSpan.Zero;
        socket.Bind(settings.Endpoint);
        Console.WriteLine($"NetMQ listening on {settings.Endpoint}.");
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!socket.TryReceiveFrameString(TimeSpan.FromMilliseconds(200), out var json)) continue;
            var reply = "ok";
            try
            {
                var message = JsonSerializer.Deserialize<AcquisitionMessage>(json, EdgeTopics.Json)
                    ?? throw new ArgumentException("Empty message.");
                message.Validate();
                var added = message.Reading is { } reading
                    ? outbox.AcceptReading(reading)
                    : outbox.AcceptDeviceOffline(message.DeviceId);
                Console.WriteLine($"{(added ? "Buffered" : "Duplicate/no change")} {message.DeviceId} via NetMQ.");
            }
            catch (Exception error) when (error is ArgumentException or JsonException)
            {
                reply = "invalid";
                Console.WriteLine($"Rejected acquisition message: {error.Message}");
            }
            catch (Exception error)
            {
                reply = "retry";
                Console.WriteLine($"Outbox commit failed ({error.GetType().Name}). Sender will retry.");
            }
            // Success means SQLite committed, not merely that NetMQ received the bytes.
            socket.SendFrame(reply);
        }
    }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
