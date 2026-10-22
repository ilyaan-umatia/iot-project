# IoT Project

Requires .NET 8 SDK and Docker Desktop. MQTTX or MQTT Explorer to watch the broker.

```text
EdgeGateway.Configuration (device registry, REST :5300)
        │ device list (fetched every 10 s, cached locally)
        ▼
Modbus devices ◄── Modbus TCP polling ── EdgeGateway.Gateway [poll → SQLite outbox → Sparkplug B]
(pymodbus simulators :5021-5023)                    │ MQTT over TLS
                                                    ▼
                                   Mosquitto (:8883) ──► MQTTX / MQTT Explorer, demo console
```

- **Devices:** each fridge is a [pymodbus](https://github.com/pymodbus-dev/pymodbus) Modbus TCP server in Docker (`simulators/modbus`). Any Modbus tool can read or write them.
- **Configuration service:** stores each device's address, unit ID, poll interval, GPIO pin and register map. `GET/POST/PUT/DELETE /devices`, `GET /config`. It holds configuration only; online/offline comes from the gateway. Seeded with fridge-01..03 on first start.
- **Gateway (one process):** one poll loop per enabled device. Each reading is numbered (persisted per device) and saved to the SQLite outbox before publishing. Committed readings survive a gateway crash or a broker outage. The publishing loop drains the outbox in order as Sparkplug B (NBIRTH/DBIRTH/DDATA/DDEATH, Rebirth, historical replay) and marks a row sent only after the broker's acknowledgement.
  - After 3 failed polls a device is reported offline (DDEATH); the next good poll brings it back (DBIRTH).
  - Devices added, changed or removed in the registry apply within about 10 seconds; a changed metric list triggers a new DBIRTH.
  - Startup also reconciles saved device state: disabled/deleted devices get a DDEATH after any queued readings replay. An initial saved DBIRTH can still appear before that replay finishes.
  - If the configuration service is down, it keeps polling from its cached device list.

## Reading registers

Each metric in the registry says where its value lives and how to decode it:

| Field | Meaning |
|---|---|
| `register` | `holding`, `input` (16-bit registers), `coil`, `discrete` (single bits) |
| `address` | first register (0-based) |
| `dataType` | `int16` (default), `uint16`, `int32`, `uint32`, `float32` for registers; `bool` for bits |
| `wordOrder` | 32-bit types span two registers: `big` (default, high word first) or `little` (word-swapped devices) |
| `scale`, `unit` | value = decoded × scale, e.g. `0.1` for a register holding tenths |

The gateway groups each device's metrics into **frames**: one read request per run of nearby registers of the same type (up to 125 registers or 2000 bits; gaps of up to 8 registers are read through), then decodes each metric from its slice. The demo fridges use one holding-register frame (0–13) and one coil frame:

| Metric | Register | Type |
|---|---|---|
| Temperature | holding 0, × 0.1 °C | int16 |
| Humidity | holding 1, × 0.1 % | uint16 |
| CompressorPower | holding 10–11, W | float32, big word order |
| DoorOpenings | holding 12–13 | uint32, big word order |
| GpioValue (door) | coil 0 | bool |

## Configuration

Root `appsettings.json` (ignored by Git) holds shared settings; the settings classes provide the other defaults:

```json
{
  "Sparkplug": { "GroupId": "warehouse-01", "EdgeNodeId": "gateway-01", "HostId": "" },
  "Mqtt": { "Host": "127.0.0.1", "Port": 8883, "ClientId": "gateway-01", "UseTls": true, "BrokerCertificatePath": "certs/broker.crt" }
}
```

The backend (`Application.Api`) is not part of this flow; MQTTX and the demo console are the consumers. A non-empty `HostId` would make the gateway wait for a Sparkplug Primary Host's STATE, so both launchers start the gateway with an empty one.

## Demo console (recommended)

```powershell
./scripts/New-LocalBrokerCertificate.ps1   # first setup only
./scripts/Start-Demo.ps1
```

This opens `http://127.0.0.1:5200` (loopback only, because it starts and kills processes). **Reset demo** starts fresh: simulators and broker in Docker, then the configuration service and the gateway. No backend: the console reads Mosquitto itself with a persistent QoS 1 Sparkplug subscription, like MQTTX.

- **Data path:** Start, Stop and Crash for every component; Start, Stop, Restart and Crash for each fridge simulator and the broker.
- **Fridges:** door, cooling fault and pause. The console runs a small thermal model and writes it into each simulator over Modbus, the way sensors update a PLC.
- **Device registry tab:** add, enable, disable or remove devices in the configuration service, and watch the gateway follow.
- **Resilience test:** crashes Mosquitto, the gateway, the configuration service and a simulator, then checks every reading the gateway polled arrived on Mosquitto exactly once.
- **Device cards, chart and missing/duplicate counts:** rebuilt from the Sparkplug messages on Mosquitto, using each reading's DeviceSeq.
- **On the wire:** decoded Sparkplug traffic on Mosquitto, plus the logs of each process.

## Edge only, up to Mosquitto

```powershell
./scripts/Start-IoT.ps1
```

This starts the simulators and the configuration service, and runs the gateway in the terminal. The simulators start with zeroed registers: write values with a Modbus tool (Modbus Poll, QModMaster) or use the demo console.

In MQTTX, connect to `127.0.0.1:8883` with TLS, trust `broker/certs/broker.crt`, use a unique client ID, and subscribe to `spBv1.0/warehouse-01/#` before starting the gateway. To decode payloads, import `schemas/sparkplug_b.proto` and select `org.eclipse.tahu.protobuf.Payload`.

## Reliability behaviour

- **Automatic reconnect:** the gateway retries Mosquitto with backoff (2 s doubling to 30 s), then sends a new NBIRTH and a DBIRTH per online device.
- **Data restored after disconnects:** every polled reading is committed to the SQLite outbox first; after a reconnect the backlog is replayed in order, marked `is_historical`, and each row is marked sent only after the broker's acknowledgement.
- **Source timestamps:** a reading carries the time the poll request was sent, and keeps it through buffering and replay.
- **Storage failures:** the affected poller pauses and retries the same sample or offline event every second. Storage errors do not count as device failures. One pending sample stays in memory; stopping before it can be saved logs a warning, and that sample will be lost.
- **Data quality:** each value is Good, Bad or Stale, using the Sparkplug `Quality` metric property (192, 0, 500); a good value carries no property. A failed poll on an online device produces a sample with every value Bad (a null metric), until the third failure reports the device offline (DDEATH), after which its values count as Stale. A float register holding NaN or infinity is reported Bad on its own.
- **Graceful shutdown:** SIGTERM (docker stop, Kubernetes, systemd) and Ctrl+C stop polling, let the current publish finish, send NDEATH and disconnect, with a 15-second limit. Committed readings remain in the outbox if the limit is hit, and the broker publishes the NDEATH Will.
- **Not bounded yet:** the outbox has no size limit; a long outage grows it until the disk fills.

## Checks and limits

```powershell
dotnet run --project tests/IoT.Checks
```

- **Polling gaps:** while the gateway is down, nothing is read. A poller also pauses while its pending sample cannot be saved; readings are durable only after the SQLite commit.
- **Modbus security:** Modbus has no authentication or encryption; keep devices on an isolated network. The configuration API and Mosquitto have no authentication yet.
- **Not yet supported:** Modbus RTU (serial), byte-swapped (as opposed to word-swapped) registers, 64-bit values, strings, and Sparkplug metric aliases.
- **Sparkplug deviations:** data uses QoS 1, and the demo console subscribes with a persistent session.
- **Console view resets with the console:** after a console restart it counts from the readings it receives next, so earlier readings are not in its totals.
- **Backend:** `Application.Api` stays in the repository but is outside this flow; its checks still expect the fridge metric names.
