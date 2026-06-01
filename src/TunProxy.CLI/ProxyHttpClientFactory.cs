using System.Net;
using System.Net.Sockets;
using TunProxy.Core.Configuration;
using TunProxy.Core.Connections;

namespace TunProxy.CLI;

internal static class ProxyHttpClientFactory
{
    public static HttpClient Create(ProxyConfig? proxyConfig, TimeSpan timeout, int? linuxSocketMark = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        ConfigureLinuxSocketMark(handler, linuxSocketMark);

        var proxy = CreateProxy(proxyConfig);
        if (proxy != null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }

        return new HttpClient(handler) { Timeout = timeout };
    }

    private static void ConfigureLinuxSocketMark(SocketsHttpHandler handler, int? linuxSocketMark)
    {
        if (linuxSocketMark == null || !OperatingSystem.IsLinux())
        {
            return;
        }

        handler.ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                LinuxSocketMark.TryApply(socket, linuxSocketMark);
                await socket.ConnectAsync(context.DnsEndPoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    internal static IWebProxy? CreateProxy(ProxyConfig? proxyConfig)
    {
        var proxyUri = BuildProxyUri(proxyConfig);
        if (proxyUri == null)
        {
            return null;
        }

        var proxy = new WebProxy(proxyUri);
        if (!string.IsNullOrEmpty(proxyConfig?.Username))
        {
            proxy.Credentials = new NetworkCredential(
                proxyConfig.Username,
                proxyConfig.Password ?? string.Empty);
        }

        return proxy;
    }

    internal static Uri? BuildProxyUri(ProxyConfig? proxyConfig)
    {
        if (proxyConfig == null || string.IsNullOrWhiteSpace(proxyConfig.Host) || proxyConfig.Port <= 0)
        {
            return null;
        }

        var scheme = proxyConfig.GetProxyType() switch
        {
            ProxyType.Http => "http",
            ProxyType.Socks5 => "socks5",
            _ => null
        };

        return scheme == null
            ? null
            : new Uri($"{scheme}://{proxyConfig.Host}:{proxyConfig.Port}");
    }
}
