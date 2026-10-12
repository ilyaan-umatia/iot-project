# IoT Project

Requires .NET 8 SDK, Docker Desktop and Git for Windows.

Add your settings and Azure connection strings to root `appsettings.json` (not included in Git).

Run from the project folder:

```powershell
./scripts/New-LocalBrokerCertificate.ps1 # first run only
./scripts/Start-IoT.ps1
```

Or start Mosquitto, then run each application in a separate terminal:

```powershell
docker compose up -d
dotnet run --project src/Application.Api
dotnet run --project src/Cloud.Forwarder
# Wait for "Cloud forwarder subscribed", then start the node:
dotnet run --project src/Device.Gateway
```

The forwarder runs independently of the node and backend. Keep it running when stopping the node.
If the forwarder starts late or misses births after a broker restart, restart the node to synchronize; automatic Rebirth is not implemented.

The launcher starts all three applications and stops its services when the node exits. Use separate terminals to test independent restarts.
