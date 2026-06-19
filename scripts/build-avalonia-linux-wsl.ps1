param(
    [string]$Distribution = "",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = "src/VComTunnel.Gui.Avalonia/VComTunnel.Gui.Avalonia.csproj"

$wslBaseArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Distribution)) {
    $wslBaseArgs += @("-d", $Distribution)
}

$windowsRootForWsl = $repoRoot.Path.Replace("\", "\\")
$pathArgs = @($wslBaseArgs + @("wslpath", "-a", $windowsRootForWsl))
$wslRootOutput = & wsl.exe @pathArgs
$wslRoot = ($wslRootOutput | Select-Object -First 1)
if ($null -ne $wslRoot) {
    $wslRoot = $wslRoot.Trim()
}

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wslRoot)) {
    Write-Host "ERROR: Could not map repository path into WSL."
    exit 1
}

$dotnetCheckArgs = @($wslBaseArgs + @("bash", "-lc", "command -v dotnet >/dev/null 2>&1"))
& wsl.exe @dotnetCheckArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet SDK was not found in WSL. Install the .NET 8 SDK in WSL, then rerun this script."
    exit 1
}

$restore = if ($NoRestore) {
    "true"
} else {
    "dotnet restore $project"
}

$buildCommand = "cd '$wslRoot' && $restore && dotnet build $project --no-restore"
$buildArgs = @($wslBaseArgs + @("bash", "-lc", $buildCommand))
& wsl.exe @buildArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
