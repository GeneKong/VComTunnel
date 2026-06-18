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
    _In_ WDFREQUEST Request
    )
{
    SERIAL_STATUS status;

    RtlZeroMemory(&status, sizeof(status));
    status.HoldReasons = 0;
    status.AmountInInQueue = 0;
    status.AmountInOutQueue = 0;
    status.EofReceived = FALSE;
    status.WaitForImmediate = FALSE;

    return VctCopyOutputBuffer(Request, &status, sizeof(status));
}

NTSTATUS
VctQueueInitialize(
    _In_ WDFDEVICE Device
    )
{
    NTSTATUS status;
    WDF_IO_QUEUE_CONFIG queueConfig;
    PDEVICE_CONTEXT context;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = VctEvtIoDeviceControl;
    queueConfig.EvtIoRead = VctEvtIoRead;
    queueConfig.EvtIoWrite = VctEvtIoWrite;

    context = DeviceGetContext(Device);
    status = WdfIoQueueCreate(Device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &context->DefaultQueue);

    return status;
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

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    device = WdfIoQueueGetDevice(Queue);
    context = DeviceGetContext(device);
    WdfRequestSetInformation(Request, 0);

    switch (IoControlCode) {
    case IOCTL_SERIAL_SET_BAUD_RATE:
        status = VctCopyInputBuffer(Request, &context->BaudRate, sizeof(context->BaudRate));
        break;

    case IOCTL_SERIAL_GET_BAUD_RATE:
        status = VctCopyOutputBuffer(Request, &context->BaudRate, sizeof(context->BaudRate));
        break;

    case IOCTL_SERIAL_SET_LINE_CONTROL:
        status = VctCopyInputBuffer(Request, &context->LineControl, sizeof(context->LineControl));
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
        status = VctCopyInputBuffer(Request, &context->Handflow, sizeof(context->Handflow));
        break;

    case IOCTL_SERIAL_GET_HANDFLOW:
        status = VctCopyOutputBuffer(Request, &context->Handflow, sizeof(context->Handflow));
        break;

    case IOCTL_SERIAL_SET_WAIT_MASK:
        status = VctCopyInputBuffer(Request, &context->WaitMask, sizeof(context->WaitMask));
        break;

    case IOCTL_SERIAL_GET_WAIT_MASK:
        status = VctCopyOutputBuffer(Request, &context->WaitMask, sizeof(context->WaitMask));
        break;

    case IOCTL_SERIAL_WAIT_ON_MASK:
        {
            ULONG eventMask = 0;
            status = VctCopyOutputBuffer(Request, &eventMask, sizeof(eventMask));
        }
        break;

    case IOCTL_SERIAL_GET_PROPERTIES:
        status = VctCompleteCommProperties(Request);
        break;

    case IOCTL_SERIAL_GET_COMMSTATUS:
        status = VctCompleteCommStatus(Request);
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
        context->Dtr = TRUE;
        break;

    case IOCTL_SERIAL_CLR_DTR:
        context->Dtr = FALSE;
        break;

    case IOCTL_SERIAL_SET_RTS:
        context->Rts = TRUE;
        break;

    case IOCTL_SERIAL_CLR_RTS:
        context->Rts = FALSE;
        break;

    case IOCTL_SERIAL_PURGE:
    case IOCTL_SERIAL_RESET_DEVICE:
    case IOCTL_SERIAL_SET_BREAK_ON:
    case IOCTL_SERIAL_SET_BREAK_OFF:
    case IOCTL_SERIAL_SET_QUEUE_SIZE:
        break;

    case IOCTL_VCOMTUNNEL_ATTACH:
        context->ConnectionState = VComTunnelConnecting;
        break;

    case IOCTL_VCOMTUNNEL_SET_CONNECTION_STATE:
        status = VctCopyInputBuffer(Request, &context->ConnectionState, sizeof(context->ConnectionState));
        break;

    case IOCTL_VCOMTUNNEL_DETACH:
        context->ConnectionState = VComTunnelDisconnected;
        break;

    case IOCTL_VCOMTUNNEL_WAIT_EVENT:
    case IOCTL_VCOMTUNNEL_COMPLETE_EVENT:
    case IOCTL_VCOMTUNNEL_PUSH_RX:
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
    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(Length);

    WdfRequestCompleteWithInformation(Request, STATUS_DEVICE_NOT_READY, 0);
}

VOID
VctEvtIoWrite(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t Length
    )
{
    UNREFERENCED_PARAMETER(Queue);

    WdfRequestCompleteWithInformation(Request, STATUS_DEVICE_NOT_READY, Length);
}
