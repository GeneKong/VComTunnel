# VComTunnel.Serial KMDF Prototype

This directory contains the phase 2 driver design and KMDF driver prototype.
The driver and service channel are implemented enough for experimental
single-port RFC2217 bridging, but the package remains test-signed work intended
for authorized evaluation until broader serial-tool and ESP-DAP validation is
complete.

The intended phase 2 result is a single visible COM device backed by
`VComTunnel.Service`, replacing the phase 1 `com0com + hub4com` chain for one
mapping at a time.

Read these documents before writing driver code:

- [DESIGN.md](DESIGN.md) - driver architecture, queues, serial behavior, risks
- [SERVICE_CHANNEL.md](SERVICE_CHANNEL.md) - private service/driver protocol
- [SERVICE_BACKEND.md](SERVICE_BACKEND.md) - user-mode backend and acceptance gate

Current status:

- The `.NET` service can start `kmdf` mappings through `KmdfTunnelSession`.
- `VComTunnel.Serial.vcxproj` builds the KMDF prototype and test-signed package.
- KMDF ports are installed/removed by the management tooling rather than
  through com0com pairs.
- The data path supports service `ATTACH`, service `WAIT_EVENT`, serial TX
  events, RFC2217 control events, service `PUSH_RX`, modem/line-state updates,
  pending serial reads, and basic wait-mask notifications.
- `VComTunnel.Serial.inf` is usable as the package INF after the WDK build
  produces a matching `.sys` and `.cat`.
- `install-test-driver.ps1` refuses to install unless those package files exist.

Implementation entry point:

1. Build the WDK project:
   `powershell -ExecutionPolicy Bypass -File .\build-driver.ps1 -Configuration Release`
2. Confirm the package contains `VComTunnel.Serial.sys`, `VComTunnel.Serial.inf`,
   and `VComTunnel.Serial.cat`.
3. Review the private service channel described in `SERVICE_CHANNEL.md`.
4. Review the service-side backend described in `SERVICE_BACKEND.md`.
5. Run the user-mode tests and driver build before installing a new package.
6. Only then install the test-signed package on an authorized evaluation or
   internal validation Windows 10/11 x64 machine with a rollback plan.

Build notes:

- The project currently disables Spectre-mitigated library linkage because this
  local VS installation does not have the kernel Spectre libraries installed.
  Re-enable it before release packaging.
- The default install check points to
  `x64\Release\VComTunnel.Serial\VComTunnel.Serial.inf` when that WDK package
  exists. Passing `-Install` runs `pnputil`; do that only from an elevated
  PowerShell after explicitly reviewing the package.
