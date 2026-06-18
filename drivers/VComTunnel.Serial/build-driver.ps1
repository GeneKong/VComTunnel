param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

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
