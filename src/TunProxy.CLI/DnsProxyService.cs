using System.Net;
using Serilog;
using TunProxy.Core.Connections;
using TunProxy.Core.Dns;
using TunProxy.Core.Packets;
using TunProxy.Core.Tun;
using TunProxy.Core.Wintun;

namespace TunProxy.CLI;

public class DnsProxyService
{
    private readonly string _proxyHost;
    private readonly int _proxyPort;
    private readonly ProxyType _proxyType;
    private readonly IpCacheManager _ipCache;
    private readonly string _upstreamDns;
    private readonly string? _proxyUsername;
    private readonly string? _proxyPassword;

    public DnsProxyService(
        string proxyHost,
        int proxyPort,
        ProxyType proxyType,
        IpCacheManager ipCache,
        string upstreamDns = "8.8.8.8",
        string? proxyUsername = null,
        string? proxyPassword = null)
    {
        _proxyHost = proxyHost;
        _proxyPort = proxyPort;
        _proxyType = proxyType;
        _ipCache = ipCache;
        _upstreamDns = upstreamDns;
        _proxyUsername = proxyUsername;
        _proxyPassword = proxyPassword;
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
            Log.Debug("[DNS ] Query: {Domain}", domain);

            var dnsServer = ProtocolInspector.IsPrivateIp(requestPacket.Header.DestinationAddress)
                ? _upstreamDns
                : requestPacket.Header.DestinationAddress.ToString();

            var dnsResponse = await QueryDnsViaProxyAsync(requestPacket.Payload, domain, dnsServer, ct);
            if (dnsResponse == null || dnsResponse.Length == 0)
            {
                Log.Warning("[DNS ] {Domain} query failed", domain);
                return;
            }

            try
            {
                var dnsRespPacket = DnsPacket.Parse(dnsResponse);
                if (dnsRespPacket != null)
                {
                    var resolvedIps = new List<string>();
                    foreach (var answer in dnsRespPacket.Answers)
                    {
                        if (answer.Type == 1 && answer.Data.Length == 4)
                        {
                            var ip = new IPAddress(answer.Data).ToString();
                            _ipCache.CacheHostname(ip, domain);
                            resolvedIps.Add(ip);
                        }
                    }

                    if (resolvedIps.Count > 0)
                        Log.Information("[DNS ] {Domain} -> {IPs}", domain, string.Join(", ", resolvedIps));
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

    internal Task<byte[]?> QueryDnsViaProxyAsync(string domain, string dnsServer, CancellationToken ct)
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

        return QueryDnsViaProxyAsync(dnsQuery.Build(), domain, dnsServer, ct);
    }

    internal async Task<byte[]?> QueryDnsViaProxyAsync(byte[] dnsQueryPayload, string domain, string dnsServer, CancellationToken ct)
    {
        System.Net.Sockets.TcpClient? client = null;
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
                        if (!response.Split('\r')[0].Contains("200", StringComparison.Ordinal))
                        {
                            Log.Warning("HTTP proxy rejected DNS CONNECT: {Status}", response.Split('\r')[0]);
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
            return dnsResponseData;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DNS query via {ProxyType} proxy failed for {Domain}", _proxyType, domain);
            return null;
        }
        finally
        {
            client?.Dispose();
        }
    }
}
