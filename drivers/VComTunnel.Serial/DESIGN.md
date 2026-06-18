# VComTunnel.Serial Driver Design

## Goal

Phase 2 replaces this phase 1 chain:

```text
serial tool -> COMx -> com0com -> CNCBx -> hub4com/com2tcp-rfc2217 -> RFC2217
```

with:

```text
serial tool -> COMx -> VComTunnel.Serial.sys -> VComTunnel.Service -> RFC2217
```

The driver must look enough like a normal Windows serial port that terminal
tools, firmware tools, and ESP-DAP workflows can open it, configure it, write
bytes, read bytes, and toggle line-control signals.

## Non-goals

- No RFC2217 or TCP code in kernel mode.
- No TLS, authentication, or LAN security policy in the driver.
- No multi-port bus driver in the first prototype.
- No formal EV/attestation-signing release in the prototype.
- No silent driver installation from the GUI.

## Driver Shape

Use a KMDF function driver installed as a root-enumerated Ports-class device.
The first prototype exposes one COM port, for example `COM40`.

Objects:

- `WDFDRIVER`: global driver object.
- `WDFDEVICE`: one virtual serial port.
- Default device-control queue: serial IOCTLs.
- Read queue: manual or parallel queue for pending read requests.
- Write queue: sequential queue for client writes.
- Service-control queue: private IOCTLs used only by `VComTunnel.Service`.
- RX ring buffer: bytes received from service/RFC2217 and consumed by client
  reads.
- TX pending list: writes waiting for service consumption or completion.
- State block: cached baud rate, line control, modem control, timeouts, masks,
  connection state, open handle count, and removal state.

The serial port side and service-control side must be separated by device
interface security. Normal users may open the COM port according to Ports-class
policy. Only LocalSystem/Administrators should open the service-control channel.

## COM Registration

The INF should install as `Class=Ports` and set a default `PortName`, such as
`COM40`, for the prototype. Later versions can let the service allocate or
rename the COM number.

The service must still treat COM names as user-facing configuration and must
diagnose conflicts before attempting installation or rename operations.

## Open and Close Semantics

Prototype policy:

- One serial client at a time.
- `CreateFile("\\\\.\\COM40")` succeeds when the driver is installed and the
  port is not already open.
- If `VComTunnel.Service` is offline, open can still succeed, but reads and
  writes complete with `STATUS_DEVICE_NOT_READY` or timeout. This avoids
  hanging user tools forever when the service is stopped.
- Close cancels pending reads/writes, clears wait masks, and emits a close event
  to the service if the service channel is attached.

This can be tightened later to fail open when service is offline, but allowing
open gives better diagnostics during bring-up.

## Minimum Serial Surface

Implement the smallest set that common serial clients expect:

- `IRP_MJ_CREATE`
- `IRP_MJ_CLOSE`
- `IRP_MJ_READ`
- `IRP_MJ_WRITE`
- `IRP_MJ_CLEANUP`
- `IRP_MJ_DEVICE_CONTROL`

Minimum IOCTL families:

- Baud rate:
  - `IOCTL_SERIAL_GET_BAUD_RATE`
  - `IOCTL_SERIAL_SET_BAUD_RATE`
- Line format:
  - `IOCTL_SERIAL_GET_LINE_CONTROL`
  - `IOCTL_SERIAL_SET_LINE_CONTROL`
- Timeout behavior:
  - `IOCTL_SERIAL_GET_TIMEOUTS`
  - `IOCTL_SERIAL_SET_TIMEOUTS`
- Queue and status:
  - `IOCTL_SERIAL_SET_QUEUE_SIZE`
  - `IOCTL_SERIAL_GET_COMMSTATUS`
  - `IOCTL_SERIAL_PURGE`
  - `IOCTL_SERIAL_GET_PROPERTIES`
  - `IOCTL_SERIAL_SET_FIFO_CONTROL`
  - `IOCTL_SERIAL_APPLY_DEFAULT_CONFIGURATION`
  - `IOCTL_SERIAL_CONFIG_SIZE`
  - `IOCTL_SERIAL_GET_COMMCONFIG`
  - `IOCTL_SERIAL_SET_COMMCONFIG`
  - `IOCTL_SERIAL_GET_STATS`
  - `IOCTL_SERIAL_CLEAR_STATS`
  - `IOCTL_SERIAL_IMMEDIATE_CHAR`
- Modem and handshake:
  - `IOCTL_SERIAL_GET_MODEMSTATUS`
  - `IOCTL_SERIAL_GET_MODEM_CONTROL`
  - `IOCTL_SERIAL_SET_MODEM_CONTROL`
  - `IOCTL_SERIAL_SET_DTR`
  - `IOCTL_SERIAL_CLR_DTR`
  - `IOCTL_SERIAL_SET_RTS`
  - `IOCTL_SERIAL_CLR_RTS`
  - `IOCTL_SERIAL_SET_XOFF`
  - `IOCTL_SERIAL_SET_XON`
  - `IOCTL_SERIAL_GET_HANDFLOW`
  - `IOCTL_SERIAL_SET_HANDFLOW`
- Events:
  - `IOCTL_SERIAL_SET_WAIT_MASK`
  - `IOCTL_SERIAL_GET_WAIT_MASK`
  - `IOCTL_SERIAL_WAIT_ON_MASK`

ESP-DAP-style workflows especially need baud-rate propagation and DTR/RTS
changes to be correct.

## Data Flow

Client write:

```text
IRP_MJ_WRITE
  -> copy request buffer into driver-owned nonpaged memory
  -> enqueue TxData event for service
  -> complete write when service accepts bytes into its outbound queue
```

Service outbound:

```text
VComTunnel.Service
  -> receives TxData event
  -> sends bytes to RFC2217 endpoint
  -> reports accepted/failure through private IOCTL completion
```

Network inbound:

```text
RFC2217 endpoint
  -> VComTunnel.Service
  -> IOCTL_VCOMTUNNEL_PUSH_RX
  -> driver RX ring buffer
  -> complete pending reads
```

Line-control changes:

```text
serial client IOCTL
  -> update driver cached serial state
  -> enqueue LineStateChanged event
  -> service translates to RFC2217 negotiation/control
```

## Buffering

Initial limits:

- RX ring buffer: 64 KiB.
- Per-write maximum: 64 KiB.
- TX pending total: 256 KiB.
- Drop policy: do not drop silently. Return status to client/service and log.

When the service is disconnected:

- Reads wait according to configured timeouts.
- Writes complete with `STATUS_DEVICE_NOT_READY` unless a compatibility mode is
  added later.
- Line-control IOCTLs update local cache but return a warning event to service
  only after reconnect.

## Timeouts and Cancellation

Use WDF request cancellation and timers. Every pending read/write/wait request
must be cancellable. Driver unload, device removal, service disconnect, and
serial handle cleanup must complete or cancel all outstanding requests.

The first prototype can implement conservative timeout behavior, then refine it
against .NET `SerialPort`, PuTTY, and firmware tool behavior.

## Service Offline Behavior

Required behavior:

- No system hang.
- No user-mode app hang beyond configured serial timeouts.
- No bugcheck when `VComTunnel.Service` exits, restarts, or disconnects while a
  serial client has pending I/O.

Driver states:

```text
NoService -> ServiceAttached -> NetworkOnline
          -> NetworkOffline
          -> ServiceDetached
          -> Removing
```

`NoService` and `ServiceDetached` are not fatal device states. They are normal
operational states with controlled I/O failures.

## Service Responsibilities

Keep these in user mode:

- RFC2217 negotiation and reconnect.
- DNS and TCP.
- Logging and diagnostics.
- Mapping configuration.
- Driver installation orchestration.
- COM allocation policy.
- Retry/backoff.

The driver should only emulate a serial device and exchange bounded messages
with the service.

## Installation and Signing

Prototype installation requires:

- WDK build output: `VComTunnel.Serial.sys`.
- INF: `VComTunnel.Serial.inf`.
- Test catalog: `VComTunnel.Serial.cat`.
- Test signing enabled on the target machine.
- Manual administrator approval.

Do not install from the GUI automatically. The GUI can show diagnostics and
launch a reviewed script, but driver install must remain explicit.

## Milestones

M0: Design only.

- This document and protocol spec exist.
- INF/script block accidental install without package files.

M1: Skeleton driver loads.

- WDK project builds.
- Device Manager shows `VComTunnel Virtual Serial Port (COM40)`.
- `CreateFile("\\\\.\\COM40")` succeeds or fails deterministically.

M2: Service channel only.

- `VComTunnel.Service` attaches to the private interface.
- Driver emits open/close/control events.
- No RFC2217.
- Service-side behavior follows `SERVICE_BACKEND.md`.

M3: Serial loopback through service.

- A fake service backend echoes bytes.
- Reads/writes, timeouts, cancellation, DTR/RTS, and baud state are observable.
- GUI can start `kmdf` in loopback mode without com0com/hub4com.

M4: RFC2217 backend.

- Service bridges driver events to the existing RFC2217 adapter.
- GUI `kmdf` mapping can start one port without com0com/hub4com.

M5: Hardening.

- Driver Verifier smoke.
- Reconnect tests.
- Service crash/restart tests.
- Install/uninstall and COM rename tests.

## Risks

- Serial clients depend on subtle timeout and IOCTL behavior.
- Incorrect cancellation or queue ownership can hang apps or bugcheck the
  machine.
- COM name allocation conflicts are easy to create.
- Driver signing and test-signing state will slow iteration.
- Full serial compatibility is larger than the first ESP-DAP use case.

## References

- Microsoft Learn: INF AddReg directive:
  https://learn.microsoft.com/en-us/windows-hardware/drivers/install/inf-addreg-directive

- Service-side backend design:
  SERVICE_BACKEND.md
