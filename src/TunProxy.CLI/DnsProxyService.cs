using System.Net;
using Serilog;
using TunProxy.Core.Connections;
using TunProxy.Core.Dns;
using TunProxy.Core.Packets;
using TunProxy.Core.Wintun;

namespace TunProxy.CLI;

/// <summary>
/// DNS 代理服务
/// 拦截 DNS 查询并通过 SOCKS5/HTTP 代理转发
/// 从 TunProxyService 中提取，符合 SRP 原则
/// </summary>
public class DnsProxyService
{
    private readonly string _proxyHost;
    private readonly int _proxyPort;
    private readonly ProxyType _proxyType;
    private readonly IpCacheManager _ipCache;

    public DnsProxyService(string proxyHost, int proxyPort, ProxyType proxyType, IpCacheManager ipCache)
    {
        _proxyHost = proxyHost;
        _proxyPort = proxyPort;
        _proxyType = proxyType;
        _ipCache = ipCache;
    }

    /// <summary>
    /// 处理 DNS 查询
    /// </summary>
    public async Task ProcessDnsQueryAsync(WintunSession session, IPPacket requestPacket, CancellationToken ct)
    {
        try
        {
            var dnsPacket = DnsPacket.Parse(requestPacket.Payload);
            if (dnsPacket == null || dnsPacket.Questions.Count == 0)
            {
                Log.Warning("无效的 DNS 查询包");
                return;
            }

            var domain = dnsPacket.Questions[0].Name;
            Log.Debug("DNS 查询：{Domain}", domain);

            var dnsResponse = await QueryDnsViaProxyAsync(domain, requestPacket.Header.DestinationAddress.ToString(), ct);
            if (dnsResponse == null || dnsResponse.Length == 0)
            {
                Log.Warning("DNS 查询失败：{Domain}", domain);
                return;
            }

            Log.Debug("DNS 响应：{Domain}, {Bytes} bytes", domain, dnsResponse.Length);

            // 解析 DNS 响应，缓存 IP → 域名映射
            try
            {
                var dnsRespPacket = DnsPacket.Parse(dnsResponse);
                if (dnsRespPacket != null)
                {
                    foreach (var answer in dnsRespPacket.Answers)
                    {
                        if (answer.Type == 1 && answer.Data.Length == 4) // A 记录
                        {
                            var ip = new IPAddress(answer.Data).ToString();
                            _ipCache.CacheHostname(ip, domain);
                            Log.Debug("DNS缓存：{IP} → {Domain}", ip, domain);
                        }
                    }
                }
            }
            catch { }

            TunWriter.WriteUdpResponse(session, requestPacket, dnsResponse);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理 DNS 查询失败");
        }
    }

    /// <summary>
    /// 通过代理查询 DNS（支持 HTTP CONNECT 和 SOCKS5 两种代理协议）
    /// </summary>
    private async Task<byte[]?> QueryDnsViaProxyAsync(string domain, string dnsServer, CancellationToken ct)
    {
        System.Net.Sockets.TcpClient? client = null;
        try
        {
            client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(_proxyHost, _proxyPort, ct);
            var stream = client.GetStream();

            if (_proxyType == ProxyType.Http)
            {
                var connectReq = $"CONNECT {dnsServer}:53 HTTP/1.1\r\nHost: {dnsServer}:53\r\nProxy-Connection: Keep-Alive\r\n\r\n";
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(connectReq), ct);

                var httpBuf = new byte[4096];
                var httpSb = new System.Text.StringBuilder();
                while (true)
                {
                    var n = await stream.ReadAsync(httpBuf, ct);
                    if (n == 0) { Log.Warning("HTTP 代理连接 DNS 服务器时关闭连接"); return null; }
                    httpSb.Append(System.Text.Encoding.UTF8.GetString(httpBuf, 0, n));
                    var r = httpSb.ToString();
                    if (r.Contains("\r\n\r\n"))
                    {
                        if (!r.Split('\r')[0].Contains("200")) { Log.Warning("HTTP 代理拒绝连接 DNS：{Status}", r.Split('\r')[0]); return null; }
                        break;
                    }
                }
            }
            else
            {
                // SOCKS5 握手
                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
                var response = new byte[2];
                await stream.ReadExactlyAsync(response, ct);

                if (response[0] != 0x05 || response[1] != 0x00)
                {
                    Log.Warning("SOCKS5 握手失败");
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
                    Log.Warning("SOCKS5 连接 DNS 服务器失败");
                    return null;
                }
            }

            // 构建 DNS A 记录查询（DNS-over-TCP）
            var dnsQuery = new DnsPacket
            {
                TransactionId = (ushort)Random.Shared.Next(0, 65536),
                Flags = new DnsFlags(0x0100),
                Questions = new List<DnsQuestion>
                {
                    new DnsQuestion { Name = domain, Type = 1, Class = 1 }
                }
            };
            var queryBytes = dnsQuery.Build();
            var tcpQuery = new byte[queryBytes.Length + 2];
            NetworkHelper.WriteUInt16BigEndian(tcpQuery.AsSpan(0, 2), (ushort)queryBytes.Length);
            Array.Copy(queryBytes, 0, tcpQuery, 2, queryBytes.Length);

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
            Log.Error(ex, "DNS 查询异常（通过 {ProxyType} 代理）：{Domain}", _proxyType, domain);
            return null;
        }
        finally
        {
            client?.Dispose();
        }
    }
}
