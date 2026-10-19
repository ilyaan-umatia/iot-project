using System.Text.Json;
using IoT.Contracts;
using NetMQ;
using NetMQ.Sockets;

namespace EdgeGateway.DataAcquisition.Intake;

public sealed class BrokerSender(NetMqSettings settings)
{
    // MQTT dispatches messages in order. Wait for a durable reply before acknowledging
    // the edge message. Each socket lives entirely on one thread, including retries.
    public Task SendAsync(AcquisitionMessage message, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var json = JsonSerializer.Serialize(message, EdgeTopics.Json);
        var timeout = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            // A timed-out REQ socket cannot send another request: recreate it.
            using var socket = new RequestSocket();
            socket.Options.Linger = TimeSpan.Zero;
            socket.Connect(settings.Endpoint);
            if (socket.TrySendFrame(timeout, json) && socket.TryReceiveFrameString(timeout, out var reply))
            {
                if (reply == "ok") return;
                if (reply == "invalid") throw new ArgumentException("EdgeGateway.BrokerMqtt rejected the message.");
            }
            Console.WriteLine("EdgeGateway.BrokerMqtt has not confirmed storage. Retrying in 2s.");
            cancellationToken.WaitHandle.WaitOne(timeout);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }, cancellationToken);
}
