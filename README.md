# VComTunnel

VComTunnel is a Windows virtual COM to RFC2217 bridge manager.

The current implementation delivers the phase 1 baseline:

- Multi-mapping configuration in `%ProgramData%\VComTunnel\config.json`
- Local loopback HTTP API on `http://127.0.0.1:44817`
- WPF GUI for mappings, dependencies, start/stop, and logs
- CLI helper `vcomtunnelctl`
- External dependency detection for `com0com`, `hub4com.exe`, and `com2tcp-rfc2217.bat`
- Release-package bundled setup for com0com and hub4com, with download fallback for development builds
- Experimental KMDF backend with a buildable driver skeleton and raw TCP service path

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

Published VComTunnel packages include the upstream `com0com` and `hub4com`
archives under the release `dependencies` directory:

- `dependencies\hub4com-2.1.0.0-386.zip`
- `dependencies\com0com-3.0.0.0-i386-and-x64-signed.zip`

When you run dependency setup, VComTunnel first uses those bundled archives. If
they are missing, development builds fall back to downloading the same archives.

```powershell
vcomtunnelctl diagnose
vcomtunnelctl create-hints
```

VComTunnel can also assist with dependency setup:

```powershell
vcomtunnelctl deps install
vcomtunnelctl deps launch-com0com
```

`deps install` extracts or downloads:

- hub4com 2.1.0.0 to `%ProgramData%\VComTunnel\tools\hub4com`
- com0com 3.0.0.0 signed package to `%ProgramData%\VComTunnel\tools\com0com`

hub4com becomes usable after extraction because VComTunnel scans its tools cache. com0com is a Windows driver, so it still requires an interactive elevated installer run; use `deps launch-com0com` or install the downloaded package manually. Restart `VComTunnel.Service` after installing dependencies so the GUI/API sees the new files.

The GUI has a single `Setup deps` button. It checks local dependency state, extracts bundled archives or downloads them when needed, and asks whether to launch the elevated com0com driver installer if `setupc.exe` is still missing. After launching the installer, the GUI polls dependency status for a few minutes and refreshes automatically when `setupc.exe` becomes available.

## Release packaging

Use the packaging script to publish GUI, service, CLI, and bundled dependency
archives into one distributable folder and `.zip`:

```powershell
scripts\package-release.ps1 -Version 0.1.0 -Runtime win-x64
```

The script defaults to `--no-restore`; run a normal restore/build first, or pass
`-Restore` when the release machine is allowed to access NuGet.

By default the script downloads the two upstream dependency archives into the
package `dependencies` directory. For repeatable/offline release builds, provide
a pre-populated archive cache:

```powershell
scripts\package-release.ps1 -Version 0.1.0 -Runtime win-x64 -DependencyArchiveRoot C:\Deps\VComTunnel
```

The package also writes `THIRD-PARTY-NOTICES.txt` and `SHA256SUMS.txt`. The
included com0com driver package still requires an interactive elevated install
step on the target machine; bundling it removes the runtime network dependency,
but does not bypass Windows driver installation policy.

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

## Phase 2 KMDF backend

`drivers/VComTunnel.Serial` contains the driver-side design notes, private
service-channel protocol, INF, WDK project, and guarded manual install script.
The service can now start a `kmdf` mapping by opening the visible COM device,
attaching to the driver IOCTL channel, and forwarding bytes to a TCP endpoint.

The intended KMDF backend removes the phase 1 dependency on com0com and hub4com:

```text
serial tool -> COMx -> VComTunnel.Serial.sys -> VComTunnel.Service -> RFC2217
```

Current status is still experimental: the WDK project produces a test-signed
`.sys` and `.cat`, but the user-mode path is raw TCP and RFC2217 negotiation is
not implemented yet.
