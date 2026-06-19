$ErrorActionPreference = 'Stop'

function Stop-SmokeProcessTree {
    param([int]$ProcessId)

    if ($ProcessId -le 0) {
        return
    }

    $children = Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue
    foreach ($child in $children) {
        Stop-SmokeProcessTree -ProcessId $child.ProcessId
    }

    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
}

$root = Resolve-Path "$PSScriptRoot\.."
$smokeHome = Join-Path $env:TEMP ("VComTunnelSmoke-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $smokeHome | Out-Null
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$base = "http://127.0.0.1:$port"

dotnet build "$root\VComTunnel.sln" | Write-Host

$command = @"
`$env:VCOMTUNNEL_HOME = '$smokeHome'
`$env:VCOMTUNNEL_LISTEN_URLS = '$base'
dotnet run --project '$root\src\VComTunnel.Service\VComTunnel.Service.csproj' --no-build -- --console
"@

$service = Start-Process -FilePath "powershell.exe" -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $command) -WindowStyle Hidden -PassThru

try {
    $deadline = (Get-Date).AddSeconds(20)
    $status = $null
    do {
        try {
            if ($service.HasExited) {
                throw "Smoke service exited before it became ready."
            }

            $status = Invoke-RestMethod -Uri "$base/api/status" -TimeoutSec 2
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    } while ((Get-Date) -lt $deadline)

    if ($null -eq $status) {
        throw "Smoke service did not become ready at $base."
    }

    if (!$status.configPath.StartsWith($smokeHome, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Smoke connected to an unexpected service config path: $($status.configPath)"
    }

    $mappings = @(
        @{
            id = 'invalid-backing-port'
            name = 'Invalid backing port check'
            backend = 'com0comHub4com'
            visiblePort = 'COM253'
            backingPort = 'CNCB253'
            host = '127.0.0.1'
            port = 5000
            protocol = 'rfc2217'
            autoStart = $false
            restartOnFailure = $false
        },
        @{
            id = 'driver-prototype'
            name = 'Driver prototype check'
            backend = 'kmdf'
            visiblePort = 'COM22'
            backingPort = $null
            host = '127.0.0.1'
            port = 5000
            protocol = 'rfc2217'
            autoStart = $false
            restartOnFailure = $false
        }
    )

    Invoke-RestMethod -Uri "$base/api/mappings" -Method Put -Body ($mappings | ConvertTo-Json -Depth 5) -ContentType 'application/json' | Out-Null
    $saved = Invoke-RestMethod -Uri "$base/api/mappings" -TimeoutSec 5
    if ($saved.Count -ne 2) { throw "Expected 2 mappings, got $($saved.Count)" }

    $invalidBacking = Invoke-RestMethod -Uri "$base/api/mappings/invalid-backing-port/start" -Method Post -TimeoutSec 5
    if ($invalidBacking.state -ne 'faulted') { throw "Expected invalid-backing-port to fault, got $($invalidBacking.state)" }

    $logs = Invoke-RestMethod -Uri "$base/api/logs" -TimeoutSec 5
    if ($logs.Count -lt 2) { throw "Expected diagnostic logs." }

    $cliProject = "$root\src\VComTunnel.Cli\VComTunnel.Cli.csproj"
    $env:VCOMTUNNEL_SERVICE_URL = $base
    $cliMappings = dotnet run --project $cliProject --no-build -- mappings
    if (($cliMappings -join "`n") -notmatch 'invalid-backing-port') { throw "CLI mappings did not show invalid-backing-port." }
    $cliStatus = dotnet run --project $cliProject --no-build -- status
    if (($cliStatus -join "`n") -notmatch 'configPath') { throw "CLI status did not return service status." }
    $cliLogs = dotnet run --project $cliProject --no-build -- logs
    $cliLogsText = $cliLogs -join "`n"
    if ($cliLogsText -notmatch 'timestamp' -or $cliLogsText -notmatch 'message') { throw "CLI logs did not return service logs." }

    Write-Host "Smoke PASS url=$base home=$smokeHome mappings=$($saved.Count) logs=$($logs.Count)"
}
finally {
    Remove-Item Env:\VCOMTUNNEL_SERVICE_URL -ErrorAction SilentlyContinue
    Stop-SmokeProcessTree -ProcessId $service.Id
    Remove-Item -Recurse -Force -Path $smokeHome -ErrorAction SilentlyContinue
}
