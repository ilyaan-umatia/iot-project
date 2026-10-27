# Runs the edge only, up to Mosquitto: configuration service and the gateway. Devices are registered separately.
# No backend, so the gateway does not wait for a primary host. Watch the data in MQTTX / MQTT Explorer.
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$logDirectory = Join-Path $workspace 'logs'
$services = @()
Push-Location $workspace
try {
    if (-not $NoBuild) {
        dotnet build IoT.sln --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    # Through cmd so Windows PowerShell does not treat docker's progress output on stderr as an error.
    cmd /c 'docker compose up -d --build --wait --remove-orphans 2>&1'
    if ($LASTEXITCODE -ne 0) { throw 'Broker or simulator startup failed. Is Docker Desktop running?' }
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

    function Start-Service([string]$name, [string[]]$arguments = @()) {
        $assembly = Join-Path $workspace "src/EdgeGateway/$name/bin/Debug/net8.0/$name.dll"
        Start-Process -FilePath (Get-Command dotnet).Source `
            -ArgumentList (@('"' + $assembly + '"') + $arguments) -WorkingDirectory $workspace `
            -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $logDirectory "$name.log") `
            -RedirectStandardError (Join-Path $logDirectory "$name.error.log")
    }

    $services += Start-Service 'EdgeGateway.Configuration'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ($true) {
        try { Invoke-WebRequest -Uri 'http://127.0.0.1:5300/health' -UseBasicParsing -TimeoutSec 1 | Out-Null; break } catch { }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Configuration service did not start. Check logs/.' }
        Start-Sleep -Milliseconds 300
    }
    Write-Output 'Edge running: configuration http://127.0.0.1:5300, gateway -> Mosquitto :8883. Logs: logs/'
    Write-Output 'Register devices with POST http://127.0.0.1:5300/devices (or the demo console), e.g. ModSim on port 502.'
    Write-Output 'The gateway runs below (no backend: an empty HostId, so it publishes without waiting for one). Press Ctrl+C to stop everything.'
    dotnet (Join-Path $workspace 'src/EdgeGateway/EdgeGateway.Gateway/bin/Debug/net8.0/EdgeGateway.Gateway.dll') '--Sparkplug:HostId=' |
        Tee-Object -FilePath (Join-Path $logDirectory 'EdgeGateway.Gateway.log')
}
finally {
    foreach ($service in $services) {
        if ($null -ne $service -and -not $service.HasExited) {
            Stop-Process -Id $service.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Pop-Location
}
