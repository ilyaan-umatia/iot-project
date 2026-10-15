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
    if (-not $NoBuild) {
        dotnet build IoT.sln --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    # Through cmd so Windows PowerShell does not treat docker's progress output on stderr as an error.
    cmd /c 'docker compose up -d --wait 2>&1'
    if ($LASTEXITCODE -ne 0) { throw 'Broker startup failed.' }
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

    function Start-Service([string]$name) {
        $assembly = Join-Path $workspace "src/$name/bin/Debug/net8.0/$name.dll"
        Start-Process -FilePath (Get-Command dotnet).Source `
            -ArgumentList @('"' + $assembly + '"') -WorkingDirectory $workspace `
            -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $logDirectory "$name.log") `
            -RedirectStandardError (Join-Path $logDirectory "$name.error.log")
    }

    # Backend first so its STATE is online before the gateway births.
    $backend = Start-Service 'Application.Api'
    $services += $backend
    $apiUrl = if ($settings.Urls) { $settings.Urls.Split(';')[0].TrimEnd('/') } else { 'http://localhost:5114' }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ($true) {
        if ($backend.HasExited) { throw 'Backend stopped. Check logs/.' }
        try {
            Invoke-WebRequest -Uri ($apiUrl + '/') -UseBasicParsing -TimeoutSec 1 -ErrorAction Stop | Out-Null
            break
        } catch {
        }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Backend did not become ready. Check logs/.' }
        Start-Sleep -Milliseconds 200
    }
    $services += Start-Service 'Device.Gateway'
    # The edge broker only keeps readings for the gateway once its session exists, so devices start after it.
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Select-String -LiteralPath (Join-Path $logDirectory 'Device.Gateway.log') -Pattern '^Intake subscribed' -Quiet)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Gateway intake did not subscribe. Check logs/.' }
        Start-Sleep -Milliseconds 200
    }
    Write-Output "Application started. Dashboard and API: $apiUrl | service logs: logs/"
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
