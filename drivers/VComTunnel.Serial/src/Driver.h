#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <ntddser.h>
#include <ntstrsafe.h>

#include "VComTunnelIoctl.h"

#define VCOMTUNNEL_COM_LINK_PREFIX L"\\DosDevices\\"
#define VCOMTUNNEL_CONTROL_LINK_PREFIX L"\\DosDevices\\VComTunnelCtl_"
#define VCOMTUNNEL_LEGACY_CONTROL_LINK L"\\DosDevices\\VComTunnelCtl0"
#define VCOMTUNNEL_DEFAULT_PORT_NAME L"COM40"
#define VCOMTUNNEL_RX_QUEUE_SIZE 4096
#define VCOMTUNNEL_TX_QUEUE_SIZE 4096
#define VCOMTUNNEL_EVENT_QUEUE_SIZE 64
#define VCOMTUNNEL_EVENT_PAYLOAD_SIZE 32

typedef struct _VCOMTUNNEL_QUEUED_EVENT {
    USHORT Type;
    USHORT Flags;
    ULONG PayloadSize;
    UCHAR Payload[VCOMTUNNEL_EVENT_PAYLOAD_SIZE];
} VCOMTUNNEL_QUEUED_EVENT, *PVCOMTUNNEL_QUEUED_EVENT;

typedef struct _DEVICE_CONTEXT {
    WDFSPINLOCK Lock;
    SERIAL_BAUD_RATE BaudRate;
    SERIAL_LINE_CONTROL LineControl;
    SERIAL_TIMEOUTS Timeouts;
    SERIAL_CHARS Chars;
    SERIAL_HANDFLOW Handflow;
    ULONG WaitMask;
    ULONG ModemStatus;
    ULONG LineErrors;
    BOOLEAN Dtr;
    BOOLEAN Rts;
    BOOLEAN ServiceAttached;
    BOOLEAN ControlLinkCreated;
    BOOLEAN LegacyControlLinkCreated;
    VCOMTUNNEL_CONNECTION_STATE ConnectionState;
    WDFQUEUE DefaultQueue;
    WDFFILEOBJECT ServiceFileObject;
    WCHAR NtDeviceName[64];
    WCHAR ControlLinkName[64];
    WCHAR PortName[32];
    WDFREQUEST PendingRead;
    WDFREQUEST PendingWaitMask;
    WDFREQUEST PendingServiceWait;
    ULONGLONG NextSequence;
    VCOMTUNNEL_QUEUED_EVENT EventQueue[VCOMTUNNEL_EVENT_QUEUE_SIZE];
    ULONG EventHead;
    ULONG EventTail;
    ULONG EventCount;
    UCHAR RxBuffer[VCOMTUNNEL_RX_QUEUE_SIZE];
    ULONG RxHead;
    ULONG RxTail;
    ULONG RxCount;
    UCHAR TxBuffer[VCOMTUNNEL_TX_QUEUE_SIZE];
    ULONG TxHead;
    ULONG TxTail;
    ULONG TxCount;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, DeviceGetContext)

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD VctEvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL VctEvtIoDeviceControl;
EVT_WDF_IO_QUEUE_IO_READ VctEvtIoRead;
EVT_WDF_IO_QUEUE_IO_WRITE VctEvtIoWrite;
EVT_WDF_DEVICE_FILE_CREATE VctEvtDeviceFileCreate;
EVT_WDF_FILE_CLOSE VctEvtFileClose;
EVT_WDF_FILE_CLEANUP VctEvtFileCleanup;
EVT_WDF_REQUEST_CANCEL VctEvtRequestCancel;

NTSTATUS
VctQueueInitialize(
    _In_ WDFDEVICE Device
    );

VOID
VctCancelPendingRequestsForFile(
    _In_ WDFDEVICE Device,
    _In_ WDFFILEOBJECT FileObject,
    _In_ NTSTATUS Status
    );
