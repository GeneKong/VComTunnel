dotnet build "$PSScriptRoot\..\VComTunnel.sln"
dotnet run --no-build --project "$PSScriptRoot\..\tests\VComTunnel.Tests\VComTunnel.Tests.csproj"
& "$PSScriptRoot\smoke-local.ps1"
