param(
    [string]$InfPath,
    [switch]$Install
)

$ErrorActionPreference = "Stop"

if (-not $InfPath) {
    $packageInf = Join-Path $PSScriptRoot "x64\Release\VComTunnel.Serial\VComTunnel.Serial.inf"
    $sourceInf = Join-Path $PSScriptRoot "VComTunnel.Serial.inf"
    $InfPath = if (Test-Path -LiteralPath $packageInf) { $packageInf } else { $sourceInf }
}

$packageRoot = Split-Path -Parent $InfPath
$sysPath = Join-Path $packageRoot "VComTunnel.Serial.sys"
$catPath = Get-ChildItem -Path $packageRoot -Filter "*.cat" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName

Write-Host "VComTunnel.Serial KMDF package check"
Write-Host "INF: $InfPath"
Write-Host "SYS: $sysPath"
Write-Host "CAT: $catPath"
Write-Host ""

if (-not (Test-Path -LiteralPath $InfPath)) {
    throw "INF was not found: $InfPath"
}

if (-not (Test-Path -LiteralPath $sysPath) -or -not $catPath -or -not (Test-Path -LiteralPath $catPath)) {
    Write-Host "The installable package was not found. Build and test-sign the WDK driver first."
    Write-Host "Expected files:"
    Write-Host "  VComTunnel.Serial.sys"
    Write-Host "  *.cat"
    Write-Host ""
    Write-Host "Build command:"
    Write-Host "powershell -ExecutionPolicy Bypass -File `"$PSScriptRoot\build-driver.ps1`" -Configuration Release"
    exit 2
}

Write-Host "Package files are present."
Write-Host "Review DESIGN.md and SERVICE_CHANNEL.md before installing on a test machine."
Write-Host ""
Write-Host "Install command:"
Write-Host "pnputil.exe /add-driver `"$InfPath`" /install"

if ($Install) {
    Write-Host ""
    Write-Host "Running pnputil. This must be executed from an elevated PowerShell."
    pnputil.exe /add-driver "$InfPath" /install
}
