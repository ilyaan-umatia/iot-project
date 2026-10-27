# IoT Project

Requires .NET 8 SDK and Docker Desktop. MQTTX or MQTT Explorer to watch the broker.

```text
EdgeGateway.Configuration (device registry, REST :5300)
        │ device list (fetched every 10 s, cached locally)
        ▼
Modbus devices ◄── Modbus TCP polling ── EdgeGateway.Gateway [poll → SQLite outbox → Sparkplug B]
(ModSim, PLCs, any Modbus TCP server)               │ MQTT over TLS
                                                    ▼
                                   Mosquitto (:8883) ──► MQTTX / MQTT Explorer, demo console
```

- **Devices:** any Modbus TCP server, such as ModSim or a PLC. Nothing is built in: a device exists only once it is registered in the configuration service, and its values change on the device itself.
- **Configuration service:** stores each device's address, unit ID, poll interval, GPIO pin and register map. `GET/POST/PUT/DELETE /devices`, `GET /config`. It holds configuration only; online/offline comes from the gateway. It starts empty.
- **Gateway (one process):** one poll loop per enabled device. Each reading is numbered (persisted per device) and committed to the SQLite outbox the moment it is polled, so it survives a gateway crash or a broker outage. The publishing loop drains the outbox in order as Sparkplug B (NBIRTH/DBIRTH/DDATA/DDEATH, Rebirth, historical replay) and marks a row sent only after the broker's acknowledgement.
  - After 3 failed polls a device is reported offline (DDEATH); the next good poll brings it back (DBIRTH).
  - Devices added, changed or removed in the registry apply within about 10 seconds; a changed metric list triggers a new DBIRTH.
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

The gateway groups each device's metrics into **frames**: one read request per run of nearby registers of the same type (up to 125 registers or 2000 bits; gaps of up to 8 registers are read through), then decodes each metric from its slice.

Addresses are 0-based. Tools such as ModSim number holding registers from 40001, so register 40001 is address 0 and 40100 is address 99.

Example: registering a ModSim device on port 502 with a temperature in its first holding register:

```bash
curl -X POST http://127.0.0.1:5300/devices -H "Content-Type: application/json" -d '{"id":"modsim-01","enabled":true,"protocol":"ModbusTcp","host":"127.0.0.1","port":502,"unitId":1,"pollIntervalSeconds":3,"description":"ModSim","metrics":[{"name":"Temperature","register":"holding","address":0,"dataType":"int16","scale":0.1,"unit":"°C"}]}'
```

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

This opens `http://127.0.0.1:5200` (loopback only, because it starts and kills processes). **Reset demo** starts fresh: an empty broker, an empty device registry and an empty outbox, then the configuration service and the gateway. Registered devices are deleted too. No backend: the console reads Mosquitto itself with a persistent QoS 1 Sparkplug subscription, like MQTTX.

- **Data path:** one box per registered device with its live polling state, plus Start, Stop and Crash for the configuration service and gateway, and Start, Stop, Restart and Crash for the broker.
- **Devices being polled:** each device's state (polling, failing, offline), endpoint, last reading and last error.
- **Device registry tab:** add, edit, enable, disable or remove devices with a form covering every field, and see what the gateway is doing with each one: state, read frames, raw register values and the last error.
- **Activity tab:** every change in one timeline: registry edits, configuration loads, devices online, failing or offline, births and deaths, broker connection and replay, component actions.
- **Resilience test:** crashes Mosquitto, the gateway and the configuration service, then checks every reading the gateway polled arrived on Mosquitto exactly once.
- **Device cards, chart and missing/duplicate counts:** rebuilt from the Sparkplug messages on Mosquitto, using each reading's DeviceSeq.
- **On the wire:** decoded Sparkplug traffic on Mosquitto, plus the logs of each process.

## Edge only, up to Mosquitto

```powershell
./scripts/Start-IoT.ps1
```

This starts the configuration service and runs the gateway in the terminal. Register devices with the API (see above) or the demo console.

In MQTTX, connect to `127.0.0.1:8883` with TLS, trust `broker/certs/broker.crt`, use a unique client ID, and subscribe to `spBv1.0/warehouse-01/#` before starting the gateway. To decode payloads, import `schemas/sparkplug_b.proto` and select `org.eclipse.tahu.protobuf.Payload`.

## Reliability behaviour

- **Automatic reconnect:** the gateway retries Mosquitto with backoff (2 s doubling to 30 s), then sends a new NBIRTH and a DBIRTH per online device.
- **Data restored after disconnects:** every polled reading is committed to the SQLite outbox first; after a reconnect the backlog is replayed in order, marked `is_historical`, and each row is marked sent only after the broker's acknowledgement.
- **Source timestamps:** a reading carries the time the poll request was sent, and keeps it through buffering and replay.
- **Data quality:** each value is Good, Bad or Stale, using the Sparkplug `Quality` metric property (192, 0, 500); a good value carries no property. A failed poll on an online device produces a sample with every value Bad (a null metric), until the third failure reports the device offline (DDEATH), after which its values count as Stale. A float register holding NaN or infinity is reported Bad on its own.
- **Graceful shutdown:** SIGTERM (docker stop, Kubernetes, systemd) and Ctrl+C stop polling, let the current publish finish, send NDEATH and disconnect, with a 15-second limit. Nothing is lost if the limit is hit: the outbox is on disk and the broker publishes the NDEATH Will.
- **Not bounded yet:** the outbox has no size limit; a long outage grows it until the disk fills.

## Checks and limits

```powershell
dotnet run --project tests/IoT.Checks
```

- **Polling gaps:** while the gateway is down, nothing is read (a gap in sampling, not lost messages). Every reading it did poll is already in the SQLite outbox.
- **Modbus security:** Modbus has no authentication or encryption; keep devices on an isolated network. The configuration API and Mosquitto have no authentication yet.
- **Not yet supported:** Modbus RTU (serial), byte-swapped (as opposed to word-swapped) registers, 64-bit values, strings, and Sparkplug metric aliases.
- **Sparkplug deviations:** data uses QoS 1, and the demo console subscribes with a persistent session.
- **Console view resets with the console:** after a console restart it counts from the readings it receives next, so earlier readings are not in its totals.
- **Backend:** `Application.Api` stays in the repository but is outside this flow; its checks still expect the fridge metric names.
