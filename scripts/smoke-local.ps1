$ErrorActionPreference = 'Stop'

$root = Resolve-Path "$PSScriptRoot\.."
$smokeHome = Join-Path $env:TEMP ("VComTunnelSmoke-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $smokeHome | Out-Null

dotnet build "$root\VComTunnel.sln" | Write-Host

$command = @"
`$env:VCOMTUNNEL_HOME = '$smokeHome'
dotnet run --project '$root\src\VComTunnel.Service\VComTunnel.Service.csproj' --no-build -- --console
"@

$service = Start-Process -FilePath "powershell.exe" -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $command) -WindowStyle Hidden -PassThru

try {
    $base = 'http://127.0.0.1:44817'
    $deadline = (Get-Date).AddSeconds(20)
    do {
        try {
            Invoke-RestMethod -Uri "$base/api/status" -TimeoutSec 2 | Out-Null
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    } while ((Get-Date) -lt $deadline)

    $mappings = @(
        @{
            id = 'missing-deps'
            name = 'Missing deps check'
            backend = 'com0comHub4com'
            visiblePort = 'COM12'
            backingPort = 'CNCB12'
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

    $missing = Invoke-RestMethod -Uri "$base/api/mappings/missing-deps/start" -Method Post -TimeoutSec 5
    if ($missing.state -ne 'faulted') { throw "Expected missing-deps to fault, got $($missing.state)" }

    $kmdf = Invoke-RestMethod -Uri "$base/api/mappings/driver-prototype/start" -Method Post -TimeoutSec 5
    if ($kmdf.state -ne 'unsupported') { throw "Expected driver-prototype to be unsupported, got $($kmdf.state)" }

    $logs = Invoke-RestMethod -Uri "$base/api/logs" -TimeoutSec 5
    if ($logs.Count -lt 2) { throw "Expected diagnostic logs." }

    $cliProject = "$root\src\VComTunnel.Cli\VComTunnel.Cli.csproj"
    $cliMappings = dotnet run --project $cliProject --no-build -- mappings
    if (($cliMappings -join "`n") -notmatch 'missing-deps') { throw "CLI mappings did not show missing-deps." }
    $cliStatus = dotnet run --project $cliProject --no-build -- status
    if (($cliStatus -join "`n") -notmatch 'configPath') { throw "CLI status did not return service status." }
    $cliLogs = dotnet run --project $cliProject --no-build -- logs
    if (($cliLogs -join "`n") -notmatch 'KMDF backend scaffold') { throw "CLI logs did not return service logs." }

    Write-Host "Smoke PASS home=$smokeHome mappings=$($saved.Count) logs=$($logs.Count)"
}
finally {
    if (!$service.HasExited) {
        Stop-Process -Id $service.Id -Force
    }
}
