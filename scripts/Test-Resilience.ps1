# Kills and restarts every component in turn, then checks that the backend received every reading once.
param([int]$StepSeconds = 15, [switch]$NoBuild)

$ErrorActionPreference = 'Continue' # docker writes progress to stderr
function Invoke-Docker { $output = & docker @args 2>&1; if ($LASTEXITCODE -ne 0) { $output | Write-Output; throw "docker $args failed." } }
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$run = Join-Path $workspace ('logs/resilience-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$dotnet = (Get-Command dotnet).Source
$settings = Get-Content -LiteralPath (Join-Path $workspace 'appsettings.json') -Raw | ConvertFrom-Json
$apiUrl = if ($settings.Urls) { $settings.Urls.Split(';')[0].TrimEnd('/') } else { 'http://localhost:5114' }
$arguments = @{
    'Application.Api' = @("--Storage:DatabasePath=$run/backend.db")
    'Device.Gateway' = @("--Gateway:DatabasePath=$run/outbox.db")
    'Device.Simulator' = @('--Simulator:ReadingIntervalSeconds=2', "--Simulator:StateDirectory=$run/devices")
}
$processes = @{}
$launches = @{}

function Start-Component([string]$name) {
    $launches[$name] = 1 + [int]$launches[$name]
    $assembly = Join-Path $workspace "src/$name/bin/Debug/net8.0/$name.dll"
    $processes[$name] = Start-Process -FilePath $dotnet -ArgumentList (@("`"$assembly`"") + $arguments[$name]) `
        -WorkingDirectory $workspace -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$run/$name.$($launches[$name]).log" -RedirectStandardError "$run/$name.$($launches[$name]).error.log"
}
function Stop-Component([string]$name) {
    Stop-Process -Id $processes[$name].Id -Force
    $processes[$name].WaitForExit()
}
function Wait-IntakeSubscribed {
    # The edge broker only keeps readings for the gateway once its session exists, so devices start after it.
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Select-String -Path "$run/Device.Gateway.$($launches['Device.Gateway']).log" -Pattern '^Intake subscribed' -Quiet)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Gateway intake did not subscribe.' }
        Start-Sleep -Milliseconds 200
    }
}
function Step([string]$message) {
    Write-Output ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $message)
    Start-Sleep -Seconds $StepSeconds
}

Push-Location $workspace
try {
    if (-not $NoBuild) {
        dotnet build IoT.sln --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    # Start from empty broker volumes so sessions queued by earlier runs do not mix into this one.
    Invoke-Docker compose down -v
    Invoke-Docker compose up -d --wait

    Start-Component 'Application.Api'
    Start-Sleep -Seconds 5
    Start-Component 'Device.Gateway'
    Wait-IntakeSubscribed
    Start-Component 'Device.Simulator'
    Step 'All components running.'

    Stop-Component 'Application.Api'; Step 'Backend killed: gateway should end its session and buffer.'
    Start-Component 'Application.Api'; Step 'Backend restarted: buffered readings replay as historical.'
    Stop-Component 'Device.Gateway'; Step 'Gateway killed: edge broker should hold readings.'
    Start-Component 'Device.Gateway'; Step 'Gateway restarted: outbox and edge session drain.'
    Invoke-Docker restart dotnet-mqtt-demo-mosquitto-1; Step 'Upstream broker restarted.'
    Invoke-Docker restart dotnet-mqtt-demo-edge-mosquitto-1; Step 'Edge broker restarted: simulator queues and resends.'
    Stop-Component 'Device.Simulator'; Step 'Simulator killed: expect DDEATH.'
    Start-Component 'Device.Simulator'; Step 'Simulator restarted: expect DBIRTH and continued numbering.'
    Stop-Component 'Device.Simulator'; Step 'Simulator stopped: letting the pipeline drain.'

    $sent = Get-ChildItem "$run/Device.Simulator.*.log" | Get-Content |
        Select-String '^Sent #(\d+):' | ForEach-Object { [long]$_.Matches[0].Groups[1].Value } | Sort-Object -Unique
    $events = @()
    $before = $null
    while ($true) {
        $uri = "$apiUrl/devices/fridge-01/history?limit=500" + $(if ($before) { "&before=$before" } else { '' })
        $page = @((Invoke-RestMethod -Uri $uri -ErrorAction Stop) | ForEach-Object { $_ })
        if ($page.Count -eq 0) { break }
        $events += $page
        $before = $page[-1].id
    }
    $readings = $events | Where-Object { $_.event.messageType -in 'DBIRTH', 'DDATA' -and $null -ne $_.event.metrics.DeviceSeq }
    $received = $readings | ForEach-Object { [long]$_.event.metrics.DeviceSeq.number }
    $missing = @($sent | Where-Object { $_ -notin $received })
    $duplicates = @($readings | Where-Object { $_.event.messageType -eq 'DDATA' } |
        Group-Object { $_.event.metrics.DeviceSeq.number } | Where-Object Count -gt 1)
    $historical = @($readings | Where-Object { $_.event.historical }).Count
    Write-Output "Readings sent: $($sent.Count) | received: $(@($received | Sort-Object -Unique).Count) | historical: $historical | missing: $($missing.Count) | duplicate DDATA: $($duplicates.Count)"
    if ($missing.Count -gt 0) { Write-Output "Missing: $($missing -join ', ')" }
    if ($missing.Count -gt 0 -or $duplicates.Count -gt 0) { throw 'Data was lost or duplicated.' }
    Write-Output "PASS: no readings lost or duplicated. Logs: $run"
}
finally {
    foreach ($process in $processes.Values) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    Pop-Location
}
