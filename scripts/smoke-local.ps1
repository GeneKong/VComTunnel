param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

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

    try {
        $deadline = (Get-Date).AddSeconds(20)
        $ready = $false
        do {
            if ($service.HasExited) {
                $stdout = if (Test-Path $serviceOut) { Get-Content $serviceOut -Raw } else { "" }
                $stderr = if (Test-Path $serviceErr) { Get-Content $serviceErr -Raw } else { "" }
                throw "Service exited before readiness. stdout:`n$stdout`nstderr:`n$stderr"
            }

            try {
                Invoke-RestMethod -Uri "$base/api/status" -TimeoutSec 2 | Out-Null
                $ready = $true
                break
            } catch {
                Start-Sleep -Milliseconds 500
            }
        } while ((Get-Date) -lt $deadline)

        if (!$ready) {
            $stdout = if (Test-Path $serviceOut) { Get-Content $serviceOut -Raw } else { "" }
            $stderr = if (Test-Path $serviceErr) { Get-Content $serviceErr -Raw } else { "" }
            throw "Service did not become ready at $base. stdout:`n$stdout`nstderr:`n$stderr"
        }

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
        if ($kmdf.state -notin @('unsupported', 'faulted')) {
            throw "Expected driver-prototype to be unsupported or faulted, got $($kmdf.state)"
        }

        $logs = Invoke-RestMethod -Uri "$base/api/logs" -TimeoutSec 5
        if ($logs.Count -lt 2) { throw "Expected diagnostic logs." }

        $cliProject = "$root\src\VComTunnel.Cli\VComTunnel.Cli.csproj"
        $cliMappings = Invoke-DotnetCapture @('run', '-c', $Configuration, '--project', $cliProject, '--no-build', '--', 'mappings')
        if (($cliMappings -join "`n") -notmatch 'missing-deps') { throw "CLI mappings did not show missing-deps." }
        $cliStatus = Invoke-DotnetCapture @('run', '-c', $Configuration, '--project', $cliProject, '--no-build', '--', 'status')
        if (($cliStatus -join "`n") -notmatch 'configPath') { throw "CLI status did not return service status." }
        $cliLogs = Invoke-DotnetCapture @('run', '-c', $Configuration, '--project', $cliProject, '--no-build', '--', 'logs')
        if (($cliLogs -join "`n") -notmatch 'missing-deps|driver-prototype|KMDF|dependencies') {
            throw "CLI logs did not return smoke service logs.`n$($cliLogs -join "`n")"
        }

        Write-Host "Smoke PASS url=$base home=$smokeHome mappings=$($saved.Count) logs=$($logs.Count)"
    }
    finally {
        if ($service -and !$service.HasExited) {
            Stop-Process -Id $service.Id -Force
        }
    }
}
finally {
    $env:VCOMTUNNEL_HOME = $oldHome
    $env:VCOMTUNNEL_SERVICE_URL = $oldServiceUrl
}
