param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSEdition -eq "Core" -and $IsWindows) {
    $environment = [System.Environment]::GetEnvironmentVariables("Process")
    $pathKeys = @($environment.Keys | Where-Object {
            [string]::Equals([string]$_, "PATH", [System.StringComparison]::OrdinalIgnoreCase)
        })

    if ($pathKeys.Count -gt 1) {
        $windowsPowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
        if (Test-Path -LiteralPath $windowsPowerShell) {
            & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Configuration $Configuration -Platform $Platform
            exit $LASTEXITCODE
        }
    }
}

function Normalize-ProcessPathVariable {
    $environment = [System.Environment]::GetEnvironmentVariables("Process")
    $pathKeys = @($environment.Keys | Where-Object {
            [string]::Equals([string]$_, "PATH", [System.StringComparison]::OrdinalIgnoreCase)
        })

    if ($pathKeys.Count -le 1) {
        return
    }

    $pathValue = $pathKeys |
        ForEach-Object { [string]$environment[$_] } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1

    foreach ($pathKey in $pathKeys) {
        [System.Environment]::SetEnvironmentVariable([string]$pathKey, $null, "Process")
    }

    if (-not [string]::IsNullOrWhiteSpace($pathValue)) {
        [System.Environment]::SetEnvironmentVariable("Path", $pathValue, "Process")
    }
}

Normalize-ProcessPathVariable

$projectPath = Join-Path $PSScriptRoot "VComTunnel.Serial.vcxproj"
$msbuildCandidates = @(
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
)

$msbuild = $msbuildCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $msbuild) {
    throw "MSBuild was not found. Install Visual Studio 2022 with WDK integration."
}

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Driver project was not found: $projectPath"
}

Write-Host "Building VComTunnel.Serial"
Write-Host "Project: $projectPath"
Write-Host "MSBuild: $msbuild"
Write-Host "Configuration: $Configuration"
Write-Host "Platform: $Platform"
Write-Host ""

& $msbuild $projectPath /m /t:Build /p:Configuration=$Configuration /p:Platform=$Platform
if ($LASTEXITCODE -ne 0) {
    throw "Driver build failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Driver build completed."
Write-Host "Search output:"
Get-ChildItem -Path $PSScriptRoot -Recurse -Include "VComTunnel.Serial.sys","VComTunnel.Serial.inf","VComTunnel.Serial.cat" -ErrorAction SilentlyContinue |
    Sort-Object FullName |
    Select-Object FullName
