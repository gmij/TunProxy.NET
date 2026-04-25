using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TunProxy.Core.Connections;

public enum UpstreamConnectionMode
{
    Direct,
    Socks5Tunnel,
    HttpTunnel,
    HttpForward
}

public sealed record UpstreamConnectionOptions(
    string ProxyHost,
    int ProxyPort,
    string? Username = null,
    string? Password = null,
    TimeSpan? ConnectionTimeout = null,
    IPAddress? BindAddress = null);

public static class UpstreamTcpConnector
{
    public static UpstreamConnectionMode SelectMode(ProxyType proxyType, int destPort) => proxyType switch
    {
        ProxyType.Direct => UpstreamConnectionMode.Direct,
        ProxyType.Socks5 => UpstreamConnectionMode.Socks5Tunnel,
        ProxyType.Http when destPort == 443 => UpstreamConnectionMode.HttpTunnel,
        ProxyType.Http => UpstreamConnectionMode.HttpForward,
        _ => UpstreamConnectionMode.Direct
    };

    public static async Task<TcpClient> ConnectAsync(
        string destHost,
        int destPort,
        UpstreamConnectionOptions options,
        UpstreamConnectionMode mode,
        CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            if (options.BindAddress != null)
            {
                client.Client.Bind(new IPEndPoint(options.BindAddress, 0));
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(options.ConnectionTimeout ?? TimeSpan.FromSeconds(30));

            var targetHost = mode == UpstreamConnectionMode.Direct ? destHost : options.ProxyHost;
            var targetPort = mode == UpstreamConnectionMode.Direct ? destPort : options.ProxyPort;

            await client.ConnectAsync(targetHost, targetPort, cts.Token);
            var stream = client.GetStream();

            if (mode == UpstreamConnectionMode.Socks5Tunnel)
            {
                await Socks5HandshakeAsync(stream, destHost, destPort, options.Username, options.Password, cts.Token);
            }
            else if (mode == UpstreamConnectionMode.HttpTunnel)
            {
                await HttpConnectAsync(stream, destHost, destPort, options.Username, options.Password, cts.Token);
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task Socks5HandshakeAsync(
        NetworkStream stream,
        string destHost,
        int destPort,
        string? username,
        string? password,
        CancellationToken ct)
    {
        var hasAuth = !string.IsNullOrEmpty(username);
        var greeting = hasAuth
            ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
            : new byte[] { 0x05, 0x01, 0x00 };

        await stream.WriteAsync(greeting, ct);

        var response = new byte[2];
        await stream.ReadExactlyAsync(response, ct);
        if (response[0] != 0x05)
        {
            throw new InvalidOperationException("Not SOCKS5");
        }

        if (response[1] == 0x02 && hasAuth)
        {
            var usernameBytes = Encoding.UTF8.GetBytes(username!);
            var passwordBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
            if (usernameBytes.Length > byte.MaxValue || passwordBytes.Length > byte.MaxValue)
            {
                throw new InvalidOperationException("SOCKS5 credentials are too long");
            }

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
                throw new InvalidOperationException("Authentication failed");
            }
        }
        else if (response[1] != 0x00)
        {
            throw new InvalidOperationException("Unsupported auth method");
        }

        var connectRequest = BuildSocks5ConnectRequest(destHost, destPort);
        await stream.WriteAsync(connectRequest, ct);

        var connectResponse = new byte[4];
        await stream.ReadExactlyAsync(connectResponse, ct);
        if (connectResponse[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 error: {connectResponse[1]}");
        }

        await DrainSocks5BindAddressAsync(stream, connectResponse[3], ct);
    }

    private static byte[] BuildSocks5ConnectRequest(string destHost, int destPort)
    {
        var request = new List<byte> { 0x05, 0x01, 0x00 };
        if (IPAddress.TryParse(destHost, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            request.Add(bytes.Length == 4 ? (byte)0x01 : (byte)0x04);
            request.AddRange(bytes);
        }
        else
        {
            var hostBytes = Encoding.UTF8.GetBytes(destHost);
            if (hostBytes.Length > byte.MaxValue)
            {
                throw new InvalidOperationException("SOCKS5 destination host is too long");
            }

            request.Add(0x03);
            request.Add((byte)hostBytes.Length);
            request.AddRange(hostBytes);
        }

        request.Add((byte)(destPort >> 8));
        request.Add((byte)(destPort & 0xFF));
        return request.ToArray();
    }

    private static async Task DrainSocks5BindAddressAsync(NetworkStream stream, byte addressType, CancellationToken ct)
    {
        var remaining = addressType switch
        {
            0x01 => 4 + 2,
            0x03 => await ReadDomainLengthAsync(stream, ct) + 2,
            0x04 => 16 + 2,
            _ => throw new InvalidOperationException($"SOCKS5 unsupported address type: {addressType}")
        };

        var buffer = new byte[remaining];
        await stream.ReadExactlyAsync(buffer, ct);
    }

    private static async Task<int> ReadDomainLengthAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer[0];
    }

    private static async Task HttpConnectAsync(
        NetworkStream stream,
        string destHost,
        int destPort,
        string? username,
        string? password,
        CancellationToken ct)
    {
        var request = new StringBuilder();
        request.Append($"CONNECT {destHost}:{destPort} HTTP/1.1\r\n");
        request.Append($"Host: {destHost}:{destPort}\r\n");
        if (!string.IsNullOrEmpty(username))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password ?? string.Empty}"));
            request.Append($"Proxy-Authorization: Basic {credentials}\r\n");
        }

        request.Append("Proxy-Connection: Keep-Alive\r\n\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request.ToString()), ct);

        var responseBuffer = new byte[4096];
        var response = new StringBuilder();
        while (true)
        {
            var bytesRead = await stream.ReadAsync(responseBuffer, ct);
            if (bytesRead == 0)
            {
                throw new InvalidOperationException("Proxy connection closed");
            }

            response.Append(Encoding.UTF8.GetString(responseBuffer, 0, bytesRead));
            var text = response.ToString();
            if (!text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                continue;
            }

            var statusLine = text.Split('\r')[0];
            if (!statusLine.Contains("200", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"HTTP Proxy error: {statusLine}");
            }

            return;
        }
    }
}
