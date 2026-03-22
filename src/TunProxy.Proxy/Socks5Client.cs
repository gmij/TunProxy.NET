using System.Net;
using System.Net.Sockets;

namespace TunProxy.Proxy;

/// <summary>
/// SOCKS5 代理客户端
/// </summary>
public class Socks5Client : IDisposable
{
    private readonly string _proxyHost;
    private readonly int _proxyPort;
    private readonly string? _username;
    private readonly string? _password;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public Socks5Client(string proxyHost, int proxyPort, string? username = null, string? password = null)
    {
        _proxyHost = proxyHost;
        _proxyPort = proxyPort;
        _username = username;
        _password = password;
    }

    public async Task ConnectAsync(string destinationHost, int destinationPort, CancellationToken ct = default)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(_proxyHost, _proxyPort, ct);
        _stream = _client.GetStream();

        // 1. 握手：发送支持的认证方法
        var handshake = new byte[] { 0x05, 0x02, 0x00, 0x02 }; // SOCKS5, 支持无认证和用户名密码
        await _stream.WriteAsync(handshake, ct);

        var response = new byte[2];
        await _stream.ReadExactlyAsync(response, ct);

        if (response[0] != 0x05)
            throw new InvalidOperationException("Not SOCKS5");

        byte authMethod = response[1];

        // 2. 如果需要用户名密码认证
        if (authMethod == 0x02)
        {
            if (string.IsNullOrEmpty(_username) || string.IsNullOrEmpty(_password))
                throw new InvalidOperationException("Proxy requires authentication");

            var authRequest = new List<byte> { 0x01, (byte)_username.Length };
            authRequest.AddRange(System.Text.Encoding.UTF8.GetBytes(_username));
            authRequest.Add((byte)_password.Length);
            authRequest.AddRange(System.Text.Encoding.UTF8.GetBytes(_password));

            await _stream.WriteAsync(authRequest.ToArray(), ct);

            var authResponse = new byte[2];
            await _stream.ReadExactlyAsync(authResponse, ct);

            if (authResponse[1] != 0x00)
                throw new InvalidOperationException("Authentication failed");
        }
        else if (authMethod != 0x00)
        {
            throw new InvalidOperationException("Unsupported auth method");
        }

        // 3. 发送连接请求
        var connectRequest = new List<byte> { 0x05, 0x01, 0x00 }; // SOCKS5, CONNECT, 保留字节

        // 判断是 IP 还是域名
        if (IPAddress.TryParse(destinationHost, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4) // IPv4
            {
                connectRequest.Add(0x01); // IPv4
                connectRequest.AddRange(bytes);
            }
            else // IPv6
            {
                connectRequest.Add(0x04); // IPv6
                connectRequest.AddRange(bytes);
            }
        }
        else
        {
            connectRequest.Add(0x03); // 域名
            connectRequest.Add((byte)destinationHost.Length);
            connectRequest.AddRange(System.Text.Encoding.UTF8.GetBytes(destinationHost));
        }

        connectRequest.Add((byte)(destinationPort >> 8));
        connectRequest.Add((byte)(destinationPort & 0xFF));

        await _stream.WriteAsync(connectRequest.ToArray(), ct);

        // 4. 读取响应
        var connectResponse = new byte[256]; // 足够大以容纳域名响应
        var bytesRead = await _stream.ReadAsync(connectResponse, ct);

        if (bytesRead < 10)
            throw new InvalidOperationException("Invalid SOCKS5 response");

        if (connectResponse[1] != 0x00)
            throw new InvalidOperationException($"SOCKS5 error: {connectResponse[1]}");
    }

    public NetworkStream GetStream()
    {
        return _stream ?? throw new InvalidOperationException("Not connected");
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _client?.Dispose();
    }
}
