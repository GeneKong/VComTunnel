#include "Driver.h"

static NTSTATUS
VctCopyInputBuffer(
    _In_ WDFREQUEST Request,
    _Out_writes_bytes_(Size) PVOID Target,
    _In_ size_t Size
    )
{
    NTSTATUS status;
    PVOID inputBuffer;

    status = WdfRequestRetrieveInputBuffer(Request, Size, &inputBuffer, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlCopyMemory(Target, inputBuffer, Size);
    return STATUS_SUCCESS;
}

static NTSTATUS
VctCopyOutputBuffer(
    _In_ WDFREQUEST Request,
    _In_reads_bytes_(Size) const VOID* Source,
    _In_ size_t Size
    )
{
    NTSTATUS status;
    PVOID outputBuffer;

    status = WdfRequestRetrieveOutputBuffer(Request, Size, &outputBuffer, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlCopyMemory(outputBuffer, Source, Size);
    WdfRequestSetInformation(Request, Size);
    return STATUS_SUCCESS;
}

static NTSTATUS
VctCompleteCommProperties(
    _In_ WDFREQUEST Request
    )
{
    SERIAL_COMMPROP properties;

    RtlZeroMemory(&properties, sizeof(properties));
    properties.PacketLength = sizeof(properties);
    properties.PacketVersion = 2;
    properties.ServiceMask = SERIAL_SP_SERIALCOMM;
    properties.MaxTxQueue = VCOMTUNNEL_TX_QUEUE_SIZE;
    properties.MaxRxQueue = VCOMTUNNEL_RX_QUEUE_SIZE;
    properties.MaxBaud = SERIAL_BAUD_USER;
    properties.ProvSubType = SERIAL_SP_RS232;
    properties.ProvCapabilities =
        SERIAL_PCF_DTRDSR |
        SERIAL_PCF_RTSCTS |
        SERIAL_PCF_PARITY_CHECK |
        SERIAL_PCF_TOTALTIMEOUTS |
        SERIAL_PCF_INTTIMEOUTS;
    properties.SettableParams =
        SERIAL_SP_PARITY |
        SERIAL_SP_BAUD |
        SERIAL_SP_DATABITS |
        SERIAL_SP_STOPBITS |
        SERIAL_SP_HANDSHAKING |
        SERIAL_SP_PARITY_CHECK |
        SERIAL_SP_CARRIER_DETECT;
    properties.SettableBaud = SERIAL_BAUD_075 |
        SERIAL_BAUD_110 |
        SERIAL_BAUD_300 |
        SERIAL_BAUD_600 |
        SERIAL_BAUD_1200 |
        SERIAL_BAUD_2400 |
        SERIAL_BAUD_4800 |
        SERIAL_BAUD_9600 |
        SERIAL_BAUD_19200 |
        SERIAL_BAUD_38400 |
        SERIAL_BAUD_57600 |
        SERIAL_BAUD_115200 |
        SERIAL_BAUD_USER;
    properties.SettableData =
        SERIAL_DATABITS_5 |
        SERIAL_DATABITS_6 |
        SERIAL_DATABITS_7 |
        SERIAL_DATABITS_8;
    properties.SettableStopParity =
        SERIAL_STOPBITS_10 |
        SERIAL_STOPBITS_15 |
        SERIAL_STOPBITS_20 |
        SERIAL_PARITY_NONE |
        SERIAL_PARITY_ODD |
        SERIAL_PARITY_EVEN |
        SERIAL_PARITY_MARK |
        SERIAL_PARITY_SPACE;
    properties.CurrentTxQueue = VCOMTUNNEL_TX_QUEUE_SIZE;
    properties.CurrentRxQueue = VCOMTUNNEL_RX_QUEUE_SIZE;

    return VctCopyOutputBuffer(Request, &properties, sizeof(properties));
}

static NTSTATUS
VctCompleteCommStatus(
    _In_ WDFREQUEST Request,
    _In_ PDEVICE_CONTEXT Context
    )
{
    SERIAL_STATUS status;

    RtlZeroMemory(&status, sizeof(status));
    status.Errors = Context->LineErrors;
    status.HoldReasons = 0;
    status.AmountInInQueue = Context->RxCount;
    status.AmountInOutQueue = Context->TxCount;
    status.EofReceived = FALSE;
    status.WaitForImmediate = FALSE;

    return VctCopyOutputBuffer(Request, &status, sizeof(status));
}

static VOID
VctCompleteUnmarkedRequest(
    _In_ WDFREQUEST Request,
    _In_ NTSTATUS Status
    )
{
    NTSTATUS unmarkStatus;

    unmarkStatus = WdfRequestUnmarkCancelable(Request);
    if (unmarkStatus != STATUS_CANCELLED) {
        WdfRequestComplete(Request, Status);
    }
}

static VOID
VctCompleteWaitMaskRequest(
    _In_ WDFREQUEST Request,
    _In_ ULONG EventMask,
    _In_ BOOLEAN UnmarkCancelable
    )
{
    NTSTATUS status;
    ULONG* outputBuffer;

    if (UnmarkCancelable &&
        WdfRequestUnmarkCancelable(Request) == STATUS_CANCELLED) {
        return;
    }

    status = WdfRequestRetrieveOutputBuffer(Request, sizeof(EventMask), (PVOID*)&outputBuffer, NULL);
    if (NT_SUCCESS(status)) {
        *outputBuffer = EventMask;
        WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, sizeof(EventMask));
    } else {
        WdfRequestComplete(Request, status);
    }
}

static VOID
VctSignalWaitMaskEvent(
    _Inout_ PDEVICE_CONTEXT Context,
    _In_ ULONG EventMask
    )
{
    WDFREQUEST waitRequest = NULL;
    ULONG matchedMask = 0;

    WdfSpinLockAcquire(Context->Lock);
    matchedMask = Context->WaitMask & EventMask;
    if (matchedMask != 0 && Context->PendingWaitMask != NULL) {
        waitRequest = Context->PendingWaitMask;
        Context->PendingWaitMask = NULL;
    }
    WdfSpinLockRelease(Context->Lock);

    if (waitRequest != NULL) {
        VctCompleteWaitMaskRequest(waitRequest, matchedMask, TRUE);
    }
}

static ULONG
VctRxCopyOutLocked(
    _Inout_ PDEVICE_CONTEXT Context,
    _Out_writes_bytes_(Length) UCHAR* Buffer,
    _In_ ULONG Length
    )
{
    ULONG copied = 0;

    while (copied < Length && Context->RxCount > 0) {
        Buffer[copied] = Context->RxBuffer[Context->RxTail];
        Context->RxTail = (Context->RxTail + 1) % VCOMTUNNEL_RX_QUEUE_SIZE;
        Context->RxCount--;
        copied++;
    }

    return copied;
}

static ULONG
VctRxCopyInLocked(
    _Inout_ PDEVICE_CONTEXT Context,
    _In_reads_bytes_(Length) const UCHAR* Buffer,
    _In_ ULONG Length
    )
{
    ULONG copied = 0;

    while (copied < Length && Context->RxCount < VCOMTUNNEL_RX_QUEUE_SIZE) {
        Context->RxBuffer[Context->RxHead] = Buffer[copied];
        Context->RxHead = (Context->RxHead + 1) % VCOMTUNNEL_RX_QUEUE_SIZE;
        Context->RxCount++;
        copied++;
    }

    return copied;
}

static ULONG
VctTxCopyOutLocked(
    _Inout_ PDEVICE_CONTEXT Context,
    _Out_writes_bytes_(Length) UCHAR* Buffer,
    _In_ ULONG Length
    )
{
    ULONG copied = 0;

    while (copied < Length && Context->TxCount > 0) {
        Buffer[copied] = Context->TxBuffer[Context->TxTail];
        Context->TxTail = (Context->TxTail + 1) % VCOMTUNNEL_TX_QUEUE_SIZE;
        Context->TxCount--;
        copied++;
    }

    return copied;
}

static ULONG
VctTxCopyInLocked(
    _Inout_ PDEVICE_CONTEXT Context,
    _In_reads_bytes_(Length) const UCHAR* Buffer,
    _In_ ULONG Length
    )
{
    ULONG copied = 0;

    while (copied < Length && Context->TxCount < VCOMTUNNEL_TX_QUEUE_SIZE) {
        Context->TxBuffer[Context->TxHead] = Buffer[copied];
        Context->TxHead = (Context->TxHead + 1) % VCOMTUNNEL_TX_QUEUE_SIZE;
        Context->TxCount++;
        copied++;
    }

    return copied;
}

static VOID
VctEnqueueControlEventLocked(
    _Inout_ PDEVICE_CONTEXT Context,
    _In_ USHORT Type,
    _In_reads_bytes_opt_(PayloadSize) const VOID* Payload,
    _In_ ULONG PayloadSize
    )
{
    PVCOMTUNNEL_QUEUED_EVENT event;

    if (PayloadSize > VCOMTUNNEL_EVENT_PAYLOAD_SIZE) {
        return;
    }

    if (Context->EventCount == VCOMTUNNEL_EVENT_QUEUE_SIZE) {
        Context->EventTail = (Context->EventTail + 1) % VCOMTUNNEL_EVENT_QUEUE_SIZE;
        Context->EventCount--;
    }

    event = &Context->EventQueue[Context->EventHead];
    RtlZeroMemory(event, sizeof(*event));
    event->Type = Type;
    event->PayloadSize = PayloadSize;
    if (Payload != NULL && PayloadSize > 0) {
        RtlCopyMemory(event->Payload, Payload, PayloadSize);
    }

    Context->EventHead = (Context->EventHead + 1) % VCOMTUNNEL_EVENT_QUEUE_SIZE;
    Context->EventCount++;
}

static NTSTATUS
VctCompleteControlEventFromQueue(
    _In_ WDFREQUEST Request,
    _Inout_ PDEVICE_CONTEXT Context,
    _In_ BOOLEAN UnmarkCancelable
    )
{
    NTSTATUS status;
    UCHAR* eventBuffer;
    size_t eventBufferLength;
    VCOMTUNNEL_QUEUED_EVENT event;
    PVCT_EVENT_HEADER header;
    ULONG eventSize;

    status = WdfRequestRetrieveOutputBuffer(
        Request,
        sizeof(VCT_EVENT_HEADER) + VCOMTUNNEL_EVENT_PAYLOAD_SIZE,
        (PVOID*)&eventBuffer,
        &eventBufferLength);
    if (!NT_SUCCESS(status)) {
        if (UnmarkCancelable) {
            if (WdfRequestUnmarkCancelable(Request) != STATUS_CANCELLED) {
                WdfRequestComplete(Request, status);
            }
            return STATUS_PENDING;
        }
        WdfRequestComplete(Request, status);
        return STATUS_PENDING;
    }

    WdfSpinLockAcquire(Context->Lock);
    if (Context->EventCount == 0) {
        WdfSpinLockRelease(Context->Lock);
        if (UnmarkCancelable) {
            if (WdfRequestUnmarkCancelable(Request) != STATUS_CANCELLED) {
                WdfRequestComplete(Request, STATUS_DEVICE_NOT_READY);
            }
            return STATUS_PENDING;
        }
        WdfRequestComplete(Request, STATUS_DEVICE_NOT_READY);
        return STATUS_PENDING;
    }

    event = Context->EventQueue[Context->EventTail];
    Context->EventTail = (Context->EventTail + 1) % VCOMTUNNEL_EVENT_QUEUE_SIZE;
    Context->EventCount--;

    header = (PVCT_EVENT_HEADER)eventBuffer;
    RtlZeroMemory(header, sizeof(*header));
    header->Size = sizeof(VCT_EVENT_HEADER) + event.PayloadSize;
    header->Type = event.Type;
    header->Flags = event.Flags;
    header->Sequence = ++Context->NextSequence;
    if (event.PayloadSize > 0) {
        RtlCopyMemory(eventBuffer + sizeof(VCT_EVENT_HEADER), event.Payload, event.PayloadSize);
    }
    eventSize = header->Size;
    WdfSpinLockRelease(Context->Lock);

    if (UnmarkCancelable) {
        if (WdfRequestUnmarkCancelable(Request) != STATUS_CANCELLED) {
            WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, eventSize);
        }
        return STATUS_PENDING;
    }

    WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, eventSize);
    return STATUS_PENDING;
}

static VOID
VctQueueControlEvent(
    _Inout_ PDEVICE_CONTEXT Context,
    _In_ USHORT Type,
    _In_reads_bytes_opt_(PayloadSize) const VOID* Payload,
    _In_ ULONG PayloadSize
    )
{
    WDFREQUEST serviceWait = NULL;

    WdfSpinLockAcquire(Context->Lock);
    if (Context->ServiceAttached) {
        VctEnqueueControlEventLocked(Context, Type, Payload, PayloadSize);
        if (Context->PendingServiceWait != NULL) {
            serviceWait = Context->PendingServiceWait;
            Context->PendingServiceWait = NULL;
        }
    }
    WdfSpinLockRelease(Context->Lock);

    if (serviceWait != NULL) {
        VctCompleteControlEventFromQueue(serviceWait, Context, TRUE);
    }
}

static VOID
VctEnqueueCurrentStateLocked(
    _Inout_ PDEVICE_CONTEXT Context
    )
{
    VCT_BAUD_RATE_EVENT baudRateEvent;
    VCT_LINE_CONTROL_EVENT lineControlEvent;
    VCT_HANDFLOW_EVENT handflowEvent;
    VCT_MODEM_CONTROL_EVENT modemControlEvent;

    baudRateEvent.BaudRate = Context->BaudRate.BaudRate;
    VctEnqueueControlEventLocked(
        Context,
        VComTunnelEventSetBaudRate,
        &baudRateEvent,
        sizeof(baudRateEvent));

    RtlZeroMemory(&lineControlEvent, sizeof(lineControlEvent));
    lineControlEvent.StopBits = Context->LineControl.StopBits;
    lineControlEvent.Parity = Context->LineControl.Parity;
    lineControlEvent.WordLength = Context->LineControl.WordLength;
    VctEnqueueControlEventLocked(
        Context,
        VComTunnelEventSetLineControl,
        &lineControlEvent,
        sizeof(lineControlEvent));

    handflowEvent.ControlHandShake = Context->Handflow.ControlHandShake;
    handflowEvent.FlowReplace = Context->Handflow.FlowReplace;
    VctEnqueueControlEventLocked(
        Context,
        VComTunnelEventSetHandflow,
        &handflowEvent,
        sizeof(handflowEvent));

    RtlZeroMemory(&modemControlEvent, sizeof(modemControlEvent));
    modemControlEvent.Mask = VCOMTUNNEL_MODEM_CONTROL_DTR | VCOMTUNNEL_MODEM_CONTROL_RTS;
    modemControlEvent.Dtr = Context->Dtr;
    modemControlEvent.Rts = Context->Rts;
    VctEnqueueControlEventLocked(
        Context,
        VComTunnelEventSetModemControl,
        &modemControlEvent,
        sizeof(modemControlEvent));
}

static NTSTATUS
VctCompleteTxEventFromQueue(
    _In_ WDFREQUEST Request,
    _Inout_ PDEVICE_CONTEXT Context,
    _In_ ULONG PayloadLength
    )
{
    NTSTATUS status;
    UCHAR* eventBuffer;
    size_t eventBufferLength;
    ULONG copied;
    ULONG eventSize;
    PVCT_EVENT_HEADER header;
    BOOLEAN signalTxEmpty = FALSE;

    eventSize = (ULONG)(sizeof(VCT_EVENT_HEADER) + PayloadLength);
    status = WdfRequestRetrieveOutputBuffer(Request, eventSize, (PVOID*)&eventBuffer, &eventBufferLength);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WdfSpinLockAcquire(Context->Lock);
    copied = VctTxCopyOutLocked(Context, eventBuffer + sizeof(VCT_EVENT_HEADER), PayloadLength);
    signalTxEmpty = Context->TxCount == 0;
    header = (PVCT_EVENT_HEADER)eventBuffer;
    RtlZeroMemory(header, sizeof(*header));
    header->Size = sizeof(VCT_EVENT_HEADER) + copied;
    header->Type = VComTunnelEventTxData;
    header->Sequence = ++Context->NextSequence;
    WdfSpinLockRelease(Context->Lock);

    WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, header->Size);
    if (signalTxEmpty) {
        VctSignalWaitMaskEvent(Context, SERIAL_EV_TXEMPTY);
    }
    return STATUS_SUCCESS;
}

static NTSTATUS
VctCompleteAttach(
    _In_ WDFREQUEST Request,
    _Inout_ PDEVICE_CONTEXT Context
    )
{
    NTSTATUS status;
    PVCT_ATTACH_REQUEST attach;
    size_t inputLength;
    VCT_ATTACH_RESPONSE response;
    WDFFILEOBJECT fileObject;

    status = WdfRequestRetrieveInputBuffer(Request, sizeof(*attach), (PVOID*)&attach, &inputLength);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (attach->ProtocolMajor != VCOMTUNNEL_PROTOCOL_MAJOR) {
        return STATUS_REVISION_MISMATCH;
    }

    fileObject = WdfRequestGetFileObject(Request);

    WdfSpinLockAcquire(Context->Lock);
    if (Context->ServiceAttached) {
        WdfSpinLockRelease(Context->Lock);
        return STATUS_DEVICE_BUSY;
    }
    Context->ServiceAttached = TRUE;
    Context->ServiceFileObject = fileObject;
    Context->ConnectionState = VComTunnelConnecting;
    VctEnqueueCurrentStateLocked(Context);
    WdfSpinLockRelease(Context->Lock);

    RtlZeroMemory(&response, sizeof(response));
    response.ProtocolMajor = VCOMTUNNEL_PROTOCOL_MAJOR;
    response.ProtocolMinor = VCOMTUNNEL_PROTOCOL_MINOR;
    response.MaxEventBytes = VCOMTUNNEL_MAX_EVENT_BYTES;
    response.MaxRxBytes = VCOMTUNNEL_MAX_RX_BYTES;
    RtlStringCchCopyW(response.PortName, RTL_NUMBER_OF(response.PortName), Context->PortName);

    return VctCopyOutputBuffer(Request, &response, sizeof(response));
}

static NTSTATUS
VctQueueServiceWait(
    _In_ WDFREQUEST Request,
    _Inout_ PDEVICE_CONTEXT Context
    )
{
    NTSTATUS status = STATUS_SUCCESS;
    WDFFILEOBJECT fileObject;
    ULONG queuedBytes = 0;

    fileObject = WdfRequestGetFileObject(Request);

    WdfSpinLockAcquire(Context->Lock);
    if (!Context->ServiceAttached) {
        status = STATUS_DEVICE_NOT_READY;
    } else if (Context->ServiceFileObject != fileObject) {
        status = STATUS_ACCESS_DENIED;
    } else if (Context->PendingServiceWait != NULL) {
        status = STATUS_DEVICE_BUSY;
    } else if (Context->EventCount > 0) {
        WdfSpinLockRelease(Context->Lock);
        return VctCompleteControlEventFromQueue(Request, Context, FALSE);
    } else if (Context->TxCount > 0) {
        queuedBytes = min(Context->TxCount, (ULONG)(VCOMTUNNEL_MAX_EVENT_BYTES - sizeof(VCT_EVENT_HEADER)));
    } else {
        Context->PendingServiceWait = Request;
        status = WdfRequestMarkCancelableEx(Request, VctEvtRequestCancel);
        if (NT_SUCCESS(status)) {
            Request = NULL;
        } else {
            Context->PendingServiceWait = NULL;
        }
    }
    WdfSpinLockRelease(Context->Lock);

    if (queuedBytes > 0) {
        status = VctCompleteTxEventFromQueue(Request, Context, queuedBytes);
        if (!NT_SUCCESS(status)) {
            WdfRequestComplete(Request, status);
        }
        return STATUS_PENDING;
    }

    if (Request != NULL) {
        WdfRequestComplete(Request, status);
    }

    return STATUS_PENDING;
}

static NTSTATUS
VctPushRx(
    _In_ WDFREQUEST Request,
    _Inout_ PDEVICE_CONTEXT Context
    )
{
    NTSTATUS status;
    PVCT_PUSH_RX push;
    size_t inputLength;
    WDFREQUEST readRequest = NULL;
    ULONG pushed;
    ULONG readBytes = 0;
    UCHAR* readBuffer = NULL;
    size_t readBufferLength = 0;

    status = WdfRequestRetrieveInputBuffer(Request, sizeof(*push), (PVOID*)&push, &inputLength);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (push->ByteCount > VCOMTUNNEL_MAX_RX_BYTES ||
        inputLength < FIELD_OFFSET(VCT_PUSH_RX, Bytes) + push->ByteCount) {
        return STATUS_INVALID_PARAMETER;
    }

    WdfSpinLockAcquire(Context->Lock);
    pushed = VctRxCopyInLocked(Context, push->Bytes, push->ByteCount);
    if (pushed == push->ByteCount && Context->PendingRead != NULL) {
        readRequest = Context->PendingRead;
        Context->PendingRead = NULL;
    }
    WdfSpinLockRelease(Context->Lock);

    if (pushed != push->ByteCount) {
        return STATUS_BUFFER_OVERFLOW;
    }

    if (pushed > 0) {
        VctSignalWaitMaskEvent(Context, SERIAL_EV_RXCHAR);
    }

    if (readRequest != NULL) {
        status = WdfRequestUnmarkCancelable(readRequest);
        if (status == STATUS_CANCELLED) {
            return STATUS_SUCCESS;
        }

        status = WdfRequestRetrieveOutputBuffer(readRequest, 1, (PVOID*)&readBuffer, &readBufferLength);
        if (NT_SUCCESS(status)) {
            WdfSpinLockAcquire(Context->Lock);
            readBytes = VctRxCopyOutLocked(Context, readBuffer, (ULONG)readBufferLength);
            WdfSpinLockRelease(Context->Lock);
            WdfRequestCompleteWithInformation(readRequest, STATUS_SUCCESS, readBytes);
        } else {
            WdfRequestComplete(readRequest, status);
        }
    }

    return STATUS_SUCCESS;
}

NTSTATUS
VctQueueInitialize(
    _In_ WDFDEVICE Device
    )
{
    NTSTATUS status;
    WDF_IO_QUEUE_CONFIG queueConfig;
    PDEVICE_CONTEXT context;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = VctEvtIoDeviceControl;
    queueConfig.EvtIoRead = VctEvtIoRead;
    queueConfig.EvtIoWrite = VctEvtIoWrite;

    context = DeviceGetContext(Device);
    status = WdfIoQueueCreate(Device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &context->DefaultQueue);

    return status;
}

VOID
VctEvtRequestCancel(
    _In_ WDFREQUEST Request
    )
{
    WDFQUEUE queue;
    WDFDEVICE device;
    PDEVICE_CONTEXT context;

    queue = WdfRequestGetIoQueue(Request);
    device = WdfIoQueueGetDevice(queue);
    context = DeviceGetContext(device);

    WdfSpinLockAcquire(context->Lock);
    if (context->PendingRead == Request) {
        context->PendingRead = NULL;
    }
    if (context->PendingWaitMask == Request) {
        context->PendingWaitMask = NULL;
    }
    if (context->PendingServiceWait == Request) {
        context->PendingServiceWait = NULL;
    }
    WdfSpinLockRelease(context->Lock);

    WdfRequestComplete(Request, STATUS_CANCELLED);
}

VOID
VctCancelPendingRequestsForFile(
    _In_ WDFDEVICE Device,
    _In_ WDFFILEOBJECT FileObject,
    _In_ NTSTATUS Status
    )
{
    PDEVICE_CONTEXT context;
    WDFREQUEST readRequest = NULL;
    WDFREQUEST waitMaskRequest = NULL;
    WDFREQUEST serviceWait = NULL;

    context = DeviceGetContext(Device);

    WdfSpinLockAcquire(context->Lock);
    if (context->PendingRead != NULL &&
        WdfRequestGetFileObject(context->PendingRead) == FileObject) {
        readRequest = context->PendingRead;
        context->PendingRead = NULL;
    }
    if (context->PendingWaitMask != NULL &&
        WdfRequestGetFileObject(context->PendingWaitMask) == FileObject) {
        waitMaskRequest = context->PendingWaitMask;
        context->PendingWaitMask = NULL;
    }
    if (context->ServiceAttached &&
        context->ServiceFileObject == FileObject) {
        context->ServiceAttached = FALSE;
        context->ServiceFileObject = NULL;
        context->ConnectionState = VComTunnelDisconnected;
    }
    if (context->PendingServiceWait != NULL &&
        WdfRequestGetFileObject(context->PendingServiceWait) == FileObject) {
        serviceWait = context->PendingServiceWait;
        context->PendingServiceWait = NULL;
    }
    WdfSpinLockRelease(context->Lock);

    if (readRequest != NULL) {
        VctCompleteUnmarkedRequest(readRequest, Status);
    }
    if (waitMaskRequest != NULL) {
        VctCompleteUnmarkedRequest(waitMaskRequest, Status);
    }
    if (serviceWait != NULL) {
        VctCompleteUnmarkedRequest(serviceWait, Status);
    }
}

VOID
VctEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    NTSTATUS status = STATUS_SUCCESS;
    WDFDEVICE device;
    PDEVICE_CONTEXT context;
    ULONG modemStatus;
    WDFREQUEST serviceWait = NULL;
    WDFFILEOBJECT fileObject;

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    device = WdfIoQueueGetDevice(Queue);
    context = DeviceGetContext(device);
    fileObject = WdfRequestGetFileObject(Request);
    WdfRequestSetInformation(Request, 0);

    switch (IoControlCode) {
    case IOCTL_SERIAL_SET_BAUD_RATE:
        {
            SERIAL_BAUD_RATE baudRate;
            VCT_BAUD_RATE_EVENT event;

            status = VctCopyInputBuffer(Request, &baudRate, sizeof(baudRate));
            if (NT_SUCCESS(status)) {
                context->BaudRate = baudRate;
                event.BaudRate = baudRate.BaudRate;
                VctQueueControlEvent(context, VComTunnelEventSetBaudRate, &event, sizeof(event));
            }
        }
        break;

    case IOCTL_SERIAL_GET_BAUD_RATE:
        status = VctCopyOutputBuffer(Request, &context->BaudRate, sizeof(context->BaudRate));
        break;

    case IOCTL_SERIAL_SET_LINE_CONTROL:
        {
            SERIAL_LINE_CONTROL lineControl;
            VCT_LINE_CONTROL_EVENT event;

            status = VctCopyInputBuffer(Request, &lineControl, sizeof(lineControl));
            if (NT_SUCCESS(status)) {
                context->LineControl = lineControl;
                RtlZeroMemory(&event, sizeof(event));
                event.StopBits = lineControl.StopBits;
                event.Parity = lineControl.Parity;
                event.WordLength = lineControl.WordLength;
                VctQueueControlEvent(context, VComTunnelEventSetLineControl, &event, sizeof(event));
            }
        }
        break;

    case IOCTL_SERIAL_GET_LINE_CONTROL:
        status = VctCopyOutputBuffer(Request, &context->LineControl, sizeof(context->LineControl));
        break;

    case IOCTL_SERIAL_SET_TIMEOUTS:
        status = VctCopyInputBuffer(Request, &context->Timeouts, sizeof(context->Timeouts));
        break;

    case IOCTL_SERIAL_GET_TIMEOUTS:
        status = VctCopyOutputBuffer(Request, &context->Timeouts, sizeof(context->Timeouts));
        break;

    case IOCTL_SERIAL_SET_CHARS:
        status = VctCopyInputBuffer(Request, &context->Chars, sizeof(context->Chars));
        break;

    case IOCTL_SERIAL_GET_CHARS:
        status = VctCopyOutputBuffer(Request, &context->Chars, sizeof(context->Chars));
        break;

    case IOCTL_SERIAL_SET_HANDFLOW:
        {
            SERIAL_HANDFLOW handflow;
            VCT_HANDFLOW_EVENT event;

            status = VctCopyInputBuffer(Request, &handflow, sizeof(handflow));
            if (NT_SUCCESS(status)) {
                context->Handflow = handflow;
                event.ControlHandShake = handflow.ControlHandShake;
                event.FlowReplace = handflow.FlowReplace;
                VctQueueControlEvent(context, VComTunnelEventSetHandflow, &event, sizeof(event));
            }
        }
        break;

    case IOCTL_SERIAL_GET_HANDFLOW:
        status = VctCopyOutputBuffer(Request, &context->Handflow, sizeof(context->Handflow));
        break;

    case IOCTL_SERIAL_SET_WAIT_MASK:
        {
            ULONG waitMask = 0;
            WDFREQUEST waitRequest = NULL;

            status = VctCopyInputBuffer(Request, &waitMask, sizeof(waitMask));
            if (NT_SUCCESS(status)) {
                WdfSpinLockAcquire(context->Lock);
                context->WaitMask = waitMask;
                if (waitMask == 0 && context->PendingWaitMask != NULL) {
                    waitRequest = context->PendingWaitMask;
                    context->PendingWaitMask = NULL;
                }
                WdfSpinLockRelease(context->Lock);

                if (waitRequest != NULL) {
                    VctCompleteWaitMaskRequest(waitRequest, 0, TRUE);
                }
            }
        }
        break;

    case IOCTL_SERIAL_GET_WAIT_MASK:
        status = VctCopyOutputBuffer(Request, &context->WaitMask, sizeof(context->WaitMask));
        break;

    case IOCTL_SERIAL_WAIT_ON_MASK:
        {
            PVOID outputBuffer;
            WDFREQUEST oldWaitRequest = NULL;

            status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG), &outputBuffer, NULL);
            if (!NT_SUCCESS(status)) {
                break;
            }
            UNREFERENCED_PARAMETER(outputBuffer);

            WdfSpinLockAcquire(context->Lock);
            if (context->WaitMask == 0) {
                WdfSpinLockRelease(context->Lock);
                VctCompleteWaitMaskRequest(Request, 0, FALSE);
                return;
            }

            if (context->PendingWaitMask != NULL) {
                oldWaitRequest = context->PendingWaitMask;
            }
            context->PendingWaitMask = Request;
            status = WdfRequestMarkCancelableEx(Request, VctEvtRequestCancel);
            if (!NT_SUCCESS(status)) {
                context->PendingWaitMask = NULL;
            }
            WdfSpinLockRelease(context->Lock);

            if (oldWaitRequest != NULL) {
                VctCompleteWaitMaskRequest(oldWaitRequest, 0, TRUE);
            }

            if (!NT_SUCCESS(status)) {
                break;
            }

            return;
        }

    case IOCTL_SERIAL_GET_PROPERTIES:
        status = VctCompleteCommProperties(Request);
        break;

    case IOCTL_SERIAL_GET_COMMSTATUS:
        WdfSpinLockAcquire(context->Lock);
        status = VctCompleteCommStatus(Request, context);
        WdfSpinLockRelease(context->Lock);
        break;

    case IOCTL_SERIAL_GET_MODEMSTATUS:
        modemStatus = context->ModemStatus;
        if (context->Dtr) {
            modemStatus |= SERIAL_DTR_STATE;
        }
        if (context->Rts) {
            modemStatus |= SERIAL_RTS_STATE;
        }
        status = VctCopyOutputBuffer(Request, &modemStatus, sizeof(modemStatus));
        break;

    case IOCTL_SERIAL_GET_DTRRTS:
        modemStatus = 0;
        if (context->Dtr) {
            modemStatus |= SERIAL_DTR_STATE;
        }
        if (context->Rts) {
            modemStatus |= SERIAL_RTS_STATE;
        }
        status = VctCopyOutputBuffer(Request, &modemStatus, sizeof(modemStatus));
        break;

    case IOCTL_SERIAL_SET_DTR:
        {
            VCT_MODEM_CONTROL_EVENT event;

            RtlZeroMemory(&event, sizeof(event));
            event.Mask = VCOMTUNNEL_MODEM_CONTROL_DTR;
            event.Dtr = TRUE;
            event.Rts = context->Rts;
            VctQueueControlEvent(context, VComTunnelEventSetModemControl, &event, sizeof(event));
        }
        context->Dtr = TRUE;
        break;

    case IOCTL_SERIAL_CLR_DTR:
        {
            VCT_MODEM_CONTROL_EVENT event;

            RtlZeroMemory(&event, sizeof(event));
            event.Mask = VCOMTUNNEL_MODEM_CONTROL_DTR;
            event.Dtr = FALSE;
            event.Rts = context->Rts;
            VctQueueControlEvent(context, VComTunnelEventSetModemControl, &event, sizeof(event));
        }
        context->Dtr = FALSE;
        break;

    case IOCTL_SERIAL_SET_RTS:
        {
            VCT_MODEM_CONTROL_EVENT event;

            RtlZeroMemory(&event, sizeof(event));
            event.Mask = VCOMTUNNEL_MODEM_CONTROL_RTS;
            event.Dtr = context->Dtr;
            event.Rts = TRUE;
            VctQueueControlEvent(context, VComTunnelEventSetModemControl, &event, sizeof(event));
        }
        context->Rts = TRUE;
        break;

    case IOCTL_SERIAL_CLR_RTS:
        {
            VCT_MODEM_CONTROL_EVENT event;

            RtlZeroMemory(&event, sizeof(event));
            event.Mask = VCOMTUNNEL_MODEM_CONTROL_RTS;
            event.Dtr = context->Dtr;
            event.Rts = FALSE;
            VctQueueControlEvent(context, VComTunnelEventSetModemControl, &event, sizeof(event));
        }
        context->Rts = FALSE;
        break;

    case IOCTL_SERIAL_PURGE:
        {
            ULONG purgeMask = 0;
            VCT_PURGE_EVENT event;

            status = VctCopyInputBuffer(Request, &purgeMask, sizeof(purgeMask));
            if (NT_SUCCESS(status)) {
                event.Mask = purgeMask;
                VctQueueControlEvent(context, VComTunnelEventPurge, &event, sizeof(event));
            }
        }
        break;

    case IOCTL_SERIAL_RESET_DEVICE:
    case IOCTL_SERIAL_SET_QUEUE_SIZE:
        break;

    case IOCTL_SERIAL_SET_BREAK_ON:
        {
            VCT_BREAK_EVENT event;

            RtlZeroMemory(&event, sizeof(event));
            event.Enabled = TRUE;
            VctQueueControlEvent(context, VComTunnelEventSetBreak, &event, sizeof(event));
        }
        break;

    case IOCTL_SERIAL_SET_BREAK_OFF:
        {
            VCT_BREAK_EVENT event;

            RtlZeroMemory(&event, sizeof(event));
            event.Enabled = FALSE;
            VctQueueControlEvent(context, VComTunnelEventSetBreak, &event, sizeof(event));
        }
        break;

    case IOCTL_VCOMTUNNEL_ATTACH:
        status = VctCompleteAttach(Request, context);
        break;

    case IOCTL_VCOMTUNNEL_SET_CONNECTION_STATE:
        status = VctCopyInputBuffer(Request, &context->ConnectionState, sizeof(context->ConnectionState));
        break;

    case IOCTL_VCOMTUNNEL_SET_MODEM_STATE:
        {
            VCT_MODEM_STATE modemState;
            ULONG oldModemStatus;
            ULONG eventMask = 0;

            status = VctCopyInputBuffer(Request, &modemState, sizeof(modemState));
            if (NT_SUCCESS(status)) {
                oldModemStatus = context->ModemStatus;
                context->ModemStatus = modemState.ModemStatus;
                if ((oldModemStatus & SERIAL_CTS_STATE) != (modemState.ModemStatus & SERIAL_CTS_STATE)) {
                    eventMask |= SERIAL_EV_CTS;
                }
                if ((oldModemStatus & SERIAL_DSR_STATE) != (modemState.ModemStatus & SERIAL_DSR_STATE)) {
                    eventMask |= SERIAL_EV_DSR;
                }
                if ((oldModemStatus & SERIAL_DCD_STATE) != (modemState.ModemStatus & SERIAL_DCD_STATE)) {
                    eventMask |= SERIAL_EV_RLSD;
                }
                if ((oldModemStatus & SERIAL_RI_STATE) != (modemState.ModemStatus & SERIAL_RI_STATE)) {
                    eventMask |= SERIAL_EV_RING;
                }
                if (eventMask != 0) {
                    VctSignalWaitMaskEvent(context, eventMask);
                }
            }
        }
        break;

    case IOCTL_VCOMTUNNEL_SET_LINE_STATE:
        {
            VCT_LINE_STATE lineState;
            ULONG eventMask = 0;

            status = VctCopyInputBuffer(Request, &lineState, sizeof(lineState));
            if (NT_SUCCESS(status)) {
                context->LineErrors = lineState.Errors;
                if (lineState.Errors != 0) {
                    eventMask |= SERIAL_EV_ERR;
                }
                if ((lineState.Errors & SERIAL_ERROR_BREAK) != 0) {
                    eventMask |= SERIAL_EV_BREAK;
                }
                if (eventMask != 0) {
                    VctSignalWaitMaskEvent(context, eventMask);
                }
            }
        }
        break;

    case IOCTL_VCOMTUNNEL_DETACH:
        WdfSpinLockAcquire(context->Lock);
        if (context->ServiceAttached && context->ServiceFileObject == fileObject) {
            serviceWait = context->PendingServiceWait;
            context->PendingServiceWait = NULL;
            context->ServiceAttached = FALSE;
            context->ServiceFileObject = NULL;
            context->ConnectionState = VComTunnelDisconnected;
        } else {
            status = STATUS_ACCESS_DENIED;
        }
        WdfSpinLockRelease(context->Lock);
        if (serviceWait != NULL) {
            VctCompleteUnmarkedRequest(serviceWait, STATUS_CANCELLED);
        }
        break;

    case IOCTL_VCOMTUNNEL_WAIT_EVENT:
        VctQueueServiceWait(Request, context);
        return;

    case IOCTL_VCOMTUNNEL_PUSH_RX:
        status = VctPushRx(Request, context);
        break;

    case IOCTL_VCOMTUNNEL_COMPLETE_EVENT:
        status = STATUS_NOT_IMPLEMENTED;
        break;

    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    WdfRequestComplete(Request, status);
}

VOID
VctEvtIoRead(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t Length
    )
{
    NTSTATUS status = STATUS_SUCCESS;
    WDFDEVICE device;
    PDEVICE_CONTEXT context;
    UCHAR* readBuffer;
    size_t readBufferLength;
    ULONG copied = 0;

    device = WdfIoQueueGetDevice(Queue);
    context = DeviceGetContext(device);

    status = WdfRequestRetrieveOutputBuffer(Request, Length == 0 ? 1 : Length, (PVOID*)&readBuffer, &readBufferLength);
    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
        return;
    }

    WdfSpinLockAcquire(context->Lock);
    if (context->RxCount > 0) {
        copied = VctRxCopyOutLocked(context, readBuffer, (ULONG)readBufferLength);
    }
    WdfSpinLockRelease(context->Lock);

    WdfRequestCompleteWithInformation(Request, status, copied);
}

VOID
VctEvtIoWrite(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t Length
    )
{
    NTSTATUS status;
    WDFDEVICE device;
    PDEVICE_CONTEXT context;
    WDFREQUEST serviceWait = NULL;
    UCHAR* inputBuffer;
    UCHAR* eventBuffer;
    size_t inputLength;
    size_t eventBufferLength;
    ULONG eventSize;
    ULONG copied;
    PVCT_EVENT_HEADER header;
    BOOLEAN signalTxEmpty = FALSE;

    device = WdfIoQueueGetDevice(Queue);
    context = DeviceGetContext(device);

    status = WdfRequestRetrieveInputBuffer(Request, Length == 0 ? 1 : Length, (PVOID*)&inputBuffer, &inputLength);
    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
        return;
    }

    if (inputLength > VCOMTUNNEL_MAX_EVENT_BYTES - sizeof(VCT_EVENT_HEADER)) {
        WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL);
        return;
    }

    status = STATUS_DEVICE_NOT_READY;
    WdfSpinLockAcquire(context->Lock);
    if (context->ServiceAttached && context->PendingServiceWait != NULL) {
        serviceWait = context->PendingServiceWait;
        context->PendingServiceWait = NULL;
    } else if (context->ServiceAttached) {
        copied = VctTxCopyInLocked(context, inputBuffer, (ULONG)inputLength);
        status = copied == inputLength ? STATUS_SUCCESS : STATUS_BUFFER_TOO_SMALL;
    }
    WdfSpinLockRelease(context->Lock);

    if (serviceWait == NULL) {
        WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? inputLength : 0);
        return;
    }

    status = WdfRequestUnmarkCancelable(serviceWait);
    if (status == STATUS_CANCELLED) {
        WdfRequestCompleteWithInformation(Request, STATUS_DEVICE_NOT_READY, 0);
        return;
    }

    eventSize = (ULONG)(sizeof(VCT_EVENT_HEADER) + inputLength);
    status = WdfRequestRetrieveOutputBuffer(serviceWait, eventSize, (PVOID*)&eventBuffer, &eventBufferLength);
    if (NT_SUCCESS(status)) {
        header = (PVCT_EVENT_HEADER)eventBuffer;
        header->Size = eventSize;
        header->RequestId = 0;
        header->Type = VComTunnelEventTxData;
        header->Flags = 0;

        WdfSpinLockAcquire(context->Lock);
        header->Sequence = ++context->NextSequence;
        WdfSpinLockRelease(context->Lock);

        RtlCopyMemory(eventBuffer + sizeof(VCT_EVENT_HEADER), inputBuffer, inputLength);
        WdfRequestCompleteWithInformation(serviceWait, STATUS_SUCCESS, eventSize);
        WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, inputLength);
        signalTxEmpty = TRUE;
    } else {
        WdfRequestComplete(serviceWait, status);
        WdfRequestCompleteWithInformation(Request, STATUS_DEVICE_NOT_READY, 0);
    }

    if (signalTxEmpty) {
        VctSignalWaitMaskEvent(context, SERIAL_EV_TXEMPTY);
    }
}
