# Contributing to VComTunnel

Thanks for taking the time to improve VComTunnel.

This project touches Windows services, virtual serial ports, driver prototypes,
and attached hardware. Please keep changes small, reviewable, and backed by a
clear validation path.

## Development Setup

Required for the managed projects:

- Windows 10 or Windows 11
- .NET SDK compatible with the project files
- PowerShell

Optional, depending on the backend you are working on:

- com0com and hub4com for the phase 1 backend
- Visual Studio with WDK for the KMDF prototype
- A safe RFC2217 endpoint or the built-in smoke-test fake server

## Build and Test

Run the managed build and tests before sending changes:

```powershell
dotnet restore VComTunnel.sln
dotnet build VComTunnel.sln --no-restore
dotnet run --project tests\VComTunnel.Tests\VComTunnel.Tests.csproj --no-restore
powershell -ExecutionPolicy Bypass -File scripts\smoke-local.ps1
```

For RFC2217 protocol work, prefer the fake server probe before touching real
hardware:

```powershell
dotnet run -c Release --project tools\VComTunnel.Smoke\VComTunnel.Smoke.csproj -- --probe-rfc2217 127.0.0.1 44000 3 --probe-query --probe-settings --fake-server
```

Only add `--probe-controls` when the target is safe to reset or disturb.

For Avalonia GUI work, run the headless GUI smoke check:

```powershell
dotnet run --project src\VComTunnel.Gui.Avalonia\VComTunnel.Gui.Avalonia.csproj --no-restore -- --smoke
```

When WSL is available, validate the Linux build without installing the .NET SDK
inside WSL:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke-avalonia-linux-wsl.ps1
```

## Change Guidelines

- Keep service, GUI, CLI, core, and driver changes separated when possible.
- Preserve backend boundaries: RFC2217 and network logic stay in user mode, not
  in the KMDF driver.
- Document user-visible behavior changes in `README.md` or the relevant driver
  design document.
- Avoid silent driver installation. Driver installation should remain explicit
  and administrator-reviewed.
- Include the backend and verification path in the pull request description.

## Review Checklist

Before a change is merged, check:

- Build and tests pass.
- New failure modes are reported through logs or diagnostics.
- COM port naming and cleanup behavior are considered.
- Attached hardware cannot be reset unexpectedly by default test paths.
- Third-party dependency or driver-signing implications are documented.
