#include "Driver.h"

#define VCOMTUNNEL_TIMEOUT_MAX_ULONG ((ULONG)-1)

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

static ULONG
VctReadTimeoutMilliseconds(
    _In_ const SERIAL_TIMEOUTS* Timeouts,
    _In_ size_t ReadLength,
    _Out_ BOOLEAN* CompleteImmediately
    )
{
    ULONGLONG total;

    *CompleteImmediately = FALSE;
    if (Timeouts->ReadIntervalTimeout == VCOMTUNNEL_TIMEOUT_MAX_ULONG &&
        Timeouts->ReadTotalTimeoutMultiplier == 0 &&
        Timeouts->ReadTotalTimeoutConstant == 0) {
        *CompleteImmediately = TRUE;
        return 0;
    }

    total = (ULONGLONG)Timeouts->ReadTotalTimeoutConstant +
        ((ULONGLONG)Timeouts->ReadTotalTimeoutMultiplier * (ULONGLONG)ReadLength);
    if (total == 0 &&
        Timeouts->ReadIntervalTimeout != 0 &&
        Timeouts->ReadIntervalTimeout != VCOMTUNNEL_TIMEOUT_MAX_ULONG) {
        total = Timeouts->ReadIntervalTimeout;
    }

    return total > VCOMTUNNEL_TIMEOUT_MAX_ULONG
        ? VCOMTUNNEL_TIMEOUT_MAX_ULONG
        : (ULONG)total;
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

static ULONG
VctSerialConfigSize(
    VOID
    )
{
    return FIELD_OFFSET(SERIALCONFIG, ProviderData);
}

static NTSTATUS
VctCompleteConfigSize(
    _In_ WDFREQUEST Request
    )
{
    ULONG configSize;

    configSize = VctSerialConfigSize();
    return VctCopyOutputBuffer(Request, &configSize, sizeof(configSize));
}

static NTSTATUS
VctCompleteCommConfig(
    _In_ WDFREQUEST Request
    )
{
    NTSTATUS status;
    PSERIALCONFIG config;
    ULONG configSize;

    configSize = VctSerialConfigSize();
    status = WdfRequestRetrieveOutputBuffer(Request, configSize, (PVOID*)&config, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlZeroMemory(config, configSize);
    config->Size = configSize;
    config->Version = 1;
    config->SubType = SERIAL_SP_RS232;
    config->ProvOffset = 0;
    config->ProviderSize = 0;
    WdfRequestSetInformation(Request, configSize);

    return STATUS_SUCCESS;
}

static NTSTATUS
VctAcceptCommConfig(
    _In_ WDFREQUEST Request
    )
{
    PVOID inputBuffer;

    return WdfRequestRetrieveInputBuffer(Request, VctSerialConfigSize(), &inputBuffer, NULL);
}

static NTSTATUS
VctSetQueueSize(
    _In_ WDFREQUEST Request
    )
{
    NTSTATUS status;
    SERIAL_QUEUE_SIZE queueSize;

    status = VctCopyInputBuffer(Request, &queueSize, sizeof(queueSize));
    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (queueSize.InSize > VCOMTUNNEL_RX_QUEUE_SIZE ||
        queueSize.OutSize > VCOMTUNNEL_TX_QUEUE_SIZE) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    return STATUS_SUCCESS;
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

static NTSTATUS
VctCompleteSerialStats(
    _In_ WDFREQUEST Request,
    _Inout_ PDEVICE_CONTEXT Context
    )
{
    SERIALPERF_STATS stats;

    WdfSpinLockAcquire(Context->Lock);
    stats = Context->Stats;
    WdfSpinLockRelease(Context->Lock);

    return VctCopyOutputBuffer(Request, &stats, sizeof(stats));
}

static VOID
VctClearSerialStats(
    _Inout_ PDEVICE_CONTEXT Context
    )
{
    WdfSpinLockAcquire(Context->Lock);
    RtlZeroMemory(&Context->Stats, sizeof(Context->Stats));
    WdfSpinLockRelease(Context->Lock);
}

static VOID
VctAccumulateLineStatsLocked(
    _Inout_ PDEVICE_CONTEXT Context,
    _In_ ULONG Errors
    )
{
    if ((Errors & SERIAL_ERROR_FRAMING) != 0) {
        Context->Stats.FrameErrorCount++;
    }
    if ((Errors & SERIAL_ERROR_OVERRUN) != 0) {
        Context->Stats.SerialOverrunErrorCount++;
    }
    if ((Errors & SERIAL_ERROR_QUEUEOVERRUN) != 0) {
        Context->Stats.BufferOverrunErrorCount++;
    }
    if ((Errors & SERIAL_ERROR_PARITY) != 0) {
        Context->Stats.ParityErrorCount++;
    }
}

static VOID
VctQueueControlEvent(
    _Inout_ PDEVICE_CONTEXT Context,
    _In_ USHORT Type,
    _In_reads_bytes_opt_(PayloadSize) const VOID* Payload,
    _In_ ULONG PayloadSize
    );

static ULONG
VctSnapshotModemControlLocked(
    _In_ PDEVICE_CONTEXT Context
    )
{
    ULONG modemControl;

    modemControl = Context->ModemControl &
        (SERIAL_IOC_MCR_OUT1 | SERIAL_IOC_MCR_OUT2 | SERIAL_IOC_MCR_LOOP);
    if (Context->Dtr) {
        modemControl |= SERIAL_IOC_MCR_DTR;
    }
    if (Context->Rts) {
        modemControl |= SERIAL_IOC_MCR_RTS;
    }

    return modemControl;
}

static NTSTATUS
VctCompleteModemControl(
    _In_ WDFREQUEST Request,
    _Inout_ PDEVICE_CONTEXT Context
    )
{
    ULONG modemControl;

    WdfSpinLockAcquire(Context->Lock);
    modemControl = VctSnapshotModemControlLocked(Context);
    WdfSpinLockRelease(Context->Lock);

    return VctCopyOutputBuffer(Request, &modemControl, sizeof(modemControl));
}

static NTSTATUS
VctSetRawModemControl(
    _In_ WDFREQUEST Request,
    _Inout_ PDEVICE_CONTEXT Context
    )
{
    NTSTATUS status;
    ULONG modemControl;
    ULONG changedMask = 0;
    BOOLEAN dtr;
    BOOLEAN rts;
    VCT_MODEM_CONTROL_EVENT event;

    status = VctCopyInputBuffer(Request, &modemControl, sizeof(modemControl));
    if (!NT_SUCCESS(status)) {
        return status;
    }

    if ((modemControl & ~(SERIAL_IOC_MCR_DTR |
        SERIAL_IOC_MCR_RTS |
        SERIAL_IOC_MCR_OUT1 |
        SERIAL_IOC_MCR_OUT2 |
        SERIAL_IOC_MCR_LOOP)) != 0) {
        return STATUS_INVALID_PARAMETER;
    }

    dtr = (modemControl & SERIAL_IOC_MCR_DTR) != 0;
    rts = (modemControl & SERIAL_IOC_MCR_RTS) != 0;

    WdfSpinLockAcquire(Context->Lock);
    if (Context->Dtr != dtr) {
        changedMask |= VCOMTUNNEL_MODEM_CONTROL_DTR;
    }
    if (Context->Rts != rts) {
        changedMask |= VCOMTUNNEL_MODEM_CONTROL_RTS;
    }
    Context->Dtr = dtr;
    Context->Rts = rts;
    Context->ModemControl = modemControl;
    WdfSpinLockRelease(Context->Lock);

    if (changedMask != 0) {
        RtlZeroMemory(&event, sizeof(event));
        event.Mask = changedMask;
        event.Dtr = dtr;
        event.Rts = rts;
        VctQueueControlEvent(Context, VComTunnelEventSetModemControl, &event, sizeof(event));
    }

    return STATUS_SUCCESS;
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

static VOID
VctApplyLocalPurge(
    _Inout_ PDEVICE_CONTEXT Context,
    _In_ ULONG PurgeMask
    )
{
    WDFREQUEST readRequest = NULL;
    BOOLEAN signalTxEmpty = FALSE;

    WdfSpinLockAcquire(Context->Lock);
    if ((PurgeMask & SERIAL_PURGE_RXCLEAR) != 0) {
        Context->RxHead = 0;
        Context->RxTail = 0;
        Context->RxCount = 0;
    }
    if ((PurgeMask & SERIAL_PURGE_TXCLEAR) != 0) {
        Context->TxHead = 0;
        Context->TxTail = 0;
        Context->TxCount = 0;
        signalTxEmpty = TRUE;
    }
    if ((PurgeMask & SERIAL_PURGE_RXABORT) != 0 &&
        Context->PendingRead != NULL) {
        readRequest = Context->PendingRead;
        Context->PendingRead = NULL;
    }
    WdfSpinLockRelease(Context->Lock);

    if (readRequest != NULL) {
        VctCompleteUnmarkedRequest(readRequest, STATUS_CANCELLED);
    }
    if (signalTxEmpty) {
        VctSignalWaitMaskEvent(Context, SERIAL_EV_TXEMPTY);
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

static BOOLEAN
VctBufferContainsByte(
    _In_reads_bytes_(Length) const UCHAR* Buffer,
    _In_ ULONG Length,
    _In_ UCHAR Value
    )
{
    ULONG index;

    for (index = 0; index < Length; index++) {
        if (Buffer[index] == Value) {
            return TRUE;
        }
    }

    return FALSE;
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
    BOOLEAN signalTxEmpty = FALSE;
    BOOLEAN completed = FALSE;

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
    signalTxEmpty = event.Type == VComTunnelEventTxData;

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
            completed = TRUE;
        }
    } else {
        WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, eventSize);
        completed = TRUE;
    }

    if (completed && signalTxEmpty) {
        VctSignalWaitMaskEvent(Context, SERIAL_EV_TXEMPTY);
    }

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

static NTSTATUS
VctQueueImmediateChar(
    _Inout_ PDEVICE_CONTEXT Context,
    _In_ UCHAR ImmediateChar
    )
{
    NTSTATUS status = STATUS_SUCCESS;
    WDFREQUEST serviceWait = NULL;

    WdfSpinLockAcquire(Context->Lock);
    if (!Context->ServiceAttached) {
        status = STATUS_DEVICE_NOT_READY;
    } else {
        VctEnqueueControlEventLocked(
            Context,
            VComTunnelEventTxData,
            &ImmediateChar,
            sizeof(ImmediateChar));
        Context->Stats.TransmittedCount++;
        if (Context->PendingServiceWait != NULL) {
            serviceWait = Context->PendingServiceWait;
            Context->PendingServiceWait = NULL;
        }
    }
    WdfSpinLockRelease(Context->Lock);

    if (serviceWait != NULL) {
        VctCompleteControlEventFromQueue(serviceWait, Context, TRUE);
    }

    return status;
}

static NTSTATUS
VctQueueLocalFlowControl(
    _Inout_ PDEVICE_CONTEXT Context,
    _In_ BOOLEAN Suspend
    )
{
    NTSTATUS status = STATUS_SUCCESS;
    WDFREQUEST serviceWait = NULL;
    VCT_LOCAL_FLOW_CONTROL_EVENT event;

    RtlZeroMemory(&event, sizeof(event));
    event.Suspend = Suspend;

    WdfSpinLockAcquire(Context->Lock);
    if (!Context->ServiceAttached) {
        status = STATUS_DEVICE_NOT_READY;
    } else {
        VctEnqueueControlEventLocked(
            Context,
            VComTunnelEventLocalFlowControl,
            &event,
            sizeof(event));
        if (Context->PendingServiceWait != NULL) {
            serviceWait = Context->PendingServiceWait;
            Context->PendingServiceWait = NULL;
        }
    }
    WdfSpinLockRelease(Context->Lock);

    if (serviceWait != NULL) {
        VctCompleteControlEventFromQueue(serviceWait, Context, TRUE);
    }

    return status;
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
    ULONG pushed = 0;
    ULONG eventMask = 0;
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
    if (push->ByteCount > (VCOMTUNNEL_RX_QUEUE_SIZE - Context->RxCount)) {
        status = STATUS_BUFFER_OVERFLOW;
    } else {
        pushed = VctRxCopyInLocked(Context, push->Bytes, push->ByteCount);
        if (pushed > 0) {
            Context->Stats.ReceivedCount += pushed;
            eventMask = SERIAL_EV_RXCHAR;
            if (VctBufferContainsByte(push->Bytes, pushed, Context->Chars.EventChar)) {
                eventMask |= SERIAL_EV_RXFLAG;
            }
            if (Context->RxCount >= VCOMTUNNEL_RX_QUEUE_80_FULL) {
                eventMask |= SERIAL_EV_RX80FULL;
            }
        }
        if (pushed == push->ByteCount && Context->PendingRead != NULL) {
            readRequest = Context->PendingRead;
            Context->PendingRead = NULL;
        }
    }
    WdfSpinLockRelease(Context->Lock);

    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (eventMask != 0) {
        VctSignalWaitMaskEvent(Context, eventMask);
    }

    if (readRequest != NULL) {
        WdfTimerStop(Context->ReadTimer, FALSE);
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
    BOOLEAN stopReadTimer = FALSE;

    queue = WdfRequestGetIoQueue(Request);
    device = WdfIoQueueGetDevice(queue);
    context = DeviceGetContext(device);

    WdfSpinLockAcquire(context->Lock);
    if (context->PendingRead == Request) {
        context->PendingRead = NULL;
        stopReadTimer = TRUE;
    }
    if (context->PendingWaitMask == Request) {
        context->PendingWaitMask = NULL;
    }
    if (context->PendingServiceWait == Request) {
        context->PendingServiceWait = NULL;
    }
    WdfSpinLockRelease(context->Lock);

    if (stopReadTimer) {
        WdfTimerStop(context->ReadTimer, FALSE);
    }
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
    BOOLEAN stopReadTimer = FALSE;

    context = DeviceGetContext(Device);

    WdfSpinLockAcquire(context->Lock);
    if (context->PendingRead != NULL &&
        WdfRequestGetFileObject(context->PendingRead) == FileObject) {
        readRequest = context->PendingRead;
        context->PendingRead = NULL;
        stopReadTimer = TRUE;
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

    if (stopReadTimer) {
        WdfTimerStop(context->ReadTimer, FALSE);
    }
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

    case IOCTL_SERIAL_CONFIG_SIZE:
        status = VctCompleteConfigSize(Request);
        break;

    case IOCTL_SERIAL_GET_COMMCONFIG:
        status = VctCompleteCommConfig(Request);
        break;

    case IOCTL_SERIAL_SET_COMMCONFIG:
        status = VctAcceptCommConfig(Request);
        break;

    case IOCTL_SERIAL_GET_STATS:
        status = VctCompleteSerialStats(Request, context);
        break;

    case IOCTL_SERIAL_CLEAR_STATS:
        VctClearSerialStats(context);
        break;

    case IOCTL_SERIAL_IMMEDIATE_CHAR:
        {
            UCHAR immediateChar;

            status = VctCopyInputBuffer(Request, &immediateChar, sizeof(immediateChar));
            if (NT_SUCCESS(status)) {
                status = VctQueueImmediateChar(context, immediateChar);
            }
        }
        break;

    case IOCTL_SERIAL_SET_XOFF:
        status = VctQueueLocalFlowControl(context, TRUE);
        break;

    case IOCTL_SERIAL_SET_XON:
        status = VctQueueLocalFlowControl(context, FALSE);
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

    case IOCTL_SERIAL_GET_MODEM_CONTROL:
        status = VctCompleteModemControl(Request, context);
        break;

    case IOCTL_SERIAL_SET_MODEM_CONTROL:
        status = VctSetRawModemControl(Request, context);
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
                if ((purgeMask & ~(SERIAL_PURGE_TXABORT | SERIAL_PURGE_RXABORT | SERIAL_PURGE_TXCLEAR | SERIAL_PURGE_RXCLEAR)) != 0) {
                    status = STATUS_INVALID_PARAMETER;
                    break;
                }

                VctApplyLocalPurge(context, purgeMask);
                event.Mask = purgeMask;
                VctQueueControlEvent(context, VComTunnelEventPurge, &event, sizeof(event));
            }
        }
        break;

    case IOCTL_SERIAL_SET_QUEUE_SIZE:
        status = VctSetQueueSize(Request);
        break;

    case IOCTL_SERIAL_RESET_DEVICE:
    case IOCTL_SERIAL_SET_FIFO_CONTROL:
    case IOCTL_SERIAL_APPLY_DEFAULT_CONFIGURATION:
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
        {
            VCOMTUNNEL_CONNECTION_STATE connectionState;
            WDFREQUEST readRequest = NULL;

            status = VctCopyInputBuffer(Request, &connectionState, sizeof(connectionState));
            if (NT_SUCCESS(status)) {
                WdfSpinLockAcquire(context->Lock);
                context->ConnectionState = connectionState;
                if (connectionState != VComTunnelConnecting &&
                    connectionState != VComTunnelConnected &&
                    context->PendingRead != NULL) {
                    readRequest = context->PendingRead;
                    context->PendingRead = NULL;
                }
                WdfSpinLockRelease(context->Lock);

                if (readRequest != NULL) {
                    WdfTimerStop(context->ReadTimer, FALSE);
                    VctCompleteUnmarkedRequest(readRequest, STATUS_DEVICE_NOT_READY);
                }
            }
        }
        break;

    case IOCTL_VCOMTUNNEL_SET_MODEM_STATE:
        {
            VCT_MODEM_STATE modemState;
            ULONG oldModemStatus;
            ULONG eventMask;

            status = VctCopyInputBuffer(Request, &modemState, sizeof(modemState));
            if (NT_SUCCESS(status)) {
                oldModemStatus = context->ModemStatus;
                context->ModemStatus = modemState.ModemStatus;
                eventMask = modemState.EventMask;
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
                WdfSpinLockAcquire(context->Lock);
                context->LineErrors = lineState.Errors;
                VctAccumulateLineStatsLocked(context, lineState.Errors);
                if (lineState.Errors != 0) {
                    eventMask |= SERIAL_EV_ERR;
                }
                if ((lineState.Errors & SERIAL_ERROR_BREAK) != 0) {
                    eventMask |= SERIAL_EV_BREAK;
                }
                WdfSpinLockRelease(context->Lock);
                if (eventMask != 0) {
                    VctSignalWaitMaskEvent(context, eventMask);
                }
            }
        }
        break;

    case IOCTL_VCOMTUNNEL_SET_REMOTE_SETTINGS:
        {
            VCT_REMOTE_SETTINGS settings;

            status = VctCopyInputBuffer(Request, &settings, sizeof(settings));
            if (NT_SUCCESS(status)) {
                if ((settings.Mask & VCOMTUNNEL_REMOTE_BAUD_RATE) != 0 &&
                    settings.BaudRate != 0) {
                    context->BaudRate.BaudRate = settings.BaudRate;
                }
                if ((settings.Mask & VCOMTUNNEL_REMOTE_WORD_LENGTH) != 0) {
                    context->LineControl.WordLength = settings.WordLength;
                }
                if ((settings.Mask & VCOMTUNNEL_REMOTE_PARITY) != 0) {
                    context->LineControl.Parity = settings.Parity;
                }
                if ((settings.Mask & VCOMTUNNEL_REMOTE_STOP_BITS) != 0) {
                    context->LineControl.StopBits = settings.StopBits;
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
    BOOLEAN pending = FALSE;
    BOOLEAN completeImmediately = FALSE;
    ULONG timeoutMs = 0;

    device = WdfIoQueueGetDevice(Queue);
    context = DeviceGetContext(device);

    if (Length == 0) {
        WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, 0);
        return;
    }

    status = WdfRequestRetrieveOutputBuffer(Request, Length, (PVOID*)&readBuffer, &readBufferLength);
    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
        return;
    }

    WdfSpinLockAcquire(context->Lock);
    if (context->RxCount > 0) {
        copied = VctRxCopyOutLocked(context, readBuffer, (ULONG)readBufferLength);
    } else if (context->ConnectionState != VComTunnelConnecting &&
        context->ConnectionState != VComTunnelConnected) {
        status = STATUS_DEVICE_NOT_READY;
    } else if (context->PendingRead != NULL) {
        status = STATUS_DEVICE_BUSY;
    } else {
        timeoutMs = VctReadTimeoutMilliseconds(&context->Timeouts, Length, &completeImmediately);
        if (completeImmediately) {
            copied = 0;
        } else {
            context->PendingRead = Request;
            status = WdfRequestMarkCancelableEx(Request, VctEvtRequestCancel);
            if (NT_SUCCESS(status)) {
                pending = TRUE;
            } else {
                context->PendingRead = NULL;
            }
        }
    }
    WdfSpinLockRelease(context->Lock);

    if (pending) {
        if (timeoutMs != 0) {
            WdfTimerStart(context->ReadTimer, WDF_REL_TIMEOUT_IN_MS(timeoutMs));
        }
        return;
    }

    WdfRequestCompleteWithInformation(Request, status, copied);
}

VOID
VctEvtReadTimer(
    _In_ WDFTIMER Timer
    )
{
    WDFDEVICE device;
    PDEVICE_CONTEXT context;
    WDFREQUEST readRequest = NULL;
    NTSTATUS status;

    device = (WDFDEVICE)WdfTimerGetParentObject(Timer);
    context = DeviceGetContext(device);

    WdfSpinLockAcquire(context->Lock);
    if (context->PendingRead != NULL) {
        readRequest = context->PendingRead;
        context->PendingRead = NULL;
    }
    WdfSpinLockRelease(context->Lock);

    if (readRequest != NULL) {
        status = WdfRequestUnmarkCancelable(readRequest);
        if (status != STATUS_CANCELLED) {
            WdfRequestCompleteWithInformation(readRequest, STATUS_SUCCESS, 0);
        }
    }
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
        if (inputLength > (VCOMTUNNEL_TX_QUEUE_SIZE - context->TxCount)) {
            status = STATUS_BUFFER_OVERFLOW;
        } else {
            copied = VctTxCopyInLocked(context, inputBuffer, (ULONG)inputLength);
            if (copied == inputLength) {
                context->Stats.TransmittedCount += copied;
                status = STATUS_SUCCESS;
            } else {
                status = STATUS_BUFFER_OVERFLOW;
            }
        }
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
        context->Stats.TransmittedCount += (ULONG)inputLength;
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
