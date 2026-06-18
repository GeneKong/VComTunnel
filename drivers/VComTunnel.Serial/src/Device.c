#include "Driver.h"

static VOID
VctInitializeSerialDefaults(
    _In_ PDEVICE_CONTEXT Context
    )
{
    RtlZeroMemory(Context, sizeof(*Context));

    Context->BaudRate.BaudRate = 115200;
    Context->LineControl.StopBits = STOP_BIT_1;
    Context->LineControl.Parity = NO_PARITY;
    Context->LineControl.WordLength = 8;
    Context->Timeouts.ReadIntervalTimeout = 0;
    Context->Timeouts.ReadTotalTimeoutMultiplier = 0;
    Context->Timeouts.ReadTotalTimeoutConstant = 0;
    Context->Timeouts.WriteTotalTimeoutMultiplier = 0;
    Context->Timeouts.WriteTotalTimeoutConstant = 0;
    Context->Handflow.ControlHandShake = 0;
    Context->Handflow.FlowReplace = 0;
    Context->Handflow.XonLimit = 0;
    Context->Handflow.XoffLimit = 0;
    Context->ModemStatus = SERIAL_CTS_STATE | SERIAL_DSR_STATE | SERIAL_DCD_STATE;
    Context->ConnectionState = VComTunnelDisconnected;
}

NTSTATUS
VctEvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDF_FILEOBJECT_CONFIG fileConfig;
    UNICODE_STRING comLink;
    PDEVICE_CONTEXT context;

    UNREFERENCED_PARAMETER(Driver);

    WdfDeviceInitSetDeviceType(DeviceInit, FILE_DEVICE_SERIAL_PORT);
    WdfDeviceInitSetIoType(DeviceInit, WdfDeviceIoBuffered);
    WdfDeviceInitSetCharacteristics(DeviceInit, FILE_DEVICE_SECURE_OPEN, FALSE);

    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, VctEvtDeviceFileCreate, VctEvtFileClose, WDF_NO_EVENT_CALLBACK);
    WdfDeviceInitSetFileObjectConfig(DeviceInit, &fileConfig, WDF_NO_OBJECT_ATTRIBUTES);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&deviceAttributes, DEVICE_CONTEXT);

    status = WdfDeviceCreate(&DeviceInit, &deviceAttributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    context = DeviceGetContext(device);
    VctInitializeSerialDefaults(context);

    RtlInitUnicodeString(&comLink, VCOMTUNNEL_DEFAULT_COM_LINK);
    status = WdfDeviceCreateSymbolicLink(device, &comLink);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = VctQueueInitialize(device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    return STATUS_SUCCESS;
}

VOID
VctEvtDeviceFileCreate(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request,
    _In_ WDFFILEOBJECT FileObject
    )
{
    UNREFERENCED_PARAMETER(Device);
    UNREFERENCED_PARAMETER(FileObject);

    WdfRequestComplete(Request, STATUS_SUCCESS);
}

VOID
VctEvtFileClose(
    _In_ WDFFILEOBJECT FileObject
    )
{
    UNREFERENCED_PARAMETER(FileObject);
}
