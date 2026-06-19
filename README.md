# VComTunnel

[English](README.md) | [简体中文](README.zh-CN.md)

VComTunnel is a Windows virtual COM to RFC2217 bridge manager. It lets
existing serial tools open a local COM port while a local service forwards data
and serial-control changes to a remote RFC2217 endpoint.

The project is aimed at embedded development, remote device access, firmware
flashing, serial logging, and lab workflows where existing Windows tools still
expect a normal `COMx` device.

## Highlights

- WPF GUI for mapping management, dependency setup, service control, and logs
- Local Windows service/API for long-running tunnel lifecycle management
- CLI helper `vcomtunnelctl` for diagnostics, setup, status, and automation
- Multiple mappings stored in `%ProgramData%\VComTunnel\config.json`
- Baseline bridge path through `com0com` and `hub4com`/`com2tcp-rfc2217`
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
serial tool -> COMx -> com0com -> CNCBx -> hub4com/com2tcp-rfc2217 -> RFC2217

com0comService:
serial tool -> COMx -> com0com -> CNCBx -> VComTunnel.Service -> RFC2217

kmdf experimental:
serial tool -> COMx -> VComTunnel.Serial.sys -> VComTunnel.Service -> RFC2217
```

The GUI is a controller for the local service. Closing the GUI does not stop
running tunnels; stop mappings from the GUI or with `vcomtunnelctl stop`.

## Build

```powershell
dotnet build VComTunnel.sln
dotnet run --no-build --project tests\VComTunnel.Tests\VComTunnel.Tests.csproj
scripts\smoke-local.ps1
```

The KMDF smoke tool can exercise a test-installed `VComTunnel.Serial` port
against a local fake RFC2217 echo server:

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

When the GUI needs the local API, it first connects to an installed
`VComTunnel` Windows service; if that is not installed, it starts
`VComTunnel.Service.exe --console` as a hidden background process. Use
`vcomtunnelctl service stop` when using an installed Windows service.

## Phase 1 dependency model

Published VComTunnel packages include the upstream `com0com` and `hub4com`
archives under the release `dependencies` directory:

- `dependencies\hub4com-2.1.0.0-386.zip`
- `dependencies\com0com-3.0.0.0-i386-and-x64-signed.zip`

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
com2tcp-rfc2217.bat \\.\CNCB12 192.168.1.50 5000
```

This mirrors the known hub4com RFC2217 client pattern and keeps baud-rate and line-control negotiation inside the wrapper.

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

## Safety and Security

- The local API is intended for loopback use at `127.0.0.1:44817`.
- Treat RFC2217 endpoints as trusted lab infrastructure. RFC2217 itself does
  not provide encryption or authentication.
- Do not expose VComTunnel service ports directly to untrusted networks.
- The KMDF backend is test-signed prototype work. Install it only on a
  disposable, backed-up, or otherwise recoverable Windows test machine.
- Some DTR/RTS/BREAK/purge tests can reset or disturb attached boards. Use the
  safer RFC2217 probe modes first when working against real hardware.

See [SECURITY.md](SECURITY.md) for reporting and deployment guidance.

## Contributing

Contributions are welcome, especially around serial-client compatibility,
RFC2217 interoperability, diagnostics, packaging, and documentation. Please
read [CONTRIBUTING.md](CONTRIBUTING.md) before sending changes.

## License

VComTunnel is released under the MIT License. See [LICENSE](LICENSE).
