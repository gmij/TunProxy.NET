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

        if (_connections.TryGetValue(connKey, out var connection))
        {
            return connection;
        }

        if (_connections.Count >= _maxConnections)
        {
            CleanupIdleConnections(TimeSpan.FromMinutes(1));

            if (_connections.Count >= _maxConnections)
            {
                return null;
            }
        }

        return _connections.GetOrAdd(
            connKey,
            _ => new TcpConnection(_proxyHost, _proxyPort, _proxyType, _username, _password, _connectionTimeout, _bindAddress));
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
        return ProtocolInspector.MakeConnectionKey(packet);
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
    private const int MaxRetries = 1;
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
                    var mode = UpstreamTcpConnector.SelectMode(_proxyType, destPort);
                    _client = await UpstreamTcpConnector.ConnectAsync(
                        destHost,
                        destPort,
                        new UpstreamConnectionOptions(
                            _proxyHost,
                            _proxyPort,
                            _username,
                            _password,
                            _connectionTimeout,
                            _bindAddress),
                        mode,
                        ct);
                    _stream = _client.GetStream();

                    if (mode == UpstreamConnectionMode.HttpForward)
                    {
                        _isHttpPlainMode = true;
                        _needsHttpRewrite = true;
                        _destHost = destHost;
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
