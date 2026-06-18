#include <initguid.h>
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
    RtlStringCchCopyW(Context->PortName, RTL_NUMBER_OF(Context->PortName), VCOMTUNNEL_DEFAULT_PORT_NAME);
}

static VOID
VctReadAssignedPortName(
    _In_ WDFDEVICE Device,
    _Inout_ PDEVICE_CONTEXT Context
    )
{
    NTSTATUS status;
    WDFKEY key;
    UNICODE_STRING valueName;
    UNICODE_STRING portName;

    status = WdfDeviceOpenRegistryKey(Device, PLUGPLAY_REGKEY_DEVICE, KEY_READ, WDF_NO_OBJECT_ATTRIBUTES, &key);
    if (!NT_SUCCESS(status)) {
        return;
    }

    RtlInitUnicodeString(&valueName, L"PortName");
    RtlInitEmptyUnicodeString(&portName, Context->PortName, sizeof(Context->PortName));
    status = WdfRegistryQueryUnicodeString(key, &valueName, NULL, &portName);
    WdfRegistryClose(key);

    if (NT_SUCCESS(status)) {
        Context->PortName[portName.Length / sizeof(WCHAR)] = UNICODE_NULL;
    }
}

static NTSTATUS
VctCreateComLink(
    _In_ WDFDEVICE Device,
    _In_ PDEVICE_CONTEXT Context
    )
{
    WCHAR linkBuffer[64];
    UNICODE_STRING comLink;

    NTSTATUS status = RtlStringCchPrintfW(
        linkBuffer,
        RTL_NUMBER_OF(linkBuffer),
        L"%ws%ws",
        VCOMTUNNEL_COM_LINK_PREFIX,
        Context->PortName);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlInitUnicodeString(&comLink, linkBuffer);
    return WdfDeviceCreateSymbolicLink(Device, &comLink);
}

static VOID
VctCreateControlLink(
    _Inout_ PDEVICE_CONTEXT Context
    )
{
    UNICODE_STRING deviceName;
    UNICODE_STRING controlLink;

    RtlInitUnicodeString(&deviceName, VCOMTUNNEL_NT_DEVICE_NAME);
    RtlInitUnicodeString(&controlLink, VCOMTUNNEL_CONTROL_LINK);
    Context->ControlLinkCreated = NT_SUCCESS(IoCreateSymbolicLink(&controlLink, &deviceName));
}

static VOID
VctRegisterSerialCommName(
    _In_ WDFDEVICE Device,
    _In_ PDEVICE_CONTEXT Context
    )
{
    NTSTATUS status;
    WDFKEY key;
    UNICODE_STRING keyName;
    UNICODE_STRING valueName;
    UNICODE_STRING portName;

    RtlInitUnicodeString(&keyName, L"SERIALCOMM");
    status = WdfDeviceOpenDevicemapKey(Device, &keyName, KEY_SET_VALUE, WDF_NO_OBJECT_ATTRIBUTES, &key);
    if (!NT_SUCCESS(status)) {
        return;
    }

    RtlInitUnicodeString(&valueName, VCOMTUNNEL_NT_DEVICE_NAME);
    RtlInitUnicodeString(&portName, Context->PortName);
    WdfRegistryAssignUnicodeString(key, &valueName, &portName);
    WdfRegistryClose(key);
}

static VOID
VctEvtDeviceContextCleanup(
    _In_ WDFOBJECT Object
    )
{
    PDEVICE_CONTEXT context;
    UNICODE_STRING controlLink;

    context = DeviceGetContext((WDFDEVICE)Object);
    if (context->ControlLinkCreated) {
        RtlInitUnicodeString(&controlLink, VCOMTUNNEL_CONTROL_LINK);
        IoDeleteSymbolicLink(&controlLink);
        context->ControlLinkCreated = FALSE;
    }
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
    UNICODE_STRING ntDeviceName;
    UNICODE_STRING sddl;
    PDEVICE_CONTEXT context;

    UNREFERENCED_PARAMETER(Driver);

    WdfDeviceInitSetDeviceType(DeviceInit, FILE_DEVICE_SERIAL_PORT);
    WdfDeviceInitSetIoType(DeviceInit, WdfDeviceIoBuffered);
    WdfDeviceInitSetCharacteristics(DeviceInit, FILE_DEVICE_SECURE_OPEN, FALSE);
    RtlInitUnicodeString(&ntDeviceName, VCOMTUNNEL_NT_DEVICE_NAME);
    status = WdfDeviceInitAssignName(DeviceInit, &ntDeviceName);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlInitUnicodeString(&sddl, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;WD)");
    status = WdfDeviceInitAssignSDDLString(DeviceInit, &sddl);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, VctEvtDeviceFileCreate, VctEvtFileClose, VctEvtFileCleanup);
    WdfDeviceInitSetFileObjectConfig(DeviceInit, &fileConfig, WDF_NO_OBJECT_ATTRIBUTES);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&deviceAttributes, DEVICE_CONTEXT);
    deviceAttributes.EvtCleanupCallback = VctEvtDeviceContextCleanup;

    status = WdfDeviceCreate(&DeviceInit, &deviceAttributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    context = DeviceGetContext(device);
    VctInitializeSerialDefaults(context);
    VctReadAssignedPortName(device, context);

    status = WdfSpinLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &context->Lock);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = VctCreateComLink(device, context);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    VctCreateControlLink(context);

    WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_COMPORT, NULL);

    VctRegisterSerialCommName(device, context);

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

VOID
VctEvtFileCleanup(
    _In_ WDFFILEOBJECT FileObject
    )
{
    WDFDEVICE device;

    device = WdfFileObjectGetDevice(FileObject);
    VctCancelPendingRequestsForFile(device, FileObject, STATUS_CANCELLED);
}
