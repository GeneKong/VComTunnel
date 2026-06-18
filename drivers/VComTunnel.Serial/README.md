# VComTunnel.Serial KMDF Prototype

This directory contains the phase 2 driver design and the first KMDF driver
skeleton. It is not a usable RFC2217 bridge yet: read/write currently returns a
controlled not-ready result until the service channel is implemented.

The intended phase 2 result is a single visible COM device backed by
`VComTunnel.Service`, replacing the phase 1 `com0com + hub4com` chain for one
mapping at a time.

Read these documents before writing driver code:

- [DESIGN.md](DESIGN.md) - driver architecture, queues, serial behavior, risks
- [SERVICE_CHANNEL.md](SERVICE_CHANNEL.md) - private service/driver protocol
- [SERVICE_BACKEND.md](SERVICE_BACKEND.md) - user-mode backend and acceptance gate

Current status:

- The `.NET` service and GUI still treat `kmdf` mappings as unsupported.
- `VComTunnel.Serial.vcxproj` builds the first fixed-port KMDF prototype.
- The driver publishes a fixed test link: `COM40`.
- `VComTunnel.Serial.inf` is usable as the package INF after the WDK build
  produces a matching `.sys` and `.cat`.
- `install-test-driver.ps1` refuses to install unless those package files exist.

Implementation entry point:

1. Build the WDK project:
   `powershell -ExecutionPolicy Bypass -File .\build-driver.ps1 -Configuration Release`
2. Confirm the package contains `VComTunnel.Serial.sys`, `VComTunnel.Serial.inf`,
   and `VComTunnel.Serial.cat`.
3. Implement the private service channel described in `SERVICE_CHANNEL.md`.
4. Implement buffered read/write and service-side event forwarding.
5. Add the service-side backend described in `SERVICE_BACKEND.md`.
6. Add fake-driver tests before real RFC2217.
7. Only then install the test-signed package on a disposable or
   backed-up Windows 10/11 x64 machine.

Build notes:

- The project currently disables Spectre-mitigated library linkage because this
  local VS installation does not have the kernel Spectre libraries installed.
  Re-enable it before release packaging.
- The default install check points to
  `x64\Release\VComTunnel.Serial\VComTunnel.Serial.inf` when that WDK package
  exists. Passing `-Install` runs `pnputil`; do that only from an elevated
  PowerShell after explicitly reviewing the package.
