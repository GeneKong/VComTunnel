# VComTunnel

[English](README.md) | [简体中文](README.zh-CN.md)

VComTunnel is a Windows virtual COM to RFC2217 bridge manager. It lets
existing serial tools open a local COM port while a local service forwards data
and serial-control changes to a remote RFC2217 endpoint.

The project is aimed at embedded development, remote device access, firmware
flashing, serial logging, and lab workflows where existing Windows tools still
expect a normal `COMx` device.

## Supported Windows Versions

The current release line targets Windows 10 and Windows 11 on x64. Windows 7,
Windows 8, and Windows 8.1 are not supported targets for this .NET 8 / WPF /
Windows Service implementation.

The default release package is self-contained, which means users do not need to
install a separate .NET runtime. It does not extend the supported OS range. The
bundled com0com archive may contain upstream installer names with `W7`, but
that is a third-party driver package detail, not a VComTunnel Windows 7 support
statement.

## Highlights

- WPF GUI for mapping management, dependency setup, service control, and logs
- Local Windows service/API for long-running tunnel lifecycle management
- CLI helper `vcomtunnelctl` for diagnostics, setup, status, and automation
- Multiple mappings stored in `%ProgramData%\VComTunnel\config.json`
- Baseline bridge path through `com0com` and hub4com without default control-line forwarding
- Transitional `com0comService` backend that removes the hub4com process from
  the data path
- Experimental native KMDF virtual serial backend for future dependency-free
  virtual COM tunneling
- Release packaging script with bundled third-party dependency archives,
  notices, and SHA-256 manifest generation

## Status

The current implementation is a practical phase 1/phase 2 workbench:

- The `com0comHub4com` path is the baseline integration path.
- The `com0comService` path is a service-managed bridge that still uses
  com0com for the visible virtual port.
- The `kmdf` path is experimental prototype work. It is useful for validation,
  but it is not yet a production-ready signed driver release.

Driver installation, COM port allocation, DTR/RTS behavior, and RFC2217
control negotiation can affect attached hardware. Review the backend status and
test with a safe target before using a new release in a production lab.

## Architecture

```text
serial tool -> visible COMx -> backend -> VComTunnel.Service -> RFC2217 host:port
```

Supported backend shapes:

```text
com0comHub4com:
serial tool -> COMx -> com0com -> CNCBx -> hub4com no-control-lines bridge -> RFC2217

com0comService:
serial tool -> COMx -> com0com -> CNCBx -> VComTunnel.Service -> RFC2217

kmdf experimental:
serial tool -> COMx -> VComTunnel.Serial.sys -> VComTunnel.Service -> RFC2217
```

The GUI is a controller for the local service. Closing the GUI does not stop
running tunnels; stop mappings from the GUI or with `vcomtunnelctl stop`.

## Verified Windows GUI State

![VComTunnel Windows GUI running one com0comHub4com tunnel](docs/images/vcomtunnel-gui-runtime.png)

The captured Windows run shows the stable `com0comHub4com` backend in service:
the local service is connected, one mapping is running, and no mapping fault is
reported. The active mapping uses `COM12` as the visible port and `CNCB12` as
the com0com backing port. The diagnostics panel reports `com0com/hub4com ready:
True`, `KMDF install tool ready: True`, and resolved paths for `setupc.exe` and
`hub4com.exe`. The optional legacy `com2tcp-rfc2217.bat` wrapper is reported
when present, but the default bridge path does not use it. The log panel shows
RFC2217 traffic for `Tunnel 1`.

## Verified Windows Build

The following sequence is the verified local build gate. `dotnet restore`
requires NuGet access; the later `--no-restore` commands assume that restore
has completed successfully.

```powershell
dotnet restore VComTunnel.sln
dotnet build -c Release --no-restore VComTunnel.sln
dotnet run -c Release --no-build --project tests\VComTunnel.Tests\VComTunnel.Tests.csproj
scripts\smoke-local.ps1 -Configuration Release -NoBuild
```

The smoke script runs the service on a temporary loopback port with a temporary
`VCOMTUNNEL_HOME`, so it does not modify an installed local service.

The following validation commands are hardware- or endpoint-dependent and are
not part of the default build gate. Use them only when the corresponding driver,
COM port, or RFC2217 endpoint is available. The KMDF smoke tool can exercise a
test-installed `VComTunnel.Serial` port against a local fake RFC2217 echo
server:

```powershell
dotnet run -c Release --project tools\VComTunnel.Smoke\VComTunnel.Smoke.csproj -- COM27
```

Local smoke runs include control IOCTL probes for comm config, queue size,
stats, baud-rate, line-control, raw modem control, DTR/RTS reset-style pulses,
CTS/RTS handflow, BREAK, purge, XOFF/XON, immediate-char echo, and the RFC2217
frames emitted by those controls.
The local fake server also injects RFC2217 modem/line-state notifications and
checks that they appear through local serial status IOCTLs and wait-mask events.
It also sends peer `FLOWCONTROL-SUSPEND` / `FLOWCONTROL-RESUME` notifications
and verifies that outbound serial data is gated until the peer resumes.
The RFC2217 client requests full hub4com-style line/modem-state masks while
tolerating ACKs for subsets that the remote endpoint actually supports.
Remote smoke skips those extra probes unless `--control-ioctls` is passed.
When the KMDF driver is not installed or needs a protocol update, probe an
RFC2217 endpoint directly without opening a COM port:

```powershell
dotnet run -c Release --project tools\VComTunnel.Smoke\VComTunnel.Smoke.csproj -- --probe-rfc2217 10.0.2.196 5000
```

`--probe-only` is accepted as an alias for `--probe-rfc2217`.
Add `--probe-settings` to also verify baud-rate and 8N1 line-control ACKs
without toggling DTR, RTS, BREAK, or purge state.
Add `--probe-query` to ask the endpoint for current baud, line-control, flow,
DTR, RTS, and BREAK state without changing them.
If the endpoint omits the initial line/modem-state mask ACKs, the probe reports
the missing ACKs and continues; later setting/control ACK failures still fail
the probe.
If the endpoint explicitly rejects Telnet `COM-PORT-OPTION`, the KMDF backend
fails fast because the peer is not offering RFC2217 serial-control negotiation.
Add `--probe-controls` only on a safe target to verify DTR, RTS, BREAK, and
purge ACKs; those controls can reset or disturb some connected boards.
Use the built-in fake RFC2217 server to validate the full probe path locally:

```powershell
dotnet run -c Release --project tools\VComTunnel.Smoke\VComTunnel.Smoke.csproj -- --probe-rfc2217 127.0.0.1 44000 3 --probe-query --probe-settings --probe-controls --fake-server
```

## Run

Open the GUI. It will check `127.0.0.1:44817` and try to start the local service
automatically if it is offline:

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

`service install` also repairs an existing `VComTunnel` service registration by
updating its `binPath` to the current service executable before the service is
started again.

When the GUI needs the local API, it first connects to an installed
`VComTunnel` Windows service; if that is not installed, it starts
`VComTunnel.Service.exe --console` as a hidden background process. Use
`vcomtunnelctl service stop` when using an installed Windows service.

## Phase 1 dependency model

Published VComTunnel packages include the upstream `com0com` and `hub4com`
archives under the release `dependencies` directory:

- `dependencies\hub4com-2.1.0.0-386.zip`
- `dependencies\com0com-3.0.0.0-i386-and-x64-signed.zip`

The repository keeps pinned copies of these upstream archives under
`third_party\dependencies` so local and GitHub release packaging can run without
network access to SourceForge. The packaging script validates the archive
contents and SHA256 values before copying them into a release package.

Official dependency download links:

- hub4com 2.1.0.0:
  <https://sourceforge.net/projects/com0com/files/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip/download>
- com0com 3.0.0.0 signed package:
  <https://sourceforge.net/projects/com0com/files/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip/download>
- com0com project files index:
  <https://sourceforge.net/projects/com0com/files/>

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

Use the verified Windows portable packaging command to publish GUI, service,
CLI, launch scripts, release notes, and bundled dependency archives into one
distributable folder and `.zip`:

```powershell
scripts\package-release.ps1 -Version 1.0.0.rc2 -Runtime win-x64 -Restore
```

The default package is self-contained and is intended for direct user download:
extract it to a writable folder and run `Start-VComTunnel-Portable.cmd`. The
portable launcher keeps VComTunnel config, logs, downloads, and tool cache under
the release folder's `data` directory. The GUI will start the local
`VComTunnel.Service.exe --console` helper when a Windows service is not
installed.

Do not omit `-Restore` unless a runtime-specific restore for the requested RID
has already been completed. A normal solution restore is not sufficient for the
`win-x64` publish assets used by this release script. For a smaller package
that requires installed .NET runtimes on the target machine, pass
`-FrameworkDependent` together with the same restore policy:

```powershell
scripts\package-release.ps1 -Version 1.0.0.rc2 -Runtime win-x64 -Restore -FrameworkDependent
```

By default the script copies the pinned archives from
`third_party\dependencies` into the package `dependencies` directory. If you
need to build with a separately reviewed cache, provide a pre-populated archive
directory with the same file names and SHA256 values through
`-DependencyArchiveRoot`.

The package also includes:

- `README-FIRST.txt` and `README-FIRST.zh-CN.txt`
- `Start-VComTunnel.cmd`
- `Start-VComTunnel-Portable.cmd`
- `Setup-Dependencies-Portable.cmd`
- `Install-Windows-Service.cmd` and `Uninstall-Windows-Service.cmd`
- `LICENSE`, `README.md`, `README.zh-CN.md`, and `SECURITY.md`
- bundled `dependencies` archives for com0com and hub4com
- `THIRD-PARTY-NOTICES.txt`
- `SHA256SUMS.txt`

The included com0com driver package still requires an interactive elevated
install step on the target machine; bundling it removes the runtime network
dependency, but does not bypass Windows driver installation policy.

For installer-style distribution, use the verified Windows Velopack packaging
command:

```powershell
scripts\package-velopack.ps1 -Version 1.0.0.rc2 -Runtime win-x64 -Restore -Msi
```

Velopack is the preferred installer/update tool because the same packaging
model can cover Windows, macOS, and Linux after the cross-platform Avalonia GUI
becomes the primary UI. With the current WPF GUI, only Windows packaging is
available. On Windows the command above produces a Velopack `Setup.exe`, an
MSI, a Velopack portable zip, the staged portable zip, and a SHA-256 manifest.

Public download files are copied to `artifacts\velopack\public\<runtime>` with
the release version in every file name, for example
`VComTunnel-1.0.0.rc2-win-x64-Setup.exe`. Velopack's raw update-feed files stay
under the runtime output directory and keep Velopack's expected names.

Non-Windows installer packaging is not documented as a verified command yet.
It depends on the Avalonia GUI becoming the primary UI and on platform-specific
validation for Linux/macOS publish output. macOS installer releases must be
produced on macOS because Apple packaging and signing tools are required.
Public installer releases should be code-signed.

MSIX is not the primary path right now because VComTunnel needs explicit
service and driver setup flows, while Velopack fits the current desktop app and
future cross-platform installer/update story better.

GitHub Actions provides the online Windows release path through the `Package`
workflow. The `v1.0.0.rc2` release was produced through this workflow with
versioned public assets. The workflow runs on `windows-latest`, builds and
tests the solution, runs `scripts\package-velopack.ps1`, uploads the versioned
public release assets as a workflow artifact, and can upload them to the
matching GitHub Release when requested. Release versions containing `rc`,
`alpha`, `beta`, `pre`, or `preview` use the workflow's pre-release marking
logic; confirm the GitHub Release flag before publishing. The current online
packaging job is Windows-only until the Avalonia GUI publish output is
available for Linux/macOS.

Each `com0comHub4com` mapping expects:

- `visiblePort`: the COM port shown to user tools, for example `COM12`
- `backingPort`: the com0com peer consumed by hub4com, for example `CNCB12`
- `host` and `port`: the ESP-DAP RFC2217 endpoint

The bridge process is launched through hub4com directly, using a default
no-control-lines filter chain:

```text
hub4com --create-filter=escparse,com,parse --add-filters=0:com --create-filter=telnet,tcp,telnet:" --comport=client" --add-filters=1:tcp --octs=off \\.\CNCB12 --use-driver=tcp *192.168.1.50:5000
```

This establishes the data/Telnet bridge but does not install the DTR, RTS,
BREAK, or line-control forwarding filters. Mapping start, `autoStart`, and
service recovery therefore do not implicitly reset a target or put it into a
bootloader. Target reset and bootloader entry should come from an explicit
tool action instead of the default tunnel start path.

Set `hub4comForwardControlLines: true` on a `com0comHub4com` mapping only when
the target expects RFC2217 control-line forwarding through hub4com. That mode
adds the same `pinmap` and `linectl` filters used by `com2tcp-rfc2217.bat`, so
RTS/DTR/BREAK and line-control changes can reach the target. It also means the
hub4com path no longer has VComTunnel's startup-only suppression hook; validate
it on hardware that can tolerate the initial line state.

## Phase 1 real device bring-up

For a `com0comHub4com` mapping, install external dependencies and create the
com0com pair first:

1. Run `vcomtunnelctl diagnose` until `com0com/hub4com ready` is `True`.
2. Start `VComTunnel.Service`.
3. Add or edit a mapping in the GUI with the visible COM port, backing `CNCB` port, and ESP-DAP RFC2217 host/port.
4. Start the mapping from GUI or `vcomtunnelctl start <mappingId>`.
5. Open the visible `COMx` with a serial terminal or flashing tool and verify data, baud-rate changes, DTR/RTS, and reconnect behavior.

This path intentionally still depends on externally installed com0com/hub4com
or the bundled dependency archive plus an interactive elevated driver install.

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
`.sys` and `.cat`, and the user-mode path now performs RFC2217/Telnet
negotiation including hub4com-style remote ECHO acceptance plus baud-rate,
line-control, DTR/RTS including raw modem-control
and EscapeCommFunction-style IOCTLs, BREAK, flow-control, purge, command ACK
correlation with accepted-value validation and timeout retry,
startup line/modem mask ACK validation, remote flow-control suspend/resume
handling, local XOFF/XON and RX backpressure through RFC2217
FLOWCONTROL-SUSPEND/RESUME,
hub4com client-mode purge semantics where local RX clear stays inside the
virtual COM driver and only TX clear is sent as RFC2217 PURGE-DATA,
SIGNATURE request response, serialized Telnet/RFC2217 writes with idle NOP
keep-alive that continues during serial flow-control suspension,
modem/line notification handling, and basic wait-mask notifications
for RX, RXFLAG/EventChar, RX80FULL, TXEMPTY, CTS, DSR, RLSD, RING, BREAK, and
ERR events,
plus serial RX/TX/error statistics through `IOCTL_SERIAL_GET_STATS` and
`IOCTL_SERIAL_CLEAR_STATS`, and immediate-character transmit through
`IOCTL_SERIAL_IMMEDIATE_CHAR`. Zero-length serial writes are accepted as
successful no-op writes for compatibility with user-mode serial libraries.
Basic read timeout behavior from
`IOCTL_SERIAL_SET_TIMEOUTS` is honored for immediate empty reads and total read
timeouts, so synchronous readers can fail/return predictably when no RX data is
available. Windows serial configuration probes
(`CONFIG_SIZE`, `GET_COMMCONFIG`, and `SET_COMMCONFIG`) return a minimal RS232
configuration with no provider-specific data; queue-size requests are validated
against the fixed virtual queues, and FIFO/default-configuration UART requests
are accepted as no-op compatibility calls.
Remaining hardening work is broader serial compatibility coverage and live ESP-DAP
compatibility validation against real tools.

The GUI does not install the KMDF driver during the normal dependency setup
path. It only prompts for the test-signed driver when a user
selects a `kmdf` mapping and explicitly creates or updates that KMDF port.
That elevated KMDF add/update flow may add the bundled
`VComTunnel.Serial.cer` test certificate to the local machine certificate
stores, install or update the driver, and require a reboot. Windows may require
Test Mode, and Secure Boot or driver signing policy can block installation.

## Safety and Security

- The local API is intended for loopback use at `127.0.0.1:44817`.
- Treat RFC2217 endpoints as trusted lab infrastructure. RFC2217 itself does
  not provide encryption or authentication.
- Do not expose VComTunnel service ports directly to untrusted networks.
- The KMDF backend in this package is test-signed work intended for authorized
  evaluation or internal validation.
- Some DTR/RTS/BREAK/purge tests can reset or disturb attached boards. Use the
  safer RFC2217 probe modes first when working against real hardware.
- Mapping start, `autoStart`, and automatic service recovery are data-link
  paths by default. They avoid DTR/RTS/BREAK forwarding; target reset and
  bootloader entry must come from an explicit manual or tool-driven action.

See [SECURITY.md](SECURITY.md) for reporting and deployment guidance.

## Contributing

Contributions are welcome, especially around serial-client compatibility,
RFC2217 interoperability, diagnostics, packaging, and documentation. Please
read [CONTRIBUTING.md](CONTRIBUTING.md) before sending changes.

## License

VComTunnel is released under the MIT License. See [LICENSE](LICENSE).
