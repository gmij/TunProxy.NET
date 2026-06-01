using System.Diagnostics;
using System.Net;
using System.Collections.Concurrent;
using Serilog;
using TunProxy.Core.Connections;
using TunProxy.Core.Configuration;
using TunProxy.Core.Dns;
using TunProxy.Core.Packets;
using TunProxy.Core.Tun;
using TunProxy.Core.Wintun;

namespace TunProxy.CLI;

public class DnsProxyService
{
    private const string FakeIpLastMethod = "fakeip";
    internal const string DefaultDomesticDns = "119.29.29.29";
    private static readonly TimeSpan DirectDnsTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HttpConnectRejectWarningWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DohSuccessInfoWindow = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> DomesticDnsServers = new(StringComparer.OrdinalIgnoreCase)
    {
        "223.5.5.5",
        "223.6.6.6",
        "119.29.29.29",
        "182.254.116.116",
        "114.114.114.114",
        "114.114.115.115",
        "180.76.76.76"
    };

    private readonly string _proxyHost;
    private readonly int _proxyPort;
    private readonly ProxyType _proxyType;
    private readonly DnsResolutionStore _resolutionStore;
    private readonly string _upstreamDns;
    private readonly string? _proxyUsername;
    private readonly string? _proxyPassword;
    private readonly ProxyConfig _proxyConfig;
    private readonly RouteDecisionService? _routeDecision;
    private readonly Func<IPAddress, RouteDecision, CancellationToken, Task>? _onDirectRouteCandidate;
    private readonly Func<IPAddress, CancellationToken, Task>? _onDirectDnsServerCandidate;
    private readonly FakeIpPool? _fakeIpPool;
    private readonly HashSet<string> _probeDirectDomainSuffixes;
    private readonly string _dohEndpoint;
    private long _tcpQueries;
    private long _tcpSuccesses;
    private long _tcpFailures;
    private long _dohQueries;
    private long _dohSuccesses;
    private long _dohFailures;
    private string? _lastDomain;
    private string? _lastMethod;
    private string? _lastError;
    private DateTime? _lastQueryUtc;
    private DateTime? _lastSuccessUtc;
    private DateTime? _lastFailureUtc;
    private volatile bool _httpConnectTo53Rejected;
    private DateTime _lastHttpConnectRejectWarningUtc = DateTime.MinValue;
    private long _suppressedHttpConnectRejectWarnings;
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _dnsResolveInflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _fakeIpBackgroundResolveInflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastDohSuccessInfoByDomain = new(StringComparer.OrdinalIgnoreCase);

    public DnsProxyService(
        string proxyHost,
        int proxyPort,
        ProxyType proxyType,
        DnsResolutionStore resolutionStore,
        string upstreamDns = "8.8.8.8",
        string? proxyUsername = null,
        string? proxyPassword = null,
        RouteDecisionService? routeDecision = null,
        Func<IPAddress, RouteDecision, CancellationToken, Task>? onDirectRouteCandidate = null,
        Func<IPAddress, CancellationToken, Task>? onDirectDnsServerCandidate = null,
        IEnumerable<string>? probeDirectDomains = null,
        FakeIpPool? fakeIpPool = null)
    {
        _proxyHost = proxyHost;
        _proxyPort = proxyPort;
        _proxyType = proxyType;
        _resolutionStore = resolutionStore;
        _upstreamDns = upstreamDns;
        _proxyUsername = proxyUsername;
        _proxyPassword = proxyPassword;
        _routeDecision = routeDecision;
        _onDirectRouteCandidate = onDirectRouteCandidate;
        _onDirectDnsServerCandidate = onDirectDnsServerCandidate;
        _fakeIpPool = fakeIpPool;
        _dohEndpoint = ResolveDohEndpoint(upstreamDns);
        _probeDirectDomainSuffixes = new HashSet<string>(
            (probeDirectDomains ?? [])
                .Select(static value => value.Trim().TrimEnd('.').ToLowerInvariant())
                .Where(static value => value.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        _proxyConfig = new ProxyConfig
        {
            Host = proxyHost,
            Port = proxyPort,
            Type = proxyType == ProxyType.Http ? "Http" : "Socks5",
            Username = proxyUsername,
            Password = proxyPassword
        };

        Log.Information("[DNS ] DoH fallback endpoint selected: {Endpoint} (upstream DNS: {UpstreamDns})", _dohEndpoint, upstreamDns);
    }

    public async Task ProcessDnsQueryAsync(ITunDevice device, IPPacket requestPacket, CancellationToken ct)
    {
        try
        {
            var dnsPacket = DnsPacket.Parse(requestPacket.Payload);
            if (dnsPacket == null || dnsPacket.Questions.Count == 0)
            {
                Log.Warning("Invalid DNS query packet");
                return;
            }

            var domain = dnsPacket.Questions[0].Name;
            var traceId = BuildTraceId(dnsPacket.TransactionId);
            _lastDomain = domain;
            _lastQueryUtc = DateTime.UtcNow;
            Log.Debug("[DNS:{Trace}] Query: {Domain}", traceId, domain);

            if (IsProbeDomain(domain))
            {
                if (await HandleProbeDomainQueryAsync(device, requestPacket, dnsPacket, domain, traceId, ct))
                {
                    return;
                }
            }

            // FakeIP mode: intercept A-record queries and return a synthetic fake address.
            if (_fakeIpPool != null && dnsPacket.Questions[0].Type == 1 /* A record */)
            {
                if (ShouldBypassFakeIpForDomain(domain))
                {
                    await HandleFakeIpBypassQueryAsync(device, requestPacket, dnsPacket, domain, traceId, ct);
                    return;
                }

                await HandleFakeIpQueryAsync(device, requestPacket, dnsPacket, domain, ct);
                return;
            }

            if (_resolutionStore.TryBuildCachedDnsResponse(dnsPacket, out var cachedResponse))
            {
                _lastMethod = "cache";
                _lastSuccessUtc = DateTime.UtcNow;
                _lastError = null;
                Log.Debug("[DNS:{Trace}] Cache hit for {Domain}", traceId, domain);
                await RecordResolvedAddressesAsync(cachedResponse, domain, traceId, ct);
                TunWriter.WriteUdpResponse(device, requestPacket, cachedResponse);
                return;
            }

            var requestedDnsServer = ProtocolInspector.IsPrivateIp(requestPacket.Header.DestinationAddress)
                ? _upstreamDns
                : requestPacket.Header.DestinationAddress.ToString();
            var domainDecision = _routeDecision?.TryDecideWithoutIp(domain);
            var dnsServer = SelectRoutingDnsServer(_upstreamDns, requestedDnsServer, domainDecision);
            var useProxySideResolver = ShouldUseProxySideResolver(domainDecision);

            var dnsResponse = await ResolveDnsThroughUpstreamWithSingleflightAsync(
                requestPacket.Payload,
                domain,
                dnsServer,
                useProxySideResolver,
                traceId,
                ct);

            if (dnsResponse == null || dnsResponse.Length == 0)
            {
                Log.Warning("[DNS:{Trace}] {Domain} query failed", traceId, domain);
                return;
            }

            try
            {
                var dnsRespPacket = DnsPacket.Parse(dnsResponse);
                if (dnsRespPacket != null)
                {
                    _resolutionStore.StoreDnsResponseInCache(dnsRespPacket);
                    await RecordResolvedAddressesAsync(dnsRespPacket, domain, traceId, ct);
                }
            }
            catch
            {
            }

            TunWriter.WriteUdpResponse(device, requestPacket, dnsResponse);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to process DNS query");
        }
    }

    public DnsDiagnosticsSnapshot GetDiagnostics() => new()
    {
        TcpQueries = Interlocked.Read(ref _tcpQueries),
        TcpSuccesses = Interlocked.Read(ref _tcpSuccesses),
        TcpFailures = Interlocked.Read(ref _tcpFailures),
        DohQueries = Interlocked.Read(ref _dohQueries),
        DohSuccesses = Interlocked.Read(ref _dohSuccesses),
        DohFailures = Interlocked.Read(ref _dohFailures),
        CacheHits = _resolutionStore.CacheHits,
        CacheMisses = _resolutionStore.CacheMisses,
        CacheEntries = _resolutionStore.CacheEntryCount,
        LastDomain = _lastDomain,
        LastMethod = _lastMethod,
        LastError = _lastError,
        LastQueryUtc = _lastQueryUtc,
        LastSuccessUtc = _lastSuccessUtc,
        LastFailureUtc = _lastFailureUtc
    };

    private async Task<byte[]?> ResolveDnsThroughUpstreamWithSingleflightAsync(
        byte[] dnsQueryPayload,
        string domain,
        string dnsServer,
        bool useProxySideResolver,
        string traceId,
        CancellationToken ct)
    {
        var key = $"{domain.Trim().ToLowerInvariant()}|{dnsServer.Trim()}|{(useProxySideResolver ? "proxy" : "direct")}";
        var task = _dnsResolveInflight.GetOrAdd(
            key,
            _ => ResolveDnsThroughUpstreamAsync(dnsQueryPayload, domain, dnsServer, useProxySideResolver, traceId, ct));

        try
        {
            return await task;
        }
        finally
        {
            _dnsResolveInflight.TryRemove(key, out _);
        }
    }

    private async Task<byte[]?> ResolveDnsThroughUpstreamAsync(
        byte[] dnsQueryPayload,
        string domain,
        string dnsServer,
        bool useProxySideResolver,
        string traceId,
        CancellationToken ct)
    {
        if (!useProxySideResolver)
        {
            var directDnsResponse = await QueryDnsDirectUdpAsync(dnsQueryPayload, domain, dnsServer, traceId, ct);
            if (directDnsResponse != null && directDnsResponse.Length > 0)
            {
                return directDnsResponse;
            }

            Log.Warning("[DNS:{Trace}] Direct DNS failed for {Domain} via {DnsServer}; trying proxy/DoH fallback",
                traceId,
                domain,
                dnsServer);
        }

        if (ShouldBypassTcpProxyDns())
        {
            return await QueryDnsOverHttpsAsync(dnsQueryPayload, domain, traceId, ct, ResolveDohEndpoint(dnsServer));
        }

        var dnsResponse = await QueryDnsViaProxyAsync(dnsQueryPayload, domain, dnsServer, traceId, ct);
        if (dnsResponse == null || dnsResponse.Length == 0)
        {
            Log.Warning("[DNS:{Trace}] TCP DNS failed for {Domain}; trying DoH fallback", traceId, domain);
            dnsResponse = await QueryDnsOverHttpsAsync(dnsQueryPayload, domain, traceId, ct, ResolveDohEndpoint(dnsServer));
        }

        return dnsResponse;
    }

    private bool ShouldBypassTcpProxyDns() =>
        _proxyType == ProxyType.Http && _httpConnectTo53Rejected;

    // ── FakeIP helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Handles an A-record DNS query in FakeIP mode:
    /// <list type="number">
    ///   <item>Allocates (or reuses) a fake IP for the queried domain.</item>
    ///   <item>Records the fake-IP → domain mapping in the resolution store so that
    ///         subsequent TCP connections to the fake IP can resolve the hostname.</item>
    ///   <item>Returns a synthetic DNS response with the fake IP to the client immediately.</item>
    ///   <item>Fires a background real-DNS resolution via the upstream proxy so that routing
    ///         decisions (GeoIP, GFW) can be updated with the actual remote IP.</item>
    /// </list>
    /// </summary>
    private async Task HandleFakeIpBypassQueryAsync(
        ITunDevice device,
        IPPacket requestPacket,
        DnsPacket query,
        string domain,
        string traceId,
        CancellationToken ct)
    {
        var requestedDnsServer = ProtocolInspector.IsPrivateIp(requestPacket.Header.DestinationAddress)
            ? _upstreamDns
            : requestPacket.Header.DestinationAddress.ToString();
        var domainDecision = _routeDecision?.TryDecideWithoutIp(domain);
        var dnsServer = SelectRoutingDnsServer(_upstreamDns, requestedDnsServer, domainDecision);
        var useProxySideResolver = ShouldUseProxySideResolver(domainDecision);
        var dnsResponse = await ResolveDnsThroughUpstreamWithSingleflightAsync(
            requestPacket.Payload,
            domain,
            dnsServer,
            useProxySideResolver,
            traceId,
            ct);

        if (dnsResponse == null || dnsResponse.Length == 0)
        {
            Log.Warning("[DNS:{Trace}] {Domain} FakeIP bypass query failed", traceId, domain);
            return;
        }

        var dnsRespPacket = DnsPacket.Parse(dnsResponse);
        if (dnsRespPacket != null)
        {
            _resolutionStore.StoreDnsResponseInCache(dnsRespPacket);
            await RecordResolvedAddressesAsync(dnsRespPacket, domain, traceId, ct);
        }

        _lastMethod = "fakeip-bypass";
        _lastSuccessUtc = DateTime.UtcNow;
        _lastError = null;
        Log.Debug("[DNS:{Trace}] {Domain} bypassed FakeIP", traceId, domain);
        TunWriter.WriteUdpResponse(device, requestPacket, dnsResponse);
    }

    private async Task HandleFakeIpQueryAsync(
        ITunDevice device,
        IPPacket requestPacket,
        DnsPacket query,
        string domain,
        CancellationToken ct)
    {
        var fakeIp = _fakeIpPool!.AllocateOrGet(domain);
        var fakeIpStr = fakeIp.ToString();
        var traceId = BuildTraceId(query.TransactionId);

        Log.Debug("[DNS ] FakeIP {FakeIP} allocated for {Domain}", fakeIpStr, domain);
        _lastMethod = FakeIpLastMethod;
        _lastSuccessUtc = DateTime.UtcNow;
        _lastError = null;

        // Keep fake IP mappings inside FakeIpPool. They are virtual routing handles,
        // not real DNS answers, so they should not pollute user-visible DNS records.

        // Return the synthetic response immediately – no upstream round-trip needed.
        var fakeResponse = BuildFakeIpDnsResponse(query, domain, fakeIp);

        // Background: resolve the real IP for routing-decision quality. Start it before
        // returning the fake response so a following TCP connection can reuse the same work.
        // We deliberately do NOT await so the client gets an instant response.
        _ = EnsureFakeIpRealDnsResolutionAsync(domain, traceId, ct);
        TunWriter.WriteUdpResponse(device, requestPacket, fakeResponse);
    }

    internal Task EnsureFakeIpRealDnsResolutionAsync(string domain, string traceId, CancellationToken ct)
    {
        var key = domain.Trim().ToLowerInvariant();
        return _fakeIpBackgroundResolveInflight.GetOrAdd(
            key,
            _key => Task.Run(async () =>
            {
                try
                {
                    var queryPayload = BuildAQueryPayload(domain);
                    var domainDecision = _routeDecision?.TryDecideWithoutIp(domain);
                    var dnsServer = SelectRoutingDnsServer(_upstreamDns, _upstreamDns, domainDecision);
                    var useProxySideResolver = ShouldUseProxySideResolver(domainDecision);
                    var realDnsResponse = await ResolveDnsThroughUpstreamWithSingleflightAsync(queryPayload, domain, dnsServer, useProxySideResolver, traceId, ct);
                    if (realDnsResponse != null && realDnsResponse.Length > 0)
                    {
                        var realPacket = DnsPacket.Parse(realDnsResponse);
                        if (realPacket != null)
                        {
                            await RecordResolvedAddressesAsync(realPacket, domain, traceId, ct);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[DNS:{Trace}] FakeIP background real-DNS resolution failed for {Domain}", traceId, domain);
                }
                finally
                {
                    _fakeIpBackgroundResolveInflight.TryRemove(key, out var _);
                }
            }, ct));
    }

    /// <summary>
    /// Builds a synthetic DNS A-record response that returns <paramref name="fakeIp"/>
    /// for <paramref name="domain"/> with a short TTL (300 s).
    /// </summary>
    private static byte[] BuildFakeIpDnsResponse(DnsPacket query, string domain, IPAddress fakeIp)
    {
        // Response flags: QR=1, RA=1, preserve RD from query.
        var flags = (ushort)(0x8000 | 0x0080 | (query.Flags.Value & 0x0100));
        var response = new DnsPacket
        {
            TransactionId = query.TransactionId,
            Flags = new DnsFlags(flags),
            Questions = [new DnsQuestion { Name = domain, Type = 1, Class = 1 }],
            Answers =
            [
                new DnsAnswer
                {
                    Name = domain,
                    Type = 1,
                    Class = 1,
                    TTL = 300,
                    Data = fakeIp.GetAddressBytes()
                }
            ]
        };
        return response.Build();
    }

    private async Task RecordResolvedAddressesAsync(byte[] dnsResponse, string domain, string traceId, CancellationToken ct)
    {
        var dnsRespPacket = DnsPacket.Parse(dnsResponse);
        if (dnsRespPacket != null)
        {
            await RecordResolvedAddressesAsync(dnsRespPacket, domain, traceId, ct);
        }
    }

    private async Task RecordResolvedAddressesAsync(DnsPacket dnsRespPacket, string domain, string traceId, CancellationToken ct)
    {
        var resolvedIps = new List<string>();
        foreach (var answer in dnsRespPacket.Answers)
        {
            if (answer.Type != 1 || answer.Data.Length != 4)
            {
                continue;
            }

            var ipAddress = new IPAddress(answer.Data);
            var ip = ipAddress.ToString();
            var decision = _routeDecision == null
                ? null
                : await _routeDecision.DecideForObservedAddressAsync(domain, ipAddress, ct);
            _resolutionStore.RecordObservedHostname(
                ip,
                domain,
                "DNS",
                decision == null ? "UNKNOWN" : decision.ShouldProxy ? "PROXY" : "DIRECT",
                decision?.Reason,
                traceId);
            if (decision is { ShouldProxy: false } && _onDirectRouteCandidate != null)
            {
                _ = _onDirectRouteCandidate(ipAddress, decision, ct);
            }

            resolvedIps.Add(ip);
        }

        if (resolvedIps.Count > 0)
        {
            Log.Debug("[DNS ] {Domain} returned {IPs}", domain, string.Join(", ", resolvedIps));
        }
    }

    internal Task<byte[]?> QueryDnsViaProxyAsync(string domain, string dnsServer, string traceId, CancellationToken ct)
    {
        var dnsQuery = new DnsPacket
        {
            TransactionId = (ushort)Random.Shared.Next(0, 65536),
            Flags = new DnsFlags(0x0100),
            Questions = new List<DnsQuestion>
            {
                new DnsQuestion { Name = domain, Type = 1, Class = 1 }
            }
        };

        return QueryDnsViaProxyAsync(dnsQuery.Build(), domain, dnsServer, traceId, ct);
    }

    internal async Task<byte[]?> QueryDnsDirectUdpAsync(
        byte[] dnsQueryPayload,
        string domain,
        string dnsServer,
        string traceId,
        CancellationToken ct)
    {
        if (!IPAddress.TryParse(dnsServer, out var dnsAddress) ||
            dnsAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        _lastMethod = "udp-direct";

        try
        {
            if (_onDirectDnsServerCandidate != null)
            {
                await _onDirectDnsServerCandidate(dnsAddress, ct);
            }

            using var udp = new System.Net.Sockets.UdpClient(System.Net.Sockets.AddressFamily.InterNetwork);
            await udp.SendAsync(dnsQueryPayload, new IPEndPoint(dnsAddress, 53), ct);
            var result = await udp.ReceiveAsync(ct).AsTask().WaitAsync(DirectDnsTimeout, ct);
            _lastSuccessUtc = DateTime.UtcNow;
            Log.Debug("[DNS:{Trace}] Direct UDP DNS succeeded for {Domain} via {DnsServer} in {ElapsedMs} ms",
                traceId,
                domain,
                dnsServer,
                stopwatch.ElapsedMilliseconds);
            return result.Buffer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastFailureUtc = DateTime.UtcNow;
            _lastError = $"udp-direct: {ex.GetType().Name}: {ex.Message}";
            Log.Debug(ex,
                "[DNS:{Trace}] Direct UDP DNS failed for {Domain} via {DnsServer} in {ElapsedMs} ms",
                traceId,
                domain,
                dnsServer,
                stopwatch.ElapsedMilliseconds);
            return null;
        }
    }

    internal async Task<byte[]?> QueryDnsViaProxyAsync(byte[] dnsQueryPayload, string domain, string dnsServer, string traceId, CancellationToken ct)
    {
        System.Net.Sockets.TcpClient? client = null;
        var stopwatch = Stopwatch.StartNew();
        Interlocked.Increment(ref _tcpQueries);
        _lastMethod = "tcp-proxy";

        if (ShouldBypassTcpProxyDns())
        {
            // Some HTTP proxies explicitly deny CONNECT to destination port 53.
            // After the first definitive policy rejection, skip repeated CONNECT attempts
            // and let caller fall back to DoH directly.
            return null;
        }

        try
        {
            client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(_proxyHost, _proxyPort, ct);
            var stream = client.GetStream();

            if (_proxyType == ProxyType.Http)
            {
                var connectReq = $"CONNECT {dnsServer}:53 HTTP/1.1\r\nHost: {dnsServer}:53\r\n";
                if (!string.IsNullOrEmpty(_proxyUsername) && !string.IsNullOrEmpty(_proxyPassword))
                {
                    var credentials = Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes($"{_proxyUsername}:{_proxyPassword}"));
                    connectReq += $"Proxy-Authorization: Basic {credentials}\r\n";
                }

                connectReq += "Proxy-Connection: Keep-Alive\r\n\r\n";
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(connectReq), ct);

                var httpBuf = new byte[4096];
                var httpSb = new System.Text.StringBuilder();
                while (true)
                {
                    var n = await stream.ReadAsync(httpBuf, ct);
                    if (n == 0)
                    {
                        Log.Warning("HTTP proxy closed the connection while connecting to the DNS server");
                        return null;
                    }

                    httpSb.Append(System.Text.Encoding.UTF8.GetString(httpBuf, 0, n));
                    var response = httpSb.ToString();
                    if (response.Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var statusLine = response.Split('\r')[0];
                        if (!statusLine.Contains("200", StringComparison.Ordinal))
                        {
                            if (statusLine.Contains("403", StringComparison.Ordinal) ||
                                statusLine.Contains("405", StringComparison.Ordinal))
                            {
                                _httpConnectTo53Rejected = true;
                            }

                            LogHttpConnectRejected(statusLine);
                            return null;
                        }

                        break;
                    }
                }
            }
            else
            {
                var greeting = !string.IsNullOrEmpty(_proxyUsername)
                    ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
                    : new byte[] { 0x05, 0x01, 0x00 };
                await stream.WriteAsync(greeting, ct);

                var response = new byte[2];
                await stream.ReadExactlyAsync(response, ct);

                if (response[0] != 0x05)
                {
                    Log.Warning("SOCKS5 handshake failed");
                    return null;
                }

                if (response[1] == 0x02 && !string.IsNullOrEmpty(_proxyUsername))
                {
                    var usernameBytes = System.Text.Encoding.UTF8.GetBytes(_proxyUsername);
                    var passwordBytes = System.Text.Encoding.UTF8.GetBytes(_proxyPassword ?? string.Empty);
                    var authRequest = new byte[3 + usernameBytes.Length + passwordBytes.Length];
                    authRequest[0] = 0x01;
                    authRequest[1] = (byte)usernameBytes.Length;
                    Buffer.BlockCopy(usernameBytes, 0, authRequest, 2, usernameBytes.Length);
                    authRequest[2 + usernameBytes.Length] = (byte)passwordBytes.Length;
                    Buffer.BlockCopy(passwordBytes, 0, authRequest, 3 + usernameBytes.Length, passwordBytes.Length);

                    await stream.WriteAsync(authRequest, ct);

                    var authResponse = new byte[2];
                    await stream.ReadExactlyAsync(authResponse, ct);
                    if (authResponse[1] != 0x00)
                    {
                        Log.Warning("SOCKS5 authentication failed");
                        return null;
                    }
                }
                else if (response[1] != 0x00)
                {
                    Log.Warning("SOCKS5 returned an unsupported authentication method");
                    return null;
                }

                var connectRequest = new List<byte> { 0x05, 0x01, 0x00, 0x01 };
                var dnsIp = IPAddress.Parse(dnsServer);
                connectRequest.AddRange(dnsIp.GetAddressBytes());
                connectRequest.Add(0x00);
                connectRequest.Add(0x35);

                await stream.WriteAsync(connectRequest.ToArray(), ct);

                var connectResponse = new byte[256];
                var bytesRead = await stream.ReadAsync(connectResponse, ct);
                if (bytesRead < 10 || connectResponse[1] != 0x00)
                {
                    Log.Warning("SOCKS5 failed to connect to the DNS server");
                    return null;
                }
            }

            var tcpQuery = new byte[dnsQueryPayload.Length + 2];
            NetworkHelper.WriteUInt16BigEndian(tcpQuery.AsSpan(0, 2), (ushort)dnsQueryPayload.Length);
            Array.Copy(dnsQueryPayload, 0, tcpQuery, 2, dnsQueryPayload.Length);

            await stream.WriteAsync(tcpQuery, ct);

            var lengthBytes = new byte[2];
            await stream.ReadExactlyAsync(lengthBytes, ct);
            var responseLength = NetworkHelper.ReadUInt16BigEndian(lengthBytes);

            var dnsResponseData = new byte[responseLength];
            await stream.ReadExactlyAsync(dnsResponseData, ct);
            Interlocked.Increment(ref _tcpSuccesses);
            _lastSuccessUtc = DateTime.UtcNow;
            Log.Debug("[DNS ] TCP proxy DNS succeeded for {Domain} via {DnsServer} in {ElapsedMs} ms",
                domain,
                dnsServer,
                stopwatch.ElapsedMilliseconds);
            return dnsResponseData;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _tcpFailures);
            _lastFailureUtc = DateTime.UtcNow;
            _lastError = $"tcp-proxy: {ex.GetType().Name}: {ex.Message}";
            Log.Warning(ex,
                "[DNS ] TCP proxy DNS failed for {Domain} via {DnsServer} using {ProxyType} in {ElapsedMs} ms",
                domain,
                dnsServer,
                _proxyType,
                stopwatch.ElapsedMilliseconds);
            return null;
        }
        finally
        {
            client?.Dispose();
        }
    }

    internal async Task<byte[]?> QueryDnsOverHttpsAsync(
        byte[] dnsQueryPayload,
        string domain,
        string traceId,
        CancellationToken ct,
        string? dohEndpoint = null)
    {
        var stopwatch = Stopwatch.StartNew();
        Interlocked.Increment(ref _dohQueries);
        _lastMethod = "doh";
        var endpoint = dohEndpoint ?? _dohEndpoint;
        try
        {
            using var client = ProxyHttpClientFactory.Create(_proxyConfig, TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(dnsQueryPayload)
            };

            request.Headers.Accept.ParseAdd("application/dns-message");
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _dohFailures);
                _lastFailureUtc = DateTime.UtcNow;
                _lastError = $"doh: HTTP {(int)response.StatusCode} {response.StatusCode}";
                Log.Warning("[DNS ] DoH query for {Domain} failed: {Status}", domain, response.StatusCode);
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync(ct);
            Interlocked.Increment(ref _dohSuccesses);
            _lastSuccessUtc = DateTime.UtcNow;
            LogDohFallbackSuccess(domain, traceId, stopwatch.ElapsedMilliseconds);
            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref _dohFailures);
            _lastFailureUtc = DateTime.UtcNow;
            _lastError = $"doh: {ex.GetType().Name}: {ex.Message}";
            Log.Warning(ex, "[DNS ] DoH fallback failed for {Domain}", domain);
            return null;
        }
    }

    internal string SelectRoutingDnsServer(string domain, string requestedDnsServer) =>
        SelectRoutingDnsServer(
            _upstreamDns,
            requestedDnsServer,
            _routeDecision?.TryDecideWithoutIp(domain));

    internal static string SelectRoutingDnsServer(
        string upstreamDns,
        string requestedDnsServer,
        RouteDecision? domainDecision)
    {
        if (ShouldUseProxySideResolver(domainDecision))
        {
            return upstreamDns;
        }

        if (IsDomesticDnsServer(requestedDnsServer))
        {
            return requestedDnsServer;
        }

        if (IsDomesticDnsServer(upstreamDns))
        {
            return upstreamDns;
        }

        return DefaultDomesticDns;
    }

    private static bool ShouldUseProxySideResolver(RouteDecision? domainDecision)
    {
        if (domainDecision is not { ShouldProxy: true })
        {
            return false;
        }

        return domainDecision.Reason.Equals("GFW", StringComparison.OrdinalIgnoreCase) ||
               domainDecision.Reason.Equals("Global", StringComparison.OrdinalIgnoreCase) ||
               domainDecision.Reason.Equals("ProxyDomain", StringComparison.OrdinalIgnoreCase) ||
               domainDecision.Reason.Equals("DirectFailedFallback", StringComparison.OrdinalIgnoreCase) ||
               domainDecision.Reason.Contains(":GFW", StringComparison.OrdinalIgnoreCase) ||
               domainDecision.Reason.Contains(":Global", StringComparison.OrdinalIgnoreCase) ||
               domainDecision.Reason.Contains(":ProxyDomain", StringComparison.OrdinalIgnoreCase) ||
               domainDecision.Reason.Contains(":DirectFailedFallback", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDomesticDnsServer(string dnsServer) =>
        DomesticDnsServers.Contains(dnsServer.Trim());

    private static string ResolveDohEndpoint(string upstreamDns)
    {
        return upstreamDns switch
        {
            "223.5.5.5" or "223.6.6.6" => "https://dns.alidns.com/dns-query",
            "119.29.29.29" or "182.254.116.116" => "https://doh.pub/dns-query",
            "114.114.114.114" or "114.114.115.115" or "180.76.76.76" => "https://dns.alidns.com/dns-query",
            "1.1.1.1" or "1.0.0.1" => "https://cloudflare-dns.com/dns-query",
            _ => "https://dns.google/dns-query"
        };
    }

    private static string BuildTraceId(ushort transactionId) =>
        $"{transactionId:x4}-{DateTime.UtcNow:HHmmssfff}";

    private static byte[] BuildAQueryPayload(string domain)
    {
        var packet = new DnsPacket
        {
            TransactionId = (ushort)Random.Shared.Next(0, 65536),
            Flags = new DnsFlags(0x0100),
            Questions =
            [
                new DnsQuestion { Name = domain, Type = 1, Class = 1 }
            ]
        };

        return packet.Build();
    }

    private bool IsProbeDomain(string domain)
    {
        var normalized = domain.Trim().TrimEnd('.');
        return _probeDirectDomainSuffixes.Any(suffix =>
            normalized.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith('.' + suffix, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool ShouldBypassFakeIpForDomain(string domain)
    {
        var normalized = domain.Trim().TrimEnd('.');
        if (normalized.Length == 0)
        {
            return false;
        }

        return normalized
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(static label => label.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> HandleProbeDomainQueryAsync(
        ITunDevice device,
        IPPacket requestPacket,
        DnsPacket query,
        string domain,
        string traceId,
        CancellationToken ct)
    {
        if (query.Questions[0].Type != 1)
        {
            return false;
        }

        var addresses = await ResolveProbeDomainAddressesAsync(domain, ct);
        if (addresses.Count == 0)
        {
            return false;
        }

        var response = BuildDirectDnsResponse(query, domain, addresses);
        var responsePacket = DnsPacket.Parse(response);
        if (responsePacket != null)
        {
            _resolutionStore.StoreDnsResponseInCache(responsePacket);
            await RecordResolvedAddressesAsync(responsePacket, domain, traceId, ct);
        }

        _lastMethod = "probe-direct";
        _lastSuccessUtc = DateTime.UtcNow;
        _lastError = null;
        TunWriter.WriteUdpResponse(device, requestPacket, response);
        return true;
    }

    private static async Task<List<IPAddress>> ResolveProbeDomainAddressesAsync(string domain, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain, ct);
            return addresses
                .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static byte[] BuildDirectDnsResponse(DnsPacket query, string domain, IReadOnlyList<IPAddress> addresses)
    {
        var flags = (ushort)(0x8000 | 0x0080 | (query.Flags.Value & 0x0100));
        var response = new DnsPacket
        {
            TransactionId = query.TransactionId,
            Flags = new DnsFlags(flags),
            Questions = [new DnsQuestion { Name = domain, Type = 1, Class = 1 }],
            Answers = addresses.Select(address => new DnsAnswer
            {
                Name = domain,
                Type = 1,
                Class = 1,
                TTL = 60,
                Data = address.GetAddressBytes()
            }).ToList()
        };

        return response.Build();
    }

    private void LogHttpConnectRejected(string status)
    {
        var now = DateTime.UtcNow;
        if (now - _lastHttpConnectRejectWarningUtc >= HttpConnectRejectWarningWindow)
        {
            var suppressed = Interlocked.Exchange(ref _suppressedHttpConnectRejectWarnings, 0);
            _lastHttpConnectRejectWarningUtc = now;
            Log.Warning(
                "HTTP proxy rejected DNS CONNECT: {Status}. Suppressed={Suppressed} in last {WindowSeconds}s",
                status,
                suppressed,
                (int)HttpConnectRejectWarningWindow.TotalSeconds);
            return;
        }

        Interlocked.Increment(ref _suppressedHttpConnectRejectWarnings);
    }

    private void LogDohFallbackSuccess(string domain, string traceId, long elapsedMs)
    {
        var key = domain.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;
        var lastLogUtc = _lastDohSuccessInfoByDomain.GetOrAdd(key, DateTime.MinValue);
        if (now - lastLogUtc >= DohSuccessInfoWindow)
        {
            _lastDohSuccessInfoByDomain[key] = now;
            Log.Information("[DNS:{Trace}] DoH fallback succeeded for {Domain} in {ElapsedMs} ms", traceId, domain, elapsedMs);
            return;
        }

        Log.Debug("[DNS:{Trace}] DoH fallback succeeded for {Domain} in {ElapsedMs} ms", traceId, domain, elapsedMs);
    }
}
