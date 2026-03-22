using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TunProxy.Proxy;

/// <summary>
/// HTTP 代理客户端（支持 CONNECT 方法）
/// </summary>
public class HttpProxyClient : IDisposable
{
    private readonly string _proxyHost;
    private readonly int _proxyPort;
    private readonly string? _username;
    private readonly string? _password;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public HttpProxyClient(string proxyHost, int proxyPort, string? username = null, string? password = null)
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

        // 构建 CONNECT 请求
        var request = $"CONNECT {destinationHost}:{destinationPort} HTTP/1.1\r\n";
        request += $"Host: {destinationHost}:{destinationPort}\r\n";

        if (!string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}"));
            request += $"Proxy-Authorization: Basic {credentials}\r\n";
        }

        request += "Proxy-Connection: Keep-Alive\r\n";
        request += "\r\n";

        var requestBytes = Encoding.UTF8.GetBytes(request);
        await _stream.WriteAsync(requestBytes, ct);

        // 读取响应
        var responseBuffer = new byte[4096];
        var responseBuilder = new StringBuilder();
        var headerEnd = "\r\n\r\n";

        while (true)
        {
            var bytesRead = await _stream.ReadAsync(responseBuffer, ct);
            if (bytesRead == 0)
                throw new InvalidOperationException("Proxy connection closed");

            responseBuilder.Append(Encoding.UTF8.GetString(responseBuffer, 0, bytesRead));
            var response = responseBuilder.ToString();

            if (response.Contains(headerEnd))
            {
                var statusLine = response.Split('\r')[0];
                if (!statusLine.Contains("200"))
                    throw new InvalidOperationException($"HTTP Proxy error: {statusLine}");

                break;
            }
        }
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
