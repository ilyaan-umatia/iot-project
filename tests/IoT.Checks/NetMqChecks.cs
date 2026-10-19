using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using EdgeGateway.BrokerMqtt.Intake;
using EdgeGateway.BrokerMqtt.Storage;
using EdgeGateway.DataAcquisition.Intake;
using IoT.Contracts;
using NetMQ;
using NetMQ.Sockets;

internal static class NetMqChecks
{
    public static async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var settings = new NetMqSettings { Endpoint = $"tcp://127.0.0.1:{port}" };
        var directory = Path.Combine(Path.GetTempPath(), "iot-netmq-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "outbox.db");
        var reading = new DeviceReading("netmq-fridge", 1, DateTimeOffset.UtcNow, 4.5, 65, 17, 0);
        var message = new AcquisitionMessage(reading.DeviceId, reading);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var receiverStop = new CancellationTokenSource();
        Task? receiving = null;
        try
        {
            // Simulate commit followed by a lost reply. The sender must retry the same reading.
            var committed = Task.Factory.StartNew(() =>
            {
                using var socket = new ResponseSocket();
                socket.Options.Linger = TimeSpan.Zero;
                socket.Bind(settings.Endpoint);
                if (!socket.TryReceiveFrameString(TimeSpan.FromSeconds(8), out var json))
                    throw new Exception("Sender did not connect.");
                var received = JsonSerializer.Deserialize<AcquisitionMessage>(json, EdgeTopics.Json)!;
                using var first = new Outbox(path);
                first.AcceptReading(received.Reading!);
                // No reply: pretend the publishing process crashed immediately after commit.
            }, timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var sender = new BrokerSender(settings);
            var sending = sender.SendAsync(message, timeout.Token);
            await committed;
            await Task.Delay(250);
            if (sending.IsCompleted) throw new Exception("Sender succeeded without durable reply.");
            using var outbox = new Outbox(path);
            receiving = new AcquisitionReceiver(settings, outbox).RunAsync(receiverStop.Token);
            await sending.WaitAsync(timeout.Token);
            if (outbox.CountUnsent() != 1) throw new Exception("Lost reply created duplicate reading.");
            await sender.SendAsync(new(reading.DeviceId, null), timeout.Token);
            await sender.SendAsync(new(reading.DeviceId, null), timeout.Token);
            await sender.SendAsync(new(reading.DeviceId, reading with { DeviceSeq = 2 }), timeout.Token);
            var kinds = outbox.PeekUnsent(10).Select(row => row.Kind).ToArray();
            if (!kinds.SequenceEqual(new[] { "DBIRTH", "DDEATH", "DBIRTH" }))
                throw new Exception("Offline retry or reading order broken.");
            await Task.Run(() =>
            {
                using var socket = new RequestSocket();
                socket.Options.Linger = TimeSpan.Zero;
                socket.Connect(settings.Endpoint);
                socket.SendFrame("not-json");
                if (!socket.TryReceiveFrameString(TimeSpan.FromSeconds(3), out var reply) || reply != "invalid")
                    throw new Exception("Malformed input not rejected.");
            });
            await sender.SendAsync(new(reading.DeviceId, reading with { DeviceSeq = 3 }), timeout.Token);
            if (outbox.CountUnsent() != 4) throw new Exception("Receiver failed after invalid input.");
            receiverStop.Cancel();
            await receiving;
            using var cancelSend = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                await sender.SendAsync(message, cancelSend.Token).WaitAsync(TimeSpan.FromSeconds(6));
                throw new Exception("Unavailable receiver incorrectly acknowledged data.");
            }
            catch (OperationCanceledException) when (cancelSend.IsCancellationRequested) { }
            Console.WriteLine("PASS: NetMQ durable reply, lost-reply retry/dedup, offline ordering, invalid input and cancellation.");
        }
        finally
        {
            receiverStop.Cancel();
            if (receiving is not null) await receiving;
            NetMQConfig.Cleanup();
            Directory.Delete(directory, recursive: true);
        }
    }
}
