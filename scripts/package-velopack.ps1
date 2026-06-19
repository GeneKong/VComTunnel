param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    [string]$PackId = "VComTunnel",
    [string]$PackTitle = "VComTunnel",
    [string]$OutputRoot = "",
    [string]$PackDir = "",
    [string]$MainExe = "",
    [string]$DependencyArchiveRoot = "",
    [switch]$SkipBundledDependencies,
    [switch]$Restore,
    [switch]$FrameworkDependent,
    [switch]$Msi,
    [ValidateSet("PerUser", "PerMachine", "Either")]
    [string]$InstallLocation = "PerUser"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\velopack"
}

if ($Version -notmatch '^\d+\.\d+\.\d+([\-+][0-9A-Za-z\-.]+)?$') {
    throw "Velopack requires a SemVer2 package version such as 0.1.0 or 0.1.0-beta. Provided: $Version"
}

function Get-TargetOs {
    param([string]$Runtime)

    if ($Runtime.StartsWith("win", [StringComparison]::OrdinalIgnoreCase)) {
        return "win"
    }

    if ($Runtime.StartsWith("linux", [StringComparison]::OrdinalIgnoreCase)) {
        return "linux"
    }

    if ($Runtime.StartsWith("osx", [StringComparison]::OrdinalIgnoreCase)) {
        return "osx"
    }

    throw "Unsupported runtime '$Runtime'. Expected a RID beginning with win, linux, or osx."
}

function Invoke-Vpk {
    param([string[]]$Arguments)

    $globalVpk = Get-Command "vpk" -ErrorAction SilentlyContinue
    if ($globalVpk) {
        & $globalVpk.Source @Arguments
        return
    }

    $toolManifest = Join-Path $repoRoot ".config\dotnet-tools.json"
    if (-not (Test-Path $toolManifest)) {
        throw "Velopack CLI was not found. Install it with 'dotnet tool install -g vpk --version 1.2.0'."
    }

    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool restore failed. Restore the local vpk tool or install vpk globally."
    }

    & dotnet tool run vpk -- @Arguments
}

$targetOs = Get-TargetOs -Runtime $Runtime
$resolvedPackDir = $PackDir
$resolvedMainExe = $MainExe

if ([string]::IsNullOrWhiteSpace($resolvedPackDir)) {
    if ($targetOs -ne "win") {
        throw "The current WPF GUI can only be published for Windows. For future Avalonia builds, publish that app for '$Runtime' first and pass -PackDir and -MainExe to this script."
    }

    $stagingRoot = Join-Path $OutputRoot "staging"
    $releaseArgs = @{
        Configuration = $Configuration
        Runtime = $Runtime
        Version = $Version
        OutputRoot = $stagingRoot
    }

    if (-not [string]::IsNullOrWhiteSpace($DependencyArchiveRoot)) {
        $releaseArgs.DependencyArchiveRoot = $DependencyArchiveRoot
    }

    if ($SkipBundledDependencies) {
        $releaseArgs.SkipBundledDependencies = $true
    }

    if ($Restore) {
        $releaseArgs.Restore = $true
    }

    if ($FrameworkDependent) {
        $releaseArgs.FrameworkDependent = $true
    }

    & (Join-Path $PSScriptRoot "package-release.ps1") @releaseArgs
    if ($LASTEXITCODE -ne 0) {
        throw "package-release.ps1 failed with exit code $LASTEXITCODE."
    }

    $packageFlavor = if ($FrameworkDependent) { "framework-dependent" } else { "portable" }
    $resolvedPackDir = Join-Path $stagingRoot "VComTunnel-$Version-$Runtime-$packageFlavor"
    $resolvedMainExe = "VComTunnel.Gui.exe"
}

if (-not (Test-Path $resolvedPackDir)) {
    throw "PackDir does not exist: $resolvedPackDir"
}

if ([string]::IsNullOrWhiteSpace($resolvedMainExe)) {
    throw "MainExe is required when using an explicit PackDir."
}

$velopackOutput = Join-Path $OutputRoot $Runtime
New-Item -ItemType Directory -Force -Path $velopackOutput | Out-Null

$iconPath = Join-Path $repoRoot "src\VComTunnel.Gui\Assets\app.ico"
$vpkArgs = @(
    "[$targetOs]",
    "pack",
    "--packId", $PackId,
    "--packTitle", $PackTitle,
    "--packVersion", $Version,
    "--packDir", (Resolve-Path -LiteralPath $resolvedPackDir).Path,
    "--mainExe", $resolvedMainExe,
    "--runtime", $Runtime,
    "--outputDir", $velopackOutput
)

if ($targetOs -eq "win" -and (Test-Path $iconPath)) {
    $vpkArgs += @("--icon", $iconPath)
}

if ($Msi) {
    if ($targetOs -ne "win") {
        throw "-Msi is only valid for Windows Velopack packages."
    }

    $vpkArgs += @("--msi", "--instLocation", $InstallLocation)
}

Invoke-Vpk -Arguments $vpkArgs
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    throw "vpk pack failed with exit code $exitCode."
}

Write-Host "Velopack releases: $velopackOutput"
