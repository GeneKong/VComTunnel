using VComTunnel.Core;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

var tests = new List<(string Name, Func<Task> Test)>
{
    ("RFC2217 default port is 2217", () => Task.Run(Rfc2217DefaultPortIs2217)),
    ("valid multi mapping config", () => Task.Run(ValidMultiMappingConfig)),
    ("duplicate visible COM is rejected", () => Task.Run(DuplicateVisibleComIsRejected)),
    ("same visible and backing COM is rejected", () => Task.Run(SameVisibleAndBackingComIsRejected)),
    ("KMDF backing port is rejected", () => Task.Run(KmdfBackingPortIsRejected)),
    ("invalid host and port are rejected", () => Task.Run(InvalidHostAndPortAreRejected)),
    ("wireless serial auto discovery validation", () => Task.Run(WirelessSerialAutoDiscoveryValidation)),
    ("wireless serial endpoint registry", () => Task.Run(WirelessSerialEndpointRegistryUpdatesEndpoint)),
    ("wireless serial endpoint alias API contract", () => Task.Run(WirelessSerialEndpointAliasApiContract)),
    ("wireless serial periodic query follows MAC binding", WirelessSerialPeriodicQueryFollowsMacBindingAsync),
    ("wireless serial endpoint change restarts running mapping", WirelessSerialEndpointChangeRestartsRunningMappingAsync),
    ("wireless serial MAC binding corrects wrong saved endpoint on start", WirelessSerialMacBindingCorrectsWrongSavedEndpointOnStartAsync),
    ("COM backing port is accepted", () => Task.Run(ComBackingPortIsAccepted)),
    ("config round trip", ConfigRoundTripAsync),
    ("service endpoint defaults to loopback", () => Task.Run(ServiceEndpointDefaultsToLoopback)),
    ("service endpoint accepts loopback override", () => Task.Run(ServiceEndpointAcceptsLoopbackOverride)),
    ("service endpoint rejects non-loopback override", () => Task.Run(ServiceEndpointRejectsNonLoopbackOverride)),
    ("TCP tunnel options enable low latency", () => Task.Run(TcpTunnelOptionsEnableLowLatency)),
    ("file logs rotate and cap archives", () => Task.Run(FileLogsRotateAndCapArchives)),
    ("com0com create hints", () => Task.Run(Com0comCreateHints)),
    ("com0com service maps peer modem signals", () => Task.Run(Com0comServiceMapsPeerModemSignals)),
    ("com0com service does not synthesize unobserved RTS pulse", () => Task.Run(Com0comServiceDoesNotSynthesizeUnobservedRtsPulse)),
    ("com0com service control-line switch blocks forwarding", () => Task.Run(Com0comServiceControlLineSwitchBlocksForwarding)),
    ("com0com service runtime control-line update blocks forwarding", () => Task.Run(Com0comServiceRuntimeControlLineUpdateBlocksForwarding)),
    ("com0com service remote serial settings do not overwrite local settings", () => Task.Run(Com0comServiceRemoteSerialSettingsDoNotOverwriteLocalSettings)),
    ("com0com service startup syncs serial settings without control-line replay", Com0comServiceStartupSyncsSerialSettingsWithoutControlLineReplayAsync),
    ("com0com service startup uses peer serial setting insertions", Com0comServiceStartupUsesPeerSerialSettingInsertionsAsync),
    ("com0com service forwards peer serial setting insertions", Com0comServiceForwardsPeerSerialSettingInsertionsAsync),
    ("running mapping options hot update on save", RunningMappingOptionsHotUpdateOnSaveAsync),
    ("running backend change stops old process on save", RunningBackendChangeStopsOldProcessOnSaveAsync),
    ("deleted mapping stops and removes runtime on save", DeletedMappingStopsAndRemovesRuntimeOnSaveAsync),
    ("restart option hot update cancels pending restart", RestartOptionHotUpdateCancelsPendingRestartAsync),
    ("faulted mapping restart option hot update schedules restart", FaultedMappingRestartOptionHotUpdateSchedulesRestartAsync),
    ("com0com service RX pipeline writes small chunks", Com0comServiceRxPipelineWritesSmallChunksAsync),
    ("com0com service modem polling forwards RTS", Com0comServiceModemPollingForwardsRtsAsync),
    ("com0com service backing transport is binary clean", () => Task.Run(Com0comServiceBackingTransportIsBinaryClean)),
    ("com0com service builds Win32 device paths", () => Task.Run(Com0comServiceBuildsWin32DevicePaths)),
    ("serial RX backpressure info reports remaining bytes", () => Task.Run(SerialRxBackpressureInfoReportsRemainingBytes)),
    ("esptool baud monitor waits for response", () => Task.Run(EspToolBaudMonitorWaitsForResponse)),
    ("KMDF control path uses visible COM", () => Task.Run(KmdfControlPathUsesVisibleCom)),
    ("KMDF control-line switch blocks forwarding", () => Task.Run(KmdfControlLineSwitchBlocksForwarding)),
    ("KMDF data logs are throttled", () => Task.Run(KmdfDataLogsAreThrottled)),
    ("KMDF pnputil CSV parser finds VComTunnel ports", () => Task.Run(KmdfPnpUtilCsvParserFindsPorts)),
    ("KMDF driver certificate path resolves", () => Task.Run(KmdfDriverCertificatePathResolves)),
    ("RFC2217 command encoding", () => Task.Run(Rfc2217CommandEncoding)),
    ("hub4com RFC2217 client baseline", () => Task.Run(Hub4comRfc2217ClientBaseline)),
    ("RFC2217 telnet parser", () => Task.Run(Rfc2217TelnetParser)),
    ("RFC2217 stream fragmentation", () => Task.Run(Rfc2217StreamFragmentation)),
    ("RFC2217 ack semantics", () => Task.Run(Rfc2217AckSemantics)),
    ("RFC2217 notification mappings", () => Task.Run(Rfc2217NotificationMappings)),
    ("RFC2217 local flow-control depth", () => Task.Run(Rfc2217LocalFlowControlDepth)),
    ("hub4com default command avoids control filters", () => Task.Run(Hub4comDefaultCommandAvoidsControlFilters)),
    ("hub4com control command enables control filters", () => Task.Run(Hub4comControlCommandEnablesControlFilters)),
    ("missing dependencies fault mapping", MissingDependenciesFaultMappingAsync),
    ("missing backing port faults before hub4com", MissingBackingPortFaultsBeforeHub4comAsync),
    ("com0com service backend starts without hub4com", Com0comServiceBackendStartsWithoutHub4comAsync),
    ("com0com service backend restarts after network fault", Com0comServiceBackendRestartsAfterNetworkFaultAsync),
    ("restart backoff keeps retrying and limits repeated logs", RestartBackoffKeepsRetryingAndLimitsRepeatedLogsAsync),
    ("com0com service start requests are serialized", Com0comServiceStartRequestsAreSerializedAsync),
    ("starting same endpoint stops prior tunnel", StartingSameEndpointStopsPriorTunnelAsync),
    ("autostart restores last running same endpoint mapping", AutoStartRestoresLastRunningSameEndpointMappingAsync),
    ("autostart separates wireless mappings with same fallback endpoint", AutoStartSeparatesWirelessMappingsWithSameFallbackEndpointAsync),
    ("autostart timeout does not block another mapping", AutoStartTimeoutDoesNotBlockAnotherMappingAsync),
    ("stale starting session completion is ignored", StaleStartingSessionCompletionIsIgnoredAsync),
    ("hub4com to com0com service retries busy backing port", Hub4comToCom0comServiceRetriesBusyBackingPortAsync),
    ("com0com service access denied does not restart", Com0comServiceAccessDeniedDoesNotRestartAsync),
    ("stale stopped session fault is ignored", StaleStoppedSessionFaultIsIgnoredAsync),
    ("com0com service backing open diagnostics", () => Task.Run(Com0comServiceBackingOpenDiagnostics)),
    ("com0com create and remove plans", Com0comCreateAndRemovePlansAsync),
    ("KMDF mapping reports startup fault", KmdfMappingReportsStartupFaultAsync),
    ("KMDF session restarts after network fault", KmdfSessionRestartsAfterNetworkFaultAsync),
    ("KMDF default start suppresses initial control lines", KmdfDefaultStartSuppressesInitialControlLinesAsync),
    ("KMDF permanent driver faults do not restart", KmdfPermanentDriverFaultDoesNotRestartAsync),
    ("fake hub4com process starts and stops", FakeHub4comProcessStartsAndStopsAsync),
    ("fake hub4com process restarts after exit", FakeHub4comProcessRestartsAfterExitAsync),
    ("fake hub4com access denied does not restart", FakeHub4comAccessDeniedDoesNotRestartAsync),
    ("manual stop suppresses fake hub4com restart", ManualStopSuppressesFakeHub4comRestartAsync),
    ("stopping unknown mapping does not create runtime status", () => Task.Run(StoppingUnknownMappingDoesNotCreateRuntimeStatus)),
    ("dependency installer extracts tool zips", DependencyInstallerExtractsToolZipsAsync),
    ("dependency installer uses bundled release archives", DependencyInstallerUsesBundledReleaseArchivesAsync),
    ("dependency installer falls back after invalid mirror", DependencyInstallerFallsBackAfterInvalidMirrorAsync),
    ("dependency installer rejects HTML downloads", DependencyInstallerRejectsHtmlDownloadsAsync)
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

static void Rfc2217DefaultPortIs2217()
{
    var mapping = new TunnelMapping();

    AssertEqual("2217", mapping.Port.ToString());
    AssertEqual("2217", TunnelMapping.DefaultRfc2217Port.ToString());
}

static void ValidMultiMappingConfig()
{
    var config = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "A", VisiblePort = "COM12", BackingPort = "CNCB12", Host = "esp-dap.local", Port = 3333 },
            new TunnelMapping { Name = "S", Backend = TunnelBackend.Com0comService, VisiblePort = "COM32", BackingPort = "CNCB32", Host = "127.0.0.1", Port = 5000 },
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

static void WirelessSerialAutoDiscoveryValidation()
{
    var missingMac = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "Wireless", Backend = TunnelBackend.Com0comService, WirelessSerialAutoDiscover = true }
        ]
    };
    AssertContains(ConfigValidator.Validate(missingMac), "wirelessSerialMac");

    var wrongBackend = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping { Name = "Wireless", WirelessSerialAutoDiscover = true, WirelessSerialMac = "AA:BB:CC:DD:EE:FF" }
        ]
    };
    AssertContains(ConfigValidator.Validate(wrongBackend), "com0comService");

    var valid = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping
            {
                Name = "Wireless",
                Backend = TunnelBackend.Com0comService,
                BackingPort = "CNCB12",
                WirelessSerialAutoDiscover = true,
                WirelessSerialMac = "AA-BB-CC-DD-EE-FF"
            }
        ]
    };
    AssertEmpty(ConfigValidator.Validate(valid));
}

static void WirelessSerialEndpointRegistryUpdatesEndpoint()
{
    var registry = new WirelessSerialEndpointRegistry(new InMemoryLog(), deviceTtl: TimeSpan.FromMinutes(1));
    var device = registry.Upsert(new WirelessSerialEndpointUpdateRequest(
        Mac: "aa:bb:cc:dd:ee:ff",
        IpAddress: "192.168.10.42",
        ServicePort: 2217,
        DeviceId: "unit-01",
        Alias: "东侧机柜",
        Name: "XFG-N01",
        Product: "Wireless Serial",
        Board: "esp32c3",
        Firmware: "1.0.0",
        Mode: "sta",
        WifiRssi: -58,
        ConfigMode: false,
        Clients: 1,
        Source: "test"));

    AssertEqual("AABBCCDDEEFF", device.Mac);
    AssertEqual("192.168.10.42", device.IpAddress);
    AssertEqual("2217", device.ServicePort?.ToString() ?? "");
    AssertEqual("-58", device.WifiRssi?.ToString() ?? "");
    AssertEqual("东侧机柜", device.Alias ?? "");

    var endpoint = registry.FindEndpointByMac("AA-BB-CC-DD-EE-FF");
    AssertEqual("192.168.10.42", endpoint!.Host);
    AssertEqual("2217", endpoint.Port?.ToString() ?? "");

    var packet = """
        {
          "magic":"XFGWS",
          "proto":"xfg-discovery",
          "ver":1,
          "cmd":"announce",
          "device":{"name":"XFG-N02","id":"unit-02","alias":"西侧机柜","mac":"11:22:33:44:55:66"},
          "net":{"ip":"192.168.10.43","port":5000,"mode":"rfc2217"}
        }
        """;
    AssertTrue(
        registry.TryApplyDiscoveryPacket(packet, new IPEndPoint(IPAddress.Parse("192.168.10.99"), 19527), out var parsed),
        "Expected minimal WirelessSerial UDP endpoint packet to parse.");
    AssertEqual("112233445566", parsed!.Mac);
    AssertEqual("192.168.10.43", parsed.IpAddress);
    AssertEqual("西侧机柜", parsed.Alias ?? "");

    var monitorPacket = """
        {
          "magic":"XFGWS",
          "proto":"xfg-discovery",
          "ver":1,
          "cmd":"response",
          "device":{"name":"ESP32-C3-LCD-1.47","mac":"9C:CC:01:D9:30:1C"},
          "net":{"ip":"10.0.2.127","management_port":9617,"port":5000,"mode":"ttl_monitor","service_mask":8},
          "features":{"tcp_server":true,"rfc2217":false}
        }
        """;
    AssertTrue(
        !registry.TryApplyDiscoveryPacket(monitorPacket, new IPEndPoint(IPAddress.Parse("10.0.2.127"), 19527), out var ignored),
        "TTL Monitor discovery must not be accepted as an RFC2217 endpoint.");
    AssertTrue(ignored is null, "Ignored non-RFC discovery packets must not update endpoint state.");
    AssertTrue(
        registry.FindEndpointByMac("9C:CC:01:D9:30:1C") is null,
        "VirtualCom MAC binding must only follow advertised RFC2217 endpoints.");
}

static void WirelessSerialEndpointAliasApiContract()
{
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var request = JsonSerializer.Deserialize<WirelessSerialEndpointUpdateRequest>(
        """
        {
          "mac":"AA:BB:CC:DD:EE:FF",
          "ipAddress":"192.168.10.42",
          "servicePort":2217,
          "alias":"东侧机柜"
        }
        """,
        options) ?? throw new InvalidOperationException("Expected endpoint update request to deserialize.");
    var registry = new WirelessSerialEndpointRegistry(new InMemoryLog());
    var endpoint = registry.Upsert(request);
    var response = JsonSerializer.Serialize(endpoint, options);
    using var document = JsonDocument.Parse(response);

    AssertEqual("东侧机柜", request.Alias ?? "");
    AssertEqual("东侧机柜", document.RootElement.GetProperty("alias").GetString() ?? "");
}

static async Task WirelessSerialPeriodicQueryFollowsMacBindingAsync()
{
    var manualFixedEndpoint = new VComTunnelConfig
    {
        Mappings =
        [
            new TunnelMapping
            {
                Name = "Manual fixed endpoint",
                Backend = TunnelBackend.Com0comService,
                VisiblePort = "COM31",
                BackingPort = "CNCB31",
                Host = "192.168.7.88",
                Port = 2217
            }
        ]
    };
    AssertTrue(
        !WirelessSerialEndpointRegistry.HasMacBoundMapping(manualFixedEndpoint.Mappings),
        "A manual fixed endpoint without wirelessSerialMac must not trigger periodic UDP discovery.");

    var macMetadataOnly = new VComTunnelConfig
    {
        Mappings =
        [
            manualFixedEndpoint.Mappings.Single() with
            {
                WirelessSerialMac = "AA:BB:CC:DD:EE:FF",
                WirelessSerialAutoDiscover = false
            }
        ]
    };
    AssertTrue(
        !WirelessSerialEndpointRegistry.HasMacBoundMapping(macMetadataOnly.Mappings),
        "A MAC field without wirelessSerialAutoDiscover must remain manual and must not trigger periodic UDP discovery.");

    var macBoundEndpoint = new VComTunnelConfig
    {
        Mappings =
        [
            manualFixedEndpoint.Mappings.Single() with
            {
                WirelessSerialMac = "AA:BB:CC:DD:EE:FF",
                WirelessSerialAutoDiscover = true
            }
        ]
    };
    AssertTrue(
        WirelessSerialEndpointRegistry.HasMacBoundMapping(macBoundEndpoint.Mappings),
        "Only an explicit MAC-bound auto-discovery mapping should trigger periodic UDP discovery.");

    var log = new InMemoryLog();
    var registry = new WirelessSerialEndpointRegistry(
        log,
        queryInterval: TimeSpan.FromMilliseconds(50),
        periodicQueryEnabled: _ => Task.FromResult(
            WirelessSerialEndpointRegistry.HasMacBoundMapping(manualFixedEndpoint.Mappings)));

    var result = await registry.SendPeriodicQueryIfEnabledAsync();
    var sent = result.GetType().GetProperty("sent")?.GetValue(result);

    AssertTrue(sent is false, "A registry without MAC-bound mappings should report the periodic query as skipped.");
    AssertTrue(
        log.Snapshot().Any(entry =>
            entry.Message.Contains("Skipped UDP query", StringComparison.OrdinalIgnoreCase)),
        "A registry without MAC-bound mappings should not send the periodic UDP query.");
}

static async Task WirelessSerialEndpointChangeRestartsRunningMappingAsync()
{
    using var temp = new TempDir();
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var registry = new WirelessSerialEndpointRegistry(log, deviceTtl: TimeSpan.FromMinutes(1));
    registry.Upsert(new WirelessSerialEndpointUpdateRequest(
        Mac: "AA:BB:CC:DD:EE:FF",
        IpAddress: "192.168.10.42",
        ServicePort: 2217,
        Source: "test"));

    var mapping = new TunnelMapping
    {
        Id = "wireless-mapping",
        Name = "Wireless mapping",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM44",
        BackingPort = "CNCB44",
        Host = "192.168.10.10",
        Port = 5000,
        WirelessSerialAutoDiscover = true,
        WirelessSerialMac = "aa-bb-cc-dd-ee-ff"
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var endpoints = new List<string>();
    var starts = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM44", "CNCB44"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            Interlocked.Increment(ref starts);
            lock (endpoints)
            {
                endpoints.Add($"{sessionMapping.Host}:{sessionMapping.Port}");
            }

            return new FakeManagedTunnelSession(faulted, failAfterStart: null);
        },
        wirelessSerialEndpoints: registry);

    var started = await orchestrator.StartAsync(mapping.Id);
    AssertEqual(TunnelRunState.Running.ToString(), started.State.ToString());
    lock (endpoints)
    {
        AssertEqual("192.168.10.42:2217", endpoints.Single());
    }

    registry.Upsert(new WirelessSerialEndpointUpdateRequest(
        Mac: "AA-BB-CC-DD-EE-FF",
        IpAddress: "192.168.10.43",
        ServicePort: 33221,
        Alias: "东侧机柜",
        Source: "test"));

    await WaitUntilAsync(
        () => Volatile.Read(ref starts) == 2
            && orchestrator.GetStatus().Tunnels.Single(tunnel => tunnel.Id == mapping.Id).State == TunnelRunState.Running,
        "Wireless endpoint update should restart the running mapping with the new network endpoint.");

    lock (endpoints)
    {
        AssertEqual("192.168.10.43:33221", endpoints[^1]);
    }

    var errors = await orchestrator.SaveConfigAsync(new VComTunnelConfig { Mappings = [mapping] });
    AssertEqual("0", errors.Count.ToString());
    registry.Upsert(new WirelessSerialEndpointUpdateRequest(
        Mac: "AA-BB-CC-DD-EE-FF",
        IpAddress: "192.168.10.43",
        ServicePort: 33221,
        Alias: "西侧机柜",
        Source: "test"));
    await Task.Delay(150);
    AssertEqual("2", Volatile.Read(ref starts).ToString());
}

static async Task WirelessSerialMacBindingCorrectsWrongSavedEndpointOnStartAsync()
{
    using var temp = new TempDir();
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var registry = new WirelessSerialEndpointRegistry(log, deviceTtl: TimeSpan.FromMinutes(1));
    registry.Upsert(new WirelessSerialEndpointUpdateRequest(
        Mac: "9C:CC:01:D9:30:1C",
        IpAddress: "10.0.2.127",
        ServicePort: 5000,
        Source: "test"));

    var mapping = new TunnelMapping
    {
        Id = "wireless-wrong-saved-endpoint",
        Name = "Wireless wrong saved endpoint",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM30",
        BackingPort = "CNCB30",
        Host = "10.0.2.196",
        Port = 5000,
        WirelessSerialAutoDiscover = true,
        WirelessSerialMac = "9c-cc-01-d9-30-1c"
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var endpoints = new List<string>();
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM30", "CNCB30"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            lock (endpoints)
            {
                endpoints.Add($"{sessionMapping.Host}:{sessionMapping.Port}");
            }

            return new FakeManagedTunnelSession(faulted, failAfterStart: null);
        },
        wirelessSerialEndpoints: registry);

    var started = await orchestrator.StartAsync(mapping.Id);
    AssertEqual(TunnelRunState.Running.ToString(), started.State.ToString());
    lock (endpoints)
    {
        AssertEqual("10.0.2.127:5000", endpoints.Single());
    }

    var savedMapping = (await store.LoadAsync()).Mappings.Single();
    AssertEqual("10.0.2.196", savedMapping.Host);
    AssertTrue(
        log.Snapshot().Any(entry =>
            entry.Message.Contains("Resolved wireless serial MAC 9CCC01D9301C to 10.0.2.127:5000", StringComparison.OrdinalIgnoreCase)),
        "The service log should explain that the wrong saved endpoint was corrected by MAC discovery.");
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
            new TunnelMapping
            {
                Name = "RoundTrip",
                Backend = TunnelBackend.Com0comService,
                VisiblePort = "COM33",
                BackingPort = "CNCB33",
                Host = "127.0.0.1",
                Port = 2217,
                Hub4comForwardControlLines = true,
                AutoStart = true,
                WirelessSerialAutoDiscover = true,
                WirelessSerialMac = "AA:BB:CC:DD:EE:FF",
                WirelessSerialDeviceId = "unit-01"
            }
        ]
    };

    await store.SaveAsync(config);
    var loaded = await store.LoadAsync();
    AssertEqual("RoundTrip", loaded.Mappings.Single().Name);
    AssertEqual("COM33", loaded.Mappings.Single().VisiblePort);
    AssertTrue(loaded.Mappings.Single().Hub4comForwardControlLines, "hub4com control-line mode should round-trip.");
    AssertTrue(loaded.Mappings.Single().AutoStart, "AutoStart should round-trip.");
    AssertTrue(loaded.Mappings.Single().WirelessSerialAutoDiscover, "Wireless serial auto-discovery should round-trip.");
    AssertEqual("AA:BB:CC:DD:EE:FF", loaded.Mappings.Single().WirelessSerialMac ?? "");
    AssertEqual("unit-01", loaded.Mappings.Single().WirelessSerialDeviceId ?? "");
}

static void ServiceEndpointDefaultsToLoopback()
{
    var oldUrl = Environment.GetEnvironmentVariable(ServiceEndpoint.EnvironmentVariable);
    Environment.SetEnvironmentVariable(ServiceEndpoint.EnvironmentVariable, null);
    try
    {
        AssertEqual(ServiceEndpoint.DefaultUrl, ServiceEndpoint.GetBaseUrl());
    }
    finally
    {
        Environment.SetEnvironmentVariable(ServiceEndpoint.EnvironmentVariable, oldUrl);
    }
}

static void ServiceEndpointAcceptsLoopbackOverride()
{
    var oldUrl = Environment.GetEnvironmentVariable(ServiceEndpoint.EnvironmentVariable);
    Environment.SetEnvironmentVariable(ServiceEndpoint.EnvironmentVariable, "http://localhost:44999/");
    try
    {
        AssertEqual("http://localhost:44999", ServiceEndpoint.GetBaseUrl());
    }
    finally
    {
        Environment.SetEnvironmentVariable(ServiceEndpoint.EnvironmentVariable, oldUrl);
    }
}

static void ServiceEndpointRejectsNonLoopbackOverride()
{
    var oldUrl = Environment.GetEnvironmentVariable(ServiceEndpoint.EnvironmentVariable);
    Environment.SetEnvironmentVariable(ServiceEndpoint.EnvironmentVariable, "http://192.0.2.1:44817");
    try
    {
        try
        {
            _ = ServiceEndpoint.GetBaseUrl();
            throw new Exception("Expected non-loopback service URL to be rejected.");
        }
        catch (InvalidOperationException ex)
        {
            AssertStringContains(ex.Message, "loopback");
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable(ServiceEndpoint.EnvironmentVariable, oldUrl);
    }
}

static void TcpTunnelOptionsEnableLowLatency()
{
    using var client = new System.Net.Sockets.TcpClient();
    AssertTrue(!client.NoDelay, "TcpClient should expose the platform default before tunnel tuning.");
    TunnelTcpOptions.ConfigureLowLatency(client);
    AssertTrue(client.NoDelay, "RFC2217 tunnel sockets must disable Nagle for low-latency serial traffic.");
}

static void FileLogsRotateAndCapArchives()
{
    using var temp = new TempDir();
    var logDir = Path.Combine(temp.Path, "logs");
    const long maxFileBytes = 1024;

    using (var log = new InMemoryLog(logDir, maxFileBytes: maxFileBytes, maxArchiveFiles: 2))
    {
        for (var i = 0; i < 60; i++)
        {
            log.Info("rotate", $"entry-{i:D2} {new string('x', 120)}");
        }
    }

    var active = Path.Combine(logDir, "service.log");
    var archive1 = Path.Combine(logDir, "service.1.log");
    var archive2 = Path.Combine(logDir, "service.2.log");
    var archive3 = Path.Combine(logDir, "service.3.log");
    AssertTrue(File.Exists(active), "active service.log should exist after flush.");
    AssertTrue(File.Exists(archive1), "first rotated archive should exist.");
    AssertTrue(File.Exists(archive2), "second rotated archive should exist.");
    AssertTrue(!File.Exists(archive3), "archives beyond the configured retention must be removed.");

    foreach (var file in Directory.EnumerateFiles(logDir, "service*.log"))
    {
        var length = new FileInfo(file).Length;
        AssertTrue(length <= maxFileBytes, $"{Path.GetFileName(file)} should stay under {maxFileBytes} bytes, actual {length}.");
    }

    AssertStringContains(File.ReadAllText(active), "entry-59");
}

static void Com0comCreateHints()
{
    var mapping = new TunnelMapping { Name = "A", VisiblePort = "COM12", BackingPort = "CNCB12" };
    var builder = new Hub4comCommandBuilder(new DependencyDetector());
    var hint = builder.BuildCom0comCreateHint(mapping);
    AssertEqual("setupc.exe install PortName=COM12,EmuBR=yes PortName=CNCB12", hint);

    var serviceHint = builder.BuildCom0comCreateHint(mapping with { Backend = TunnelBackend.Com0comService });
    AssertEqual("setupc.exe install PortName=COM12,EmuBR=yes PortName=CNCB12", serviceHint);
}

static void Com0comServiceMapsPeerModemSignals()
{
    AssertTrue(!Com0comServiceTunnelSession.MapCom0comPeerDtr(0), "No DSR should map to peer DTR off.");
    AssertTrue(!Com0comServiceTunnelSession.MapCom0comPeerRts(0), "No CTS should map to peer RTS off.");
    AssertTrue(
        Com0comServiceTunnelSession.MapCom0comPeerDtr(SerialPortSnapshot.Dsr),
        "Backing DSR should map to visible-side DTR on.");
    AssertTrue(
        Com0comServiceTunnelSession.MapCom0comPeerRts(SerialPortSnapshot.Cts),
        "Backing CTS should map to visible-side RTS on.");
}

static void Com0comServiceDoesNotSynthesizeUnobservedRtsPulse()
{
    AssertBytes(
        [],
        Com0comServiceTunnelSession.BuildCom0comPeerModemControlFrames(0, 0, SerialPortSnapshot.EventCts));

    AssertBytes(
        Rfc2217Client.BuildSetModemControl(null, true),
        Com0comServiceTunnelSession.BuildCom0comPeerModemControlFrames(0, SerialPortSnapshot.Cts, SerialPortSnapshot.EventCts));
}

static void Com0comServiceControlLineSwitchBlocksForwarding()
{
    AssertBytes(
        [],
        Com0comServiceTunnelSession.BuildCom0comPeerModemControlFrames(
            0,
            SerialPortSnapshot.Cts,
            SerialPortSnapshot.EventCts,
            forwardControlLines: false));

    AssertBytes(
        [],
        Com0comServiceTunnelSession.BuildCom0comPeerModemControlFrames(
            0,
            0,
            SerialPortSnapshot.EventCts,
            forwardControlLines: false));
}

static void Com0comServiceRuntimeControlLineUpdateBlocksForwarding()
{
    using var temp = new TempDir();
    using var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var mapping = new TunnelMapping
    {
        Name = "Runtime controls",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM93",
        BackingPort = "CNCB93",
        Host = "127.0.0.1",
        Port = 5000,
        Hub4comForwardControlLines = true
    };
    using var session = new Com0comServiceTunnelSession(mapping, log, (_, _) => { });

    AssertBytes([], InvokeCom0comUpdateSerialModemState(session, 0, SerialPortSnapshot.EventNone));
    AssertBytes(Rfc2217Client.BuildSetModemControl(null, true), InvokeCom0comUpdateSerialModemState(session, SerialPortSnapshot.Cts, SerialPortSnapshot.EventCts));

    session.UpdateMapping(mapping with { Hub4comForwardControlLines = false });
    AssertBytes([], InvokeCom0comUpdateSerialModemState(session, 0, SerialPortSnapshot.EventCts));

    session.UpdateMapping(mapping with { Hub4comForwardControlLines = true });
    AssertBytes(Rfc2217Client.BuildSetModemControl(null, true), InvokeCom0comUpdateSerialModemState(session, SerialPortSnapshot.Cts, SerialPortSnapshot.EventCts));
}

static void Com0comServiceRemoteSerialSettingsDoNotOverwriteLocalSettings()
{
    using var temp = new TempDir();
    using var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var serial = new RecordingSerialPortEndpoint();
    using var session = new Com0comServiceTunnelSession(
        new TunnelMapping
        {
            Name = "Remote serial settings",
            Backend = TunnelBackend.Com0comService,
            VisiblePort = "COM97",
            BackingPort = "CNCB97",
            Host = "127.0.0.1",
            Port = 5000
        },
        log,
        (_, _) => { },
        new RecordingSerialPortEndpointFactory(serial));

    SetPrivateField(session, "_serial", serial);
    SetPrivateField(session, "_lastSerialSnapshot", new SerialPortSnapshot(0, 115200, 8, 0, 0));

    InvokeCom0comApplyNotification(session, new Rfc2217Notification(Rfc2217Client.AckSetBaudRate, Rfc2217Client.BuildUInt32Payload(9600)));
    InvokeCom0comApplyNotification(session, new Rfc2217Notification(Rfc2217Client.AckSetDataSize, [7]));
    InvokeCom0comApplyNotification(session, new Rfc2217Notification(Rfc2217Client.AckSetParity, [3]));
    InvokeCom0comApplyNotification(session, new Rfc2217Notification(Rfc2217Client.AckSetStopSize, [2]));

    var settings = serial.GetSettings();
    AssertEqual("115200", settings.BaudRate.ToString());
    AssertEqual("8", settings.ByteSize.ToString());
    AssertEqual("0", settings.Parity.ToString());
    AssertEqual("0", settings.StopBits.ToString());
    AssertBytes(
        Concat(
            Rfc2217Client.BuildSetBaudRate(115200),
            Rfc2217Client.BuildSetLineControl(stopBits: 0, parity: 0, wordLength: 8)),
        InvokeCom0comUpdateSerialSettings(session, settings));
}

static async Task Com0comServiceStartupSyncsSerialSettingsWithoutControlLineReplayAsync()
{
    using var temp = new TempDir();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var startupBytes = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    var serverTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(cts.Token);
        TunnelTcpOptions.ConfigureLowLatency(client);
        var stream = client.GetStream();
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
        await stream.WriteAsync(Concat(
            BuildRfc2217Ack(Rfc2217Client.AckSetLineStateMask, 0xFF),
            BuildRfc2217Ack(Rfc2217Client.AckSetModemStateMask, 0xFF)), cts.Token);

        var collected = new List<byte>();
        while (!cts.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            idle.CancelAfter(TimeSpan.FromMilliseconds(100));
            try
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), idle.Token);
                if (read == 0)
                {
                    break;
                }

                collected.AddRange(buffer.Take(read));
                continue;
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
                break;
            }
        }

        startupBytes.TrySetResult(collected.ToArray());
        await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
    }, cts.Token);

    var serial = new RecordingSerialPortEndpoint();
    using var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    using var session = new Com0comServiceTunnelSession(
        new TunnelMapping
        {
            Name = "Startup serial settings",
            Backend = TunnelBackend.Com0comService,
            VisiblePort = "COM98",
            BackingPort = "CNCB98",
            Host = IPAddress.Loopback.ToString(),
            Port = port
        },
        log,
        (_, _) => { },
        new RecordingSerialPortEndpointFactory(serial));

    try
    {
        await session.StartAsync(cts.Token);
        var bytes = await startupBytes.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
        AssertTrue(ContainsSequence(bytes, Rfc2217Client.BuildSetBaudRate(115200)), "Startup should send the current local baud rate instead of querying baud=0.");
        AssertTrue(ContainsSequence(bytes, Rfc2217Client.BuildSetLineControl(0, 0, 8)), "Startup should send the current local line control instead of querying data/parity/stop=0.");
        AssertTrue(!ContainsSequence(bytes, Rfc2217Client.BuildSetBaudRate(0)), "Startup must not send an RFC2217 baud=0 query to devices that treat it as a rejected setting.");
        AssertTrue(ContainsSequence(bytes, Rfc2217Client.BuildQueryControlState()), "Startup should still query remote control state without replaying local control lines.");
        AssertTrue(!ContainsSequence(bytes, Rfc2217Client.BuildSetModemControl(false, false)), "Startup must not replay DTR/RTS off as explicit control-line updates.");
        AssertTrue(!ContainsSequence(bytes, Rfc2217Client.BuildSetBreak(false)), "Startup must not replay BREAK off as an explicit control-line update.");
    }
    finally
    {
        session.Dispose();
        await cts.CancelAsync();
        listener.Stop();
        try
        {
            await serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

static async Task Com0comServiceStartupUsesPeerSerialSettingInsertionsAsync()
{
    using var temp = new TempDir();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var startupBytes = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    var serverTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(cts.Token);
        TunnelTcpOptions.ConfigureLowLatency(client);
        var stream = client.GetStream();
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
        await stream.WriteAsync(Concat(
            BuildRfc2217Ack(Rfc2217Client.AckSetLineStateMask, 0xFF),
            BuildRfc2217Ack(Rfc2217Client.AckSetModemStateMask, 0xFF)), cts.Token);

        var collected = new List<byte>();
        while (!cts.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            idle.CancelAfter(TimeSpan.FromMilliseconds(100));
            try
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), idle.Token);
                if (read == 0)
                {
                    break;
                }

                collected.AddRange(buffer.Take(read));
                continue;
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
                break;
            }
        }

        startupBytes.TrySetResult(collected.ToArray());
        await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
    }, cts.Token);

    var serial = new RecordingSerialPortEndpoint();
    serial.SetPeerSettingsInsertion(new SerialPeerSettingsInsertion(
        true,
        Concat(Com0comPeerBaudInsertion(19200), Com0comPeerLineInsertion(7, 2, 2))));
    using var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    using var session = new Com0comServiceTunnelSession(
        new TunnelMapping
        {
            Name = "Startup peer serial settings",
            Backend = TunnelBackend.Com0comService,
            VisiblePort = "COM96",
            BackingPort = "CNCB96",
            Host = IPAddress.Loopback.ToString(),
            Port = port
        },
        log,
        (_, _) => { },
        new RecordingSerialPortEndpointFactory(serial));

    try
    {
        await session.StartAsync(cts.Token);
        var bytes = await startupBytes.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
        AssertTrue(ContainsSequence(bytes, Rfc2217Client.BuildSetBaudRate(19200)), "Startup should prefer the visible peer baud reported by com0com insertion events.");
        AssertTrue(ContainsSequence(bytes, Rfc2217Client.BuildSetLineControl(stopBits: 2, parity: 2, wordLength: 7)), "Startup should prefer the visible peer line control reported by com0com insertion events.");
        AssertTrue(!ContainsSequence(bytes, Rfc2217Client.BuildSetBaudRate(115200)), "Startup must not send the backing-port DCB baud when com0com reports visible peer settings.");
        AssertStringContains(string.Join("\n", log.Snapshot().Select(entry => entry.Message)), "peer serial-setting events enabled");
    }
    finally
    {
        session.Dispose();
        await cts.CancelAsync();
        listener.Stop();
        try
        {
            await serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

static async Task Com0comServiceForwardsPeerSerialSettingInsertionsAsync()
{
    using var temp = new TempDir();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var serverReady = new TaskCompletionSource<NetworkStream>(TaskCreationOptions.RunContinuationsAsynchronously);
    var serverTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(cts.Token);
        TunnelTcpOptions.ConfigureLowLatency(client);
        var stream = client.GetStream();
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
        await stream.WriteAsync(Concat(
            BuildRfc2217Ack(Rfc2217Client.AckSetLineStateMask, 0xFF),
            BuildRfc2217Ack(Rfc2217Client.AckSetModemStateMask, 0xFF)), cts.Token);
        serverReady.TrySetResult(stream);
        await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
    }, cts.Token);

    var serial = new RecordingSerialPortEndpoint();
    serial.SetPeerSettingsInsertion(new SerialPeerSettingsInsertion(true, []));
    using var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    using var session = new Com0comServiceTunnelSession(
        new TunnelMapping
        {
            Name = "Peer serial settings",
            Backend = TunnelBackend.Com0comService,
            VisiblePort = "COM95",
            BackingPort = "CNCB95",
            Host = IPAddress.Loopback.ToString(),
            Port = port
        },
        log,
        (_, _) => { },
        new RecordingSerialPortEndpointFactory(serial));

    try
    {
        await session.StartAsync(cts.Token);
        var stream = await serverReady.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
        serial.EnqueueRead(Com0comPeerBaudInsertion(230400));
        await WaitForStreamBytesAsync(stream, Rfc2217Client.BuildSetBaudRate(230400), TimeSpan.FromSeconds(2));

        serial.EnqueueRead(Com0comPeerLineInsertion(7, 2, 2));
        await WaitForStreamBytesAsync(stream, Rfc2217Client.BuildSetLineControl(stopBits: 2, parity: 2, wordLength: 7), TimeSpan.FromSeconds(2));

        serial.EnqueueRead([0x41, 0xFF, 0x00, 0x42]);
        await WaitForStreamBytesAsync(stream, [0x41, 0xFF, 0xFF, 0x42], TimeSpan.FromSeconds(2));

        var messages = string.Join("\n", log.Snapshot().Select(entry => entry.Message));
        AssertStringContains(messages, "com0com peer serial setting baud=230400");
        AssertStringContains(messages, "line data=7, parity=2, stop=2");
    }
    finally
    {
        session.Dispose();
        await cts.CancelAsync();
        listener.Stop();
        try
        {
            await serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

static async Task RunningMappingOptionsHotUpdateOnSaveAsync()
{
    using var temp = new TempDir();
    var mapping = new TunnelMapping
    {
        Id = "hot",
        Name = "Hot options",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM94",
        BackingPort = "CNCB94",
        Host = "127.0.0.1",
        Port = 5000,
        Hub4comForwardControlLines = true,
        AutoStart = false,
        RestartOnFailure = true
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var starts = 0;
    HotUpdateManagedTunnelSession? session = null;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM94", "CNCB94"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            Interlocked.Increment(ref starts);
            session = new HotUpdateManagedTunnelSession(faulted);
            return session;
        },
        restartDelay: TimeSpan.FromMilliseconds(50));

    var started = await orchestrator.StartAsync("hot");
    AssertEqual(TunnelRunState.Running.ToString(), started.State.ToString());

    var updated = mapping with
    {
        Hub4comForwardControlLines = false,
        AutoStart = true,
        RestartOnFailure = false
    };
    var errors = await orchestrator.SaveConfigAsync(new VComTunnelConfig { Mappings = [updated] });
    AssertEqual("0", errors.Count.ToString());

    var runtimeUpdate = session?.Updates.SingleOrDefault() ?? throw new Exception("Running session did not receive a mapping update.");
    AssertTrue(!runtimeUpdate.Hub4comForwardControlLines, "Control-line forwarding should hot update to disabled.");
    AssertTrue(runtimeUpdate.AutoStart, "Auto-start should hot update in the running mapping snapshot.");
    AssertTrue(!runtimeUpdate.RestartOnFailure, "Restart-on-failure should hot update in the running mapping snapshot.");

    session!.Fault("late network failure");
    await Task.Delay(150);

    AssertEqual("1", Volatile.Read(ref starts).ToString());
    AssertTrue(
        !log.Snapshot().Any(e => e.Message.Contains("Scheduling com0comService restart", StringComparison.OrdinalIgnoreCase)),
        "RestartOnFailure=false should apply to faults raised after a hot config update.");

    var messages = string.Join("\n", log.Snapshot().Select(entry => entry.Message));
    AssertStringContains(messages, "Runtime control-line forwarding disabled");
    AssertStringContains(messages, "Runtime auto-start option enabled");
    AssertStringContains(messages, "Runtime restart-on-failure option disabled");
}

static async Task RunningBackendChangeStopsOldProcessOnSaveAsync()
{
    using var temp = new TempDir();
    CreateFakeDependencies(temp.Path);
    var mapping = new TunnelMapping
    {
        Id = "switch-backend",
        Name = "Switch backend",
        Backend = TunnelBackend.Com0comHub4com,
        VisiblePort = "COM59",
        BackingPort = "CNCB59",
        Host = "127.0.0.1",
        Port = 2217,
        RestartOnFailure = true
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM59", "CNCB59"],
        hub4comCommandFactory: map => BuildFakeHub4comCommand(temp.Path, map));

    var started = await orchestrator.StartAsync(mapping.Id);
    AssertEqual(TunnelRunState.Running.ToString(), started.State.ToString());
    var oldProcessId = started.ProcessId ?? throw new Exception("Started hub4com process id was not reported.");

    var updated = mapping with { Backend = TunnelBackend.Com0comService };
    var errors = await orchestrator.SaveConfigAsync(new VComTunnelConfig { Mappings = [updated] });
    AssertEqual("0", errors.Count.ToString());

    await WaitUntilAsync(
        () => ProcessHasExited(oldProcessId),
        "Saving a backend change did not stop the old hub4com process.");

    var status = orchestrator.GetStatus().Tunnels.Single(t => t.Id == mapping.Id);
    AssertEqual(TunnelRunState.Stopped.ToString(), status.State.ToString());
    AssertEqual("Com0comService", status.Backend);
    AssertTrue(
        log.Snapshot().Any(e => e.Message.Contains("Stopped running tunnel because the saved mapping changed", StringComparison.OrdinalIgnoreCase)),
        "Backend-changing save should log that the old running tunnel was stopped.");
}

static async Task DeletedMappingStopsAndRemovesRuntimeOnSaveAsync()
{
    using var temp = new TempDir();
    var mapping = new TunnelMapping
    {
        Id = "deleted-mapping",
        Name = "Deleted mapping",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM76",
        BackingPort = "CNCB76",
        Host = "127.0.0.1",
        Port = 5000,
        RestartOnFailure = true
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    FakeManagedTunnelSession? session = null;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM76", "CNCB76"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            session = new FakeManagedTunnelSession(faulted, failAfterStart: null);
            return session;
        });

    var started = await orchestrator.StartAsync(mapping.Id);
    AssertEqual(TunnelRunState.Running.ToString(), started.State.ToString());

    var errors = await orchestrator.SaveConfigAsync(new VComTunnelConfig { Mappings = [] });
    AssertEqual("0", errors.Count.ToString());

    AssertEqual(TunnelRunState.Stopped.ToString(), session?.State.ToString() ?? "");
    AssertTrue(
        !orchestrator.GetStatus().Tunnels.Any(t => t.Id == mapping.Id),
        "Deleted mapping should not remain in /api/status.");
    AssertTrue(
        log.Snapshot().Any(e => e.Message.Contains("saved mapping was deleted", StringComparison.OrdinalIgnoreCase)),
        "Deleting a saved mapping should log that the runtime entry was removed.");
}

static async Task RestartOptionHotUpdateCancelsPendingRestartAsync()
{
    using var temp = new TempDir();
    var mapping = new TunnelMapping
    {
        Id = "cancel-restart",
        Name = "Cancel restart",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM95",
        BackingPort = "CNCB95",
        Host = "127.0.0.1",
        Port = 5000,
        RestartOnFailure = true
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var starts = 0;
    HotUpdateManagedTunnelSession? session = null;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM95", "CNCB95"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            Interlocked.Increment(ref starts);
            session = new HotUpdateManagedTunnelSession(faulted);
            return session;
        },
        restartDelay: TimeSpan.FromMilliseconds(50));

    var started = await orchestrator.StartAsync("cancel-restart");
    AssertEqual(TunnelRunState.Running.ToString(), started.State.ToString());

    session!.Fault("temporary network failure");
    await WaitUntilAsync(
        () => log.Snapshot().Any(e => e.Message.Contains("Scheduling com0comService restart", StringComparison.OrdinalIgnoreCase)),
        "Initial fault did not schedule a restart.");

    var errors = await orchestrator.SaveConfigAsync(new VComTunnelConfig { Mappings = [mapping with { RestartOnFailure = false }] });
    AssertEqual("0", errors.Count.ToString());
    await Task.Delay(200);

    AssertEqual("1", Volatile.Read(ref starts).ToString());
    AssertStringContains(
        string.Join("\n", log.Snapshot().Select(entry => entry.Message)),
        "Runtime restart-on-failure option disabled");
}

static async Task FaultedMappingRestartOptionHotUpdateSchedulesRestartAsync()
{
    using var temp = new TempDir();
    var mapping = new TunnelMapping
    {
        Id = "resume-restart",
        Name = "Resume restart",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM96",
        BackingPort = "CNCB96",
        Host = "127.0.0.1",
        Port = 5000,
        RestartOnFailure = false
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var starts = 0;
    HotUpdateManagedTunnelSession? session = null;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM96", "CNCB96"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            Interlocked.Increment(ref starts);
            session = new HotUpdateManagedTunnelSession(faulted);
            return session;
        },
        restartDelay: TimeSpan.FromMilliseconds(50));

    var started = await orchestrator.StartAsync("resume-restart");
    AssertEqual(TunnelRunState.Running.ToString(), started.State.ToString());

    session!.Fault("temporary network failure");

    // RestartOnFailure is disabled, so the fault alone must not schedule a restart.
    await Task.Delay(150);
    AssertEqual("1", Volatile.Read(ref starts).ToString());

    var errors = await orchestrator.SaveConfigAsync(new VComTunnelConfig { Mappings = [mapping with { RestartOnFailure = true }] });
    AssertEqual("0", errors.Count.ToString());

    await WaitUntilAsync(
        () => Volatile.Read(ref starts) >= 2,
        "Enabling restart-on-failure on a faulted tunnel did not schedule a restart.");

    AssertStringContains(
        string.Join("\n", log.Snapshot().Select(entry => entry.Message)),
        "Runtime restart-on-failure option enabled");
}

static async Task Com0comServiceModemPollingForwardsRtsAsync()
{
    using var temp = new TempDir();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var serverReady = new TaskCompletionSource<NetworkStream>(TaskCreationOptions.RunContinuationsAsynchronously);
    var serverTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(cts.Token);
        TunnelTcpOptions.ConfigureLowLatency(client);
        var stream = client.GetStream();
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
        await stream.WriteAsync(Concat(
            BuildRfc2217Ack(Rfc2217Client.AckSetLineStateMask, 0xFF),
            BuildRfc2217Ack(Rfc2217Client.AckSetModemStateMask, 0xFF)), cts.Token);
        serverReady.TrySetResult(stream);
        await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
    }, cts.Token);

    var serial = new RecordingSerialPortEndpoint();
    using var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    using var session = new Com0comServiceTunnelSession(
        new TunnelMapping
        {
            Name = "RTS poll",
            Backend = TunnelBackend.Com0comService,
            VisiblePort = "COM92",
            BackingPort = "CNCB92",
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            Hub4comForwardControlLines = true
        },
        log,
        (_, _) => { },
        new RecordingSerialPortEndpointFactory(serial));

    try
    {
        await session.StartAsync(cts.Token);
        var stream = await serverReady.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
        serial.SetModemStatus(SerialPortSnapshot.Cts);
        await WaitForStreamBytesAsync(stream, Rfc2217Client.BuildSetModemControl(null, true), TimeSpan.FromSeconds(2));
        serial.SetModemStatus(0);
        await WaitForStreamBytesAsync(stream, Rfc2217Client.BuildSetModemControl(null, false), TimeSpan.FromSeconds(2));
    }
    finally
    {
        session.Dispose();
        await cts.CancelAsync();
        listener.Stop();
        try
        {
            await serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
static void Com0comServiceBackingTransportIsBinaryClean()
{
    const uint binary = 0x00000001;
    const uint parityEnabled = 0x00000002;
    const uint outxCtsFlow = 0x00000004;
    const uint outxDsrFlow = 0x00000008;
    const uint dtrControlMask = 0x00000030;
    const uint dsrSensitivity = 0x00000040;
    const uint outX = 0x00000100;
    const uint inX = 0x00000200;
    const uint rtsControlMask = 0x00003000;
    const uint abortOnError = 0x00004000;
    var original = parityEnabled
        | outxCtsFlow
        | outxDsrFlow
        | dtrControlMask
        | dsrSensitivity
        | outX
        | inX
        | rtsControlMask
        | abortOnError;

    var normalized = InvokeNormalizeLocalSerialFlags(original);

    AssertTrue((normalized & binary) != 0, "Local serial handles should stay in binary mode.");
    AssertEqual("0", (normalized & (parityEnabled | outxCtsFlow | outxDsrFlow | dsrSensitivity | outX | inX | abortOnError)).ToString());
    AssertEqual((original & dtrControlMask).ToString(), (normalized & dtrControlMask).ToString());
    AssertEqual((original & rtsControlMask).ToString(), (normalized & rtsControlMask).ToString());

    var transport = InvokeNormalizeBackingTransportDataFormat(new SerialPortSettings(1200, 7, 2, 0));
    AssertEqual("1200", transport.BaudRate.ToString());
    AssertEqual("8", transport.ByteSize.ToString());
    AssertEqual("0", transport.Parity.ToString());
    AssertEqual("0", transport.StopBits.ToString());
}
static void Com0comServiceBuildsWin32DevicePaths()
{
    AssertEqual(@"\\.\COM12", InvokeBuildDevicePath("COM12"));
    AssertEqual(@"\\.\COM13", InvokeBuildDevicePath(@"\\.\COM13"));
}
static async Task Com0comServiceRxPipelineWritesSmallChunksAsync()
{
    using var temp = new TempDir();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var serverReady = new TaskCompletionSource<NetworkStream>(TaskCreationOptions.RunContinuationsAsynchronously);
    var serverTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(cts.Token);
        TunnelTcpOptions.ConfigureLowLatency(client);
        var stream = client.GetStream();
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
        await stream.WriteAsync(Concat(
            BuildRfc2217Ack(Rfc2217Client.AckSetLineStateMask, 0xFF),
            BuildRfc2217Ack(Rfc2217Client.AckSetModemStateMask, 0xFF)), cts.Token);
        serverReady.TrySetResult(stream);
        await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
    }, cts.Token);

    var serial = new RecordingSerialPortEndpoint();
    using var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    using var session = new Com0comServiceTunnelSession(
        new TunnelMapping
        {
            Name = "RX pipeline",
            Backend = TunnelBackend.Com0comService,
            VisiblePort = "COM91",
            BackingPort = "CNCB91",
            Host = IPAddress.Loopback.ToString(),
            Port = port
        },
        log,
        (_, _) => { },
        new RecordingSerialPortEndpointFactory(serial));

    try
    {
        await session.StartAsync(cts.Token);
        var stream = await serverReady.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
        var payload = Enumerable.Repeat((byte)0x55, 1025).ToArray();
        await stream.WriteAsync(payload, cts.Token);
        await serial.WaitForTotalBytesAsync(payload.Length, TimeSpan.FromSeconds(2));

        var writes = serial.WriteSizesSnapshot();
        AssertEqual("5", writes.Count.ToString());
        AssertTrue(writes.All(size => size <= 256), "RFC2217 RX data should be pushed to the local COM in low-latency chunks.");
        AssertEqual("1", writes[^1].ToString());
    }
    finally
    {
        session.Dispose();
        await cts.CancelAsync();
        listener.Stop();
        try
        {
            await serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
static void SerialRxBackpressureInfoReportsRemainingBytes()
{
    var info = new SerialPortBackpressureInfo(BytesWritten: 17, TotalBytes: 64, Duration: TimeSpan.FromMilliseconds(750));
    AssertEqual("47", info.RemainingBytes.ToString());
    AssertEqual("64", info.TotalBytes.ToString());
}

static void EspToolBaudMonitorWaitsForResponse()
{
    var monitor = new EspToolBaudRateMonitor();
    var request = SlipFrame(
        0x00, 0x0F,
        0x08, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x08, 0x07, 0x00,
        0x00, 0xC2, 0x01, 0x00);
    var response = SlipFrame(
        0x01, 0x0F,
        0x02, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00);

    AssertEqual("460800", monitor.ObserveOutbound(request, 0, request.Length)?.ToString() ?? "");
    AssertTrue(monitor.ObserveInbound([0x55, 0xAA], 0, 2) is null, "Non-SLIP data must not confirm a baud change.");
    AssertEqual("460800", monitor.ObserveInbound(response, 0, response.Length)?.ToString() ?? "");
    AssertTrue(monitor.ObserveInbound(response, 0, response.Length) is null, "Response without a pending request must be ignored.");
}

static void KmdfControlPathUsesVisibleCom()
{
    AssertEqual(@"\\.\VComTunnelCtl_COM27", KmdfTunnelSession.BuildControlDevicePath("com27"));
}

static void KmdfControlLineSwitchBlocksForwarding()
{
    using var temp = new TempDir();
    using var log = new InMemoryLog(temp.Path);
    using var session = new KmdfTunnelSession(
        new TunnelMapping
        {
            Name = "KmdfControlSwitch",
            Backend = TunnelBackend.Kmdf,
            VisiblePort = "COM77",
            BackingPort = null,
            Host = "127.0.0.1",
            Port = 2217,
            Hub4comForwardControlLines = false
        },
        log,
        (_, _) => { });

    var modemPayload = new byte[8];
    BitConverter.GetBytes((uint)0x03).CopyTo(modemPayload, 0);
    modemPayload[4] = 1;
    modemPayload[5] = 1;
    AssertBytes([], InvokeKmdfBuildNetworkFrame(session, type: 4, flags: 0, modemPayload));

    var handflowPayload = new byte[8];
    BitConverter.GetBytes((uint)0x22).CopyTo(handflowPayload, 0);
    AssertBytes([], InvokeKmdfBuildNetworkFrame(session, type: 5, flags: 0, handflowPayload));

    AssertBytes([], InvokeKmdfBuildNetworkFrame(session, type: 6, flags: 0, [1, 0, 0, 0]));
    AssertBytes(Rfc2217Client.BuildSetBaudRate(115200), InvokeKmdfBuildNetworkFrame(session, type: 2, flags: 0, BitConverter.GetBytes((uint)115200)));

    var messages = string.Join("\n", log.Snapshot().Select(entry => entry.Message));
    AssertStringContains(messages, "Suppressed modem-control because control-line forwarding is disabled");
    AssertStringContains(messages, "Suppressed handflow because control-line forwarding is disabled");
    AssertStringContains(messages, "Suppressed break because control-line forwarding is disabled");
}

static void KmdfDataLogsAreThrottled()
{
    using var temp = new TempDir();
    using var log = new InMemoryLog(temp.Path);
    using var session = new KmdfTunnelSession(
        new TunnelMapping
        {
            Name = "KmdfDataThrottle",
            Backend = TunnelBackend.Kmdf,
            VisiblePort = "COM78",
            BackingPort = null,
            Host = "127.0.0.1",
            Port = 2217,
            Hub4comForwardControlLines = true
        },
        log,
        (_, _) => { });

    AssertBytes([0x01], InvokeKmdfBuildNetworkFrame(session, type: 1, flags: 0, [0x01]));
    AssertBytes([0x02], InvokeKmdfBuildNetworkFrame(session, type: 1, flags: 0, [0x02]));

    var txLogs = log.Snapshot().Count(entry => entry.Message.Contains("KMDF TX serial", StringComparison.OrdinalIgnoreCase));
    AssertEqual("1", txLogs.ToString());
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

static void KmdfDriverCertificatePathResolves()
{
    using var temp = new TempDir();
    var package = Path.Combine(temp.Path, "drivers", "VComTunnel.Serial", "x64", "Release", "VComTunnel.Serial");
    Directory.CreateDirectory(package);
    var inf = Path.Combine(package, "VComTunnel.Serial.inf");
    var sys = Path.Combine(package, "VComTunnel.Serial.sys");
    var cat = Path.Combine(package, "vcomtunnel.serial.cat");
    File.WriteAllText(inf, "");
    File.WriteAllText(sys, "");
    File.WriteAllText(cat, "");

    var packageCertificate = Path.Combine(package, "VComTunnel.Serial.cer");
    File.WriteAllText(packageCertificate, "package certificate");
    AssertEqual(packageCertificate, KmdfDeviceManager.ResolveDriverCertificatePath(inf) ?? "");

    File.Delete(packageCertificate);
    var releaseCertificate = Path.Combine(Directory.GetParent(package)!.FullName, "VComTunnel.Serial.cer");
    File.WriteAllText(releaseCertificate, "release certificate");
    AssertEqual(releaseCertificate, KmdfDeviceManager.ResolveDriverCertificatePath(inf) ?? "");
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
    AssertTrue(
        !Rfc2217Client.RequiresSerialDataEscaping([0x56, 0x43], 0, 2),
        "Serial data without IAC should use the zero-copy write path.");
    AssertTrue(
        Rfc2217Client.RequiresSerialDataEscaping([0x56, 0xFF, 0x43], 0, 3),
        "Serial data containing IAC must still be escaped.");

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

    AssertRfc2217Notifications(
        Rfc2217Client.BuildStartupStatusQuery(),
        new Rfc2217Notification(1, [0x00, 0x00, 0x00, 0x00]),
        new Rfc2217Notification(2, [0]),
        new Rfc2217Notification(3, [0]),
        new Rfc2217Notification(4, [0]),
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
    AssertEqual("outbound-flow cts", Rfc2217Client.DescribeSetControlValue(3));
    AssertEqual("break on", Rfc2217Client.DescribeSetControlValue(5));
    AssertEqual("dtr on", Rfc2217Client.DescribeSetControlValue(8));
    AssertEqual("rts off", Rfc2217Client.DescribeSetControlValue(12));
    AssertEqual("inbound-flow rts", Rfc2217Client.DescribeSetControlValue(16));
    AssertEqual("outbound-flow dcd", Rfc2217Client.DescribeSetControlValue(17));
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

static void Rfc2217LocalFlowControlDepth()
{
    var state = new Rfc2217LocalFlowControlState();

    AssertEqual(Rfc2217LocalFlowControlAction.Suspend.ToString(), state.Apply(suspend: true).ToString());
    AssertEqual("1", state.SuspendDepth.ToString());
    AssertEqual(Rfc2217LocalFlowControlAction.None.ToString(), state.Apply(suspend: true).ToString());
    AssertEqual("2", state.SuspendDepth.ToString());
    AssertEqual(Rfc2217LocalFlowControlAction.None.ToString(), state.Apply(suspend: false).ToString());
    AssertEqual("1", state.SuspendDepth.ToString());
    AssertEqual(Rfc2217LocalFlowControlAction.Resume.ToString(), state.Apply(suspend: false).ToString());
    AssertEqual("0", state.SuspendDepth.ToString());
    AssertEqual(Rfc2217LocalFlowControlAction.None.ToString(), state.Apply(suspend: false).ToString());
    AssertEqual("0", state.SuspendDepth.ToString());
}

static void Hub4comDefaultCommandAvoidsControlFilters()
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

    AssertStringContains(command.FileName, "hub4com.exe");
    AssertStringContains(command.Arguments, "\\\\.\\CNCB12");
    AssertStringContains(command.Arguments, "*192.168.1.50:3333");
    AssertStringContains(command.Arguments, "--create-filter=telnet,tcp,telnet");
    AssertTrue(!command.Arguments.Contains("com2tcp-rfc2217.bat", StringComparison.OrdinalIgnoreCase), "Default command must not use the com2tcp batch wrapper.");
    AssertTrue(!command.Arguments.Contains("pinmap", StringComparison.OrdinalIgnoreCase), "Default command must not install pinmap filters.");
    AssertTrue(!command.Arguments.Contains("linectl", StringComparison.OrdinalIgnoreCase), "Default command must not install line-control filters.");
    AssertTrue(!command.Arguments.Contains("--rts", StringComparison.OrdinalIgnoreCase), "Default command must not map RTS.");
    AssertTrue(!command.Arguments.Contains("--dtr", StringComparison.OrdinalIgnoreCase), "Default command must not map DTR.");
    AssertTrue(!command.Arguments.Contains("break=break", StringComparison.OrdinalIgnoreCase), "Default command must not map BREAK.");
}

static void Hub4comControlCommandEnablesControlFilters()
{
    using var temp = new TempDir();
    CreateFakeDependencies(temp.Path);
    var detector = new DependencyDetector([temp.Path], pathOverride: "");
    var command = new Hub4comCommandBuilder(detector).Build(new TunnelMapping
    {
        BackingPort = "CNCB12",
        Host = "192.168.1.50",
        Port = 3333,
        Hub4comForwardControlLines = true
    });

    AssertStringContains(command.Arguments, "--create-filter=pinmap,com,pinmap");
    AssertStringContains(command.Arguments, "--create-filter=linectl,com,lc");
    AssertStringContains(command.Arguments, "--create-filter=pinmap,tcp,pinmap");
    AssertStringContains(command.Arguments, "--create-filter=linectl,tcp,lc");
    AssertStringContains(command.Arguments, "--rts=cts");
    AssertStringContains(command.Arguments, "--dtr=dsr");
    AssertStringContains(command.Arguments, "--break=break");
}

static async Task MissingDependenciesFaultMappingAsync()
{
    using var temp = new TempDir();
    var store = await StoreWithMappingAsync(temp.Path, new TunnelMapping { Name = "Missing", Host = "127.0.0.1" });
    var orchestrator = CreateOrchestrator(store, new DependencyDetector([Path.Combine(temp.Path, "missing")], pathOverride: ""), new InMemoryLog(Path.Combine(temp.Path, "logs")));

    var status = await orchestrator.StartAsync((await store.LoadAsync()).Mappings.Single().Id);
    AssertEqual(TunnelRunState.Faulted.ToString(), status.State.ToString());
    AssertStringContains(status.LastError ?? "", "dependencies are missing");
    AssertEqual(TunnelFaultKind.MissingDependencies.ToString(), status.FaultKind?.ToString() ?? "");
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
        new InMemoryLog(Path.Combine(temp.Path, "logs")),
        ["COM27", "COM28"]);

    var status = await orchestrator.StartAsync((await store.LoadAsync()).Mappings.Single().Id);
    AssertEqual(TunnelRunState.Faulted.ToString(), status.State.ToString());
    AssertStringContains(status.LastError ?? "", "Backing port CNCB27 is not registered");
    AssertStringContains(status.LastError ?? "", "Existing ports: COM27, COM28");
    AssertEqual(TunnelFaultKind.MissingLocalCom.ToString(), status.FaultKind?.ToString() ?? "");
}

static async Task Com0comServiceBackendStartsWithoutHub4comAsync()
{
    using var temp = new TempDir();
    File.WriteAllText(Path.Combine(temp.Path, "setupc.exe"), "");
    var mapping = new TunnelMapping
    {
        Name = "Managed com0com",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM30",
        BackingPort = "CNCB30",
        Host = "127.0.0.1",
        Port = 5000
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var starts = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        new InMemoryLog(Path.Combine(temp.Path, "logs")),
        ["COM30", "CNCB30"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            Interlocked.Increment(ref starts);
            return new FakeManagedTunnelSession(faulted, failAfterStart: null);
        });

    var status = await orchestrator.StartAsync((await store.LoadAsync()).Mappings.Single().Id);
    AssertEqual(TunnelRunState.Running.ToString(), status.State.ToString());
    AssertEqual("1", Volatile.Read(ref starts).ToString());
}

static async Task Com0comServiceBackendRestartsAfterNetworkFaultAsync()
{
    using var temp = new TempDir();
    var mapping = new TunnelMapping
    {
        Name = "Restarting managed com0com",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM31",
        BackingPort = "CNCB31",
        Host = "127.0.0.1",
        Port = 5000,
        RestartOnFailure = true
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var starts = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM31", "CNCB31"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            var startNumber = Interlocked.Increment(ref starts);
            return new FakeManagedTunnelSession(
                faulted,
                failAfterStart: startNumber == 1 ? "Remote endpoint closed the TCP connection." : null);
        },
        restartDelay: TimeSpan.FromMilliseconds(50));

    var id = (await store.LoadAsync()).Mappings.Single().Id;
    var first = await orchestrator.StartAsync(id);
    AssertEqual(TunnelRunState.Running.ToString(), first.State.ToString());

    await WaitUntilAsync(() => Volatile.Read(ref starts) >= 2, "com0com service backend restart did not run.");

    var status = orchestrator.GetStatus().Tunnels.Single(t => t.Id == id);
    AssertEqual(TunnelRunState.Running.ToString(), status.State.ToString());
    AssertTrue(
        log.Snapshot().Any(e => e.Message.Contains("Scheduling Com0comService restart", StringComparison.OrdinalIgnoreCase)),
        "com0com service network fault should schedule a restart.");
}

static async Task RestartBackoffKeepsRetryingAndLimitsRepeatedLogsAsync()
{
    using var temp = new TempDir();
    var mapping = new TunnelMapping
    {
        Name = "Backoff managed com0com",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM33",
        BackingPort = "CNCB33",
        Host = "10.0.2.196",
        Port = 5000,
        RestartOnFailure = true
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var starts = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM33", "CNCB33"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            Interlocked.Increment(ref starts);
            return new FakeManagedTunnelSession(
                faulted,
                failAfterStart: null,
                failOnStart: "Could not connect to RFC2217 endpoint 10.0.2.196:5000: timeout");
        },
        restartDelay: TimeSpan.FromMilliseconds(10),
        restartMaxDelay: TimeSpan.FromMilliseconds(40));

    var id = (await store.LoadAsync()).Mappings.Single().Id;
    var first = await orchestrator.StartAsync(id);
    AssertEqual(TunnelRunState.Faulted.ToString(), first.State.ToString());
    AssertEqual(TunnelFaultKind.NetworkTimeout.ToString(), first.FaultKind?.ToString() ?? "");

    await WaitUntilAsync(
        () => Volatile.Read(ref starts) >= 4,
        "automatic restart attempts did not keep running under backoff.");
    orchestrator.Stop(id);

    var schedules = log.Snapshot()
        .Where(e => e.Message.Contains("Scheduling Com0comService restart", StringComparison.OrdinalIgnoreCase))
        .Select(e => e.Message)
        .ToArray();
    AssertEqual("2", schedules.Length.ToString());
    AssertStringContains(schedules[0], "attempt 1 in 10 ms");
    AssertStringContains(schedules[1], "attempt 2 in 20 ms");
    AssertTrue(
        schedules.All(message =>
            !message.Contains("attempt 3", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("attempt 4", StringComparison.OrdinalIgnoreCase)),
        "same-error restart scheduling logs should stop after the first two attempts.");

    var startupErrorCount = log.Snapshot().Count(e =>
        e.Level == "error" &&
        e.Source == "Backoff managed com0com" &&
        e.Message.Contains("Could not connect to RFC2217 endpoint", StringComparison.OrdinalIgnoreCase));
    AssertEqual("1", startupErrorCount.ToString());
}

static async Task Com0comServiceStartRequestsAreSerializedAsync()
{
    using var temp = new TempDir();
    var mapping = new TunnelMapping
    {
        Name = "Serialized managed com0com",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM32",
        BackingPort = "CNCB32",
        Host = "127.0.0.1",
        Port = 5000,
        RestartOnFailure = true
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var firstStartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirstStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var starts = 0;
    var activeStarts = 0;
    var maxActiveStarts = 0;
    var disposed = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        new InMemoryLog(Path.Combine(temp.Path, "logs")),
        ["COM32", "CNCB32"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            var startNumber = Interlocked.Increment(ref starts);
            return new DelayedManagedTunnelSession(
                async cancellationToken =>
                {
                    var active = Interlocked.Increment(ref activeStarts);
                    UpdateMax(ref maxActiveStarts, active);
                    try
                    {
                        if (startNumber == 1)
                        {
                            firstStartEntered.SetResult();
                            await releaseFirstStart.Task.WaitAsync(cancellationToken);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeStarts);
                    }
                },
                () => Interlocked.Increment(ref disposed));
        });

    var id = (await store.LoadAsync()).Mappings.Single().Id;
    var firstStart = orchestrator.StartAsync(id);
    await firstStartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var secondStart = orchestrator.StartAsync(id);

    await Task.Delay(50);
    AssertEqual("1", Volatile.Read(ref starts).ToString());

    releaseFirstStart.SetResult();
    await Task.WhenAll(firstStart, secondStart);

    AssertEqual("2", Volatile.Read(ref starts).ToString());
    AssertEqual("1", Volatile.Read(ref maxActiveStarts).ToString());
    AssertTrue(Volatile.Read(ref disposed) >= 1, "Second start should stop the first managed session before replacing it.");
}

static async Task StartingSameEndpointStopsPriorTunnelAsync()
{
    using var temp = new TempDir();
    var managed = new TunnelMapping
    {
        Id = "managed",
        Name = "COM27 managed",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM27",
        BackingPort = "CNCB27",
        Host = "127.0.0.1",
        Port = 5000
    };
    var kmdf = new TunnelMapping
    {
        Id = "kmdf",
        Name = "COM25 driver",
        Backend = TunnelBackend.Kmdf,
        VisiblePort = "COM25",
        BackingPort = null,
        Host = "127.0.0.1",
        Port = 5000
    };
    var store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
    await store.SaveAsync(new VComTunnelConfig { Mappings = [managed, kmdf] });
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    FakeManagedTunnelSession? managedSession = null;
    FakeKmdfTunnelSession? kmdfSession = null;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM27", "CNCB27"],
        kmdfSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            kmdfSession = new FakeKmdfTunnelSession(faulted, failAfterStart: null);
            return kmdfSession;
        },
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            managedSession = new FakeManagedTunnelSession(faulted, failAfterStart: null);
            return managedSession;
        });

    var first = await orchestrator.StartAsync("managed");
    AssertEqual(TunnelRunState.Running.ToString(), first.State.ToString());

    var second = await orchestrator.StartAsync("kmdf");
    AssertEqual(TunnelRunState.Running.ToString(), second.State.ToString());
    AssertEqual(TunnelRunState.Stopped.ToString(), managedSession?.State.ToString() ?? "");
    AssertEqual(TunnelRunState.Running.ToString(), kmdfSession?.State.ToString() ?? "");

    var statuses = orchestrator.GetStatus().Tunnels.ToDictionary(t => t.Id);
    AssertEqual(TunnelRunState.Stopped.ToString(), statuses["managed"].State.ToString());
    AssertEqual(TunnelRunState.Running.ToString(), statuses["kmdf"].State.ToString());
    AssertTrue(
        log.Snapshot().Any(e => e.Message.Contains("same RFC2217 endpoint 127.0.0.1:5000", StringComparison.OrdinalIgnoreCase)),
        "Starting a mapping should explain why a prior same-endpoint tunnel was stopped.");
}
static async Task AutoStartRestoresLastRunningSameEndpointMappingAsync()
{
    using var temp = new TempDir();
    var managed = new TunnelMapping
    {
        Id = "tun2",
        Name = "Tunnel 2",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM12",
        BackingPort = "CNCB12",
        Host = "10.0.2.196",
        Port = 5000,
        AutoStart = true,
        RestartOnFailure = true
    };
    var kmdf = new TunnelMapping
    {
        Id = "tun1",
        Name = "Tunnel 1",
        Backend = TunnelBackend.Kmdf,
        VisiblePort = "COM13",
        BackingPort = null,
        Host = "10.0.2.196",
        Port = 5000,
        AutoStart = true,
        RestartOnFailure = true
    };
    var store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
    await store.SaveAsync(new VComTunnelConfig { Mappings = [managed, kmdf] });

    var firstLog = new InMemoryLog(Path.Combine(temp.Path, "first-logs"));
    var firstOrchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        firstLog,
        ["COM12", "CNCB12"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) => new FakeManagedTunnelSession(faulted, failAfterStart: null));
    var first = await firstOrchestrator.StartAsync("tun2");
    AssertEqual(TunnelRunState.Running.ToString(), first.State.ToString());
    firstOrchestrator.Stop("tun2");

    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var kmdfStarts = 0;
    var managedStarts = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM12", "CNCB12"],
        kmdfSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            Interlocked.Increment(ref kmdfStarts);
            return new FakeKmdfTunnelSession(faulted, failAfterStart: null);
        },
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            Interlocked.Increment(ref managedStarts);
            return new FakeManagedTunnelSession(faulted, failAfterStart: null);
        });

    await orchestrator.StartAutoStartMappingsAsync();

    AssertEqual("0", Volatile.Read(ref kmdfStarts).ToString());
    AssertEqual("1", Volatile.Read(ref managedStarts).ToString());
    var statuses = orchestrator.GetStatus().Tunnels.ToDictionary(t => t.Id);
    AssertEqual(TunnelRunState.Stopped.ToString(), statuses["tun1"].State.ToString());
    AssertEqual(TunnelRunState.Running.ToString(), statuses["tun2"].State.ToString());
    AssertTrue(
        log.Snapshot().Any(e => e.Source == "Tunnel 1" && e.Message.Contains("last mapping that successfully reached Running", StringComparison.OrdinalIgnoreCase)),
        "AutoStart should restore the mapping that last reached Running for the endpoint, not the last configured row.");
}

static async Task AutoStartSeparatesWirelessMappingsWithSameFallbackEndpointAsync()
{
    using var temp = new TempDir();
    var first = new TunnelMapping
    {
        Id = "wireless-1",
        Name = "Wireless 1",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM81",
        BackingPort = "CNCB81",
        Host = "127.0.0.1",
        Port = 5000,
        AutoStart = true,
        WirelessSerialAutoDiscover = true,
        WirelessSerialMac = "AA:BB:CC:DD:EE:01"
    };
    var second = first with
    {
        Id = "wireless-2",
        Name = "Wireless 2",
        VisiblePort = "COM82",
        BackingPort = "CNCB82",
        WirelessSerialMac = "AA:BB:CC:DD:EE:02"
    };
    var store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
    await store.SaveAsync(new VComTunnelConfig { Mappings = [first, second] });

    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var registry = new WirelessSerialEndpointRegistry(log, deviceTtl: TimeSpan.FromMinutes(1));
    registry.Upsert(new WirelessSerialEndpointUpdateRequest(
        Mac: first.WirelessSerialMac!,
        IpAddress: "192.168.10.81",
        ServicePort: 2217,
        Source: "test"));
    registry.Upsert(new WirelessSerialEndpointUpdateRequest(
        Mac: second.WirelessSerialMac!,
        IpAddress: "192.168.10.82",
        ServicePort: 2217,
        Source: "test"));

    var endpoints = new List<string>();
    var starts = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM81", "CNCB81", "COM82", "CNCB82"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            Interlocked.Increment(ref starts);
            lock (endpoints)
            {
                endpoints.Add($"{sessionMapping.Id}:{sessionMapping.Host}:{sessionMapping.Port}");
            }

            return new FakeManagedTunnelSession(faulted, failAfterStart: null);
        },
        wirelessSerialEndpoints: registry);

    await orchestrator.StartAutoStartMappingsAsync();

    AssertEqual("2", Volatile.Read(ref starts).ToString());
    var statuses = orchestrator.GetStatus().Tunnels.ToDictionary(t => t.Id);
    AssertEqual(TunnelRunState.Running.ToString(), statuses["wireless-1"].State.ToString());
    AssertEqual(TunnelRunState.Running.ToString(), statuses["wireless-2"].State.ToString());
    lock (endpoints)
    {
        AssertTrue(endpoints.Contains("wireless-1:192.168.10.81:2217"), "First MAC-bound tunnel should use its discovered endpoint.");
        AssertTrue(endpoints.Contains("wireless-2:192.168.10.82:2217"), "Second MAC-bound tunnel should use its discovered endpoint.");
    }
}

static async Task AutoStartTimeoutDoesNotBlockAnotherMappingAsync()
{
    using var temp = new TempDir();
    var slow = new TunnelMapping
    {
        Id = "slow",
        Name = "Slow tunnel",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM70",
        BackingPort = "CNCB70",
        Host = "127.0.0.1",
        Port = 5000,
        AutoStart = true,
        RestartOnFailure = false
    };
    var fast = new TunnelMapping
    {
        Id = "fast",
        Name = "Fast tunnel",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM71",
        BackingPort = "CNCB71",
        Host = "127.0.0.1",
        Port = 5001,
        AutoStart = true,
        RestartOnFailure = false
    };
    var store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
    await store.SaveAsync(new VComTunnelConfig { Mappings = [slow, fast] });
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var slowDisposed = 0;
    var fastStarts = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM70", "CNCB70", "COM71", "CNCB71"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            if (sessionMapping.Id == "slow")
            {
                return new DelayedManagedTunnelSession(
                    async cancellationToken =>
                    {
                        slowEntered.SetResult();
                        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                    },
                    () => Interlocked.Increment(ref slowDisposed));
            }

            Interlocked.Increment(ref fastStarts);
            return new FakeManagedTunnelSession(faulted, failAfterStart: null);
        },
        sessionStartTimeout: TimeSpan.FromMilliseconds(80));

    var autoStart = orchestrator.StartAutoStartMappingsAsync();
    await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await autoStart.WaitAsync(TimeSpan.FromSeconds(2));

    AssertEqual("1", Volatile.Read(ref fastStarts).ToString());
    AssertTrue(Volatile.Read(ref slowDisposed) > 0, "Timed-out startup session should be disposed.");
    var statuses = orchestrator.GetStatus().Tunnels.ToDictionary(t => t.Id);
    AssertEqual(TunnelRunState.Faulted.ToString(), statuses["slow"].State.ToString());
    AssertStringContains(statuses["slow"].LastError ?? "", "startup timed out");
    AssertEqual(TunnelRunState.Running.ToString(), statuses["fast"].State.ToString());
}

static async Task StaleStartingSessionCompletionIsIgnoredAsync()
{
    using var temp = new TempDir();
    var oldMapping = new TunnelMapping
    {
        Id = "old",
        Name = "Old tunnel",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM72",
        BackingPort = "CNCB72",
        Host = "127.0.0.1",
        Port = 5000,
        RestartOnFailure = false
    };
    var newMapping = new TunnelMapping
    {
        Id = "new",
        Name = "New tunnel",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM73",
        BackingPort = "CNCB73",
        Host = "127.0.0.1",
        Port = 5000,
        RestartOnFailure = false
    };
    var store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
    await store.SaveAsync(new VComTunnelConfig { Mappings = [oldMapping, newMapping] });
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var oldStartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseOldStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM72", "CNCB72", "COM73", "CNCB73"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            if (sessionMapping.Id == "old")
            {
                return new DelayedManagedTunnelSession(
                    async cancellationToken =>
                    {
                        oldStartEntered.SetResult();
                        await releaseOldStart.Task.WaitAsync(cancellationToken);
                    },
                    () => { });
            }

            return new FakeManagedTunnelSession(faulted, failAfterStart: null);
        },
        sessionStartTimeout: TimeSpan.FromSeconds(2));

    var oldStart = orchestrator.StartAsync("old");
    await oldStartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

    var replacement = await orchestrator.StartAsync("new");
    AssertEqual(TunnelRunState.Running.ToString(), replacement.State.ToString());

    releaseOldStart.SetResult();
    var staleResult = await oldStart.WaitAsync(TimeSpan.FromSeconds(2));
    AssertEqual(TunnelRunState.Stopped.ToString(), staleResult.State.ToString());

    var statuses = orchestrator.GetStatus().Tunnels.ToDictionary(t => t.Id);
    AssertEqual(TunnelRunState.Stopped.ToString(), statuses["old"].State.ToString());
    AssertEqual(TunnelRunState.Running.ToString(), statuses["new"].State.ToString());
    AssertTrue(
        log.Snapshot().Any(e => e.Source == "Old tunnel" && e.Message.Contains("Ignored stale", StringComparison.OrdinalIgnoreCase)),
        "A late startup completion from a stopped tunnel should be ignored and logged.");
}

static async Task Hub4comToCom0comServiceRetriesBusyBackingPortAsync()
{
    using var temp = new TempDir();
    CreateFakeDependencies(temp.Path);
    var hub = new TunnelMapping
    {
        Id = "hub",
        Name = "COM61 hub",
        Backend = TunnelBackend.Com0comHub4com,
        VisiblePort = "COM61",
        BackingPort = "CNCB61",
        Host = "127.0.0.1",
        Port = 5000,
        RestartOnFailure = false
    };
    var managed = new TunnelMapping
    {
        Id = "managed",
        Name = "COM61 managed",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM61",
        BackingPort = "CNCB61",
        Host = "127.0.0.1",
        Port = 5000,
        RestartOnFailure = true
    };
    var store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
    await store.SaveAsync(new VComTunnelConfig { Mappings = [hub, managed] });
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var starts = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM61", "CNCB61"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            var start = Interlocked.Increment(ref starts);
            return new FakeManagedTunnelSession(
                faulted,
                failAfterStart: null,
                failOnStart: start == 1 ? BusyBackingPortMessage("CNCB61") : null);
        },
        hub4comCommandFactory: mapping => BuildFakeHub4comCommand(temp.Path, mapping),
        restartDelay: TimeSpan.FromMilliseconds(50),
        portReleaseRetryTimeout: TimeSpan.FromMilliseconds(300),
        portReleaseRetryDelay: TimeSpan.FromMilliseconds(10));

    var first = await orchestrator.StartAsync("hub");
    AssertEqual(TunnelRunState.Running.ToString(), first.State.ToString());

    var second = await orchestrator.StartAsync("managed");
    AssertEqual(TunnelRunState.Running.ToString(), second.State.ToString());
    AssertEqual("2", Volatile.Read(ref starts).ToString());

    var statuses = orchestrator.GetStatus().Tunnels.ToDictionary(t => t.Id);
    AssertEqual(TunnelRunState.Stopped.ToString(), statuses["hub"].State.ToString());
    AssertEqual(TunnelRunState.Running.ToString(), statuses["managed"].State.ToString());
    AssertTrue(
        log.Snapshot().Any(e => e.Message.Contains("still busy after stopping the previous backend", StringComparison.OrdinalIgnoreCase)),
        "Backend switch should retry while the old process releases the backing port.");
    AssertTrue(
        !log.Snapshot().Any(e => e.Message.Contains("Scheduling com0comService restart", StringComparison.OrdinalIgnoreCase)),
        "Transient switch-time port release should not be surfaced as a restart fault.");

    orchestrator.Stop("managed");
}

static async Task Com0comServiceAccessDeniedDoesNotRestartAsync()
{
    using var temp = new TempDir();
    var mapping = new TunnelMapping
    {
        Id = "managed",
        Name = "Busy managed",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM62",
        BackingPort = "CNCB62",
        Host = "127.0.0.1",
        Port = 5000,
        RestartOnFailure = true
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var starts = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM62", "CNCB62"],
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            Interlocked.Increment(ref starts);
            return new FakeManagedTunnelSession(faulted, failAfterStart: null, failOnStart: BusyBackingPortMessage("CNCB62"));
        },
        restartDelay: TimeSpan.FromMilliseconds(50),
        portReleaseRetryTimeout: TimeSpan.FromMilliseconds(40),
        portReleaseRetryDelay: TimeSpan.FromMilliseconds(10));

    var status = await orchestrator.StartAsync("managed");
    AssertEqual(TunnelRunState.Faulted.ToString(), status.State.ToString());
    AssertStringContains(status.LastError ?? "", "ERROR 5");
    await Task.Delay(160);

    AssertTrue(Volatile.Read(ref starts) >= 2, "The startup path should retry briefly before surfacing ERROR 5.");
    var current = orchestrator.GetStatus().Tunnels.Single(t => t.Id == "managed");
    AssertEqual(TunnelRunState.Faulted.ToString(), current.State.ToString());
    AssertTrue(
        !log.Snapshot().Any(e => e.Message.Contains("Scheduling com0comService restart", StringComparison.OrdinalIgnoreCase)),
        "A persistent backing-port access-denied error should not auto-restart and spam the log.");
}

static async Task StaleStoppedSessionFaultIsIgnoredAsync()
{
    using var temp = new TempDir();
    var managed = new TunnelMapping
    {
        Id = "managed",
        Name = "COM27 managed",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM27",
        BackingPort = "CNCB27",
        Host = "127.0.0.1",
        Port = 5000
    };
    var kmdf = new TunnelMapping
    {
        Id = "kmdf",
        Name = "COM25 driver",
        Backend = TunnelBackend.Kmdf,
        VisiblePort = "COM25",
        BackingPort = null,
        Host = "127.0.0.1",
        Port = 5000
    };
    var store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
    await store.SaveAsync(new VComTunnelConfig { Mappings = [managed, kmdf] });
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        ["COM27", "CNCB27"],
        kmdfSessionFactory: (sessionMapping, sessionLog, faulted) => new FakeKmdfTunnelSession(faulted, failAfterStart: null),
        com0comServiceSessionFactory: (sessionMapping, sessionLog, faulted) => new FakeManagedTunnelSession(
            faulted,
            failAfterStart: "late old tunnel fault"));

    var first = await orchestrator.StartAsync("managed");
    AssertEqual(TunnelRunState.Running.ToString(), first.State.ToString());

    var second = await orchestrator.StartAsync("kmdf");
    AssertEqual(TunnelRunState.Running.ToString(), second.State.ToString());

    await Task.Delay(120);

    var statuses = orchestrator.GetStatus().Tunnels.ToDictionary(t => t.Id);
    AssertEqual(TunnelRunState.Stopped.ToString(), statuses["managed"].State.ToString());
    AssertEqual(TunnelRunState.Running.ToString(), statuses["kmdf"].State.ToString());
    AssertTrue(
        log.Snapshot().Any(e => e.Message.Contains("Ignored stale com0comService fault", StringComparison.OrdinalIgnoreCase)),
        "A fault from a stopped same-endpoint tunnel should be ignored.");
    AssertTrue(
        !log.Snapshot().Any(e => e.Message.Contains("Scheduling com0comService restart", StringComparison.OrdinalIgnoreCase)),
        "A stale stopped tunnel fault must not schedule a restart.");
}

static void Com0comServiceBackingOpenDiagnostics()
{
    var mapping = new TunnelMapping
    {
        Name = "Managed com0com",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM27",
        BackingPort = "CNCB27",
        Host = "127.0.0.1",
        Port = 5000
    };
    var notFound = Com0comServiceTunnelSession.BuildBackingPortOpenError(
        mapping,
        new SerialPortOpenException("CNCB27", @"\\.\CNCB27", 2, "open"));
    AssertStringContains(notFound, "ERROR 2");
    AssertStringContains(notFound, "setupc.exe install PortName=COM27,EmuBR=yes PortName=CNCB27");

    var accessDenied = Com0comServiceTunnelSession.BuildBackingPortOpenError(
        mapping,
        new SerialPortOpenException("CNCB27", @"\\.\CNCB27", 5, "open"));
    AssertStringContains(accessDenied, "ERROR 5");
    AssertStringContains(accessDenied, "already open");
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
    var orchestrator = CreateOrchestrator(store, new DependencyDetector([temp.Path], pathOverride: ""), new InMemoryLog(Path.Combine(temp.Path, "logs")));

    var status = await orchestrator.StartAsync((await store.LoadAsync()).Mappings.Single().Id);
    AssertEqual(TunnelRunState.Faulted.ToString(), status.State.ToString());
    var acceptedStartupFaults = new[]
    {
        "Could not open KMDF control channel",
        "KMDF driver protocol",
        "Could not connect to RFC2217 endpoint",
        "The requested resource is in use",
        "请求的资源在使用中"
    };
    AssertStringContainsAny(status.LastError ?? "", acceptedStartupFaults);

    var secondStatus = await orchestrator.StartAsync((await store.LoadAsync()).Mappings.Single().Id);
    AssertEqual(TunnelRunState.Faulted.ToString(), secondStatus.State.ToString());
    AssertStringContainsAny(secondStatus.LastError ?? "", acceptedStartupFaults);
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
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var starts = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        [],
        kmdfSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            var startNumber = Interlocked.Increment(ref starts);
            return new FakeKmdfTunnelSession(
                faulted,
                failAfterStart: startNumber == 1 ? "Remote endpoint closed the TCP connection." : null);
        },
        restartDelay: TimeSpan.FromMilliseconds(50));

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

static async Task KmdfDefaultStartSuppressesInitialControlLinesAsync()
{
    using var temp = new TempDir();
    var mapping = new TunnelMapping
    {
        Name = "Default safe driver",
        Backend = TunnelBackend.Kmdf,
        VisiblePort = "COM49",
        BackingPort = null,
        RestartOnFailure = false
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    bool? suppressInitialControlLineSync = null;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        new InMemoryLog(Path.Combine(temp.Path, "logs")),
        [],
        kmdfSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            suppressInitialControlLineSync = sessionMapping.SuppressInitialControlLineSync;
            return new FakeKmdfTunnelSession(faulted, failAfterStart: null);
        });

    await orchestrator.StartAsync(mapping.Id);

    AssertTrue(suppressInitialControlLineSync == true, "KMDF default start should suppress the initial DTR/RTS sync event.");
    var status = orchestrator.GetStatus().Tunnels.Single(t => t.Id == mapping.Id);
    AssertEqual(TunnelRunState.Running.ToString(), status.State.ToString());
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
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var starts = 0;
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        [],
        kmdfSessionFactory: (sessionMapping, sessionLog, faulted) =>
        {
            Interlocked.Increment(ref starts);
            return new FakeKmdfTunnelSession(
                faulted,
                failAfterStart: null,
                failOnStart: "KMDF driver protocol 1.1 is older than required 1.2. Rebuild and reinstall VComTunnel.Serial.");
        },
        restartDelay: TimeSpan.FromMilliseconds(50));

    var id = (await store.LoadAsync()).Mappings.Single().Id;
    var first = await orchestrator.StartAsync(id);
    AssertEqual(TunnelRunState.Faulted.ToString(), first.State.ToString());
    AssertStringContains(first.LastError ?? "", "driver protocol");
    var stopped = orchestrator.Stop(id);
    AssertEqual(TunnelRunState.Stopped.ToString(), stopped.State.ToString());
    AssertEqual("Kmdf", stopped.Backend);

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
        Id = "hub",
        Name = "Managed",
        VisiblePort = "COM29",
        BackingPort = "CNCB29",
        Host = "127.0.0.1",
        Port = 4000
    };
    var serviceMapping = mapping with
    {
        Id = "svc",
        Name = "Managed service",
        Backend = TunnelBackend.Com0comService,
        VisiblePort = "COM30",
        BackingPort = "CNCB30"
    };
    var store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
    await store.SaveAsync(new VComTunnelConfig { Mappings = [mapping, serviceMapping] });
    var detector = new DependencyDetector([temp.Path], pathOverride: "");
    var manager = new Com0comSetupManager(
        store,
        detector,
        new FakeComPortInventory(["COM28", "CNCB28"], [new Com0comPairInfo(2, "COM28", "CNCB28", @"\Device\com0com12", @"\Device\com0com22", true)]));

    var create = await manager.BuildCreatePlanAsync("hub");
    AssertStringContains(create.Arguments, "install PortName=COM29,EmuBR=yes PortName=CNCB29");
    AssertTrue(create.RequiresElevation, "setupc plans should require elevation.");

    var serviceCreate = await manager.BuildCreatePlanAsync("svc");
    AssertStringContains(serviceCreate.Arguments, "install PortName=COM30,EmuBR=yes PortName=CNCB30");

    var remove = manager.BuildRemovePlan(2);
    AssertStringContains(remove.Arguments, "remove 2");
    AssertEqual(1.ToString(), manager.GetPairs().Count.ToString());

    var existingManager = new Com0comSetupManager(
        store,
        detector,
        new FakeComPortInventory(["COM29", "CNCB29"], [new Com0comPairInfo(3, "COM29", "CNCB29", @"\Device\com0com13", @"\Device\com0com23", true)]));
    try
    {
        await existingManager.BuildCreatePlanAsync("hub");
        throw new Exception("Existing com0com pair should not produce a create plan.");
    }
    catch (InvalidOperationException ex)
    {
        AssertStringContains(ex.Message, "already exists");
    }
}

static async Task FakeHub4comProcessStartsAndStopsAsync()
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
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        [],
        hub4comCommandFactory: mapping => BuildFakeHub4comCommand(temp.Path, mapping));
    var id = (await store.LoadAsync()).Mappings.Single().Id;

    var started = await orchestrator.StartAsync(id);
    AssertEqual(TunnelRunState.Running.ToString(), started.State.ToString());
    AssertTrue(started.ProcessId is not null, "Process id should be reported.");
    await Task.Delay(500);
    AssertTrue(log.Snapshot().Any(e => e.Message.Contains("fake-hub4com", StringComparison.OrdinalIgnoreCase)), "Fake process output should be logged.");

    var stopped = orchestrator.Stop(id);
    AssertEqual(TunnelRunState.Stopped.ToString(), stopped.State.ToString());
}

static async Task FakeHub4comProcessRestartsAfterExitAsync()
{
    using var temp = new TempDir();
    CreateFakeDependencies(
        temp.Path,
        """
        @echo off
        echo fake-hub4com %*
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
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        [],
        hub4comCommandFactory: mapping => BuildFakeHub4comCommand(temp.Path, mapping),
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

static async Task FakeHub4comAccessDeniedDoesNotRestartAsync()
{
    using var temp = new TempDir();
    CreateFakeDependencies(
        temp.Path,
        """
        @echo off
        echo ComIo::OpenPath(): CreateFile("\\.\CNCB58") ERROR 5 - Access is denied. 1>&2
        exit /b 0
        """);
    var mapping = new TunnelMapping
    {
        Name = "Busy fake bridge",
        VisiblePort = "COM58",
        BackingPort = "CNCB58",
        Host = "127.0.0.1",
        Port = 2217,
        RestartOnFailure = true
    };
    var store = await StoreWithMappingAsync(temp.Path, mapping);
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        [],
        hub4comCommandFactory: mapping => BuildFakeHub4comCommand(temp.Path, mapping),
        restartDelay: TimeSpan.FromMilliseconds(50));
    var id = (await store.LoadAsync()).Mappings.Single().Id;

    var started = await orchestrator.StartAsync(id);
    AssertEqual(TunnelRunState.Faulted.ToString(), started.State.ToString());
    AssertStringContains(started.LastError ?? "", "ERROR 5");
    AssertStringContains(started.LastError ?? "", "already open");
    AssertEqual(TunnelFaultKind.LocalComBusy.ToString(), started.FaultKind?.ToString() ?? "");
    await Task.Delay(300);

    var status = orchestrator.GetStatus().Tunnels.Single(t => t.Id == id);
    AssertEqual(TunnelRunState.Faulted.ToString(), status.State.ToString());
    AssertStringContains(status.LastError ?? "", "ERROR 5");
    AssertStringContains(status.LastError ?? "", "already open");
    AssertEqual(
        "1",
        log.Snapshot().Count(e => e.Message.Contains("Started hub4com process", StringComparison.OrdinalIgnoreCase)).ToString());
}

static async Task ManualStopSuppressesFakeHub4comRestartAsync()
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
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        [],
        hub4comCommandFactory: mapping => BuildFakeHub4comCommand(temp.Path, mapping),
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

static void StoppingUnknownMappingDoesNotCreateRuntimeStatus()
{
    using var temp = new TempDir();
    var log = new InMemoryLog(Path.Combine(temp.Path, "logs"));
    var store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
    var orchestrator = CreateOrchestratorWithPorts(
        store,
        new DependencyDetector([temp.Path], pathOverride: ""),
        log,
        []);

    var stopped = orchestrator.Stop("missing mapping id");

    AssertEqual(TunnelRunState.Stopped.ToString(), stopped.State.ToString());
    AssertEqual("0", orchestrator.GetStatus().Tunnels.Count.ToString());
    AssertTrue(
        log.Snapshot().Any(entry =>
            entry.Message.Contains("Ignored stop request for unknown mapping", StringComparison.OrdinalIgnoreCase)),
        "Unknown stop requests should be visible in diagnostics without creating ghost tunnel rows.");
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
            new Dictionary<string, string>
            {
                ["setupc.exe"] = "",
                ["Setup_com0com_v3.0.0.0_W7_x64_signed.exe"] = "",
                ["Setup_com0com_v3.0.0.0_W7_x86_signed.exe"] = ""
            });

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

static async Task DependencyInstallerFallsBackAfterInvalidMirrorAsync()
{
    using var temp = new TempDir();
    var oldHome = Environment.GetEnvironmentVariable("VCOMTUNNEL_HOME");
    Environment.SetEnvironmentVariable("VCOMTUNNEL_HOME", temp.Path);
    try
    {
        var handler = new InvalidThenZipHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.invalid/")
        };
        var detector = new DependencyDetector([AppPaths.ToolsDirectory], pathOverride: "");
        var installer = new DependencyInstaller(detector, http);
        var result = await installer.InstallAsync(new DependencyInstallRequest(DownloadCom0com: false));

        var step = result.Steps.Single();
        AssertTrue(step.Success, step.Message);
        AssertTrue(handler.RequestCount >= 2, "Installer should try the next dependency URL after an invalid archive.");
        AssertStringContains(step.Message, "Downloaded from");
        AssertTrue(File.Exists(Path.Combine(AppPaths.ToolsDirectory, "hub4com", "com2tcp-rfc2217.bat")), "hub4com batch should be extracted after fallback.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("VCOMTUNNEL_HOME", oldHome);
    }
}

static async Task DependencyInstallerRejectsHtmlDownloadsAsync()
{
    using var temp = new TempDir();
    var oldHome = Environment.GetEnvironmentVariable("VCOMTUNNEL_HOME");
    Environment.SetEnvironmentVariable("VCOMTUNNEL_HOME", temp.Path);
    try
    {
        var http = new HttpClient(new HtmlDownloadHandler())
        {
            BaseAddress = new Uri("https://example.invalid/")
        };
        var detector = new DependencyDetector([AppPaths.ToolsDirectory], pathOverride: "");
        var installer = new DependencyInstaller(detector, http);
        var result = await installer.InstallAsync(new DependencyInstallRequest(DownloadCom0com: false));

        var step = result.Steps.Single();
        AssertTrue(!step.Success, "HTML download should not be accepted as a dependency archive.");
        AssertStringContains(step.Message, "not a valid zip");
        AssertTrue(!Directory.Exists(Path.Combine(AppPaths.ToolsDirectory, "hub4com"))
            || !Directory.EnumerateFiles(Path.Combine(AppPaths.ToolsDirectory, "hub4com"), "com2tcp-rfc2217.bat", SearchOption.AllDirectories).Any(),
            "Invalid downloads should not produce usable hub4com tools.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("VCOMTUNNEL_HOME", oldHome);
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
    Func<TunnelMapping, InMemoryLog, Action<IManagedTunnelSession, string>, IManagedTunnelSession>? com0comServiceSessionFactory = null,
    Func<TunnelMapping, Hub4comCommand>? hub4comCommandFactory = null,
    TimeSpan? restartDelay = null,
    TimeSpan? portReleaseRetryTimeout = null,
    TimeSpan? portReleaseRetryDelay = null,
    TimeSpan? restartMaxDelay = null,
    TimeSpan? sessionStartTimeout = null,
    WirelessSerialEndpointRegistry? wirelessSerialEndpoints = null)
{
    return new TunnelOrchestrator(
        store,
        detector,
        new Hub4comCommandBuilder(detector),
        new FakeComPortInventory(registeredPorts),
        log,
        kmdfSessionFactory,
        com0comServiceSessionFactory,
        hub4comCommandFactory,
        restartDelay,
        portReleaseRetryTimeout,
        portReleaseRetryDelay,
        restartMaxDelay,
        sessionStartTimeout,
        wirelessSerialEndpoints);
}

static Hub4comCommand BuildFakeHub4comCommand(string root, TunnelMapping mapping)
{
    var script = Path.Combine(root, "fake-hub4com.cmd");
    var args = $"/d /c \"\"{script}\" \"\\\\.\\{mapping.BackingPort}\" {mapping.Host} {mapping.Port}\"";
    return new Hub4comCommand("cmd.exe", args);
}

static void CreateFakeDependencies(string root, string? fakeHub4comBody = null)
{
    File.WriteAllText(Path.Combine(root, "setupc.exe"), "");
    File.WriteAllText(Path.Combine(root, "hub4com.exe"), "");
    File.WriteAllText(Path.Combine(root, "com2tcp-rfc2217.bat"), "@echo off");
    File.WriteAllText(
        Path.Combine(root, "fake-hub4com.cmd"),
        fakeHub4comBody ?? """
        @echo off
        echo fake-hub4com %*
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

static string BusyBackingPortMessage(string portName) =>
    $@"Could not open serial port \\.\{portName}: ERROR 5 - Access is denied. ERROR 5 usually means the backing port is already open by another mapping, hub4com/com2tcp, or a serial tool; stop that process and retry.";

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

static uint InvokeNormalizeLocalSerialFlags(uint flags)
{
    var endpointType = typeof(Com0comServiceTunnelSession).Assembly.GetType("VComTunnel.Core.Win32SerialPortEndpoint")
        ?? throw new Exception("Win32SerialPortEndpoint reflection target missing.");
    var method = endpointType.GetMethod(
        "NormalizeLocalSerialFlags",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
        ?? throw new Exception("NormalizeLocalSerialFlags reflection target missing.");
    return (uint)(method.Invoke(null, [flags]) ?? throw new Exception("NormalizeLocalSerialFlags returned null."));
}
static SerialPortSettings InvokeNormalizeBackingTransportDataFormat(SerialPortSettings settings)
{
    var endpointType = typeof(Com0comServiceTunnelSession).Assembly.GetType("VComTunnel.Core.Win32SerialPortEndpoint")
        ?? throw new Exception("Win32SerialPortEndpoint reflection target missing.");
    var method = endpointType.GetMethod(
        "NormalizeBackingTransportDataFormat",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
        ?? throw new Exception("NormalizeBackingTransportDataFormat reflection target missing.");
    return (SerialPortSettings)(method.Invoke(null, [settings])
        ?? throw new Exception("NormalizeBackingTransportDataFormat returned null."));
}
static string InvokeBuildDevicePath(string portName)
{
    var endpointType = typeof(Com0comServiceTunnelSession).Assembly.GetType("VComTunnel.Core.Win32SerialPortEndpoint")
        ?? throw new Exception("Win32SerialPortEndpoint reflection target missing.");
    var method = endpointType.GetMethod(
        "BuildDevicePath",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
        ?? throw new Exception("BuildDevicePath reflection target missing.");
    return (string)(method.Invoke(null, [portName]) ?? throw new Exception("BuildDevicePath returned null."));
}
static byte[] InvokeCom0comUpdateSerialModemState(Com0comServiceTunnelSession session, uint currentModemStatus, uint eventMask)
{
    var method = typeof(Com0comServiceTunnelSession).GetMethod(
        "UpdateSerialModemState",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new Exception("UpdateSerialModemState reflection target missing.");
    var frame = method.Invoke(session, [currentModemStatus, eventMask])
        ?? throw new Exception("UpdateSerialModemState returned null.");
    return (byte[])(frame.GetType().GetProperty("Bytes")?.GetValue(frame)
        ?? throw new Exception("UpdateSerialModemState returned a frame without Bytes."));
}

static byte[] InvokeCom0comUpdateSerialSettings(Com0comServiceTunnelSession session, SerialPortSettings settings)
{
    var method = typeof(Com0comServiceTunnelSession).GetMethod(
        "UpdateSerialSettings",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
        binder: null,
        types: [typeof(SerialPortSettings)],
        modifiers: null)
        ?? throw new Exception("UpdateSerialSettings reflection target missing.");
    var frame = method.Invoke(session, [settings])
        ?? throw new Exception("UpdateSerialSettings returned null.");
    return (byte[])(frame.GetType().GetProperty("Bytes")?.GetValue(frame)
        ?? throw new Exception("UpdateSerialSettings returned a frame without Bytes."));
}

static void InvokeCom0comApplyNotification(Com0comServiceTunnelSession session, Rfc2217Notification notification)
{
    var method = typeof(Com0comServiceTunnelSession).GetMethod(
        "ApplyNotification",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new Exception("ApplyNotification reflection target missing.");
    method.Invoke(session, [notification]);
}

static void SetPrivateField(object instance, string name, object value)
{
    var field = instance.GetType().GetField(
        name,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new Exception($"{name} reflection target missing.");
    field.SetValue(instance, value);
}

static async Task WaitForStreamBytesAsync(NetworkStream stream, byte[] expected, TimeSpan timeout)
{
    using var timeoutCts = new CancellationTokenSource(timeout);
    var buffer = new byte[512];
    var received = new List<byte>();
    while (true)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token);
        if (read == 0)
        {
            throw new Exception("Network stream closed before expected bytes were observed.");
        }

        received.AddRange(buffer.Take(read));
        if (ContainsSequence(received, expected))
        {
            return;
        }
    }
}

static bool ContainsSequence(IReadOnlyList<byte> bytes, IReadOnlyList<byte> expected)
{
    if (expected.Count == 0)
    {
        return true;
    }

    for (var i = 0; i <= bytes.Count - expected.Count; i++)
    {
        var matched = true;
        for (var j = 0; j < expected.Count; j++)
        {
            if (bytes[i + j] != expected[j])
            {
                matched = false;
                break;
            }
        }

        if (matched)
        {
            return true;
        }
    }

    return false;
}
static byte[] Com0comPeerBaudInsertion(uint baudRate)
{
    return
    [
        0xFF,
        16,
        (byte)baudRate,
        (byte)(baudRate >> 8),
        (byte)(baudRate >> 16),
        (byte)(baudRate >> 24)
    ];
}

static byte[] Com0comPeerLineInsertion(byte byteSize, byte parity, byte stopBits)
{
    return [0xFF, 17, byteSize, parity, stopBits];
}

static byte[] BuildRfc2217Ack(byte command, byte payload)
{
    return payload == 0xFF
        ? [255, 250, Rfc2217Client.TelnetOptionComPortControl, command, 255, 255, 255, 240]
        : [255, 250, Rfc2217Client.TelnetOptionComPortControl, command, payload, 255, 240];
}

static byte[] SlipFrame(params byte[] payload)
{
    var bytes = new List<byte> { 0xC0 };
    foreach (var value in payload)
    {
        if (value == 0xC0)
        {
            bytes.Add(0xDB);
            bytes.Add(0xDC);
        }
        else if (value == 0xDB)
        {
            bytes.Add(0xDB);
            bytes.Add(0xDD);
        }
        else
        {
            bytes.Add(value);
        }
    }

    bytes.Add(0xC0);
    return bytes.ToArray();
}

static byte[] Concat(params byte[][] chunks) => chunks.SelectMany(chunk => chunk).ToArray();

static bool ProcessHasExited(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        return process.HasExited;
    }
    catch (ArgumentException)
    {
        return true;
    }
    catch (InvalidOperationException)
    {
        return true;
    }
}

static byte[] InvokeKmdfBuildNetworkFrame(KmdfTunnelSession session, ushort type, ushort flags, byte[] payload)
{
    var method = typeof(KmdfTunnelSession).GetMethod(
        "BuildNetworkFrame",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new Exception("BuildNetworkFrame reflection target missing.");
    var frame = method.Invoke(session, new object[] { type, flags, payload, 0, payload.Length })
        ?? throw new Exception("BuildNetworkFrame returned null.");
    return (byte[])(frame.GetType().GetProperty("Bytes")?.GetValue(frame)
        ?? throw new Exception("BuildNetworkFrame returned a frame without Bytes."));
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

static void UpdateMax(ref int target, int value)
{
    while (true)
    {
        var current = Volatile.Read(ref target);
        if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
        {
            return;
        }
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

internal sealed class RecordingSerialPortEndpointFactory(RecordingSerialPortEndpoint endpoint) : ISerialPortEndpointFactory
{
    public ISerialPortEndpoint Open(string portName) => endpoint;
}

internal sealed class RecordingSerialPortEndpoint : ISerialPortEndpoint
{
    private readonly object _lock = new();
    private readonly List<int> _writeSizes = [];
    private TaskCompletionSource _bytesWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _readReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<byte[]> _readChunks = [];
    private int _totalBytes;
    private uint _modemStatus;
    private SerialPortSettings _settings = new(115200, 8, 0, 0);
    private SerialPeerSettingsInsertion _peerSettingsInsertion = SerialPeerSettingsInsertion.Unavailable();

    public bool SupportsModemStatusEvents => false;

    public SerialPeerSettingsInsertion EnablePeerSettingsInsertion(byte escapeChar)
    {
        lock (_lock)
        {
            return _peerSettingsInsertion;
        }
    }

    public ValueTask<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        return new ValueTask<int>(WaitForSerialReadAsync(buffer, cancellationToken));
    }

    public ValueTask WriteAsync(byte[] bytes, CancellationToken cancellationToken, Action<SerialPortBackpressureInfo>? backpressure = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _writeSizes.Add(bytes.Length);
            _totalBytes += bytes.Length;
            _bytesWritten.TrySetResult();
        }

        return ValueTask.CompletedTask;
    }

    public SerialPortSnapshot GetSnapshot()
    {
        var settings = GetSettings();
        return new SerialPortSnapshot(Volatile.Read(ref _modemStatus), settings.BaudRate, settings.ByteSize, settings.Parity, settings.StopBits);
    }

    public SerialPortSettings GetSettings()
    {
        lock (_lock)
        {
            return _settings;
        }
    }

    public void SetSettings(SerialPortSettings settings)
    {
        lock (_lock)
        {
            _settings = settings;
        }
    }

    public void SetPeerSettingsInsertion(SerialPeerSettingsInsertion insertion)
    {
        lock (_lock)
        {
            _peerSettingsInsertion = insertion;
        }
    }

    public void EnqueueRead(byte[] bytes)
    {
        lock (_lock)
        {
            _readChunks.Enqueue(bytes);
            _readReady.TrySetResult();
        }
    }

    public uint GetModemStatus() => Volatile.Read(ref _modemStatus);

    public void SetModemStatus(uint modemStatus) => Volatile.Write(ref _modemStatus, modemStatus);

    public uint WaitForModemStatusChange(CancellationToken cancellationToken)
    {
        cancellationToken.WaitHandle.WaitOne();
        cancellationToken.ThrowIfCancellationRequested();
        return SerialPortSnapshot.EventNone;
    }

    public async Task WaitForTotalBytesAsync(int expectedBytes, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (true)
        {
            Task waitTask;
            lock (_lock)
            {
                if (_totalBytes >= expectedBytes)
                {
                    return;
                }

                if (_bytesWritten.Task.IsCompleted)
                {
                    _bytesWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                waitTask = _bytesWritten.Task;
            }

            await waitTask.WaitAsync(timeoutCts.Token);
        }
    }

    public IReadOnlyList<int> WriteSizesSnapshot()
    {
        lock (_lock)
        {
            return _writeSizes.ToArray();
        }
    }

    public void Dispose()
    {
    }

    private async Task<int> WaitForSerialReadAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waitTask;
            lock (_lock)
            {
                if (_readChunks.Count > 0)
                {
                    var chunk = _readChunks.Dequeue();
                    if (chunk.Length > buffer.Length)
                    {
                        throw new InvalidOperationException("Test serial read chunk is larger than the read buffer.");
                    }

                    Buffer.BlockCopy(chunk, 0, buffer, 0, chunk.Length);
                    return chunk.Length;
                }

                if (_readReady.Task.IsCompleted)
                {
                    _readReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                waitTask = _readReady.Task;
            }

            await waitTask.WaitAsync(cancellationToken);
        }
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
            : new Dictionary<string, string>
            {
                ["setupc.exe"] = "",
                ["Setup_com0com_v3.0.0.0_W7_x64_signed.exe"] = "",
                ["Setup_com0com_v3.0.0.0_W7_x86_signed.exe"] = ""
            };

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

internal sealed class HtmlDownloadHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!doctype html><html><body>download page</body></html>")
        };
        return Task.FromResult(response);
    }
}

internal sealed class InvalidThenZipHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (RequestCount == 1)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<!doctype html><html><body>mirror page</body></html>")
            });
        }

        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var exe = archive.CreateEntry("hub4com.exe");
            using (var writer = new StreamWriter(exe.Open()))
            {
                writer.Write("");
            }

            var batch = archive.CreateEntry("com2tcp-rfc2217.bat");
            using (var writer = new StreamWriter(batch.Open()))
            {
                writer.Write("@echo off");
            }
        }

        stream.Position = 0;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        });
    }
}

internal sealed class FakeManagedTunnelSession : IManagedTunnelSession
{
    private readonly Action<IManagedTunnelSession, string> _faulted;
    private readonly string? _failAfterStart;
    private readonly string? _failOnStart;

    public FakeManagedTunnelSession(Action<IManagedTunnelSession, string> faulted, string? failAfterStart, string? failOnStart = null)
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
            throw new IOException(_failOnStart);
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

internal sealed class HotUpdateManagedTunnelSession : IManagedTunnelSession
{
    private readonly Action<IManagedTunnelSession, string> _faulted;
    private readonly List<TunnelMapping> _updates = [];

    public HotUpdateManagedTunnelSession(Action<IManagedTunnelSession, string> faulted)
    {
        _faulted = faulted;
    }

    public TunnelRunState State { get; private set; } = TunnelRunState.Starting;
    public string? LastError { get; private set; }
    public IReadOnlyList<TunnelMapping> Updates => _updates;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        State = TunnelRunState.Running;
        return Task.CompletedTask;
    }

    public void UpdateMapping(TunnelMapping mapping)
    {
        _updates.Add(mapping);
    }

    public void Fault(string error)
    {
        LastError = error;
        State = TunnelRunState.Faulted;
        _faulted(this, error);
    }

    public void Dispose()
    {
        if (LastError is null)
        {
            State = TunnelRunState.Stopped;
        }
    }
}

internal sealed class DelayedManagedTunnelSession : IManagedTunnelSession
{
    private readonly Func<CancellationToken, Task> _startAsync;
    private readonly Action _disposed;

    public DelayedManagedTunnelSession(Func<CancellationToken, Task> startAsync, Action disposed)
    {
        _startAsync = startAsync;
        _disposed = disposed;
    }

    public TunnelRunState State { get; private set; } = TunnelRunState.Starting;
    public string? LastError { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startAsync(cancellationToken);
        State = TunnelRunState.Running;
    }

    public void Dispose()
    {
        _disposed();
        if (LastError is null)
        {
            State = TunnelRunState.Stopped;
        }
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
