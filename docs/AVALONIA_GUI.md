# Avalonia GUI

`VComTunnel.Gui.Avalonia` is the cross-platform GUI track. It is a controller
for the existing VComTunnel HTTP API and keeps the Windows WPF GUI intact while
the multi-platform surface matures.

## Project Boundaries

- `VComTunnel.Client` owns the HTTP/JSON contract for GUI clients.
- `VComTunnel.Gui.Avalonia` owns the cross-platform desktop UI.
- `VComTunnel.Gui` remains the Windows WPF GUI with Windows service, driver,
  and dependency setup affordances.
- `VComTunnel.Service` is still Windows-focused today because it hosts the
  Windows virtual COM backends and listens on `127.0.0.1:44817`.

The Avalonia GUI can build and run on Windows, Linux, and macOS, but full
Linux/macOS tunnel operation still needs a platform backend such as PTY plus a
service host for that OS. The current branch does not claim that the serial
tunnel backend itself is cross-platform.

## Run On Windows

Start or install the service first, then run the Avalonia GUI:

```powershell
dotnet run --project src\VComTunnel.Service\VComTunnel.Service.csproj -- --console
dotnet run --project src\VComTunnel.Gui.Avalonia\VComTunnel.Gui.Avalonia.csproj
```

The GUI reads `VCOMTUNNEL_SERVICE_URL` first. If the variable is not set, it
uses the persisted GUI setting, then falls back to `http://127.0.0.1:44817`.

## Linux / WSL Verification

Build the Linux target from Windows:

```powershell
dotnet publish src\VComTunnel.Gui.Avalonia\VComTunnel.Gui.Avalonia.csproj -c Debug -r linux-x64 --self-contained false
```

Build macOS targets from Windows for compile/package validation:

```powershell
dotnet publish src\VComTunnel.Gui.Avalonia\VComTunnel.Gui.Avalonia.csproj -c Debug -r osx-x64 --self-contained false
dotnet publish src\VComTunnel.Gui.Avalonia\VComTunnel.Gui.Avalonia.csproj -c Debug -r osx-arm64 --self-contained false
```

When WSL has a .NET 8 SDK installed, run the Linux build inside WSL:

```powershell
scripts\build-avalonia-linux-wsl.ps1
```

If the script reports that `dotnet` is missing, install the .NET 8 SDK inside
the WSL distribution and rerun it. The script intentionally fails early instead
of trying to install system packages.

Running the WSL GUI against the Windows service depends on WSL networking. The
service is intentionally loopback-only today; do not expose it on a LAN address
until authentication and threat model work is done.

## Current GUI Capabilities

- Shows service connectivity and config path at the top of the window.
- Loads, edits, adds, deletes, and saves mappings.
- Saves mappings before start to avoid stale IDs and old COM settings.
- Starts and stops the selected mapping through the local API.
- Shows service logs, dependency diagnostics, com0com pairs, and KMDF ports.
- Persists the service URL in the user's application data folder.

## Verification

```powershell
dotnet restore VComTunnel.sln
dotnet build VComTunnel.sln --no-restore
dotnet run --project tests\VComTunnel.Tests\VComTunnel.Tests.csproj --no-restore
dotnet publish src\VComTunnel.Gui.Avalonia\VComTunnel.Gui.Avalonia.csproj -c Debug -r linux-x64 --self-contained false
dotnet publish src\VComTunnel.Gui.Avalonia\VComTunnel.Gui.Avalonia.csproj -c Debug -r osx-x64 --self-contained false
dotnet publish src\VComTunnel.Gui.Avalonia\VComTunnel.Gui.Avalonia.csproj -c Debug -r osx-arm64 --self-contained false
```
