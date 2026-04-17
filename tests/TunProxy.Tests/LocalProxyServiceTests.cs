using System.Net;
using System.Net.Sockets;
using System.Text;
using TunProxy.CLI;
using TunProxy.Core.Configuration;

namespace TunProxy.Tests;

public class LocalProxyServiceTests
{
    [Fact]
    public async Task LocalProxy_UsesRelativeRequestLineWhenUpstreamIsSocks5()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
        upstreamListener.Start();
        string? upstreamRequestLine = null;

        var upstreamTask = RunSocks5HttpServerAsync(upstreamListener, cts.Token, line => upstreamRequestLine = line);
        var localPort = GetFreePort();
        var service = new LocalProxyService(new AppConfig
        {
            Proxy = new ProxyConfig
            {
                Host = "127.0.0.1",
                Port = ((IPEndPoint)upstreamListener.LocalEndpoint).Port,
                Type = "Socks5",
                Username = "user",
                Password = "pass"
            },
            LocalProxy = new LocalProxyConfig
            {
                ListenPort = localPort,
                SetSystemProxy = false
            }
        });

        var serviceTask = Task.Run(() => service.StartAsync(cts.Token), cts.Token);
        await WaitForPortAsync(localPort, cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, localPort, cts.Token);
        await using var stream = client.GetStream();

        var request = "GET http://example.com/test?q=1 HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);

        var responseText = await ReadToEndAsync(stream, cts.Token);

        Assert.Contains("200 OK", responseText);
        Assert.Equal("GET /test?q=1 HTTP/1.1", upstreamRequestLine);

        await service.StopAsync();
        cts.Cancel();
        await serviceTask;
        await upstreamTask;
    }

    private static async Task RunSocks5HttpServerAsync(TcpListener listener, CancellationToken ct, Action<string> captureRequestLine)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        await using var stream = client.GetStream();

        var handshakePrefix = new byte[2];
        await stream.ReadExactlyAsync(handshakePrefix, ct);
        var methods = new byte[handshakePrefix[1]];
        await stream.ReadExactlyAsync(methods, ct);
        await stream.WriteAsync(new byte[] { 0x05, 0x02 }, ct);

        var authPrefix = new byte[2];
        await stream.ReadExactlyAsync(authPrefix, ct);
        var username = new byte[authPrefix[1]];
        await stream.ReadExactlyAsync(username, ct);
        var passwordLength = new byte[1];
        await stream.ReadExactlyAsync(passwordLength, ct);
        var password = new byte[passwordLength[0]];
        await stream.ReadExactlyAsync(password, ct);
        await stream.WriteAsync(new byte[] { 0x01, 0x00 }, ct);

        var connectPrefix = new byte[5];
        await stream.ReadExactlyAsync(connectPrefix, ct);
        var host = new byte[connectPrefix[4]];
        await stream.ReadExactlyAsync(host, ct);
        var portBytes = new byte[2];
        await stream.ReadExactlyAsync(portBytes, ct);
        await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0x1F, 0x90 }, ct);

        var requestHeader = await ReadHeadersAsync(stream, ct);
        captureRequestLine(requestHeader.Split("\r\n", StringSplitOptions.None)[0]);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"), ct);
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
                return builder.ToString();

            builder.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                return builder.ToString();
        }
    }

    private static async Task<string> ReadToEndAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1024];
        var builder = new StringBuilder();
        while (true)
        {
            int bytesRead = await stream.ReadAsync(buffer, ct);
            if (bytesRead == 0)
                return builder.ToString();

            builder.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForPortAsync(int port, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, ct);
                return;
            }
            catch
            {
                await Task.Delay(100, ct);
            }
        }

        throw new TimeoutException($"Port {port} did not open in time.");
    }
}
