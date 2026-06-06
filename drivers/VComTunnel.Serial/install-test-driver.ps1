param(
    [string]$InfPath = "$PSScriptRoot\VComTunnel.Serial.inf"
)

Write-Host "This is a manual phase-2 scaffold. Review the INF and build a signed SYS/CAT before installing."
Write-Host "When ready, run from an elevated PowerShell:"
Write-Host "pnputil.exe /add-driver `"$InfPath`" /install"
