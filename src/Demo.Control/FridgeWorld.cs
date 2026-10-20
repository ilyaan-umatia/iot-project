using System.Collections.Concurrent;
using System.Net;
using FluentModbus;

namespace Demo.Control;

public sealed record FridgeState(bool DoorOpen, bool Spike, bool Paused);

// The "physical world" for the demo: a simple thermal model per fridge, written into each fridge's
// Modbus simulator every few seconds, the way real sensors would update a PLC's registers.
// Registers: holding 0 = temperature x10, holding 1 = humidity x10, holding 10-11 = power (float32),
// holding 12-13 = door openings (uint32), coil 0 = door switch.
public sealed class FridgeWorld : IDisposable
{
    private readonly ConcurrentDictionary<string, Model> _models = new();
    private readonly CancellationTokenSource _stop = new();

    public FridgeWorld(IEnumerable<FridgeDefinition> fridges, Func<string, bool> isRunning)
    {
        foreach (var fridge in fridges) _models[fridge.Id] = new(fridge);
        _ = Task.Factory.StartNew(() => Run(isRunning), TaskCreationOptions.LongRunning);
    }

    public FridgeState State(string id) => _models[id].State;

    public string Apply(string id, string command)
    {
        var model = _models.TryGetValue(id, out var found) ? found : throw new ArgumentException($"Unknown fridge {id}.");
        model.State = command switch
        {
            "door-open" => model.State with { DoorOpen = true },
            "door-close" => model.State with { DoorOpen = false },
            "spike-on" => model.State with { Spike = true },
            "spike-off" => model.State with { Spike = false },
            "pause" => model.State with { Paused = true },
            "resume" => model.State with { Paused = false },
            _ => throw new ArgumentException($"Unknown fridge command {command}.")
        };
        return $"{id}: {command.Replace('-', ' ')}";
    }

    // A restarted simulator starts with zeroed registers and a closed door.
    public void Reset(string id)
    {
        if (_models.TryGetValue(id, out var model)) model.Reset();
    }

    private void Run(Func<string, bool> isRunning)
    {
        while (!_stop.IsCancellationRequested)
        {
            foreach (var model in _models.Values)
            {
                if (!isRunning(model.Fridge.Id)) { model.Disconnect(); continue; }
                if (model.State.Paused) continue; // registers freeze: the sensor value stops changing
                model.Step();
                model.TryWrite();
            }
            _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var model in _models.Values) model.Disconnect();
    }

    private sealed class Model(FridgeDefinition fridge)
    {
        private readonly Random _random = new();
        private ModbusTcpClient? _client;
        private double _temperature = fridge.BaseTemperature;
        private double _humidity = 65;
        private double _power = 85;
        private uint _doorOpenings;
        private bool _wasOpen;

        public FridgeDefinition Fridge => fridge;
        public FridgeState State { get; set; } = new(false, false, false);

        public void Reset()
        {
            State = new(false, false, false);
            _temperature = fridge.BaseTemperature;
            _humidity = 65;
            _power = 85;
            _doorOpenings = 0;
            _wasOpen = false;
        }

        public void Step()
        {
            var target = State.Spike ? 12.5 : State.DoorOpen ? 10 : fridge.BaseTemperature;
            _temperature += (target - _temperature) * 0.3 + (_random.NextDouble() - 0.5) * 0.2;
            _humidity += ((State.DoorOpen ? 78 : 65) - _humidity) * 0.4 + (_random.NextDouble() - 0.5);
            _humidity = Math.Clamp(_humidity, 0, 100);
            // The compressor works harder with the door open or a cooling fault; door openings are a counter.
            _power += ((State.Spike ? 140 : State.DoorOpen ? 120 : 85) - _power) * 0.4 + (_random.NextDouble() - 0.5) * 2;
            if (State.DoorOpen && !_wasOpen) _doorOpenings++;
            _wasOpen = State.DoorOpen;
        }

        public void TryWrite()
        {
            try
            {
                if (_client is not { IsConnected: true })
                {
                    _client?.Dispose();
                    _client = new ModbusTcpClient { ConnectTimeout = 1000, ReadTimeout = 1000, WriteTimeout = 1000 };
                    _client.Connect(new IPEndPoint(IPAddress.Loopback, fridge.Port), ModbusEndianness.BigEndian);
                }
                _client.WriteMultipleRegisters(1, 0, new short[] { (short)Math.Round(_temperature * 10), (short)Math.Round(_humidity * 10) });
                // 32-bit values span two registers, high word first (big word order): float32 at 10, uint32 at 12.
                var power = BitConverter.SingleToInt32Bits((float)_power);
                _client.WriteMultipleRegisters(1, 10, new short[]
                {
                    (short)(power >> 16), (short)power, (short)(_doorOpenings >> 16), (short)_doorOpenings
                });
                _client.WriteSingleCoil(1, 0, State.DoorOpen);
            }
            catch (Exception)
            {
                Disconnect(); // the simulator is restarting; try again next tick
            }
        }

        public void Disconnect()
        {
            try { _client?.Disconnect(); } catch (Exception) { }
            _client?.Dispose();
            _client = null;
        }
    }
}
