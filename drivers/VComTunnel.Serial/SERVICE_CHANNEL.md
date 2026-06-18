# VComTunnel.Serial Service Channel

The driver cannot initiate calls into user mode. Use an inverted-call protocol:
`VComTunnel.Service` keeps one or more overlapped IOCTL requests pending, and
the driver completes them when a serial-side event occurs.

## Channel Shape

Expose a private device interface separate from the COM port. Only
`VComTunnel.Service` should open it.

Prototype naming:

```text
Interface GUID: {TBD-VCOMTUNNEL-SERVICE-CHANNEL}
Client:         VComTunnel.Service
Transport:      DeviceIoControl with overlapped I/O
Encoding:       fixed binary headers plus payload
Version:        uint16 major/minor in every attach; current version 1.2
```

Do not use JSON in the driver channel. Keep the kernel boundary fixed-size,
bounded, and easy to validate.

## Private IOCTLs

Use a vendor device type and private function codes.

```c
#define FILE_DEVICE_VCOMTUNNEL 0x8000

#define IOCTL_VCOMTUNNEL_ATTACH \
    CTL_CODE(FILE_DEVICE_VCOMTUNNEL, 0x801, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)

#define IOCTL_VCOMTUNNEL_WAIT_EVENT \
    CTL_CODE(FILE_DEVICE_VCOMTUNNEL, 0x802, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)

#define IOCTL_VCOMTUNNEL_COMPLETE_EVENT \
    CTL_CODE(FILE_DEVICE_VCOMTUNNEL, 0x803, METHOD_BUFFERED, FILE_WRITE_DATA)

#define IOCTL_VCOMTUNNEL_PUSH_RX \
    CTL_CODE(FILE_DEVICE_VCOMTUNNEL, 0x804, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)

#define IOCTL_VCOMTUNNEL_SET_CONNECTION_STATE \
    CTL_CODE(FILE_DEVICE_VCOMTUNNEL, 0x805, METHOD_BUFFERED, FILE_WRITE_DATA)

#define IOCTL_VCOMTUNNEL_DETACH \
    CTL_CODE(FILE_DEVICE_VCOMTUNNEL, 0x806, METHOD_BUFFERED, FILE_WRITE_DATA)

#define IOCTL_VCOMTUNNEL_SET_MODEM_STATE \
    CTL_CODE(FILE_DEVICE_VCOMTUNNEL, 0x807, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)

#define IOCTL_VCOMTUNNEL_SET_LINE_STATE \
    CTL_CODE(FILE_DEVICE_VCOMTUNNEL, 0x808, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)

#define IOCTL_VCOMTUNNEL_SET_REMOTE_SETTINGS \
    CTL_CODE(FILE_DEVICE_VCOMTUNNEL, 0x809, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)
```

The M2 prototype uses `METHOD_BUFFERED` for all private IOCTLs. That keeps the
first service implementation simple and matches the current driver header.

## Attach

Service sends:

```c
struct VCT_ATTACH_REQUEST {
    UINT16 ProtocolMajor;
    UINT16 ProtocolMinor;
    UINT32 Flags;
    WCHAR  ServiceInstanceId[64];
};
```

Driver returns:

```c
struct VCT_ATTACH_RESPONSE {
    UINT16 ProtocolMajor;
    UINT16 ProtocolMinor;
    UINT32 DriverFlags;
    WCHAR  PortName[32];
    UINT32 MaxEventBytes;
    UINT32 MaxRxBytes;
};
```

Rules:

- Only one attached service in the prototype.
- A second attach fails with `STATUS_DEVICE_BUSY`.
- Service detach or handle close transitions driver state to `ServiceDetached`.

## Events From Driver To Service

`IOCTL_VCOMTUNNEL_WAIT_EVENT` is pended by the service. The driver completes it
with one event:

```c
struct VCT_EVENT_HEADER {
    UINT32 Size;
    UINT32 RequestId;
    UINT16 Type;
    UINT16 Flags;
    UINT64 Sequence;
};
```

Event types:

```text
TxData
SetBaudRate
SetLineControl
SetModemControl
SetHandflow
SetBreak
Purge
```

`RequestId` is nonzero when the service must complete a client-facing driver
request. Fire-and-forget state notifications can use `RequestId = 0`.

## Completing Events

The strict protocol lets service reply for events that represent serial client
I/O:

```c
struct VCT_COMPLETE_EVENT {
    UINT32 RequestId;
    NTSTATUS Status;
    UINT32 Information;
    UINT32 Flags;
};
```

Examples:

- Write accepted into service TCP/RFC2217 queue:
  - `Status = STATUS_SUCCESS`
  - `Information = bytesAccepted`
- Network offline:
  - `Status = STATUS_DEVICE_NOT_READY`
  - `Information = 0`
- Request canceled before service replied:
  - Driver rejects late completion with `STATUS_NOT_FOUND`.

Current M2 behavior:

- `TxData` writes complete when the driver has copied bytes into a pending
  service `WAIT_EVENT` output buffer.
- If no service wait is pending, serial writes are queued only when the complete
  write fits in the TX ring; a full queue fails without copying a partial frame
  so user-mode retries cannot duplicate bytes.
- `COMPLETE_EVENT` is reserved for the next step where write completion waits
  for user-mode RFC2217 acceptance.

## RX Path

Service sends inbound bytes:

```c
struct VCT_PUSH_RX {
    UINT32 Flags;
    UINT32 ByteCount;
    UCHAR  Bytes[ByteCount];
};
```

Driver behavior:

- Validate `ByteCount <= MaxRxBytes`.
- If the full frame fits, copy it into the RX ring buffer.
- Complete pending reads in FIFO order.
- Signal `SERIAL_EV_RXCHAR` for pushed RX bytes, and also signal
  `SERIAL_EV_RXFLAG` when the frame contains the current
  `SERIAL_CHARS.EventChar`.
- Signal `SERIAL_EV_RX80FULL` when the accepted frame leaves the RX ring buffer
  at or above 80% occupancy.
- Serial reads with an empty RX queue are held as one cancelable pending read
  while the service is connecting/online, and completed when the next `PUSH_RX`
  frame arrives.
- If connection state changes to offline/faulted, any pending serial read is
  completed with `STATUS_DEVICE_NOT_READY`.
- If the full frame does not fit, fail the IOCTL with `STATUS_BUFFER_OVERFLOW`
  without copying a partial frame; service sends RFC2217 `FLOWCONTROL-SUSPEND`,
  retries the same frame, then sends `FLOWCONTROL-RESUME` after success.

## Connection State

Service informs driver of RFC2217 state:

```text
Offline
Connecting
Online
Faulted
Stopping
```

The driver uses this state for `GET_COMMSTATUS`, empty-read behavior, write
failure behavior, and diagnostics. It must not reconnect by itself.

## Remote Accepted Settings

RFC2217 server-to-client `SET-BAUDRATE`, `SET-DATASIZE`, `SET-PARITY`, and
`SET-STOPSIZE` responses are also the server's accepted serial settings. The
service updates the driver's cached settings for those responses, including
when the response completes a pending outbound ACK wait:

```c
#define VCOMTUNNEL_REMOTE_BAUD_RATE   0x00000001
#define VCOMTUNNEL_REMOTE_WORD_LENGTH 0x00000002
#define VCOMTUNNEL_REMOTE_PARITY      0x00000004
#define VCOMTUNNEL_REMOTE_STOP_BITS   0x00000008

struct VCT_REMOTE_SETTINGS {
    UINT32 Mask;
    UINT32 BaudRate;
    UCHAR  StopBits;
    UCHAR  Parity;
    UCHAR  WordLength;
    UCHAR  Reserved;
};
```

The driver applies only fields selected by `Mask`. This keeps
`IOCTL_SERIAL_GET_BAUD_RATE` and `IOCTL_SERIAL_GET_LINE_CONTROL` aligned with
unsolicited or delayed RFC2217 accepted-setting notifications, while normal
local serial IOCTLs still generate outbound RFC2217 control events. This IOCTL
requires protocol minor version 1 or newer.

## Remote Modem And Line State

RFC2217 notifications flow from service back to driver:

```c
struct VCT_MODEM_STATE {
    UINT32 ModemStatus;
    UINT32 EventMask;
};

struct VCT_LINE_STATE {
    UINT32 Errors;
    UINT32 EventMask;
};
```

The service maps RFC2217 `NOTIFY-MODEMSTATE` current-state bits to Windows
`SERIAL_CTS_STATE`, `SERIAL_DSR_STATE`, `SERIAL_RI_STATE`, and
`SERIAL_DCD_STATE`. It maps RFC2217 `NOTIFY-MODEMSTATE` delta bits to
`SERIAL_EV_CTS`, `SERIAL_EV_DSR`, `SERIAL_EV_RING`, and `SERIAL_EV_RLSD` in
`EventMask`; the driver also raises the same event bits when cached current
state changes. It maps RFC2217 `NOTIFY-LINESTATE` error bits to Windows serial
error bits surfaced through `IOCTL_SERIAL_GET_COMMSTATUS`, and maps RFC2217
line-state Data Ready plus transmitter-empty bits to `SERIAL_EV_RXCHAR` and
`SERIAL_EV_TXEMPTY` wait-mask wakeups. The line-state IOCTL accepts the
original 4-byte `{Errors}` payload and the extended 8-byte `{Errors, EventMask}`
payload so older service builds remain compatible with the updated driver. The
service requires protocol minor version 2 or newer before sending the extended
line-state payload, so an older installed driver fails attach instead of losing
wait-mask wakeups later in the session.

## Serial Statistics

The driver supports `IOCTL_SERIAL_GET_STATS` and `IOCTL_SERIAL_CLEAR_STATS`.
`ReceivedCount` tracks service `PUSH_RX` bytes accepted into the RX queue.
`TransmittedCount` tracks bytes accepted from serial client writes, whether
they are delivered directly to a waiting service request or queued for later
service consumption. RFC2217 line-state notifications update framing, serial
overrun, queue overrun, and parity counters when the corresponding Windows
serial error bits are reported.

## Serial Configuration

The driver supports the Windows serial configuration probe IOCTLs:
`IOCTL_SERIAL_CONFIG_SIZE`, `IOCTL_SERIAL_GET_COMMCONFIG`, and
`IOCTL_SERIAL_SET_COMMCONFIG`. The returned `SERIALCONFIG` advertises an RS232
subtype with no provider-specific data. `SET_COMMCONFIG` accepts the same
minimal shape and leaves baud-rate, line-control, and handflow state to their
dedicated serial IOCTLs so those changes continue to generate RFC2217 control
events.

## Queue And UART Configuration

`IOCTL_SERIAL_SET_QUEUE_SIZE` validates the requested `SERIAL_QUEUE_SIZE`
against the fixed virtual RX/TX queue capacities. Requests larger than the
driver's current 4096-byte RX or TX queues fail with
`STATUS_INSUFFICIENT_RESOURCES`; accepted requests do not reallocate the fixed
buffers. Hardware UART configuration requests with no RFC2217 equivalent,
`IOCTL_SERIAL_SET_FIFO_CONTROL` and
`IOCTL_SERIAL_APPLY_DEFAULT_CONFIGURATION`, are accepted as no-op compatibility
requests.

## Immediate Transmit

`IOCTL_SERIAL_IMMEDIATE_CHAR` accepts one byte from the serial client and
queues it as a `TxData` service event ahead of the normal TX ring buffer. The
service sends it through the existing RFC2217 serial-data path, including IAC
escaping and remote flow-control gating. If the service is not attached, the
IOCTL fails with `STATUS_DEVICE_NOT_READY`.

## Manual Flow Control

`IOCTL_SERIAL_SET_XOFF` and `IOCTL_SERIAL_SET_XON` queue a local flow-control
event for the service. The service translates those events to RFC2217
`FLOWCONTROL-SUSPEND` and `FLOWCONTROL-RESUME` respectively, asking the remote
endpoint to pause or resume data sent toward the local COM port. If the service
is not attached, the IOCTL fails with `STATUS_DEVICE_NOT_READY`.

## Raw Modem Control

`IOCTL_SERIAL_GET_MODEM_CONTROL` returns the driver's cached MCR-style control
bits. `IOCTL_SERIAL_SET_MODEM_CONTROL` accepts `SERIAL_IOC_MCR_DTR`,
`SERIAL_IOC_MCR_RTS`, `SERIAL_IOC_MCR_OUT1`, `SERIAL_IOC_MCR_OUT2`, and
`SERIAL_IOC_MCR_LOOP`; unsupported bits fail with `STATUS_INVALID_PARAMETER`.
Changes to DTR or RTS are queued as normal modem-control events so the service
translates them to RFC2217 SET-CONTROL commands. OUT1, OUT2, and LOOP are
cached for local `GET_MODEM_CONTROL` compatibility and do not generate RFC2217
traffic.

## Ordering Rules

- Preserve TxData order per serial handle.
- Preserve RX byte order.
- Immediate transmit bytes are delivered before already queued TX ring-buffer
  bytes that have not yet been handed to the service.
- Control events are sequenced relative to TxData by the driver's event
  sequence number.
- Service should process events serially for the first prototype.
- Later versions may allow pipelined writes if RequestIds preserve completion.

## Versioning

Rules:

- Major version mismatch: attach fails.
- Minor version mismatch: attach succeeds only if both sides can operate on the
  lower minor version.
- Unknown event types are fatal for the prototype and should detach the service.

## Failure Handling

Service process exits:

- Control handle closes.
- Driver completes all pending service IOCTLs.
- Driver marks service disconnected.
- Pending serial requests complete according to timeout or
  `STATUS_DEVICE_NOT_READY`.

Driver surprise removal:

- Service receives failed IOCTLs.
- Service marks mapping faulted.
- GUI shows driver removed/reinstall required.

Network disconnect:

- Service keeps channel attached.
- Driver stays installed and openable.
- Serial writes fail or time out according to policy until network returns.
