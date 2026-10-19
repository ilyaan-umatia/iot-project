param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$logDirectory = Join-Path $workspace 'logs'
$services = @()
Push-Location $workspace
try {
    if (-not (Test-Path -LiteralPath (Join-Path $workspace 'appsettings.json'))) {
        throw 'Create root appsettings.json using the settings in README.md.'
    }
    $settings = Get-Content -LiteralPath (Join-Path $workspace 'appsettings.json') -Raw | ConvertFrom-Json
    if (-not [string]::IsNullOrWhiteSpace($settings.Sparkplug.HostId)) {
        throw 'Set Sparkplug:HostId to an empty string in appsettings.json to run without the backend.'
    }
    if (-not $NoBuild) {
        dotnet build IoT.sln --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    # Through cmd so Windows PowerShell does not treat docker's progress output on stderr as an error.
    cmd /c 'docker compose up -d --wait 2>&1'
    if ($LASTEXITCODE -ne 0) { throw 'Broker startup failed.' }
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

    function Start-Service([string]$name) {
        $sourceRoot = if ($name.StartsWith('EdgeGateway.')) { 'src/EdgeGateway' } else { 'src' }
        $assembly = Join-Path $workspace "$sourceRoot/$name/bin/Debug/net8.0/$name.dll"
        Start-Process -FilePath (Get-Command dotnet).Source `
            -ArgumentList @('"' + $assembly + '"') -WorkingDirectory $workspace `
            -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $logDirectory "$name.log") `
            -RedirectStandardError (Join-Path $logDirectory "$name.error.log")
    }

    $publisher = Start-Service 'EdgeGateway.BrokerMqtt'
    $services += $publisher
    $gateway = Start-Service 'EdgeGateway.DataAcquisition'
    $services += $gateway
    # The edge broker only keeps readings for the gateway once its session exists, so devices start after it.
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $gatewayLog = Join-Path $logDirectory 'EdgeGateway.DataAcquisition.log'
    while (-not ((Test-Path -LiteralPath $gatewayLog) -and
        (Select-String -LiteralPath $gatewayLog -Pattern '^Intake subscribed' -Quiet))) {
        if ($gateway.HasExited -or $publisher.HasExited) { throw 'Gateway or EdgeGateway.BrokerMqtt stopped. Check logs/.' }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Gateway intake did not subscribe. Check logs/.' }
        Start-Sleep -Milliseconds 200
    }
    if ($publisher.HasExited) { throw 'EdgeGateway.BrokerMqtt stopped. Check logs/.' }
    Write-Output 'Gateway intake and EdgeGateway.BrokerMqtt started without the backend. Logs: logs/'
    Write-Output 'Running the fridge simulator. Press Ctrl+C to stop everything.'
    dotnet (Join-Path $workspace 'src/Device.Simulator/bin/Debug/net8.0/Device.Simulator.dll') |
        Tee-Object -FilePath (Join-Path $logDirectory 'Device.Simulator.log')
}
finally {
    foreach ($service in $services) {
        if ($null -ne $service -and -not $service.HasExited) {
            Stop-Process -Id $service.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Pop-Location
}
