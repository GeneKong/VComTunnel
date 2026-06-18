# VComTunnel.Service KMDF Backend Design

This document closes the user-mode side of the phase 2 design. The driver
emulates the COM port; `VComTunnel.Service` owns mapping configuration,
RFC2217, reconnect, logging, and GUI/API state.

## Backend Boundary

The existing phase 1 backend manages an external `hub4com` process. The KMDF
backend must instead manage one service channel to `VComTunnel.Serial.sys`.

Responsibilities:

- Discover the driver service-control interface.
- Attach to exactly one driver instance in the first prototype.
- Translate private driver events into RFC2217 client actions.
- Push RFC2217 inbound bytes back into the driver.
- Surface driver/service/network state through the existing API.
- Keep the GUI model compatible with existing `TunnelMapping` rows.

Non-responsibilities:

- Do not load unsigned drivers silently.
- Do not implement serial IOCTLs in user mode.
- Do not expose the private driver channel over HTTP.
- Do not require com0com or hub4com for `kmdf` mappings.

## Configuration

Existing mapping fields remain valid:

```json
{
  "backend": "kmdf",
  "visiblePort": "COM40",
  "backingPort": null,
  "host": "10.0.2.119",
  "port": 4000,
  "protocol": "rfc2217"
}
```

Rules:

- `backend = kmdf` requires `backingPort = null`.
- `visiblePort` is the driver COM name.
- One `kmdf` mapping is supported in the first prototype.
- The service must reject `Start` when the installed driver reports a different
  visible COM name than the mapping expects.

## Components

Add these service-side abstractions:

```text
KmdfDriverClient
  Low-level DeviceIoControl wrapper for SERVICE_CHANNEL.md.

KmdfTunnelBackend
  Implements the same lifecycle shape as the hub4com backend.

KmdfSerialSession
  Per-start state: driver attachment, RFC2217 client, event loop, cancellation.

Rfc2217Client
  Extract reusable RFC2217 behavior from the phase 1 process wrapper over time.
  Until then, first KMDF prototype can use a fake echo backend.
```

The first implementation should keep `KmdfDriverClient` isolated from the rest
of the app so driver protocol churn does not leak into GUI/API models.

Current implementation note:

- `KmdfTunnelSession` is the first service-side implementation.
- It opens the private control channel, sends `ATTACH`, waits for driver events,
  writes serial bytes and RFC2217 control frames to the TCP stream, reads TCP
  bytes, and sends `PUSH_RX` or modem/line-state updates back to the driver.
- RFC2217/Telnet support covers the hub4com client-mode baseline: binary and
  COM-PORT-OPTION negotiation, IAC escaping, baud-rate, data-size, parity,
  stop-size, DTR/RTS, BREAK, flow-control, purge, NOTIFY-LINESTATE, and
  NOTIFY-MODEMSTATE.
- RFC2217 command ACK correlation is implemented for outbound serial controls.
  The service waits for the expected ACK command and accepted value, retries
  once on timeout, and faults the tunnel if the peer rejects the value or does
  not acknowledge after retry.
- Server-to-client accepted baud/data/parity/stop settings that are not consumed
  by a pending ACK wait update the driver's cached serial settings, matching
  hub4com's COM-PORT-OPTION client behavior.
- The service requires driver protocol 1.1 so remote accepted settings can use
  `IOCTL_VCOMTUNNEL_SET_REMOTE_SETTINGS`.
- Startup sends the initial line-state and modem-state masks and waits for
  their ACKs before the mapping is reported as running.
- RFC2217 SIGNATURE requests are answered with the VComTunnel client signature.
- RFC2217 FLOWCONTROL-SUSPEND pauses outbound serial data and control commands
  until FLOWCONTROL-RESUME is received.
- TCP writes are serialized across driver events, Telnet negotiation replies,
  and idle keep-alive. Idle RFC2217 sessions send Telnet NOP every 30 seconds.
- Startup connection failures and runtime network drops feed the same
  `RestartOnFailure` policy. Manual Stop invalidates delayed restarts so a
  stopped mapping stays stopped.
- Wait-mask notifications currently cover RXCHAR, TXEMPTY, CTS, DSR, RLSD,
  RING, BREAK, and ERR events raised by service TX consumption, received bytes,
  or RFC2217 modem/line notifications. RFC2217 modem notifications propagate
  both current-state bits and explicit delta/edge event bits.
- Remaining hardening: additional serial events beyond the current wait-mask
  subset and live hardware/tool compatibility validation.

## Start Flow

```text
POST /api/mappings/{id}/start
  -> validate mapping backend == kmdf
  -> verify driver package/port is installed
  -> open service-control interface
  -> send ATTACH
  -> verify returned PortName == mapping.visiblePort
  -> connect RFC2217 host:port
  -> negotiate Telnet/RFC2217 and verify line/modem mask ACKs
  -> set driver connection state Online
  -> begin driver event loop
  -> return TunnelStatus.Running
```

If any step fails, return `TunnelStatus.Faulted` with the most concrete reason:

- driver not installed
- service channel not found
- protocol mismatch
- visible COM mismatch
- RFC2217 connection failed

If `RestartOnFailure` is enabled, transient RFC2217 connection failures are
scheduled for restart instead of requiring another manual Start.

## Stop Flow

```text
POST /api/mappings/{id}/stop
  -> cancel event loop
  -> set driver connection state Stopping/Offline
  -> detach driver channel
  -> close RFC2217 connection
  -> complete/cancel pending service requests
  -> return TunnelStatus.Stopped
```

Stopping the service must not unload the driver. The installed COM port can
remain visible, but serial I/O should fail in a controlled way until the service
starts again.

## Event Loop

The service keeps `IOCTL_VCOMTUNNEL_WAIT_EVENT` pending. Each completed event is
handled serially in the first prototype:

```text
Open
  -> log
  -> optionally reconnect RFC2217 if offline

Close/Cleanup
  -> flush pending writes and read state

TxData
  -> write bytes to RFC2217
  -> COMPLETE_EVENT success/failure

SetBaudRate / SetLineControl / SetModemControl / SetHandflow
  -> translate to RFC2217 negotiation/control
  -> complete the serial IOCTL after the driver has queued the event; service
     waits for the expected RFC2217 acknowledgement command and value with a
     bounded timeout and one retry, faulting the tunnel on rejection or timeout

Purge
  -> clear local RX/TX queues
  -> translate RX/TX clear requests to RFC2217 PURGE-DATA and wait for ACK

FLOWCONTROL-SUSPEND / FLOWCONTROL-RESUME
  -> pause and resume outbound serial data and control command writes

SetWaitMask / CancelWaitMask
  -> keep one pending WAIT_ON_MASK request and complete it when supported
     RX/modem/line events intersect the active wait mask
```

The first prototype prefers deterministic completion over perfect serial
semantics. Driver IOCTLs complete after the event is queued to the service; the
service applies RFC2217 ACK wait/retry/fault policy independently so a slow or
partial peer does not hang serial applications.

## State Model

Expose one status object per `kmdf` mapping:

```text
stopped
starting
driverMissing
driverAttached
networkConnecting
running
networkFaulted
driverFaulted
stopping
```

The existing `TunnelRunState` can stay coarse (`running`, `faulted`, etc.), but
logs and future `/api/status` details should include the more precise KMDF
substate.

## Diagnostics

Add diagnostics before enabling GUI `Start`:

- Is `VComTunnel.Serial.sys` installed?
- Does the COM name exist in the Windows COM database?
- Is the private service-control interface present?
- Is the driver protocol version compatible?
- Is another service already attached?
- Does the mapping have `backingPort = null`?

Do not report `KMDF install tooling ready` as equivalent to driver readiness.
`pnputil.exe` only means installation tooling exists, not that the driver exists.

## Fake Backend First

Before real RFC2217, implement a fake backend:

```text
serial client write -> driver TxData -> service -> push same bytes back as RX
```

This proves:

- COM port open/close.
- Driver/service attach.
- Write event delivery.
- Service completion of driver requests.
- RX push and read completion.
- Cancellation on stop.

Only after fake loopback is stable should RFC2217 be connected.

## GUI Changes

The GUI no longer blocks `kmdf` start. If the test driver is not installed, the
service returns `Faulted` with the driver open error.

Remaining GUI work:

- `backend = kmdf`
- no `backingPort` editing
- driver diagnostics panel
- install package status
- service-channel status
- "Start loopback" before "Start RFC2217"

The GUI must not hide driver signing or reboot/test-signing requirements.

## Test Matrix

Automated service tests:

- `kmdf` mapping without driver returns clear diagnostic.
- protocol mismatch returns clear diagnostic.
- fake driver client can emit Open/TxData/Close events.
- service converts TxData into backend writes and completes RequestIds.
- service stop cancels pending event loop.

Manual driver tests:

- test-signed install and uninstall.
- Device Manager shows expected COM name.
- `System.IO.Ports.SerialPort` can open and close the COM port.
- service offline read/write failure is bounded.
- fake loopback echoes bytes.
- service crash/restart does not hang the serial client or bugcheck.

RFC2217 integration tests:

- connect to echo endpoint.
- baud-rate change reaches RFC2217 layer.
- DTR/RTS transitions reach RFC2217 layer.
- network drop marks mapping faulted and recovers after reconnect policy.

## Completion Gate For Phase 2 Prototype

Phase 2 prototype is not complete until all are true:

- No com0com pair is required.
- No hub4com/com2tcp process is started.
- Device Manager shows `VComTunnel Virtual Serial Port (COMx)`.
- A serial client can open `COMx`.
- Bytes written to `COMx` reach an RFC2217 endpoint.
- Bytes from the RFC2217 endpoint are readable from `COMx`.
- Stopping `VComTunnel.Service` causes controlled serial I/O failure, not a
  hang or bugcheck.
- Uninstalling the test driver removes the COM device cleanly.
