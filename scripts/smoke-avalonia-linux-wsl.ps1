param(
    [string]$Distribution = "",
    [string]$Configuration = "Debug",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src\VComTunnel.Gui.Avalonia\VComTunnel.Gui.Avalonia.csproj"
$runtime = "linux-x64"

$publishArgs = @("publish", $project, "-c", $Configuration, "-r", $runtime, "--self-contained", "true")
if ($NoRestore) {
    $publishArgs += "--no-restore"
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$publishDirectory = Join-Path $repoRoot "src\VComTunnel.Gui.Avalonia\bin\$Configuration\net8.0\$runtime\publish"
$linuxExecutable = Join-Path $publishDirectory "VComTunnel.Gui.Avalonia"
if (-not (Test-Path $linuxExecutable)) {
    Write-Host "ERROR: Linux executable was not found: $linuxExecutable"
    exit 1
}

$wslBaseArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Distribution)) {
    $wslBaseArgs += @("-d", $Distribution)
}

$windowsExecutableForWsl = $linuxExecutable.Replace("\", "\\")
$pathArgs = @($wslBaseArgs + @("wslpath", "-a", $windowsExecutableForWsl))
$wslExecutableOutput = & wsl.exe @pathArgs
$wslExecutable = ($wslExecutableOutput | Select-Object -First 1)
if ($null -ne $wslExecutable) {
    $wslExecutable = $wslExecutable.Trim()
}

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wslExecutable)) {
    Write-Host "ERROR: Could not map Linux executable path into WSL."
    exit 1
}

$smokeCommand = "chmod +x '$wslExecutable' && '$wslExecutable' --smoke"
$smokeArgs = @($wslBaseArgs + @("bash", "-lc", $smokeCommand))
& wsl.exe @smokeArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
