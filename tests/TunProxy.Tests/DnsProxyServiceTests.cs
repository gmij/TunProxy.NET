using System.Net;
using System.Net.Sockets;
using System.Text;
using TunProxy.CLI;
using TunProxy.Core.Connections;
using TunProxy.Core.Dns;

namespace TunProxy.Tests;

public class DnsProxyServiceTests
{
    [Fact]
    public void TryBuildCachedDnsResponse_AQueryHit_ReturnsCachedAnswerWithCurrentTransactionId()
    {
        var store = new DnsResolutionStore();
        var upstreamQuery = BuildDnsQuery("example.com", 0x1111, 1);
        var upstreamResponse = DnsPacket.Parse(BuildDnsResponse(upstreamQuery, 1, [1, 2, 3, 4]))!;
        store.StoreDnsResponseInCache(upstreamResponse);

        var query = DnsPacket.Parse(BuildDnsQuery("example.com", 0x2222, 1))!;
        var hit = store.TryBuildCachedDnsResponse(query, out var cachedResponse);

        Assert.True(hit);
        var cachedPacket = DnsPacket.Parse(cachedResponse)!;
        Assert.Equal((ushort)0x2222, cachedPacket.TransactionId);
        Assert.True(cachedPacket.Flags.IsResponse);
        Assert.Single(cachedPacket.Questions);
        Assert.Single(cachedPacket.Answers);
        Assert.Equal("example.com", cachedPacket.Questions[0].Name);
        Assert.Equal("example.com", cachedPacket.Answers[0].Name);
        Assert.Equal((ushort)1, cachedPacket.Answers[0].Type);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, cachedPacket.Answers[0].Data);
        Assert.Equal(1, store.CacheHits);
    }

    [Fact]
    public void TryBuildCachedDnsResponse_AaaaQuery_DoesNotUseARecordCache()
    {
        var store = new DnsResolutionStore();
        var upstreamQuery = BuildDnsQuery("example.com", 0x1111, 1);
        var upstreamResponse = DnsPacket.Parse(BuildDnsResponse(upstreamQuery, 1, [1, 2, 3, 4]))!;
        store.StoreDnsResponseInCache(upstreamResponse);

        var query = DnsPacket.Parse(BuildDnsQuery("example.com", 0x2222, 28))!;
        var hit = store.TryBuildCachedDnsResponse(query, out var cachedResponse);

        Assert.False(hit);
        Assert.Empty(cachedResponse);
        Assert.Equal(0, store.CacheHits);
    }

    [Fact]
    public void TryBuildCachedDnsResponse_MultipleARecords_ReturnsAllActiveAddresses()
    {
        var store = new DnsResolutionStore();
        var queryBytes = BuildDnsQuery("example.com", 0x1111, 1);
        var upstreamResponse = DnsPacket.Parse(BuildDnsResponse(
            queryBytes,
            600,
            [new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6, 7, 8 }]))!;
        store.StoreDnsResponseInCache(upstreamResponse);

        var query = DnsPacket.Parse(BuildDnsQuery("example.com", 0x2222, 1))!;
        var hit = store.TryBuildCachedDnsResponse(query, out var cachedResponse);

        Assert.True(hit);
        var cachedPacket = DnsPacket.Parse(cachedResponse)!;
        Assert.Equal(2, cachedPacket.Answers.Count);
        Assert.Contains(cachedPacket.Answers, answer => answer.Data.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        Assert.Contains(cachedPacket.Answers, answer => answer.Data.SequenceEqual(new byte[] { 5, 6, 7, 8 }));
        Assert.Equal(2, store.CacheEntryCount);
    }

    [Fact]
    public void RemoveCachedAddress_RemovesOnlyFailedAddressFromMultiIpDomain()
    {
        var store = new DnsResolutionStore();
        var queryBytes = BuildDnsQuery("example.com", 0x1111, 1);
        var upstreamResponse = DnsPacket.Parse(BuildDnsResponse(
            queryBytes,
            600,
            [new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6, 7, 8 }]))!;
        store.StoreDnsResponseInCache(upstreamResponse);

        Assert.True(store.RemoveCachedAddress("example.com", "1.2.3.4"));

        var query = DnsPacket.Parse(BuildDnsQuery("example.com", 0x2222, 1))!;
        Assert.True(store.TryBuildCachedDnsResponse(query, out var cachedResponse));
        var cachedPacket = DnsPacket.Parse(cachedResponse)!;
        Assert.Single(cachedPacket.Answers);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, cachedPacket.Answers[0].Data);
        Assert.Equal(1, store.CacheEntryCount);

        Assert.True(store.RemoveCachedAddress("example.com", "5.6.7.8"));
        Assert.False(store.TryBuildCachedDnsResponse(query, out _));
        Assert.Equal(0, store.CacheEntryCount);
    }

    [Fact]
    public void TryBuildCachedDnsResponse_InactiveMoreThanTenMinutes_RemovesRecord()
    {
        var store = new DnsResolutionStore();
        var now = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc);
        var queryBytes = BuildDnsQuery("example.com", 0x1111, 1);
        var upstreamResponse = DnsPacket.Parse(BuildDnsResponse(queryBytes, 1200, [1, 2, 3, 4]))!;
        store.StoreDnsResponseInCache(upstreamResponse, now);

        var query = DnsPacket.Parse(BuildDnsQuery("example.com", 0x2222, 1))!;
        var hit = store.TryBuildCachedDnsResponse(query, out var cachedResponse, now.AddMinutes(10).AddSeconds(1));

        Assert.False(hit);
        Assert.Empty(cachedResponse);
        Assert.Equal(0, store.CacheEntryCount);
    }

    [Fact]
    public async Task QueryDnsViaProxyAsync_HttpProxyWithAuth_SendsAuthorizationHeader()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string? receivedHeader = null;
        byte[]? receivedDnsQuery = null;

        var serverTask = RunHttpProxyServerAsync(listener, cts.Token, header => receivedHeader = header, payload => receivedDnsQuery = payload);
        var originalQuery = BuildDnsQuery("example.com", 0x1234, 28);
        var service = new DnsProxyService(
            "127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port,
            ProxyType.Http,
            new DnsResolutionStore(),
            proxyUsername: "user",
            proxyPassword: "pass");

        var response = await service.QueryDnsViaProxyAsync(originalQuery, "example.com", "8.8.8.8", cts.Token);

        Assert.NotNull(response);
        Assert.Contains(
            "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass")),
            receivedHeader);
        Assert.Equal(originalQuery, receivedDnsQuery);
        Assert.Equal(0x12, response![0]);
        Assert.Equal(0x34, response[1]);
        await serverTask;
    }

    [Fact]
    public async Task QueryDnsViaProxyAsync_Socks5ProxyWithAuth_CompletesAuthHandshake()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        byte[]? greeting = null;
        byte[]? authPayload = null;
        byte[]? receivedDnsQuery = null;

        var serverTask = RunSocks5ProxyServerAsync(
            listener,
            cts.Token,
            value => greeting = value,
            value => authPayload = value,
            value => receivedDnsQuery = value);
        var originalQuery = BuildDnsQuery("example.com", 0x4321, 28);

        var service = new DnsProxyService(
            "127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port,
            ProxyType.Socks5,
            new DnsResolutionStore(),
            proxyUsername: "user",
            proxyPassword: "pass");

        var response = await service.QueryDnsViaProxyAsync(originalQuery, "example.com", "8.8.8.8", cts.Token);

        Assert.NotNull(response);
        Assert.Equal(new byte[] { 0x05, 0x02, 0x00, 0x02 }, greeting);
        Assert.Equal(0x01, authPayload![0]);
        Assert.Equal("user", Encoding.UTF8.GetString(authPayload, 2, authPayload[1]));
        Assert.Equal(originalQuery, receivedDnsQuery);
        Assert.Equal(0x43, response![0]);
        Assert.Equal(0x21, response[1]);
        await serverTask;
    }

    private static async Task RunHttpProxyServerAsync(
        TcpListener listener,
        CancellationToken ct,
        Action<string> captureHeader,
        Action<byte[]> captureQuery)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        await using var stream = client.GetStream();

        var header = await ReadHeadersAsync(stream, ct);
        captureHeader(header);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct);

        var dnsQuery = await ReadDnsFrameAsync(stream, ct);
        captureQuery(dnsQuery);
        var dnsResponse = BuildDnsResponse(dnsQuery, 28, [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
        await WriteDnsFrameAsync(stream, dnsResponse, ct);
        listener.Stop();
    }

    private static async Task RunSocks5ProxyServerAsync(
        TcpListener listener,
        CancellationToken ct,
        Action<byte[]> captureGreeting,
        Action<byte[]> captureAuth,
        Action<byte[]> captureQuery)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        await using var stream = client.GetStream();

        var handshakePrefix = new byte[2];
        await stream.ReadExactlyAsync(handshakePrefix, ct);
        var methods = new byte[handshakePrefix[1]];
        await stream.ReadExactlyAsync(methods, ct);
        captureGreeting(handshakePrefix.Concat(methods).ToArray());
        await stream.WriteAsync(new byte[] { 0x05, 0x02 }, ct);

        var authPrefix = new byte[2];
        await stream.ReadExactlyAsync(authPrefix, ct);
        var usernameLength = authPrefix[1];
        var username = new byte[usernameLength];
        await stream.ReadExactlyAsync(username, ct);
        var passwordLength = new byte[1];
        await stream.ReadExactlyAsync(passwordLength, ct);
        var password = new byte[passwordLength[0]];
        await stream.ReadExactlyAsync(password, ct);
        captureAuth(authPrefix.Concat(username).Concat(passwordLength).Concat(password).ToArray());
        await stream.WriteAsync(new byte[] { 0x01, 0x00 }, ct);

        var connectPrefix = new byte[4];
        await stream.ReadExactlyAsync(connectPrefix, ct);
        Assert.Equal((byte)0x01, connectPrefix[3]);
        var addressBytes = new byte[4];
        await stream.ReadExactlyAsync(addressBytes, ct);
        var portBytes = new byte[2];
        await stream.ReadExactlyAsync(portBytes, ct);
        await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 53 }, ct);

        var dnsQuery = await ReadDnsFrameAsync(stream, ct);
        captureQuery(dnsQuery);
        var dnsResponse = BuildDnsResponse(dnsQuery, 28, [0x20, 0x01, 0x48, 0x60, 0x48, 0x60, 0, 0, 0, 0, 0, 0, 0, 0, 0x88, 0x88]);
        await WriteDnsFrameAsync(stream, dnsResponse, ct);
        listener.Stop();
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, ct);
            if (bytesRead == 0)
                throw new EndOfStreamException();

            builder.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                return builder.ToString();
        }
    }

    private static async Task<byte[]> ReadDnsFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var lengthBytes = new byte[2];
        await stream.ReadExactlyAsync(lengthBytes, ct);
        var length = (lengthBytes[0] << 8) | lengthBytes[1];
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }

    private static async Task WriteDnsFrameAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        var frame = new byte[payload.Length + 2];
        frame[0] = (byte)(payload.Length >> 8);
        frame[1] = (byte)(payload.Length & 0xFF);
        Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
        await stream.WriteAsync(frame, ct);
    }

    private static byte[] BuildDnsQuery(string domain, ushort transactionId, ushort type)
    {
        return new DnsPacket
        {
            TransactionId = transactionId,
            Flags = new DnsFlags(0x0100),
            Questions =
            [
                new DnsQuestion
                {
                    Name = domain,
                    Type = type,
                    Class = 1
                }
            ]
        }.Build();
    }

    private static byte[] BuildDnsResponse(byte[] queryBytes, ushort answerType, byte[] data)
    {
        return BuildDnsResponse(queryBytes, 60, [data], answerType);
    }

    private static byte[] BuildDnsResponse(byte[] queryBytes, uint ttl, IReadOnlyList<byte[]> answers, ushort answerType = 1)
    {
        var query = DnsPacket.Parse(queryBytes)!;
        var response = new DnsPacket
        {
            TransactionId = query.TransactionId,
            Flags = new DnsFlags(0x8180),
            Questions = query.Questions,
            Answers = answers
                .Select(data =>
                    new DnsAnswer
                    {
                        Name = query.Questions[0].Name,
                        Type = answerType,
                        Class = 1,
                        TTL = ttl,
                        Data = data
                    })
                .ToList()
        };

        return response.Build();
    }

    private static byte[] BuildDnsResponse(byte[] queryBytes, uint ttl, byte[] data, ushort answerType = 1)
    {
        var query = DnsPacket.Parse(queryBytes)!;
        var response = new DnsPacket
        {
            TransactionId = query.TransactionId,
            Flags = new DnsFlags(0x8180),
            Questions = query.Questions,
            Answers =
            [
                new DnsAnswer
                {
                    Name = query.Questions[0].Name,
                    Type = answerType,
                    Class = 1,
                    TTL = ttl,
                    Data = data
                }
            ]
        };

        return response.Build();
    }
}
