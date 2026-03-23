using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using TunProxy.Core.Packets;

namespace TunProxy.Core.Connections;

/// <summary>
/// TCP 连接管理器
/// 维护 TUN 设备到代理服务器的长连接
/// </summary>
public class TcpConnectionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, TcpConnection> _connections = new();
    private readonly string _proxyHost;
    private readonly int _proxyPort;
    private readonly ProxyType _proxyType;
    private readonly string? _username;
    private readonly string? _password;
    private readonly int _maxConnections;
    private readonly TimeSpan _connectionTimeout;
    private readonly IPAddress? _bindAddress; // 强制绑定的本地 IP（防止走 TUN 接口）
    private bool _disposed;

    public TcpConnectionManager(
        string proxyHost,
        int proxyPort,
        ProxyType proxyType,
        string? username = null,
        string? password = null,
        int maxConnections = 10000,
        TimeSpan? connectionTimeout = null,
        IPAddress? bindAddress = null)
    {
        _proxyHost = proxyHost;
        _proxyPort = proxyPort;
        _proxyType = proxyType;
        _username = username;
        _password = password;
        _maxConnections = maxConnections;
        _connectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(30);
        _bindAddress = bindAddress;
    }

    /// <summary>
    /// 获取或创建 TCP 连接
    /// </summary>
    public TcpConnection? GetOrCreateConnection(IPPacket packet)
    {
        if (packet.SourcePort == null || packet.DestinationPort == null)
            throw new ArgumentException("Invalid packet");

        var connKey = MakeConnectionKey(packet);

        if (!_connections.TryGetValue(connKey, out var connection))
        {
            // 检查连接数限制
            if (_connections.Count >= _maxConnections)
            {
                // 尝试清理一些过期连接
                CleanupIdleConnections(TimeSpan.FromMinutes(1));

                // 如果仍然超过限制，返回 null
                if (_connections.Count >= _maxConnections)
                {
                    return null;
                }
            }

            connection = new TcpConnection(_proxyHost, _proxyPort, _proxyType, _username, _password, _connectionTimeout, _bindAddress);
            _connections[connKey] = connection;
        }

        return connection;
    }

    /// <summary>
    /// 仅查找已有连接（不创建新连接），用于非 SYN 包
    /// </summary>
    public TcpConnection? GetExistingConnection(IPPacket packet)
    {
        if (packet.SourcePort == null || packet.DestinationPort == null)
            return null;
        var connKey = MakeConnectionKey(packet);
        _connections.TryGetValue(connKey, out var connection);
        return connection;
    }

    /// <summary>
    /// 移除连接
    /// </summary>
    public void RemoveConnection(IPPacket packet)
    {
        var connKey = MakeConnectionKey(packet);
        if (_connections.TryRemove(connKey, out var connection))
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// 按连接键移除连接（供中继任务结束时使用）
    /// </summary>
    public void RemoveConnectionByKey(string connKey)
    {
        if (_connections.TryRemove(connKey, out var connection))
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// 生成连接唯一键
    /// </summary>
    private static string MakeConnectionKey(IPPacket packet)
    {
        return $"{packet.Header.SourceAddress}:{packet.SourcePort}-{packet.Header.DestinationAddress}:{packet.DestinationPort}";
    }

    /// <summary>
    /// 清理空闲连接
    /// </summary>
    public void CleanupIdleConnections(TimeSpan idleTimeout)
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _connections.ToList())
        {
            if (now - kvp.Value.LastActivity > idleTimeout)
            {
                RemoveConnectionByKvp(kvp);
            }
        }
    }

    private void RemoveConnectionByKvp(KeyValuePair<string, TcpConnection> kvp)
    {
        if (_connections.TryRemove(kvp.Key, out var connection))
        {
            connection.Dispose();
        }
    }

    public int ActiveConnections => _connections.Count;

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var conn in _connections.Values)
            {
                conn.Dispose();
            }
            _connections.Clear();
            _disposed = true;
        }
    }
}

/// <summary>
/// TCP 连接信息
/// </summary>
public class TcpConnection : IDisposable
{
    private readonly string _proxyHost;
    private readonly int _proxyPort;
    private readonly ProxyType _proxyType;
    private readonly string? _username;
    private readonly string? _password;
    private readonly TimeSpan _connectionTimeout;
    private readonly IPAddress? _bindAddress;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _connected;
    private bool _disposed;
    private int _retryCount;
    private const int MaxRetries = 3;
    private bool _isHttpPlainMode;   // HTTP 代理 + port 80：直转发模式（不用 CONNECT）
    private bool _needsHttpRewrite;  // 第一次发送时改写请求行
    private string _destHost = string.Empty;   // 目标主机（用于改写 HTTP 请求行）

    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
    public bool HasErrors { get; private set; }

    public TcpConnection(
        string proxyHost,
        int proxyPort,
        ProxyType proxyType,
        string? username = null,
        string? password = null,
        TimeSpan? connectionTimeout = null,
        IPAddress? bindAddress = null)
    {
        _proxyHost = proxyHost;
        _proxyPort = proxyPort;
        _proxyType = proxyType;
        _username = username;
        _password = password;
        _connectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(30);
        _bindAddress = bindAddress;
    }

    /// <summary>
    /// 连接到代理服务器（带重试机制）
    /// </summary>
    public async Task<bool> ConnectAsync(string destHost, int destPort, CancellationToken ct)
    {
        // 如果已连接，立即返回
        if (_connected) return true;
        if (_disposed) throw new ObjectDisposedException(nameof(TcpConnection));

        // 使用信号量确保同一连接对象只有一个线程在执行连接
        // 这防止了 TCP 重传导致的并发连接尝试
        await _connectLock.WaitAsync(ct);
        try
        {
            // 再次检查连接状态（可能在等待锁期间已连接）
            if (_connected) return true;

            Exception? lastException = null;

            for (int i = 0; i <= MaxRetries; i++)
            {
                try
                {
                    _client?.Dispose();
                    _client = new TcpClient();

                    // 绑定到物理网卡 IP，防止连接通过 TUN 接口路由（导致环路）
                    if (_bindAddress != null)
                        _client.Client.Bind(new IPEndPoint(_bindAddress, 0));

                    // 设置连接超时
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(_connectionTimeout);

                    var targetHost = _proxyType == ProxyType.Direct ? destHost : _proxyHost;
                    var targetPort = _proxyType == ProxyType.Direct ? destPort : _proxyPort;

                    await _client.ConnectAsync(targetHost, targetPort, cts.Token);
                    _stream = _client.GetStream();

                    // SOCKS5 握手
                    if (_proxyType == ProxyType.Socks5)
                    {
                        await Socks5HandshakeAsync(destHost, destPort, cts.Token);
                    }
                    else if (_proxyType == ProxyType.Http)
                    {
                        if (destPort == 443)
                        {
                            await HttpConnectAsync(destHost, destPort, cts.Token);
                        }
                        else
                        {
                            // HTTP 代理 + port 80：直转发模式，不用 CONNECT
                            // 第一个请求包需改写请求行加上完整 URL
                            _isHttpPlainMode = true;
                            _needsHttpRewrite = true;
                            _destHost = destHost;
                        }
                    }

                    _connected = true;
                    _retryCount = 0;
                    HasErrors = false;
                    LastActivity = DateTime.UtcNow;
                    return true;
                }
                catch (Exception ex) when (i < MaxRetries && !ct.IsCancellationRequested
                    && !IsProxyDeniedError(ex))  // 代理 4xx/5xx 错误不重试
                {
                    lastException = ex;
                    _retryCount++;

                    // 指数退避：等待 100ms, 200ms, 400ms
                    var delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, i));
                    await Task.Delay(delay, ct);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    break;
                }
            }

            HasErrors = true;
            // 在消息中标记是否为代理明确拒绝（4xx/5xx），让上层决定是否加入封锁缓存
            var reason = lastException != null && IsProxyDeniedError(lastException) ? "PROXY_DENIED" : "CONNECT_FAILED";
            throw new InvalidOperationException(
                $"Failed to connect after {MaxRetries + 1} attempts [{reason}]",
                lastException);
        }
        finally
        {
            // Dispose() 可能在 ConnectAsync 运行期间被 CleanupIdleConnections 调用
            // _disposed 已提前置 true，此处安全跳过 Release
            if (!_disposed)
                _connectLock.Release();
        }
    }

    /// <summary>
    /// SOCKS5 握手
    /// </summary>
    private async Task Socks5HandshakeAsync(string destHost, int destPort, CancellationToken ct)
    {
        if (_stream == null) throw new InvalidOperationException("No stream");

        // 1. 发送支持的认证方法
        var handshake = new byte[] { 0x05, 0x02, 0x00, 0x02 };
        await _stream.WriteAsync(handshake, ct);

        var response = new byte[2];
        await _stream.ReadExactlyAsync(response, ct);

        if (response[0] != 0x05)
            throw new InvalidOperationException("Not SOCKS5");

        // 2. 认证（如果需要）
        if (response[1] == 0x02 && !string.IsNullOrEmpty(_username))
        {
            var authRequest = new List<byte> { 0x01, (byte)_username.Length };
            authRequest.AddRange(System.Text.Encoding.UTF8.GetBytes(_username));
            authRequest.Add((byte)_password!.Length);
            authRequest.AddRange(System.Text.Encoding.UTF8.GetBytes(_password));

            await _stream.WriteAsync(authRequest.ToArray(), ct);

            var authResponse = new byte[2];
            await _stream.ReadExactlyAsync(authResponse, ct);

            if (authResponse[1] != 0x00)
                throw new InvalidOperationException("Authentication failed");
        }
        else if (response[1] != 0x00)
        {
            throw new InvalidOperationException("Unsupported auth method");
        }

        // 3. 发送连接请求
        var connectRequest = new List<byte> { 0x05, 0x01, 0x00 };

        if (IPAddress.TryParse(destHost, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4)
            {
                connectRequest.Add(0x01); // IPv4
                connectRequest.AddRange(bytes);
            }
            else
            {
                connectRequest.Add(0x04); // IPv6
                connectRequest.AddRange(bytes);
            }
        }
        else
        {
            connectRequest.Add(0x03); // 域名
            connectRequest.Add((byte)destHost.Length);
            connectRequest.AddRange(System.Text.Encoding.UTF8.GetBytes(destHost));
        }

        connectRequest.Add((byte)(destPort >> 8));
        connectRequest.Add((byte)(destPort & 0xFF));

        await _stream.WriteAsync(connectRequest.ToArray(), ct);

        // 4. 读取响应
        var connectResponse = new byte[256];
        var bytesRead = await _stream.ReadAsync(connectResponse, ct);

        if (bytesRead < 10 || connectResponse[1] != 0x00)
            throw new InvalidOperationException($"SOCKS5 error: {connectResponse[1]}");
    }

    /// <summary>
    /// HTTP CONNECT 方法
    /// </summary>
    private async Task HttpConnectAsync(string destHost, int destPort, CancellationToken ct)
    {
        if (_stream == null) throw new InvalidOperationException("No stream");

        var request = $"CONNECT {destHost}:{destPort} HTTP/1.1\r\n";
        request += $"Host: {destHost}:{destPort}\r\n";

        if (!string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password))
        {
            var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_username}:{_password}"));
            request += $"Proxy-Authorization: Basic {credentials}\r\n";
        }

        request += "Proxy-Connection: Keep-Alive\r\n\r\n";

        var requestBytes = System.Text.Encoding.UTF8.GetBytes(request);
        await _stream.WriteAsync(requestBytes, ct);

        // 读取响应
        var responseBuffer = new byte[4096];
        var responseBuilder = new System.Text.StringBuilder();

        while (true)
        {
            var bytesRead = await _stream.ReadAsync(responseBuffer, ct);
            if (bytesRead == 0)
                throw new InvalidOperationException("Proxy connection closed");

            responseBuilder.Append(System.Text.Encoding.UTF8.GetString(responseBuffer, 0, bytesRead));
            var response = responseBuilder.ToString();

            if (response.Contains("\r\n\r\n"))
            {
                var statusLine = response.Split('\r')[0];
                if (!statusLine.Contains("200"))
                    throw new InvalidOperationException($"HTTP Proxy error: {statusLine}");

                break;
            }
        }
    }

    /// <summary>
    /// 发送数据
    /// </summary>
    public async Task SendAsync(byte[] data, CancellationToken ct)
    {
        if (_stream == null || !_connected)
            throw new InvalidOperationException("Not connected");

        // HTTP 直转发模式：第一次发送时改写请求行，将 GET /path 改成 GET http://host/path
        if (_isHttpPlainMode && _needsHttpRewrite && data.Length > 0)
        {
            _needsHttpRewrite = false;
            data = RewriteHttpRequestLine(data, _destHost);
        }

        await _stream.WriteAsync(data, ct);
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// 将 HTTP 请求行的绝对路径改写为完整 URL，使其符合代理格式
    /// 例：GET /path HTTP/1.1  →  GET http://host/path HTTP/1.1
    /// </summary>
    private static byte[] RewriteHttpRequestLine(byte[] data, string host)
    {
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(data);
            var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (lineEnd < 0) return data;

            var firstLine = text[..lineEnd];
            var parts = firstLine.Split(' ', 3);
            if (parts.Length == 3 && parts[1].StartsWith('/'))
            {
                // 改写路径为完整 URL
                var rewritten = $"{parts[0]} http://{host}{parts[1]} {parts[2]}\r\n" + text[(lineEnd + 2)..];
                return System.Text.Encoding.UTF8.GetBytes(rewritten);
            }
        }
        catch { /* 解析失败，原样发送 */ }
        return data;
    }

    /// <summary>
    /// 接收数据
    /// </summary>
    public async Task<int> ReceiveAsync(byte[] buffer, CancellationToken ct)
    {
        if (_stream == null || !_connected)
            throw new InvalidOperationException("Not connected");

        var bytesRead = await _stream.ReadAsync(buffer, ct);
        LastActivity = DateTime.UtcNow;
        return bytesRead;
    }

    public bool IsConnected => _connected && _client?.Connected == true;

    /// <summary>
    /// 判断是否为代理拒绝错误（4xx/5xx），这类错误不需要重试
    /// </summary>
    private static bool IsProxyDeniedError(Exception ex) =>
        ex.Message.Contains("HTTP Proxy error: HTTP/1.1 4") ||
        ex.Message.Contains("HTTP Proxy error: HTTP/1.1 5");

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true; // 先置 true，阻止 ConnectAsync finally 块 Release 已销毁的锁
            _stream?.Dispose();
            _client?.Dispose();
            _connectLock.Dispose();
        }
    }
}

public enum ProxyType
{
    Socks5,
    Http,
    Direct
}
