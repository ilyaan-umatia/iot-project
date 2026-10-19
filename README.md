# IoT Project

Requires .NET 8 SDK, Docker Desktop and MQTTX.

```text
Simulator(s) → Edge Mosquitto (:1883) → EdgeGateway.DataAcquisition (intake)
    → NetMQ (:5555) → EdgeGateway.BrokerMqtt [SQLite outbox → Sparkplug B]
    → Mosquitto over TLS (:8883) → MQTTX
```

The backend is temporarily outside the active flow. The simulator and edge broker remain as the reading source until the acquisition layer is ready.

`EdgeGateway.DataAcquisition` keeps the existing edge intake. `EdgeGateway.BrokerMqtt` runs separately and owns the upstream MQTT connection, Sparkplug lifecycle and SQLite outbox. NetMQ Request/Reply connects them; the reply confirms the outbox commit before Gateway acknowledges the edge MQTT message.

## Configuration

Create root `appsettings.json` (ignored by Git). These are the minimum settings; the settings classes provide the other defaults:

```json
{
  "Sparkplug": { "GroupId": "warehouse-01", "EdgeNodeId": "gateway-01", "HostId": "" },
  "Mqtt": { "Host": "127.0.0.1", "Port": 8883, "ClientId": "gateway-01", "UseTls": true, "BrokerCertificatePath": "certs/broker.crt" }
}
```

Keep `HostId` empty for this flow. A configured HostId makes the gateway wait for that Primary Host's online STATE. MQTTX does not provide that role.

Both applications default to `NetMq:Endpoint = tcp://127.0.0.1:5555`. EdgeGateway.BrokerMqtt binds; EdgeGateway.DataAcquisition connects. Override that setting in root appsettings.json if the port is occupied. Keep this internal connection local.

When moving an existing setup, keep `Mqtt:StateDirectory` and `Gateway:DatabasePath` pointing to the existing counter and outbox; EdgeGateway.BrokerMqtt owns these now. Without explicit paths, state defaults to the application's output directory. Stop the old combined application before running the split projects. EdgeGateway.DataAcquisition keeps the existing `gateway-01-intake` identity, and EdgeGateway.BrokerMqtt keeps `gateway-01`.

## Run manually

From the project folder:

```powershell
./scripts/New-LocalBrokerCertificate.ps1 # first setup only; reuse existing valid certificates
docker compose up -d
```

In MQTTX, connect to `127.0.0.1:8883` with TLS enabled, use `broker/certs/broker.crt` as the trusted certificate, and use a unique client ID (not `gateway-01`). Subscribe to `spBv1.0/warehouse-01/#` before starting the gateway. For Protobuf decoding, import `schemas/sparkplug_b.proto` and select `org.eclipse.tahu.protobuf.Payload`.

Run each application in a separate terminal:

```powershell
dotnet run --project src/EdgeGateway/EdgeGateway.BrokerMqtt
dotnet run --project src/EdgeGateway/EdgeGateway.DataAcquisition
# Wait for "Intake subscribed", then start each simulator:
dotnet run --project src/Device.Simulator -- --Simulator:DeviceId=fridge-01
dotnet run --project src/Device.Simulator -- --Simulator:DeviceId=fridge-02
dotnet run --project src/Device.Simulator -- --Simulator:DeviceId=fridge-03
```

On empty brokers, the gateway must subscribe before the simulators publish so its edge session can hold readings during a gateway outage.

Optional: after subscribing in MQTTX, `./scripts/Start-IoT.ps1` starts the brokers, both applications and one simulator. It does not start the backend. Launcher cleanup force-stops both applications; use manual terminals and type `stop` for graceful shutdown.

## Check in MQTTX

- NBIRTH/DBIRTH announce the node and devices. NDATA reports gateway uptime; DDATA carries readings.
- Type `stop` in a simulator to see DDEATH; restart it to see DBIRTH. EdgeGateway.BrokerMqtt `stop` publishes NDEATH. Force-stopping it lets Mosquitto publish its NDEATH Last Will once the loss is detected.
- Stop EdgeGateway.BrokerMqtt: Gateway waits for confirmation and the edge session holds pending readings. Restart it to resume. Stop EdgeGateway.DataAcquisition: EdgeGateway.BrokerMqtt can still drain its outbox; new readings wait at the edge broker until intake returns. Stopping intake alone does not publish NDEATH or mark all devices offline.
- Stop the upstream broker while simulators continue: the outbox buffers readings. On reconnect, EdgeGateway.BrokerMqtt sends births and replays queued data with historical markers on DDATA.
- NCMD/Rebirth handling remains available in EdgeGateway.BrokerMqtt. Automatic host requests are absent while the backend is disconnected. Subscribe before EdgeGateway.BrokerMqtt startup to capture births; for a late MQTTX connection, restart EdgeGateway.BrokerMqtt manually to see fresh births.

The outbox marks a message sent after the broker acknowledges it. This does not confirm that MQTTX or another subscriber received or processed it. MQTTX being disconnected does not pause publishing.

## Preserved backend tools

`Application.Api`, `Demo.Control`, `Start-Demo.ps1` and `Test-Resilience.ps1` are retained for the earlier backend flow. They are not part of this demo. The old dashboard starts the backend, and the resilience script resets broker volumes; do not use them for this workflow. Returning to that flow also requires restoring a matching Primary Host ID.

## Checks and existing limits

```powershell
dotnet run --project tests/IoT.Checks
```

Existing Sparkplug deviations remain: data uses QoS 1 and the retained backend uses a persistent MQTT session. Outbox size limits, clock drift and metric aliases are not handled. A crash after broker acceptance but before marking a row sent can cause redelivery; a DBIRTH reading can appear twice in backend history.
