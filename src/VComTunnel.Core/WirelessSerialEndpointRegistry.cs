using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace VComTunnel.Core;

public sealed class WirelessSerialEndpointRegistry
{
    public const int DefaultUdpPort = 19527;
    public const string Magic = "XFGWS";
    public const string Protocol = "xfg-discovery";
    private const int Rfc2217ServiceMask = 1 << 0;

    private static readonly TimeSpan DefaultDeviceTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultQueryInterval = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, WirelessSerialDeviceEndpoint> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly InMemoryLog _log;
    private readonly int _udpPort;
    private readonly TimeSpan _deviceTtl;
    private readonly TimeSpan _queryInterval;
    private readonly Func<CancellationToken, Task<bool>> _periodicQueryEnabled;
    private readonly object _lifecycleLock = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _stop;
    private Task? _receiveLoop;
    private Task? _queryLoop;
    private int _sequence;

    public event Action<WirelessSerialDeviceEndpoint>? EndpointUpdated;

    public WirelessSerialEndpointRegistry(
        InMemoryLog? log = null,
        int udpPort = DefaultUdpPort,
        TimeSpan? deviceTtl = null,
        TimeSpan? queryInterval = null,
        Func<CancellationToken, Task<bool>>? periodicQueryEnabled = null)
    {
        if (udpPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(udpPort), "UDP discovery port must be between 1 and 65535.");
        }

        _log = log ?? new InMemoryLog();
        _udpPort = udpPort;
        _deviceTtl = deviceTtl ?? DefaultDeviceTtl;
        _queryInterval = queryInterval ?? DefaultQueryInterval;
        _periodicQueryEnabled = periodicQueryEnabled ?? (_ => Task.FromResult(true));
    }

    public IReadOnlyList<WirelessSerialDeviceEndpoint> Snapshot()
    {
        PruneExpired();
        return _devices.Values
            .OrderBy(device => device.Mac, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public WirelessSerialEndpoint? FindEndpointByMac(string? mac)
    {
        var normalized = NormalizeMac(mac);
        if (normalized is null)
        {
            return null;
        }

        PruneExpired();
        return _devices.TryGetValue(normalized, out var device)
            ? new WirelessSerialEndpoint(device.Mac, device.IpAddress, device.ServicePort, device.LastSeenAt)
            : null;
    }

    public WirelessSerialDeviceEndpoint Upsert(WirelessSerialEndpointUpdateRequest request)
    {
        var mac = NormalizeMac(request.Mac)
            ?? throw new ArgumentException("MAC must contain 12 hexadecimal digits.", nameof(request));
        if (!IPAddress.TryParse(request.IpAddress, out var ip) || IPAddress.Any.Equals(ip))
        {
            throw new ArgumentException("ipAddress must be a usable IPv4 or IPv6 address.", nameof(request));
        }

        if (request.ServicePort is < 1 or > 65535)
        {
            throw new ArgumentException("servicePort must be between 1 and 65535.", nameof(request));
        }

        var endpoint = new WirelessSerialDeviceEndpoint(
            mac,
            EmptyToNull(request.DeviceId),
            EmptyToNull(request.Name),
            EmptyToNull(request.Product),
            EmptyToNull(request.Board),
            EmptyToNull(request.Firmware),
            ip.ToString(),
            request.ServicePort,
            EmptyToNull(request.Mode),
            request.WifiRssi,
            request.ConfigMode,
            request.Clients,
            DateTimeOffset.UtcNow,
            EmptyToNull(request.Source) ?? "wireless-serial-app");
        _devices[mac] = endpoint;
        _log.Info("wireless-serial", $"Updated endpoint {mac} -> {endpoint.IpAddress}:{endpoint.ServicePort?.ToString() ?? "?"}.");
        NotifyEndpointUpdated(endpoint);
        return endpoint;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleLock)
        {
            if (_udp is not null)
            {
                return;
            }

            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _udp = new UdpClient(_udpPort)
            {
                EnableBroadcast = true
            };
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_stop.Token));
            _queryLoop = Task.Run(() => QueryLoopAsync(_stop.Token));
        }

        _log.Info("wireless-serial", $"Started minimal UDP endpoint discovery on port {_udpPort}.");
        await SendPeriodicQueryIfEnabledAsync(cancellationToken, logWhenSkipped: true).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        Task? receiveLoop;
        Task? queryLoop;
        lock (_lifecycleLock)
        {
            receiveLoop = _receiveLoop;
            queryLoop = _queryLoop;
            _receiveLoop = null;
            _queryLoop = null;
            _stop?.Cancel();
            _udp?.Dispose();
            _udp = null;
        }

        try
        {
            var loops = new List<Task>(2);
            if (receiveLoop is not null)
            {
                loops.Add(receiveLoop);
            }

            if (queryLoop is not null)
            {
                loops.Add(queryLoop);
            }

            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _stop?.Dispose();
            _stop = null;
        }
    }

    public async Task<object> SendQueryAsync(CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            magic = Magic,
            proto = Protocol,
            ver = 1,
            cmd = "query",
            seq = NextSequence()
        });
        var bytes = Encoding.UTF8.GetBytes(payload);
        var targets = UdpBroadcastTargets()
            .ToArray();
        var responseCounts = await Task.WhenAll(
            targets.Select(target => SendBroadcastQueryAsync(bytes, target, cancellationToken))).ConfigureAwait(false);

        return new
        {
            sent = true,
            message = "query sent",
            port = _udpPort,
            targets = targets.Select(item => item.Label).ToArray(),
            responses = responseCounts.Sum()
        };
    }

    public async Task<object> SendPeriodicQueryIfEnabledAsync(
        CancellationToken cancellationToken = default,
        bool logWhenSkipped = true)
    {
        if (!await IsPeriodicQueryEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            if (logWhenSkipped)
            {
                _log.Info("wireless-serial", "Skipped UDP query because no MAC-bound mapping is configured.");
            }

            return new { sent = false, message = "periodic query skipped", port = _udpPort };
        }

        return await SendQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool TryApplyDiscoveryPacket(string json, IPEndPoint remoteEndpoint, out WirelessSerialDeviceEndpoint? endpoint)
    {
        endpoint = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!StringEquals(root, "magic", Magic) || !StringEquals(root, "proto", Protocol))
            {
                return false;
            }

            var cmd = GetString(root, "cmd");
            if (!string.Equals(cmd, "announce", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(cmd, "response", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!root.TryGetProperty("device", out var deviceObject)
                || !root.TryGetProperty("net", out var netObject))
            {
                return false;
            }

            var mac = NormalizeMac(GetString(deviceObject, "mac"));
            if (mac is null)
            {
                return false;
            }

            var ip = GetString(netObject, "ip");
            if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0")
            {
                ip = remoteEndpoint.Address.ToString();
            }

            var mode = GetString(netObject, "mode");
            if (!PacketAdvertisesRfc2217(root, netObject, mode))
            {
                return false;
            }

            var port = GetInt(netObject, "port");
            if (port is < 1 or > 65535)
            {
                port = null;
            }

            endpoint = Upsert(new WirelessSerialEndpointUpdateRequest(
                mac,
                ip,
                port,
                DeviceId: GetString(deviceObject, "id"),
                Name: GetString(deviceObject, "name"),
                Product: GetString(deviceObject, "product"),
                Board: GetString(deviceObject, "board"),
                Firmware: GetString(deviceObject, "firmware"),
                Mode: mode,
                Source: "udp"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return null;
        }

        var builder = new StringBuilder(12);
        foreach (var ch in mac)
        {
            if (Uri.IsHexDigit(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
            }
        }

        return builder.Length == 12 ? builder.ToString() : null;
    }

    public static bool HasMacBoundMapping(IEnumerable<TunnelMapping> mappings)
    {
        return mappings.Any(mapping =>
            mapping.WirelessSerialAutoDiscover
            && NormalizeMac(mapping.WirelessSerialMac) is not null);
    }

    private async Task<int> SendBroadcastQueryAsync(
        byte[] bytes,
        UdpQueryTarget target,
        CancellationToken cancellationToken)
    {
        using var probe = new UdpClient(new IPEndPoint(target.BindAddress ?? IPAddress.Any, 0))
        {
            EnableBroadcast = true
        };
        probe.Client.ReceiveTimeout = 150;

        await probe.SendAsync(
            bytes,
            new IPEndPoint(target.TargetAddress, _udpPort),
            cancellationToken).ConfigureAwait(false);

        // Active discovery uses an ephemeral source port. Firmware must reply
        // to the UDP recvfrom() peer; the fixed 19527 socket is only for
        // passive device announcements.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(900);
        var responses = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                probe.Client.ReceiveTimeout = (int)Math.Clamp(remaining.TotalMilliseconds, 1, 150);
                var remote = new IPEndPoint(IPAddress.Any, 0);
                var buffer = probe.Receive(ref remote);
                var text = Encoding.UTF8.GetString(buffer);
                if (TryApplyDiscoveryPacket(text, remote, out _))
                {
                    responses++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }

        return responses;
    }

    private static IReadOnlyList<UdpQueryTarget> UdpBroadcastTargets()
    {
        var targets = new List<UdpQueryTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(IPAddress targetAddress, IPAddress? bindAddress = null)
        {
            if (targetAddress.AddressFamily == AddressFamily.InterNetwork
                && (bindAddress is null || bindAddress.AddressFamily == AddressFamily.InterNetwork))
            {
                var key = bindAddress is null
                    ? targetAddress.ToString()
                    : $"{bindAddress}->{targetAddress}";
                if (seen.Add(key))
                {
                    targets.Add(new UdpQueryTarget(targetAddress, bindAddress));
                }
            }
        }

        var bindAddresses = UdpBroadcastBindAddresses();
        if (bindAddresses.Count == 0)
        {
            Add(IPAddress.Broadcast);
        }
        else
        {
            foreach (var bindAddress in bindAddresses)
            {
                Add(IPAddress.Broadcast, bindAddress);
            }
        }

        // Do not infer directed broadcasts from local NIC masks. Multi-NIC
        // Windows hosts often expose VPN, virtual switch, WSL, and isolated
        // adapters; only operator-provided broadcast targets are safe here.
        var configured = Environment.GetEnvironmentVariable("XFG_LAN_BROADCASTS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var token in configured.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (IPAddress.TryParse(token, out var address))
                {
                    Add(address);
                }
            }
        }

        return targets;
    }

    private static IReadOnlyList<IPAddress> UdpBroadcastBindAddresses()
    {
        var configured = Environment.GetEnvironmentVariable("XFG_LAN_INTERFACE_IPS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured
                .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => IPAddress.TryParse(token, out var address) ? address : null)
                .Where(address => address is { AddressFamily: AddressFamily.InterNetwork }
                    && IsUsableInterfaceAddress(address))
                .Cast<IPAddress>()
                .DistinctBy(address => address.ToString())
                .ToArray();
        }

        var includeVirtual = IsEnabledEnvironmentFlag("XFG_LAN_INCLUDE_VIRTUAL_INTERFACES");
        var includeNoGateway = IsEnabledEnvironmentFlag("XFG_LAN_INCLUDE_NO_GATEWAY_INTERFACES");
        var addresses = new List<IPAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            if (!includeVirtual && IsLikelyVirtualAdapter(adapter))
            {
                continue;
            }

            var properties = adapter.GetIPProperties();
            if (!includeNoGateway
                && !properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork
                    && IsUsableInterfaceAddress(unicast.Address))
                {
                    addresses.Add(unicast.Address);
                }
            }
        }

        return addresses
            .DistinctBy(address => address.ToString())
            .ToArray();
    }

    private static bool IsUsableInterfaceAddress(IPAddress address)
    {
        var octets = address.GetAddressBytes();
        return octets[0] != 0
            && octets[0] != 127
            && !(octets[0] == 169 && octets[1] == 254)
            && octets[0] < 224;
    }

    private static bool IsEnabledEnvironmentFlag(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyVirtualAdapter(NetworkInterface adapter)
    {
        var text = $"{adapter.Name} {adapter.Description}";
        foreach (var marker in new[] { "virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "wsl", "docker", "tap", "tun" })
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct UdpQueryTarget(IPAddress TargetAddress, IPAddress? BindAddress)
    {
        public string Label => BindAddress is null
            ? TargetAddress.ToString()
            : $"{BindAddress}->{TargetAddress}";
    }

    private void PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - _deviceTtl;
        foreach (var (mac, device) in _devices)
        {
            if (device.LastSeenAt < cutoff)
            {
                _devices.TryRemove(mac, out _);
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var udp = _udp;
                if (udp is null)
                {
                    return;
                }

                var result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(result.Buffer);
                TryApplyDiscoveryPacket(text, result.RemoteEndPoint, out _);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or JsonException)
            {
                _log.Warn("wireless-serial", $"UDP endpoint discovery receive failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task QueryLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_queryInterval, cancellationToken).ConfigureAwait(false);
                PruneExpired();
                await SendPeriodicQueryIfEnabledAsync(cancellationToken, logWhenSkipped: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<bool> IsPeriodicQueryEnabledAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _periodicQueryEnabled(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Warn("wireless-serial", $"Could not read MAC-bound mapping state; skipping periodic UDP query: {ex.Message}");
            return false;
        }
    }

    private int NextSequence()
    {
        var sequence = Interlocked.Increment(ref _sequence);
        return sequence < 0 ? Interlocked.Exchange(ref _sequence, 1) : sequence;
    }

    private void NotifyEndpointUpdated(WirelessSerialDeviceEndpoint endpoint)
    {
        var handler = EndpointUpdated;
        if (handler is null)
        {
            return;
        }

        foreach (Action<WirelessSerialDeviceEndpoint> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(endpoint);
            }
            catch (Exception ex)
            {
                _log.Warn("wireless-serial", $"Endpoint update subscriber failed: {ex.Message}");
            }
        }
    }

    private static bool StringEquals(JsonElement root, string propertyName, string expected)
    {
        return string.Equals(GetString(root, propertyName), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static bool PacketAdvertisesRfc2217(JsonElement root, JsonElement netObject, string? mode)
    {
        // net.port is an active device service port; VCom may bind only when
        // the discovery packet identifies that endpoint as RFC2217.
        if (root.TryGetProperty("features", out var features)
            && TryGetBool(features, "rfc2217", out var rfc2217))
        {
            return rfc2217;
        }

        var serviceMask = GetInt(netObject, "service_mask");
        if (serviceMask.HasValue)
        {
            return (serviceMask.Value & Rfc2217ServiceMask) != 0;
        }

        return string.Equals(mode, "rfc2217", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetBool(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (property.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        return false;
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
