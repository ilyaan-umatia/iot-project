param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$logDirectory = Join-Path $workspace 'logs'
$backend = $null
$forwarder = $null
$gatewayStarted = $false
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
    docker compose up -d
    if ($LASTEXITCODE -ne 0) { throw 'Broker startup failed.' }
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $assembly = Join-Path $workspace 'src/Application.Api/bin/Debug/net8.0/Application.Api.dll'
    $backendLog = Join-Path $logDirectory 'Application.Api.log'
    $backend = Start-Process -FilePath (Get-Command dotnet).Source `
        -ArgumentList @('"' + $assembly + '"') -WorkingDirectory $workspace `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $backendLog `
        -RedirectStandardError (Join-Path $logDirectory 'Application.Api.error.log')
    $forwarderLog = Join-Path $logDirectory 'Cloud.Forwarder.log'
    $forwarderAssembly = Join-Path $workspace 'src/Cloud.Forwarder/bin/Debug/net8.0/Cloud.Forwarder.dll'
    $forwarder = Start-Process -FilePath (Get-Command dotnet).Source `
        -ArgumentList @('"' + $forwarderAssembly + '"') -WorkingDirectory $workspace `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $forwarderLog `
        -RedirectStandardError (Join-Path $logDirectory 'Cloud.Forwarder.error.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $apiUrl = if ($settings.Urls) { $settings.Urls.Split(';')[0].TrimEnd('/') } else { 'http://localhost:5114' }
    while ($true) {
        if ($backend.HasExited) { throw 'Backend stopped. Check logs/.' }
        if ($forwarder.HasExited) { throw 'Forwarder stopped. Check logs/.' }
        try {
            Invoke-WebRequest -Uri ($apiUrl + '/') -UseBasicParsing -TimeoutSec 1 -ErrorAction Stop | Out-Null
            if (Select-String -LiteralPath $forwarderLog -Pattern '^Cloud forwarder subscribed:' -Quiet) { break }
        } catch {
        }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Backend or forwarder did not become ready. Check logs/.' }
        Start-Sleep -Milliseconds 200
    }
    Write-Output "Application started. API: $apiUrl | service logs: logs/"
    Write-Output 'Gateway commands: offline, online, stop.'
    $gatewayStarted = $true
    $gatewayLog = Join-Path $logDirectory 'Device.Gateway.log'
    dotnet (Join-Path $workspace 'src/Device.Gateway/bin/Debug/net8.0/Device.Gateway.dll') |
        Tee-Object -FilePath $gatewayLog
    if ($LASTEXITCODE -ne 0) { throw 'Gateway stopped with an error.' }
}
finally {
    # Give the independent forwarder time to deliver the final death before stopping launcher-owned services.
    $lastSession = if ($gatewayStarted) {
        Select-String -LiteralPath $gatewayLog -Pattern '^Session bdSeq: (\d+)$' | Select-Object -Last 1
    }
    if ($null -ne $lastSession -and $null -ne $forwarder -and $settings.IoTHub.DeviceConnectionString) {
        $bdSeq = $lastSession.Matches[0].Groups[1].Value
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while (-not $forwarder.HasExited) {
            $death = Select-String -LiteralPath $forwarderLog -Pattern "^Forwarded NDEATH: ([a-f0-9]+) \(bdSeq $bdSeq\)$" |
                Select-Object -Last 1
            if ($null -ne $death) {
                $deathPattern = 'Completed queue message ' + $death.Matches[0].Groups[1].Value
                if (-not $settings.ServiceBus.ConnectionString -or $null -eq $backend -or $backend.HasExited -or
                    (Select-String -LiteralPath $backendLog -Pattern $deathPattern -Quiet)) { break }
            }
            if ([DateTime]::UtcNow -gt $deadline) {
                Write-Warning 'Final NDEATH delivery was not confirmed. Check service logs.'
                break
            }
            Start-Sleep -Milliseconds 200
        }
    }
    foreach ($service in @($forwarder, $backend)) {
        if ($null -ne $service -and -not $service.HasExited) {
            Stop-Process -Id $service.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Pop-Location
}
