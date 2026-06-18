using VComTunnel.Core;
using System.IO.Compression;
using System.Net;

var tests = new List<(string Name, Func<Task> Test)>
{
    ("valid multi mapping config", () => Task.Run(ValidMultiMappingConfig)),
    ("duplicate visible COM is rejected", () => Task.Run(DuplicateVisibleComIsRejected)),
    ("same visible and backing COM is rejected", () => Task.Run(SameVisibleAndBackingComIsRejected)),
    ("KMDF backing port is rejected", () => Task.Run(KmdfBackingPortIsRejected)),
    ("invalid host and port are rejected", () => Task.Run(InvalidHostAndPortAreRejected)),
    ("COM backing port is accepted", () => Task.Run(ComBackingPortIsAccepted)),
    ("config round trip", ConfigRoundTripAsync),
    ("com0com create hints", () => Task.Run(Com0comCreateHints)),
    ("KMDF control path uses visible COM", () => Task.Run(KmdfControlPathUsesVisibleCom)),
    ("KMDF pnputil CSV parser finds VComTunnel ports", () => Task.Run(KmdfPnpUtilCsvParserFindsPorts)),
    ("RFC2217 command encoding", () => Task.Run(Rfc2217CommandEncoding)),
    ("hub4com RFC2217 client baseline", () => Task.Run(Hub4comRfc2217ClientBaseline)),
    ("RFC2217 telnet parser", () => Task.Run(Rfc2217TelnetParser)),
    ("RFC2217 stream fragmentation", () => Task.Run(Rfc2217StreamFragmentation)),
    ("RFC2217 ack semantics", () => Task.Run(Rfc2217AckSemantics)),
    ("RFC2217 notification mappings", () => Task.Run(Rfc2217NotificationMappings)),
    ("com2tcp command uses batch wrapper", () => Task.Run(Com2TcpCommandUsesBatchWrapper)),
    ("missing dependencies fault mapping", MissingDependenciesFaultMappingAsync),
    ("missing backing port faults before hub4com", MissingBackingPortFaultsBeforeHub4comAsync),
    ("com0com create and remove plans", Com0comCreateAndRemovePlansAsync),
    ("KMDF mapping reports startup fault", KmdfMappingReportsStartupFaultAsync),
    ("KMDF session restarts after network fault", KmdfSessionRestartsAfterNetworkFaultAsync),
    ("KMDF permanent driver faults do not restart", KmdfPermanentDriverFaultDoesNotRestartAsync),
    ("fake com2tcp process starts and stops", FakeCom2TcpProcessStartsAndStopsAsync),
    ("fake com2tcp process restarts after exit", FakeCom2TcpProcessRestartsAfterExitAsync),
    ("manual stop suppresses fake com2tcp restart", ManualStopSuppressesFakeCom2TcpRestartAsync),
    ("dependency installer extracts tool zips", DependencyInstallerExtractsToolZipsAsync),
    ("dependency installer uses bundled release archives", DependencyInstallerUsesBundledReleaseArchivesAsync)
};

var failed = 0;
foreach (var (name, test) in tests)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void ValidMultiMappingConfig()
{
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "A", VisiblePort = "COM12", BackingPort = "CNCB12", Host = "esp-dap.local", Port = 3333 },
            new TunnelMapping { Name = "B", Backend = TunnelBackend.Kmdf, VisiblePort = "COM22", BackingPort = null, Host = "192.168.1.50", Port = 3333 }
        ]
    };

    AssertEmpty(ConfigValidator.Validate(config));
}

static void DuplicateVisibleComIsRejected()
{
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "A", VisiblePort = "COM12", BackingPort = "CNCB12", Host = "127.0.0.1", Port = 3333 },
            new TunnelMapping { Name = "B", VisiblePort = "COM12", BackingPort = "CNCB13", Host = "127.0.0.1", Port = 3334 }
        ]
    };

    AssertContains(ConfigValidator.Validate(config), "used more than once");
}

static void SameVisibleAndBackingComIsRejected()
{
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "Loop", VisiblePort = "COM30", BackingPort = "COM30", Host = "127.0.0.1", Port = 3333 }
        ]
    };

    AssertContains(ConfigValidator.Validate(config), "must be different");
}

static void KmdfBackingPortIsRejected()
{
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "K", Backend = TunnelBackend.Kmdf, VisiblePort = "COM22", BackingPort = "CNCB22", Host = "127.0.0.1", Port = 3333 }
        ]
    };

    AssertContains(ConfigValidator.Validate(config), "backingPort must be empty");
}

static void InvalidHostAndPortAreRejected()
{
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "Bad", VisiblePort = "COM12", BackingPort = "CNCB12", Host = "bad host name", Port = 70000 }
        ]
    };

    var errors = ConfigValidator.Validate(config);
    AssertContains(errors, "host");
    AssertContains(errors, "port");
}

static void ComBackingPortIsAccepted()
{
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "ExistingPair", VisiblePort = "COM28", BackingPort = "COM27", Host = "127.0.0.1", Port = 4000 }
        ]
    };

    AssertEmpty(ConfigValidator.Validate(config));
}

static async Task ConfigRoundTripAsync()
{
    using var temp = new TempDir();
    var store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "RoundTrip", VisiblePort = "COM33", BackingPort = "CNCB33", Host = "127.0.0.1", Port = 2217, AutoStart = true }
        ]
    };

    await store.SaveAsync(config);
    var loaded = await store.LoadAsync();
    AssertEqual("RoundTrip", loaded.Mappings.Single().Name);
    AssertEqual("COM33", loaded.Mappings.Single().VisiblePort);
    AssertTrue(loaded.Mappings.Single().AutoStart, "AutoStart should round-trip.");
}

static void Com0comCreateHints()
{
    var mapping = new TunnelMapping { Name = "A", VisiblePort = "COM12", BackingPort = "CNCB12" };
    var hint = new Hub4comCommandBuilder(new DependencyDetector()).BuildCom0comCreateHint(mapping);
    AssertEqual("setupc.exe install PortName=COM12 PortName=CNCB12", hint);
}

static void KmdfControlPathUsesVisibleCom()
{
    AssertEqual(@"\\.\VComTunnelCtl_COM27", KmdfTunnelSession.BuildControlDevicePath("com27"));
}

static void KmdfPnpUtilCsvParserFindsPorts()
{
    var csv = """
InstanceId,DeviceDescription,ClassName,ClassGuid,ManufacturerName,Status,ProblemCode,ProblemStatus,DriverName,ExtensionDriverNames
"USB\VID_303A&PID_1001\1","USB Serial Device (COM12)","Ports","{4d36e978-e325-11ce-bfc1-08002be10318}","Microsoft","Started","","","usbser.inf",""
"ROOT\PORTS\0000","VComTunnel Virtual Serial Port (COM27)","Ports","{4d36e978-e325-11ce-bfc1-08002be10318}","VComTunnel","Started","","","oem117.inf",""
"ROOT\PORTS\0001","VComTunnel Virtual Serial Port (COM31)","Ports","{4d36e978-e325-11ce-bfc1-08002be10318}","VComTunnel","Disconnected","22","","oem117.inf",""
""";

    var devices = KmdfDeviceManager.ParsePnpUtilDevicesCsv(csv);
    AssertEqual("2", devices.Count.ToString());
    AssertEqual("COM27", devices[0].PortName);
    AssertTrue(devices[0].IsStarted, "COM27 should be marked as started.");
    AssertEqual("COM31", devices[1].PortName);
    AssertEqual("22", devices[1].ProblemCode ?? "");
}

static void Rfc2217CommandEncoding()
{
    AssertBytes(
        [
            0xFF, 0xFB, 0x2C,
            0xFF, 0xFD, 0x2C,
            0xFF, 0xFB, 0x00,
            0xFF, 0xFD, 0x00,
            0xFF, 0xFB, 0x03,
            0xFF, 0xFD, 0x03,
            0xFF, 0xFA, 0x2C, 0x0A, 0xFF, 0xFF, 0xFF, 0xF0,
            0xFF, 0xFA, 0x2C, 0x0B, 0xFF, 0xFF, 0xFF, 0xF0
        ],
        new Rfc2217Client().BuildInitialNegotiation());
    var initialAcks = Rfc2217Client.BuildInitialExpectedAcks();
    AssertEqual("2", initialAcks.Length.ToString());
    AssertTrue(
        initialAcks[0].Matches(new Rfc2217Notification(Rfc2217Client.AckSetLineStateMask, [0xFF])),
        "Initial line-state mask ACK should accept the configured full mask.");
    AssertTrue(
        initialAcks[1].Matches(new Rfc2217Notification(Rfc2217Client.AckSetModemStateMask, [0xFF])),
        "Initial modem-state mask ACK should require the configured modem mask.");
    AssertTrue(
        initialAcks[0].Matches(new Rfc2217Notification(Rfc2217Client.AckSetLineStateMask, [0x1E])),
        "Initial line-state mask ACK may be a subset of the requested events.");

    AssertBytes(
        [0xFF, 0xFA, 0x2C, 0x01, 0x00, 0x01, 0xC2, 0x00, 0xFF, 0xF0],
        Rfc2217Client.BuildSetBaudRate(115200));

    AssertBytes(
        [
            0xFF, 0xFA, 0x2C, 0x02, 0x08, 0xFF, 0xF0,
            0xFF, 0xFA, 0x2C, 0x03, 0x01, 0xFF, 0xF0,
            0xFF, 0xFA, 0x2C, 0x04, 0x01, 0xFF, 0xF0
        ],
        Rfc2217Client.BuildSetLineControl(stopBits: 0, parity: 0, wordLength: 8));

    AssertBytes(
        [
            0xFF, 0xFA, 0x2C, 0x05, 0x08, 0xFF, 0xF0,
            0xFF, 0xFA, 0x2C, 0x05, 0x0C, 0xFF, 0xF0
        ],
        Rfc2217Client.BuildSetModemControl(dtr: true, rts: false));

    AssertBytes(
        [
            0xFF, 0xFA, 0x2C, 0x05, 0x11, 0xFF, 0xF0,
            0xFF, 0xFA, 0x2C, 0x05, 0x12, 0xFF, 0xF0
        ],
        Rfc2217Client.BuildSetHandflow(controlHandshake: 0x22, flowReplace: 0));

    AssertBytes(
        [
            0xFF, 0xFA, 0x2C, 0x05, 0x03, 0xFF, 0xF0,
            0xFF, 0xFA, 0x2C, 0x05, 0x10, 0xFF, 0xF0
        ],
        Rfc2217Client.BuildSetHandflow(controlHandshake: 0x08, flowReplace: 0x80));

    AssertBytes(
        [0x56, 0xFF, 0xFF, 0x43],
        Rfc2217Client.EscapeSerialData([0x56, 0xFF, 0x43], 0, 3));

    AssertBytes(
        [0xFF, 0xF1],
        Rfc2217Client.BuildTelnetNop());

    AssertBytes(
        [0xFF, 0xFA, 0x2C, 0x08, 0xFF, 0xF0],
        Rfc2217Client.BuildLocalFlowControlSuspend());

    AssertBytes(
        [0xFF, 0xFA, 0x2C, 0x09, 0xFF, 0xF0],
        Rfc2217Client.BuildLocalFlowControlResume());

    AssertBytes(
        [0xFF, 0xFA, 0x2C, 0x00, 0x56, 0x43, 0x6F, 0x6D, 0xFF, 0xF0],
        Rfc2217Client.BuildSignature("VCom"));

    AssertRfc2217Notifications(
        Rfc2217Client.BuildQuerySerialSettings(),
        new Rfc2217Notification(1, [0x00, 0x00, 0x00, 0x00]),
        new Rfc2217Notification(2, [0]),
        new Rfc2217Notification(3, [0]),
        new Rfc2217Notification(4, [0]));

    AssertRfc2217Notifications(
        Rfc2217Client.BuildQueryControlState(),
        new Rfc2217Notification(5, [0]),
        new Rfc2217Notification(5, [4]),
        new Rfc2217Notification(5, [7]),
        new Rfc2217Notification(5, [10]),
        new Rfc2217Notification(5, [13]));
}

static void Hub4comRfc2217ClientBaseline()
{
    var initial = new Rfc2217Client().BuildInitialNegotiation();
    AssertBytes(
        [
            0xFF, 0xFB, 0x2C,
            0xFF, 0xFD, 0x2C,
            0xFF, 0xFB, 0x00,
            0xFF, 0xFD, 0x00,
            0xFF, 0xFB, 0x03,
            0xFF, 0xFD, 0x03
        ],
        initial.Take(18).ToArray());
    AssertRfc2217Notifications(
        initial,
        new Rfc2217Notification(10, [0xFF]),
        new Rfc2217Notification(11, [0xFF]));

    AssertRfc2217Notifications(
        Rfc2217Client.BuildSetBaudRate(115200),
        new Rfc2217Notification(1, [0x00, 0x01, 0xC2, 0x00]));

    AssertRfc2217Notifications(
        Rfc2217Client.BuildSetLineControl(stopBits: 0, parity: 0, wordLength: 8),
        new Rfc2217Notification(2, [8]),
        new Rfc2217Notification(3, [1]),
        new Rfc2217Notification(4, [1]));

    AssertRfc2217Notifications(
        Rfc2217Client.BuildSetModemControl(dtr: true, rts: true),
        new Rfc2217Notification(5, [8]),
        new Rfc2217Notification(5, [11]));
    AssertRfc2217Notifications(
        Rfc2217Client.BuildSetModemControl(dtr: false, rts: false),
        new Rfc2217Notification(5, [9]),
        new Rfc2217Notification(5, [12]));

    AssertRfc2217Notifications(
        Rfc2217Client.BuildSetBreak(enabled: true),
        new Rfc2217Notification(5, [5]));
    AssertRfc2217Notifications(
        Rfc2217Client.BuildSetBreak(enabled: false),
        new Rfc2217Notification(5, [6]));

    AssertRfc2217Notifications(
        Rfc2217Client.BuildSetHandflow(controlHandshake: 0, flowReplace: 0),
        new Rfc2217Notification(5, [1]),
        new Rfc2217Notification(5, [14]));
    AssertRfc2217Notifications(
        Rfc2217Client.BuildSetHandflow(controlHandshake: 0, flowReplace: 0x03),
        new Rfc2217Notification(5, [2]),
        new Rfc2217Notification(5, [15]));
    AssertRfc2217Notifications(
        Rfc2217Client.BuildSetHandflow(controlHandshake: 0x08, flowReplace: 0x80),
        new Rfc2217Notification(5, [3]),
        new Rfc2217Notification(5, [16]));
    AssertRfc2217Notifications(
        Rfc2217Client.BuildSetHandflow(controlHandshake: 0x12, flowReplace: 0),
        new Rfc2217Notification(5, [19]),
        new Rfc2217Notification(5, [18]));
    AssertRfc2217Notifications(
        Rfc2217Client.BuildSetHandflow(controlHandshake: 0x20, flowReplace: 0x80),
        new Rfc2217Notification(5, [17]),
        new Rfc2217Notification(5, [16]));

    AssertRfc2217Notifications(
        Rfc2217Client.BuildPurge(0x0C),
        new Rfc2217Notification(12, [3]));
    AssertRfc2217Notifications(
        Rfc2217Client.BuildPurge(0x04),
        new Rfc2217Notification(12, [2]));
    AssertRfc2217Notifications(
        Rfc2217Client.BuildPurge(0x08),
        new Rfc2217Notification(12, [1]));
    AssertRfc2217Notifications(Rfc2217Client.BuildPurge(0x03));

    AssertEqual("240", Rfc2217Client.MapNotifyModemStateToWindowsStatus(0xF0).ToString());
    AssertEqual("312", Rfc2217Client.MapNotifyModemStateToWindowsEvents(0x0F).ToString());
    AssertEqual("23", Rfc2217Client.MapNotifyLineStateToWindowsErrors(0x1E).ToString());
    AssertEqual("23", Rfc2217Client.MapNotifyLineStateToWindowsErrors(0xFF).ToString());
}

static void Rfc2217TelnetParser()
{
    var client = new Rfc2217Client();
    var frame = client.ProcessNetworkBytes([0xFF, 0xFB, 0x2C, 0x41, 0xFF, 0xFF, 0x42], 7);

    AssertBytes([0x41, 0xFF, 0x42], frame.SerialData);
    AssertBytes([0xFF, 0xFD, 0x2C], frame.Replies);
    AssertEqual("1", frame.TelnetOptions.Count.ToString());
    AssertEqual(Rfc2217Client.TelnetCommandWill.ToString(), frame.TelnetOptions.Single().Command.ToString());
    AssertEqual(Rfc2217Client.TelnetOptionComPortControl.ToString(), frame.TelnetOptions.Single().Option.ToString());
    AssertTrue(frame.TelnetOptions.Single().Accepted, "WILL COM-PORT-OPTION should be accepted.");

    var closeClient = new Rfc2217Client();
    _ = closeClient.ProcessNetworkBytes([0xFF, 0xFB, 0x2C, 0xFF, 0xFD, 0x2C], 6);
    var closeNegotiation = closeClient.ProcessNetworkBytes([0xFF, 0xFE, 0x2C, 0xFF, 0xFC, 0x2C], 6);
    AssertBytes([0xFF, 0xFC, 0x2C, 0xFF, 0xFE, 0x2C], closeNegotiation.Replies);
    AssertEqual("2", closeNegotiation.TelnetOptions.Count.ToString());
    AssertTrue(closeNegotiation.TelnetOptions.All(option => option.Rejected), "DONT/WONT COM-PORT-OPTION should be reported as rejection.");
    AssertEqual("DONT COM-PORT-OPTION rejected", closeNegotiation.TelnetOptions[0].Describe());

    var unsupportedNegotiation = client.ProcessNetworkBytes([0xFF, 0xFD, 0x55, 0xFF, 0xFB, 0x55], 6);
    AssertBytes([0xFF, 0xFC, 0x55, 0xFF, 0xFE, 0x55], unsupportedNegotiation.Replies);
    AssertEqual("2", unsupportedNegotiation.TelnetOptions.Count.ToString());
    AssertTrue(unsupportedNegotiation.TelnetOptions.All(option => option.Rejected), "Unsupported Telnet options should be reported as rejected.");

    var echoClient = new Rfc2217Client();
    var remoteEcho = echoClient.ProcessNetworkBytes([0xFF, 0xFB, 0x01], 3);
    AssertBytes([0xFF, 0xFD, 0x01], remoteEcho.Replies);
    AssertEqual("WILL ECHO accepted", remoteEcho.TelnetOptions.Single().Describe());
    var localEcho = echoClient.ProcessNetworkBytes([0xFF, 0xFD, 0x01], 3);
    AssertBytes([0xFF, 0xFC, 0x01], localEcho.Replies);
    AssertEqual("DO ECHO rejected", localEcho.TelnetOptions.Single().Describe());

    var repeatClient = new Rfc2217Client();
    AssertBytes([0xFF, 0xFD, 0x2C], repeatClient.ProcessNetworkBytes([0xFF, 0xFB, 0x2C], 3).Replies);
    AssertBytes([], repeatClient.ProcessNetworkBytes([0xFF, 0xFB, 0x2C], 3).Replies);
    AssertBytes([0xFF, 0xFB, 0x2C], repeatClient.ProcessNetworkBytes([0xFF, 0xFD, 0x2C], 3).Replies);
    AssertBytes([], repeatClient.ProcessNetworkBytes([0xFF, 0xFD, 0x2C], 3).Replies);

    var initialClient = new Rfc2217Client();
    _ = initialClient.BuildInitialNegotiation();
    var initialReply = initialClient.ProcessNetworkBytes([0xFF, 0xFD, 0x2C, 0xFF, 0xFB, 0x2C], 6);
    AssertBytes([], initialReply.Replies);
    AssertTrue(initialReply.TelnetOptions.All(option => option.Accepted), "Initial negotiation replies should be accepted without producing a Telnet loop.");

    var notify = client.ProcessNetworkBytes([0xFF, 0xFA, 0x2C, 0x6B, 0xB0, 0xFF, 0xF0], 7);
    AssertEqual("0", notify.SerialData.Length.ToString());
    AssertEqual(Rfc2217Client.NotifyModemState.ToString(), notify.Notifications.Single().Command.ToString());
    AssertBytes([0xB0], notify.Notifications.Single().Payload);

    var ack = client.ProcessNetworkBytes([0xFF, 0xFA, 0x2C, 0x65, 0x00, 0x01, 0xC2, 0x00, 0xFF, 0xF0], 10);
    AssertTrue(Rfc2217Client.IsCommandAck(ack.Notifications.Single().Command), "SET-BAUDRATE ack should be recognized.");

    var suspend = client.ProcessNetworkBytes([0xFF, 0xFA, 0x2C, 0x6C, 0xFF, 0xF0], 6);
    AssertEqual(Rfc2217Client.FlowControlSuspend.ToString(), suspend.Notifications.Single().Command.ToString());
    AssertTrue(Rfc2217Client.IsFlowControlCommand(suspend.Notifications.Single().Command), "FLOWCONTROL-SUSPEND should be recognized.");

    var signatureRequest = client.ProcessNetworkBytes([0xFF, 0xFA, 0x2C, 0x00, 0xFF, 0xF0], 6);
    AssertEqual(Rfc2217Client.Signature.ToString(), signatureRequest.Notifications.Single().Command.ToString());
    AssertBytes(
        [0xFF, 0xFA, 0x2C, 0x00, 0x56, 0x43, 0x6F, 0x6D, 0x54, 0x75, 0x6E, 0x6E, 0x65, 0x6C, 0xFF, 0xF0],
        signatureRequest.Replies);

    var malformedNotify = client.ProcessNetworkBytes([0xFF, 0xFA, 0x2C, 0x6B, 0xB0, 0x00, 0xFF, 0xF0], 8);
    AssertEqual("0", malformedNotify.Notifications.Count.ToString());

    var malformedSuspend = client.ProcessNetworkBytes([0xFF, 0xFA, 0x2C, 0x6C, 0x00, 0xFF, 0xF0], 7);
    AssertEqual("0", malformedSuspend.Notifications.Count.ToString());

    var malformedBaudAck = client.ProcessNetworkBytes([0xFF, 0xFA, 0x2C, 0x65, 0x00, 0xFF, 0xF0], 7);
    AssertEqual("0", malformedBaudAck.Notifications.Count.ToString());
}

static void Rfc2217StreamFragmentation()
{
    var optionClient = new Rfc2217Client();
    var firstOption = optionClient.ProcessNetworkBytes([0xFF, 0xFB], 2);
    AssertEqual("0", firstOption.Replies.Length.ToString());
    var secondOption = optionClient.ProcessNetworkBytes([0x2C], 1);
    AssertBytes([0xFF, 0xFD, 0x2C], secondOption.Replies);

    var serialClient = new Rfc2217Client();
    var firstSerial = serialClient.ProcessNetworkBytes([0x41, 0xFF], 2);
    AssertBytes([0x41], firstSerial.SerialData);
    var secondSerial = serialClient.ProcessNetworkBytes([0xFF, 0x42], 2);
    AssertBytes([0xFF, 0x42], secondSerial.SerialData);

    var notificationClient = new Rfc2217Client();
    var firstNotification = notificationClient.ProcessNetworkBytes([0xFF, 0xFA, 0x2C, 0x6B, 0xFF], 5);
    AssertEqual("0", firstNotification.Notifications.Count.ToString());
    var secondNotification = notificationClient.ProcessNetworkBytes([0xFF, 0xFF, 0xF0], 3);
    AssertEqual(Rfc2217Client.NotifyModemState.ToString(), secondNotification.Notifications.Single().Command.ToString());
    AssertBytes([0xFF], secondNotification.Notifications.Single().Payload);

    var signatureClient = new Rfc2217Client();
    var firstSignature = signatureClient.ProcessNetworkBytes([0xFF, 0xFA, 0x2C, 0x00, 0xFF], 5);
    AssertEqual("0", firstSignature.Replies.Length.ToString());
    var secondSignature = signatureClient.ProcessNetworkBytes([0xF0], 1);
    AssertBytes(
        [0xFF, 0xFA, 0x2C, 0x00, 0x56, 0x43, 0x6F, 0x6D, 0x54, 0x75, 0x6E, 0x6E, 0x65, 0x6C, 0xFF, 0xF0],
        secondSignature.Replies);
}

static void Rfc2217AckSemantics()
{
    var expectedBaud = new Rfc2217ExpectedAck(Rfc2217Client.AckSetBaudRate, [0x00, 0x01, 0xC2, 0x00]);
    AssertTrue(
        expectedBaud.Matches(new Rfc2217Notification(Rfc2217Client.AckSetBaudRate, [0x00, 0x01, 0xC2, 0x00])),
        "ACK should match the command and accepted value.");
    AssertTrue(
        !expectedBaud.Matches(new Rfc2217Notification(Rfc2217Client.AckSetBaudRate, [0x00, 0x00, 0x25, 0x80])),
        "ACK with a different accepted value must not satisfy the pending command.");
    AssertTrue(
        expectedBaud.IsSameCommand(new Rfc2217Notification(Rfc2217Client.AckSetBaudRate, [0x00, 0x00, 0x25, 0x80])),
        "Different ACK value for the same command should be distinguishable as a rejection.");
    AssertEqual("2000", Rfc2217Client.RecommendedCommandAckTimeout.TotalMilliseconds.ToString("0"));
    var acceptedBaud = new Rfc2217ExpectedAck(
        Rfc2217Client.AckSetBaudRate,
        [0x00, 0x01, 0xC2, 0x00],
        AllowAcceptedValue: true);
    AssertTrue(
        acceptedBaud.MatchesAcceptedValue(new Rfc2217Notification(Rfc2217Client.AckSetBaudRate, [0x00, 0x00, 0x25, 0x80])),
        "Baud ACK can carry the remote accepted value.");
    AssertEqual("9600", Rfc2217Client.ReadUInt32Payload([0x00, 0x00, 0x25, 0x80]).ToString());
    AssertTrue(
        !acceptedBaud.MatchesAcceptedValue(new Rfc2217Notification(Rfc2217Client.AckSetBaudRate, [0x00, 0x00, 0x00, 0x00])),
        "Zero baud is not a valid accepted setting.");
    AssertTrue(
        new Rfc2217ExpectedAck(Rfc2217Client.AckSetParity, [1], AllowAcceptedValue: true)
            .MatchesAcceptedValue(new Rfc2217Notification(Rfc2217Client.AckSetParity, [3])),
        "Line-control ACK can carry the remote accepted value.");
    AssertTrue(
        !new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [8], AllowAcceptedValue: true)
            .MatchesAcceptedSetControlValue(new Rfc2217Notification(Rfc2217Client.AckSetControl, [9])),
        "SET-CONTROL is not relaxed as a remote serial setting.");
    AssertTrue(
        new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [17], AllowAcceptedValue: true)
            .MatchesAcceptedSetControlValue(new Rfc2217Notification(Rfc2217Client.AckSetControl, [1])),
        "Outbound flow-control ACK can carry the peer accepted value.");
    AssertTrue(
        new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [18], AllowAcceptedValue: true)
            .MatchesAcceptedSetControlValue(new Rfc2217Notification(Rfc2217Client.AckSetControl, [14])),
        "Inbound flow-control ACK can carry the peer accepted value.");
    AssertTrue(
        !new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [17], AllowAcceptedValue: true)
            .MatchesAcceptedSetControlValue(new Rfc2217Notification(Rfc2217Client.AckSetControl, [14])),
        "Outbound flow-control ACK must not match an inbound accepted value.");
    AssertTrue(
        new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [0], AllowAcceptedValue: true)
            .MatchesAcceptedSetControlValue(new Rfc2217Notification(Rfc2217Client.AckSetControl, [3])),
        "Outbound flow-control query should accept an outbound flow-control response.");
    AssertTrue(
        new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [4], AllowAcceptedValue: true)
            .MatchesAcceptedSetControlValue(new Rfc2217Notification(Rfc2217Client.AckSetControl, [6])),
        "BREAK query should accept BREAK off response.");
    AssertTrue(
        new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [7], AllowAcceptedValue: true)
            .MatchesAcceptedSetControlValue(new Rfc2217Notification(Rfc2217Client.AckSetControl, [8])),
        "DTR query should accept DTR on response.");
    AssertTrue(
        new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [10], AllowAcceptedValue: true)
            .MatchesAcceptedSetControlValue(new Rfc2217Notification(Rfc2217Client.AckSetControl, [12])),
        "RTS query should accept RTS off response.");
    AssertTrue(
        new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [13], AllowAcceptedValue: true)
            .MatchesAcceptedSetControlValue(new Rfc2217Notification(Rfc2217Client.AckSetControl, [16])),
        "Inbound flow-control query should accept an inbound flow-control response.");
    AssertTrue(
        !new Rfc2217ExpectedAck(Rfc2217Client.AckSetControl, [7], AllowAcceptedValue: true)
            .MatchesAcceptedSetControlValue(new Rfc2217Notification(Rfc2217Client.AckSetControl, [11])),
        "DTR query must not match an RTS response.");
    AssertEqual("17", Rfc2217Client.MapOutboundFlowControl(0x20, 0).ToString());
    AssertEqual("18", Rfc2217Client.MapInboundFlowControl(0x02, 0).ToString());
    AssertEqual("16", Rfc2217Client.MapInboundFlowControl(0, 0x80).ToString());
    AssertEqual("16", Rfc2217Client.MapInboundFlowControl(0, 0x82).ToString());
    AssertEqual("3", Rfc2217Client.MapPurge(0x0C).ToString());
    AssertEqual("1", Rfc2217Client.MapPurge(0x08).ToString());
    AssertEqual("0", Rfc2217Client.MapPurge(0x03).ToString());
    AssertEqual("2", Rfc2217Client.MapRfc2217ParityToWindows(3).ToString());
    AssertEqual("1", Rfc2217Client.MapRfc2217StopBitsToWindows(3).ToString());
}

static void Rfc2217NotificationMappings()
{
    AssertEqual("240", Rfc2217Client.MapNotifyModemStateToWindowsStatus(0xF0).ToString());
    AssertEqual("312", Rfc2217Client.MapNotifyModemStateToWindowsEvents(0x0F).ToString());
    AssertEqual("0", Rfc2217Client.MapNotifyModemStateToWindowsEvents(0xB0).ToString());
    AssertEqual("23", Rfc2217Client.MapNotifyLineStateToWindowsErrors(0x1E).ToString());
    AssertEqual("23", Rfc2217Client.MapNotifyLineStateToWindowsErrors(0xFF).ToString());
    AssertEqual("192", Rfc2217Client.MapNotifyLineStateToWindowsEvents(0x1E).ToString());
    AssertEqual("5", Rfc2217Client.MapNotifyLineStateToWindowsEvents(0x61).ToString());
    AssertEqual("133", Rfc2217Client.MapNotifyLineStateToWindowsEvents(0xE1).ToString());
}

static void Com2TcpCommandUsesBatchWrapper()
{
    using var temp = new TempDir();
    CreateFakeDependencies(temp.Path);
    var detector = new DependencyDetector([temp.Path], pathOverride: "");
    var command = new Hub4comCommandBuilder(detector).Build(new TunnelMapping
    {
        BackingPort = "CNCB12",
        Host = "192.168.1.50",
        Port = 3333
    });

    AssertEqual("cmd.exe", command.FileName);
    AssertStringContains(command.Arguments, "com2tcp-rfc2217.bat");
    AssertStringContains(command.Arguments, "\\\\.\\CNCB12");
    AssertStringContains(command.Arguments, "192.168.1.50 3333");
}

static async Task MissingDependenciesFaultMappingAsync()
{
    using var temp = new TempDir();
    var store = await StoreWithMappingAsync(temp.Path, new TunnelMapping { Name = "Missing", Host = "127.0.0.1" });
    var orchestrator = CreateOrchestrator(store, new DependencyDetector([Path.Combine(temp.Path, "missing")], pathOverride: ""), new InMemoryLog());

    var status = await orchestrator.StartAsync((await store.LoadAsync()).Mappings.Single().Id);
    AssertEqual(TunnelRunState.Faulted.ToString(), status.State.ToString());
    AssertStringContains(status.LastError ?? "", "dependencies are missing");
}

static async Task MissingBackingPortFaultsBeforeHub4comAsync()
{
    using var temp = new TempDir();
    CreateFakeDependencies(temp.Path);
    var mapping = new TunnelMapping
    {
        Name = "Bad backing",
        VisiblePort = "COM27",
        BackingPort = "CNCB27",
        Host = "127.0.0.1",
        Port = 4000
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        new InMemoryLog(),
        ["COM27", "COM28"]);

    var status = await orchestrator.StartAsync((await store.LoadAsync()).Mappings.Single().Id);
    AssertEqual(TunnelRunState.Faulted.ToString(), status.State.ToString());
    AssertStringContains(status.LastError ?? "", "Backing port CNCB27 is not registered");
    AssertStringContains(status.LastError ?? "", "Existing ports: COM27, COM28");
}

static async Task KmdfMappingReportsStartupFaultAsync()
{
    using var temp = new TempDir();
    var mapping = new TunnelMapping
    {
        Name = "Driver",
        Backend = TunnelBackend.Kmdf,
        VisiblePort = "COM44",
        BackingPort = null,
        RestartOnFailure = false
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var orchestrator = CreateOrchestrator(store, new DependencyDetector([temp.Path], pathOverride: ""), new InMemoryLog());

    var status = await orchestrator.StartAsync((await store.LoadAsync()).Mappings.Single().Id);
    AssertEqual(TunnelRunState.Faulted.ToString(), status.State.ToString());
    AssertStringContainsAny(status.LastError ?? "",
        "Could not open KMDF control channel",
        "KMDF driver protocol",
        "Could not connect to RFC2217 endpoint");

    var secondStatus = await orchestrator.StartAsync((await store.LoadAsync()).Mappings.Single().Id);
    AssertEqual(TunnelRunState.Faulted.ToString(), secondStatus.State.ToString());
    AssertStringContainsAny(secondStatus.LastError ?? "",
        "Could not open KMDF control channel",
        "KMDF driver protocol",
        "Could not connect to RFC2217 endpoint");
}

static async Task KmdfSessionRestartsAfterNetworkFaultAsync()
{
    using var temp = new TempDir();
    var mapping = new TunnelMapping
    {
        Name = "Restarting driver",
        Backend = TunnelBackend.Kmdf,
        VisiblePort = "COM47",
        BackingPort = null,
        RestartOnFailure = true
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog();
    var starts = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        [],
        (sessionMapping, sessionLog, faulted) =>
        {
            var startNumber = Interlocked.Increment(ref starts);
            return new FakeKmdfTunnelSession(
                faulted,
                failAfterStart: startNumber == 1 ? "Remote endpoint closed the TCP connection." : null);
        },
        TimeSpan.FromMilliseconds(50));

    var id = (await store.LoadAsync()).Mappings.Single().Id;
    var first = await orchestrator.StartAsync(id);
    AssertEqual(TunnelRunState.Running.ToString(), first.State.ToString());

    await WaitUntilAsync(() => Volatile.Read(ref starts) >= 2, "KMDF restart did not run.");

    var status = orchestrator.GetStatus().Tunnels.Single(t => t.Id == id);
    AssertEqual(TunnelRunState.Running.ToString(), status.State.ToString());
    AssertTrue(
        log.Snapshot().Any(e => e.Message.Contains("Scheduling KMDF restart", StringComparison.OrdinalIgnoreCase)),
        "KMDF network fault should schedule a restart.");
}

static async Task KmdfPermanentDriverFaultDoesNotRestartAsync()
{
    using var temp = new TempDir();
    var mapping = new TunnelMapping
    {
        Name = "Old driver",
        Backend = TunnelBackend.Kmdf,
        VisiblePort = "COM48",
        BackingPort = null,
        RestartOnFailure = true
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog();
    var starts = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        [],
        (sessionMapping, sessionLog, faulted) =>
        {
            Interlocked.Increment(ref starts);
            return new FakeKmdfTunnelSession(
                faulted,
                failAfterStart: null,
                failOnStart: "KMDF driver protocol 1.1 is older than required 1.2. Rebuild and reinstall VComTunnel.Serial.");
        },
        TimeSpan.FromMilliseconds(50));

    var id = (await store.LoadAsync()).Mappings.Single().Id;
    var first = await orchestrator.StartAsync(id);
    AssertEqual(TunnelRunState.Faulted.ToString(), first.State.ToString());
    AssertStringContains(first.LastError ?? "", "driver protocol");

    await Task.Delay(150);

    AssertEqual("1", Volatile.Read(ref starts).ToString());
    AssertTrue(
        !log.Snapshot().Any(e => e.Message.Contains("Scheduling KMDF restart", StringComparison.OrdinalIgnoreCase)),
        "Permanent KMDF driver errors must not schedule restart.");
}

static async Task Com0comCreateAndRemovePlansAsync()
{
    using var temp = new TempDir();
    CreateFakeDependencies(temp.Path);
    var mapping = new TunnelMapping
    {
        Name = "Managed",
        VisiblePort = "COM29",
        BackingPort = "CNCB29",
        Host = "127.0.0.1",
        Port = 4000
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var detector = new DependencyDetector([temp.Path], pathOverride: "");
    var manager = new Com0comSetupManager(
        store,
        detector,
        new FakeComPortInventory(["COM29", "CNCB29"], [new Com0comPairInfo(2, "COM29", "CNCB29", @"\Device\com0com12", @"\Device\com0com22", true)]));

    var id = (await store.LoadAsync()).Mappings.Single().Id;
    var create = await manager.BuildCreatePlanAsync(id);
    AssertStringContains(create.Arguments, "install PortName=COM29 PortName=CNCB29");
    AssertTrue(create.RequiresElevation, "setupc plans should require elevation.");

    var remove = manager.BuildRemovePlan(2);
    AssertStringContains(remove.Arguments, "remove 2");
    AssertEqual(1.ToString(), manager.GetPairs().Count.ToString());
}

static async Task FakeCom2TcpProcessStartsAndStopsAsync()
{
    using var temp = new TempDir();
    CreateFakeDependencies(temp.Path);
    var mapping = new TunnelMapping
    {
        Name = "Fake bridge",
        VisiblePort = "COM55",
        BackingPort = "CNCB55",
        Host = "127.0.0.1",
        Port = 2217,
        RestartOnFailure = false
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog();
    var orchestrator = CreateOrchestrator(store, new DependencyDetector([temp.Path], pathOverride: ""), log);
    var id = (await store.LoadAsync()).Mappings.Single().Id;

    var started = await orchestrator.StartAsync(id);
    AssertEqual(TunnelRunState.Running.ToString(), started.State.ToString());
    AssertTrue(started.ProcessId is not null, "Process id should be reported.");
    await Task.Delay(500);
    AssertTrue(log.Snapshot().Any(e => e.Message.Contains("fake-com2tcp", StringComparison.OrdinalIgnoreCase)), "Fake process output should be logged.");

    var stopped = orchestrator.Stop(id);
    AssertEqual(TunnelRunState.Stopped.ToString(), stopped.State.ToString());
}

static async Task FakeCom2TcpProcessRestartsAfterExitAsync()
{
    using var temp = new TempDir();
    CreateFakeDependencies(
        temp.Path,
        """
        @echo off
        echo fake-com2tcp %*
        ping -n 2 127.0.0.1 > nul
        """);
    var mapping = new TunnelMapping
    {
        Name = "Restarting fake bridge",
        VisiblePort = "COM56",
        BackingPort = "CNCB56",
        Host = "127.0.0.1",
        Port = 2217,
        RestartOnFailure = true
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog();
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        [],
        restartDelay: TimeSpan.FromMilliseconds(50));
    var id = (await store.LoadAsync()).Mappings.Single().Id;

    var started = await orchestrator.StartAsync(id);
    AssertEqual(TunnelRunState.Running.ToString(), started.State.ToString());

    await WaitUntilAsync(
        () => log.Snapshot().Count(e => e.Message.Contains("Started hub4com process", StringComparison.OrdinalIgnoreCase)) >= 2,
        "hub4com process was not restarted after exit.");

    var status = orchestrator.GetStatus().Tunnels.Single(t => t.Id == id);
    AssertEqual(TunnelRunState.Running.ToString(), status.State.ToString());
    orchestrator.Stop(id);
}

static async Task ManualStopSuppressesFakeCom2TcpRestartAsync()
{
    using var temp = new TempDir();
    CreateFakeDependencies(temp.Path);
    var mapping = new TunnelMapping
    {
        Name = "Stopped fake bridge",
        VisiblePort = "COM57",
        BackingPort = "CNCB57",
        Host = "127.0.0.1",
        Port = 2217,
        RestartOnFailure = true
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog();
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        [],
        restartDelay: TimeSpan.FromMilliseconds(50));
    var id = (await store.LoadAsync()).Mappings.Single().Id;

    var started = await orchestrator.StartAsync(id);
    AssertEqual(TunnelRunState.Running.ToString(), started.State.ToString());

    var stopped = orchestrator.Stop(id);
    AssertEqual(TunnelRunState.Stopped.ToString(), stopped.State.ToString());
    await Task.Delay(300);

    var status = orchestrator.GetStatus().Tunnels.Single(t => t.Id == id);
    AssertEqual(TunnelRunState.Stopped.ToString(), status.State.ToString());
    AssertEqual(
        "1",
        log.Snapshot().Count(e => e.Message.Contains("Started hub4com process", StringComparison.OrdinalIgnoreCase)).ToString());
}

static async Task DependencyInstallerExtractsToolZipsAsync()
{
    using var temp = new TempDir();
    var oldHome = Environment.GetEnvironmentVariable("VCOMTUNNEL_HOME");
    Environment.SetEnvironmentVariable("VCOMTUNNEL_HOME", temp.Path);
    try
    {
        var http = new HttpClient(new ZipHandler())
        {
            BaseAddress = new Uri("https://example.invalid/")
        };
        var detector = new DependencyDetector([AppPaths.ToolsDirectory], pathOverride: "");
        var installer = new DependencyInstaller(detector, http);
        var result = await installer.InstallAsync(new DependencyInstallRequest());

        AssertTrue(result.Steps.All(s => s.Success), string.Join("; ", result.Steps.Select(s => s.Message)));
        AssertTrue(File.Exists(Path.Combine(AppPaths.ToolsDirectory, "hub4com", "com2tcp-rfc2217.bat")), "hub4com batch should be extracted.");
        AssertTrue(File.Exists(Path.Combine(AppPaths.ToolsDirectory, "com0com", "Setup_com0com_v3.0.0.0_W7_x64_signed.exe")), "com0com installer should be extracted.");
        AssertTrue(!detector.Detect().IsReadyForCom0comHub4com, "Detector should remain not ready until com0com driver tools are installed.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("VCOMTUNNEL_HOME", oldHome);
    }
}

static async Task DependencyInstallerUsesBundledReleaseArchivesAsync()
{
    using var temp = new TempDir();
    var oldHome = Environment.GetEnvironmentVariable("VCOMTUNNEL_HOME");
    var oldArchiveRoot = Environment.GetEnvironmentVariable(DependencyInstaller.BundledDependencyArchiveDirectoryVariable);
    Environment.SetEnvironmentVariable("VCOMTUNNEL_HOME", Path.Combine(temp.Path, "home"));
    Environment.SetEnvironmentVariable(DependencyInstaller.BundledDependencyArchiveDirectoryVariable, Path.Combine(temp.Path, "bundled"));
    try
    {
        var archiveRoot = Environment.GetEnvironmentVariable(DependencyInstaller.BundledDependencyArchiveDirectoryVariable)!;
        Directory.CreateDirectory(archiveRoot);
        CreateZip(
            Path.Combine(archiveRoot, DependencyInstaller.Hub4comArchiveName),
            new Dictionary<string, string> { ["hub4com.exe"] = "", ["com2tcp-rfc2217.bat"] = "@echo off" });
        CreateZip(
            Path.Combine(archiveRoot, DependencyInstaller.Com0comArchiveName),
            new Dictionary<string, string> { ["Setup_com0com_v3.0.0.0_W7_x64_signed.exe"] = "", ["Setup_com0com_v3.0.0.0_W7_x86_signed.exe"] = "" });

        var http = new HttpClient(new ThrowingHandler());
        var detector = new DependencyDetector([AppPaths.ToolsDirectory], pathOverride: "");
        var installer = new DependencyInstaller(detector, http);
        var result = await installer.InstallAsync(new DependencyInstallRequest());

        AssertTrue(result.Steps.All(s => s.Success), string.Join("; ", result.Steps.Select(s => s.Message)));
        AssertTrue(result.Steps.All(s => s.Message.Contains("bundled release archive", StringComparison.OrdinalIgnoreCase)), "Bundled archives should be used before network downloads.");
        AssertTrue(File.Exists(Path.Combine(AppPaths.ToolsDirectory, "hub4com", "com2tcp-rfc2217.bat")), "hub4com batch should be extracted from bundled archive.");
        AssertTrue(File.Exists(Path.Combine(AppPaths.ToolsDirectory, "com0com", "Setup_com0com_v3.0.0.0_W7_x64_signed.exe")), "com0com installer should be extracted from bundled archive.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("VCOMTUNNEL_HOME", oldHome);
        Environment.SetEnvironmentVariable(DependencyInstaller.BundledDependencyArchiveDirectoryVariable, oldArchiveRoot);
    }
}

static async Task<ConfigStore> StoreWithMappingAsync(string root, TunnelMapping mapping)
{
    var store = new ConfigStore(Path.Combine(root, "config.json"));
    await store.SaveAsync(new VComTunnelConfig { Mappings = [mapping] });
    return store;
}

static TunnelOrchestrator CreateOrchestrator(ConfigStore store, DependencyDetector detector, InMemoryLog log)
{
    return CreateOrchestratorWithPorts(store, detector, log, []);
}

static TunnelOrchestrator CreateOrchestratorWithPorts(
    ConfigStore store,
    DependencyDetector detector,
    InMemoryLog log,
    IReadOnlyList<string>? registeredPorts,
    Func<TunnelMapping, InMemoryLog, Action<IKmdfTunnelSession, string>, IKmdfTunnelSession>? kmdfSessionFactory = null,
    TimeSpan? restartDelay = null)
{
    return new TunnelOrchestrator(
        store,
        detector,
        new Hub4comCommandBuilder(detector),
        new FakeComPortInventory(registeredPorts),
        log,
        kmdfSessionFactory,
        restartDelay);
}

static void CreateFakeDependencies(string root, string? com2tcpBody = null)
{
    File.WriteAllText(Path.Combine(root, "setupc.exe"), "");
    File.WriteAllText(Path.Combine(root, "hub4com.exe"), "");
    File.WriteAllText(
        Path.Combine(root, "com2tcp-rfc2217.bat"),
        com2tcpBody ?? """
        @echo off
        echo fake-com2tcp %*
        ping -n 4 127.0.0.1 > nul
        """);
}

static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 5000)
{
    var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (condition())
        {
            return;
        }

        await Task.Delay(25);
    }

    throw new Exception(message);
}

static void AssertEmpty(IReadOnlyList<string> errors)
{
    if (errors.Count != 0)
    {
        throw new Exception(string.Join("; ", errors));
    }
}

static void AssertContains(IReadOnlyList<string> errors, string expected)
{
    if (!errors.Any(e => e.Contains(expected, StringComparison.OrdinalIgnoreCase)))
    {
        throw new Exception($"Expected '{expected}' in: {string.Join("; ", errors)}");
    }
}

static void AssertStringContains(string actual, string expected)
{
    if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
    {
        throw new Exception($"Expected '{expected}' in '{actual}'.");
    }
}

static void AssertStringContainsAny(string actual, params string[] expected)
{
    if (!expected.Any(value => actual.Contains(value, StringComparison.OrdinalIgnoreCase)))
    {
        throw new Exception($"Expected one of '{string.Join("' or '", expected)}' in '{actual}'.");
    }
}

static void AssertEqual(string expected, string actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new Exception($"Expected '{expected}', got '{actual}'.");
    }
}

static void AssertBytes(byte[] expected, byte[] actual)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new Exception($"Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}.");
    }
}

static void AssertRfc2217Notifications(byte[] frame, params Rfc2217Notification[] expected)
{
    var actual = new Rfc2217Client().ProcessNetworkBytes(frame, frame.Length).Notifications;
    AssertEqual(expected.Length.ToString(), actual.Count.ToString());
    for (var i = 0; i < expected.Length; i++)
    {
        AssertEqual(expected[i].Command.ToString(), actual[i].Command.ToString());
        AssertBytes(expected[i].Payload, actual[i].Payload);
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static void CreateZip(string path, IReadOnlyDictionary<string, string> files)
{
    using var stream = File.Create(path);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    foreach (var (name, content) in files)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VComTunnelTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed class ZipHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isHub4com = request.RequestUri?.ToString().Contains("hub4com", StringComparison.OrdinalIgnoreCase) == true;
        var files = isHub4com
            ? new Dictionary<string, string> { ["hub4com.exe"] = "", ["com2tcp-rfc2217.bat"] = "@echo off" }
            : new Dictionary<string, string> { ["Setup_com0com_v3.0.0.0_W7_x64_signed.exe"] = "", ["Setup_com0com_v3.0.0.0_W7_x86_signed.exe"] = "" };

        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        stream.Position = 0;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        };
        return Task.FromResult(response);
    }
}

internal sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Network should not be used when bundled dependency archives are available.");
    }
}

internal sealed class FakeKmdfTunnelSession : IKmdfTunnelSession
{
    private readonly Action<IKmdfTunnelSession, string> _faulted;
    private readonly string? _failAfterStart;
    private readonly string? _failOnStart;

    public FakeKmdfTunnelSession(Action<IKmdfTunnelSession, string> faulted, string? failAfterStart, string? failOnStart = null)
    {
        _faulted = faulted;
        _failAfterStart = failAfterStart;
        _failOnStart = failOnStart;
    }

    public TunnelRunState State { get; private set; } = TunnelRunState.Starting;
    public string? LastError { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_failOnStart is not null)
        {
            LastError = _failOnStart;
            State = TunnelRunState.Faulted;
            throw new InvalidOperationException(_failOnStart);
        }

        State = TunnelRunState.Running;
        if (_failAfterStart is not null)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(25, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                LastError = _failAfterStart;
                State = TunnelRunState.Faulted;
                _faulted(this, _failAfterStart);
            }, cancellationToken);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (LastError is null)
        {
            State = TunnelRunState.Stopped;
        }
    }
}

internal sealed class FakeComPortInventory : IComPortInventory
{
    private readonly IReadOnlyList<string> _ports;
    private readonly IReadOnlyList<Com0comPairInfo> _pairs;

    public FakeComPortInventory(IReadOnlyList<string>? ports, IReadOnlyList<Com0comPairInfo>? pairs = null)
    {
        _ports = ports ?? [];
        _pairs = pairs ?? [];
    }

    public IReadOnlyList<string> GetRegisteredPortNames() => _ports;
    public IReadOnlyList<Com0comPairInfo> GetCom0comPairs() => _pairs;
}
