param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$Restore
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."

if ($Restore) {
    dotnet restore "$root\VComTunnel.sln"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

dotnet build "$root\VComTunnel.sln" -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run -c $Configuration --no-build --project "$root\tests\VComTunnel.Tests\VComTunnel.Tests.csproj"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& "$PSScriptRoot\smoke-local.ps1" -Configuration $Configuration -NoBuild
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
