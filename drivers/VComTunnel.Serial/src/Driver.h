#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <ntddser.h>

#include "VComTunnelIoctl.h"

#define VCOMTUNNEL_DEFAULT_COM_LINK L"\\DosDevices\\COM40"
#define VCOMTUNNEL_RX_QUEUE_SIZE 4096
#define VCOMTUNNEL_TX_QUEUE_SIZE 4096

typedef struct _DEVICE_CONTEXT {
    SERIAL_BAUD_RATE BaudRate;
    SERIAL_LINE_CONTROL LineControl;
    SERIAL_TIMEOUTS Timeouts;
    SERIAL_CHARS Chars;
    SERIAL_HANDFLOW Handflow;
    ULONG WaitMask;
    ULONG ModemStatus;
    BOOLEAN Dtr;
    BOOLEAN Rts;
    VCOMTUNNEL_CONNECTION_STATE ConnectionState;
    WDFQUEUE DefaultQueue;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, DeviceGetContext)

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD VctEvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL VctEvtIoDeviceControl;
EVT_WDF_IO_QUEUE_IO_READ VctEvtIoRead;
EVT_WDF_IO_QUEUE_IO_WRITE VctEvtIoWrite;
EVT_WDF_DEVICE_FILE_CREATE VctEvtDeviceFileCreate;
EVT_WDF_FILE_CLOSE VctEvtFileClose;

NTSTATUS
VctQueueInitialize(
    _In_ WDFDEVICE Device
    );
