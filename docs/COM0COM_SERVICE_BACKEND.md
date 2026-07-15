# com0comService Backend

`com0comService` is the transitional backend between the phase 1
`com0comHub4com` bridge and the KMDF backend.

Data path:

```text
serial tool -> visible COMx -> com0com -> backing CNCBx
  -> VComTunnel.Service -> RFC2217 host:port
```

This backend still uses com0com for the Windows-visible virtual COM pair, but
it does not start `hub4com` or `com2tcp-rfc2217.bat`. `VComTunnel.Service`
opens the backing port directly, connects to the RFC2217 endpoint, performs the
same Telnet/RFC2217 startup negotiation used by the KMDF service path, and
bridges serial bytes in both directions. The local backing port is opened with
Win32 overlapped I/O so serial RX/TX and modem-status events can progress as
separate pipeline stages instead of depending on synchronous polling.

Current scope:

- New RFC2217 mappings default to the standard TCP port `2217`; an explicitly
  configured or device-advertised custom port remains unchanged.
- Requires `backend = com0comService`.
- Requires `visiblePort` and `backingPort` to name different com0com pair
  sides.
- Exposes fixed service-side com0com pair create/remove APIs so product UIs do
  not need to run `setupc.exe` with a UAC prompt for every add/delete action.
  Driver installation or repair remains an explicit elevated setup step.
- Does not require hub4com to be installed or detected.
- Supports RFC2217 initial negotiation, line/modem notification masks,
  startup serial/control status query, SIGNATURE response, remote
  FLOWCONTROL-SUSPEND/RESUME, idle NOP keep-alive, and service-level restart
  after transient network faults.
- Observes local backing-port CTS/DSR events on the primary serial handle with
  overlapped `WaitCommEvent` and maps the com0com peer state to RFC2217
  DTR/RTS changes, matching the `hub4com` `pinmap` direction for explicit
  control-line forwarding.
- Configures the service-owned backing `CNCB` handle as an 8-bit binary byte
  transport. com0com ports can retain a legacy `7E1` DCB from their default or
  previous opener; inheriting that DCB clears payload bit 7 before the service
  can forward the byte (`0xC0` becomes `0x40`). This internal DCB does not
  override the application's serial format: visible-side baud/data/parity/stop
  changes are received through com0com insertion events and forwarded as
  RFC2217 settings to the remote physical UART.
- Writes RFC2217 RX data to the local COM side through a bounded small-chunk
  pipeline so the TCP reader is not blocked by normal local COM write latency.
- Removes stale runtime entries when a saved mapping is deleted, so `/api/status`
  does not keep advertising a COM mapping that no longer exists in config.

Known limitation:

- BREAK, purge, and XON/XOFF actions from arbitrary Windows serial tools are
  still not all surfaced through this backend with the same fidelity as
  hub4com's full filter graph.
