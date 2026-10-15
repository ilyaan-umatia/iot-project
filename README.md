# IoT Project

Requires .NET 8 SDK, Docker Desktop and Git for Windows.

```
Device.Simulator ─MQTT─► edge Mosquitto (:1883) ─► Device.Gateway [SQLite outbox → Sparkplug B]
    ─MQTT/TLS─► Mosquitto (:8883) ─► Application.Api (Sparkplug Primary Host, SQLite)
```

- **Device.Simulator** is a fridge. It publishes readings to `edge/{deviceId}/data` at QoS 1. Each reading has a persisted, never-repeating `deviceSeq`. A Last Will on `edge/{deviceId}/status` reports the device offline.
- **Device.Gateway** is the Sparkplug edge node. It commits every reading to its SQLite outbox before acknowledging it, then publishes the outbox in order (NBIRTH/DBIRTH/DDATA/DDEATH, QoS 1). It waits for the primary host's `STATE`, ends its session (NDEATH) while the host is offline, and replays the backlog as `is_historical` when the host returns. It also answers `Node Control/Rebirth` commands.
- **Application.Api** is the Sparkplug Primary Host. It holds a persistent MQTT session, publishes `spBv1.0/STATE/{HostId}`, stores each message before acknowledging it, removes duplicates by event ID, and requests a rebirth when out of sync. Its REST API serves `/gateways`, `/devices` and `/devices/{id}/history`.
- Both brokers keep sessions and queued messages on disk.

Add your settings to the root `appsettings.json` (not included in Git). The defaults in each project's settings classes cover everything else:

```json
{
  "Urls": "http://localhost:5114",
  "Sparkplug": { "GroupId": "warehouse-01", "EdgeNodeId": "gateway-01", "HostId": "iot-host" },
  "Mqtt": { "Host": "127.0.0.1", "Port": 8883, "ClientId": "gateway-01", "UseTls": true, "BrokerCertificatePath": "certs/broker.crt" }
}
```

Run from the project folder:

```powershell
./scripts/New-LocalBrokerCertificate.ps1 # first run only
./scripts/Start-IoT.ps1
```

Or start the brokers, then run each application in a separate terminal, in this order:

```powershell
docker compose up -d
dotnet run --project src/Application.Api
dotnet run --project src/Device.Gateway   # wait for "Intake subscribed"
dotnet run --project src/Device.Simulator -- --Simulator:DeviceId=fridge-01
```

## Demo console

```powershell
./scripts/Start-Demo.ps1
```

This opens `http://127.0.0.1:5200`, a single page that controls and observes everything. It listens on loopback only, because it can start and kill processes on this machine.

- **Data path:** Start, Stop (clean shutdown) and Crash (kill without warning) for the fridge, the gateway and the backend. Start, Stop, Restart and Crash for both broker containers. **Start all** respects dependency order; **Reset demo** wipes broker data and demo state.
- **Gateway buffer:** the outbox's unsent count, the upstream state, the host STATE and the session. The gateway writes these to `status.json` beside its outbox, so they stay visible while the backend is down.
- **Fridge:** open or close the door, start or fix a cooling fault, pause or resume readings. These are sent to the simulator's console input.
- **Resilience test:** crashes and restarts each component in turn, then checks the backend has every reading the fridge sent, exactly once.
- **Backend view:** readings stored, missing, duplicates, replayed as historical, the device state and a temperature chart. These are read through the console, so the last known data stays on screen while the backend is down.
- **On the wire:** live, decoded messages from Mosquitto (Sparkplug, STATE, NCMD) and from the edge broker. A Logs tab shows each component's console output.

Demo state lives in `logs/demo/state`, separate from the projects' default state folders.

On the very first run against empty brokers, start the gateway before any device. The edge broker only holds readings for the gateway once its session exists.

## Resilience test

`./scripts/Test-Resilience.ps1` resets the broker volumes, then kills and restarts each component in turn: backend, gateway, upstream broker, edge broker, simulator. It then checks through the API that every reading arrived exactly once. Logs are written to `logs/resilience-*`.

## Known PoC limits

- Data uses QoS 1 and the host uses a persistent session. This deviates from Sparkplug 3.0, which specifies QoS 0 and clean sessions, so that loss is covered at the MQTT level as well.
- Not handled: outbox size caps, broker queue limits beyond 100000 messages, edge disk redundancy, clock drift, report-by-exception and metric aliases.
- A reading whose first delivery was a DBIRTH can appear twice in history if the gateway crashes before marking it sent. DDATA readings are deduplicated by `deviceSeq`.
