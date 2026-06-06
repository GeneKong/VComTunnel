# VComTunnel

VComTunnel is a Windows virtual COM to RFC2217 bridge manager.

The current implementation delivers the phase 1 baseline:

- Multi-mapping configuration in `%ProgramData%\VComTunnel\config.json`
- Local loopback HTTP API on `http://127.0.0.1:44817`
- WPF GUI for mappings, dependencies, start/stop, and logs
- CLI helper `vcomtunnelctl`
- External dependency detection for `com0com`, `hub4com.exe`, and `com2tcp-rfc2217.bat`
- Assisted dependency download/extraction for com0com and hub4com
- KMDF backend scaffold with explicit "not ready" diagnostics

Flutter was checked first, but `flutter --version` timed out repeatedly in this environment. The GUI therefore uses the planned `.NET WPF` fallback.

## Build

```powershell
dotnet build VComTunnel.sln
dotnet run --no-build --project tests\VComTunnel.Tests\VComTunnel.Tests.csproj
scripts\smoke-local.ps1
```

## Run

Open the GUI. It will check `127.0.0.1:44817` and try to start the local service automatically if it is offline:

```powershell
dotnet run --project src\VComTunnel.Gui\VComTunnel.Gui.csproj
```

You can still start the local API/service process manually in console mode:

```powershell
dotnet run --project src\VComTunnel.Service\VComTunnel.Service.csproj -- --console
```

Create a sample config:

```powershell
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- init-config
```

## CLI

```powershell
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- diagnose
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- deps install
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- deps launch-com0com
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- create-hints
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- status
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- mappings
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- start <mappingId>
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- stop <mappingId>
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- logs
```

The service app supports both console debugging and Windows SCM hosting. Install or remove it from an elevated shell:

```powershell
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- service install
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- service start
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- service stop
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- service uninstall
```

For published builds, pass the explicit service executable if auto-discovery does not apply:

```powershell
vcomtunnelctl service install C:\Tools\VComTunnel\VComTunnel.Service.exe
```

## Phase 1 dependency model

VComTunnel does not redistribute `com0com` or `hub4com`. Install them externally, then run:

```powershell
vcomtunnelctl diagnose
vcomtunnelctl create-hints
```

VComTunnel can also assist with dependency setup:

```powershell
vcomtunnelctl deps install
vcomtunnelctl deps launch-com0com
```

`deps install` downloads and extracts:

- hub4com 2.1.0.0 to `%ProgramData%\VComTunnel\tools\hub4com`
- com0com 3.0.0.0 signed package to `%ProgramData%\VComTunnel\tools\com0com`

hub4com becomes usable after extraction because VComTunnel scans its tools cache. com0com is a Windows driver, so it still requires an interactive elevated installer run; use `deps launch-com0com` or install the downloaded package manually. Restart `VComTunnel.Service` after installing dependencies so the GUI/API sees the new files.

The GUI has a single `Setup deps` button. It checks local dependency state, downloads/extracts hub4com and the com0com installer when needed, and asks whether to launch the elevated com0com driver installer if `setupc.exe` is still missing. After launching the installer, the GUI polls dependency status for a few minutes and refreshes automatically when `setupc.exe` becomes available.

Each `com0comHub4com` mapping expects:

- `visiblePort`: the COM port shown to user tools, for example `COM12`
- `backingPort`: the com0com peer consumed by hub4com, for example `CNCB12`
- `host` and `port`: the ESP-DAP RFC2217 endpoint

The bridge process is launched through hub4com's wrapper:

```text
com2tcp-rfc2217.bat \\.\CNCB12 192.168.1.50 3333
```

This mirrors the known hub4com RFC2217 client pattern and keeps baud-rate and line-control negotiation inside the wrapper.

## Real device bring-up

After installing external dependencies and creating the com0com pair:

1. Run `vcomtunnelctl diagnose` until `com0com/hub4com ready` is `True`.
2. Start `VComTunnel.Service`.
3. Add or edit a mapping in the GUI with the visible COM port, backing `CNCB` port, and ESP-DAP RFC2217 host/port.
4. Start the mapping from GUI or `vcomtunnelctl start <mappingId>`.
5. Open the visible `COMx` with a serial terminal or flashing tool and verify data, baud-rate changes, DTR/RTS, and reconnect behavior.

Current stopping point without external integration: this repo can build, validate configs, run the service API, manage mappings, diagnose missing dependencies, and start/stop a fake `com2tcp-rfc2217` process. A real end-to-end serial session requires externally installed com0com/hub4com plus an RFC2217 endpoint.

## Phase 2 KMDF scaffold

`drivers/VComTunnel.Serial` contains the driver-side design notes, INF skeleton, and manual install script placeholder. The service returns an explicit unsupported status for `kmdf` mappings until the driver and user-mode channel are implemented.
