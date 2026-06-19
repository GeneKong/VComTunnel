param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.0.0-dev",
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

if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.\-+]*$') {
    throw "Release version may only contain letters, numbers, dot, dash, and plus. Provided: $Version"
}

function ConvertTo-VelopackVersion {
    param([string]$ReleaseVersion)

    if ($ReleaseVersion -match '^\d+\.\d+\.\d+([\-+][0-9A-Za-z\-.]+)?$') {
        return $ReleaseVersion
    }

    if ($ReleaseVersion -match '^(\d+\.\d+\.\d+)\.((?=[0-9A-Za-z\-.]*[A-Za-z])[0-9A-Za-z][0-9A-Za-z\-.]*)(\+[0-9A-Za-z\-.]+)?$') {
        return "$($Matches[1])-$($Matches[2])$($Matches[3])"
    }

    throw "Version '$ReleaseVersion' is not supported. Use SemVer2 such as 1.0.0-rc2, or release labels such as 1.0.0.rc2."
}

function Copy-PublicAsset {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,
        [Parameter(Mandatory = $true)]
        [string]$DestinationDirectory,
        [Parameter(Mandatory = $true)]
        [string]$DestinationName
    )

    if (Test-Path -LiteralPath $Source) {
        Copy-Item -LiteralPath $Source -Destination (Join-Path $DestinationDirectory $DestinationName) -Force
        return $true
    }

    return $false
}

$packVersion = ConvertTo-VelopackVersion -ReleaseVersion $Version

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
$stagedPortableZip = $null

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
    $stagedPortableZip = Join-Path $stagingRoot "VComTunnel-$Version-$Runtime-$packageFlavor.zip"
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
    "--packVersion", $packVersion,
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

$publicOutput = Join-Path (Join-Path $OutputRoot "public") $Runtime
if (Test-Path -LiteralPath $publicOutput) {
    Remove-Item -LiteralPath $publicOutput -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publicOutput | Out-Null

$copiedCount = 0
if ($stagedPortableZip) {
    if (Copy-PublicAsset -Source $stagedPortableZip -DestinationDirectory $publicOutput -DestinationName "$PackId-$Version-$Runtime-portable.zip") {
        $copiedCount++
    }
}

if (Copy-PublicAsset -Source (Join-Path $velopackOutput "$PackId-$targetOs-Setup.exe") -DestinationDirectory $publicOutput -DestinationName "$PackId-$Version-$Runtime-Setup.exe") {
    $copiedCount++
}

if (Copy-PublicAsset -Source (Join-Path $velopackOutput "$PackId-$targetOs-Portable.zip") -DestinationDirectory $publicOutput -DestinationName "$PackId-$Version-$Runtime-Velopack-Portable.zip") {
    $copiedCount++
}

if (Copy-PublicAsset -Source (Join-Path $velopackOutput "$PackId-$targetOs.msi") -DestinationDirectory $publicOutput -DestinationName "$PackId-$Version-$Runtime-Setup.msi") {
    $copiedCount++
}

if ($copiedCount -eq 0) {
    throw "No public release assets were copied from $velopackOutput."
}

$hashLines = Get-ChildItem -LiteralPath $publicOutput -File |
    Sort-Object Name |
    ForEach-Object {
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName
        "$($hash.Hash)  $($_.Name)"
    }
Set-Content -LiteralPath (Join-Path $publicOutput "$PackId-$Version-$Runtime-SHA256SUMS.txt") -Value $hashLines -Encoding ASCII

$unversionedAssets = @(
    Get-ChildItem -LiteralPath $publicOutput -File |
        Where-Object { $_.Name -notlike "*$Version*" }
)
if ($unversionedAssets.Count -gt 0) {
    throw "Public release asset names must include version '$Version': $($unversionedAssets.Name -join ', ')"
}

Write-Host "Velopack releases: $velopackOutput"
Write-Host "Public release assets: $publicOutput"
