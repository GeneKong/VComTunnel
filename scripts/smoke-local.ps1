param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoBuild
)

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

function Get-FreeLoopbackUrl {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse('127.0.0.1'), 0)
    try {
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        return "http://127.0.0.1:$port"
    }
    finally {
        $listener.Stop()
    }
}

function Invoke-Dotnet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Invoke-DotnetCapture {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE`n$($output -join "`n")"
    }

    return $output
}

$root = Resolve-Path "$PSScriptRoot\.."
$smokeHome = Join-Path $env:TEMP ("VComTunnelSmoke-" + [guid]::NewGuid().ToString('n'))
$base = Get-FreeLoopbackUrl
$serviceOut = Join-Path $smokeHome "service.out.log"
$serviceErr = Join-Path $smokeHome "service.err.log"
$oldHome = $env:VCOMTUNNEL_HOME
$oldServiceUrl = $env:VCOMTUNNEL_SERVICE_URL
$service = $null

New-Item -ItemType Directory -Force -Path $smokeHome | Out-Null

try {
    $env:VCOMTUNNEL_HOME = $smokeHome
    $env:VCOMTUNNEL_SERVICE_URL = $base

    if (!$NoBuild) {
        Invoke-Dotnet @('build', "$root\VComTunnel.sln", '-c', $Configuration, '--no-restore')
    }

    $serviceProject = "$root\src\VComTunnel.Service\VComTunnel.Service.csproj"
    $service = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList @('run', '-c', $Configuration, '--project', $serviceProject, '--no-build', '--', '--console') `
        -RedirectStandardOutput $serviceOut `
        -RedirectStandardError $serviceErr `
        -WindowStyle Hidden `
        -PassThru

    $deadline = (Get-Date).AddSeconds(20)
    $status = $null
    do {
        if ($service.HasExited) {
            $stdout = if (Test-Path $serviceOut) { Get-Content $serviceOut -Raw } else { "" }
            $stderr = if (Test-Path $serviceErr) { Get-Content $serviceErr -Raw } else { "" }
            throw "Service exited before readiness. stdout:`n$stdout`nstderr:`n$stderr"
        }

        try {
            $status = Invoke-RestMethod -Uri "$base/api/status" -TimeoutSec 2
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    } while ((Get-Date) -lt $deadline)

    if ($null -eq $status) {
        $stdout = if (Test-Path $serviceOut) { Get-Content $serviceOut -Raw } else { "" }
        $stderr = if (Test-Path $serviceErr) { Get-Content $serviceErr -Raw } else { "" }
        throw "Service did not become ready at $base. stdout:`n$stdout`nstderr:`n$stderr"
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

    $kmdf = Invoke-RestMethod -Uri "$base/api/mappings/driver-prototype/start" -Method Post -TimeoutSec 5
    if ($kmdf.state -notin @('unsupported', 'faulted')) {
        throw "Expected driver-prototype to be unsupported or faulted, got $($kmdf.state)"
    }

    $logs = Invoke-RestMethod -Uri "$base/api/logs" -TimeoutSec 5
    if ($logs.Count -lt 2) { throw "Expected diagnostic logs." }

    $cliProject = "$root\src\VComTunnel.Cli\VComTunnel.Cli.csproj"
    $cliMappings = Invoke-DotnetCapture @('run', '-c', $Configuration, '--project', $cliProject, '--no-build', '--', 'mappings')
    if (($cliMappings -join "`n") -notmatch 'invalid-backing-port') { throw "CLI mappings did not show invalid-backing-port." }
    $cliStatus = Invoke-DotnetCapture @('run', '-c', $Configuration, '--project', $cliProject, '--no-build', '--', 'status')
    if (($cliStatus -join "`n") -notmatch 'configPath') { throw "CLI status did not return service status." }
    $cliLogs = Invoke-DotnetCapture @('run', '-c', $Configuration, '--project', $cliProject, '--no-build', '--', 'logs')
    $cliLogsText = $cliLogs -join "`n"
    if ($cliLogsText -notmatch 'timestamp' -or $cliLogsText -notmatch 'message') {
        throw "CLI logs did not return smoke service logs.`n$cliLogsText"
    }

    Write-Host "Smoke PASS url=$base home=$smokeHome mappings=$($saved.Count) logs=$($logs.Count)"
}
finally {
    if ($service) {
        Stop-SmokeProcessTree -ProcessId $service.Id
    }

    $env:VCOMTUNNEL_HOME = $oldHome
    $env:VCOMTUNNEL_SERVICE_URL = $oldServiceUrl
    Remove-Item -Recurse -Force -Path $smokeHome -ErrorAction SilentlyContinue
}
