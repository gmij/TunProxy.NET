using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;
using TunProxy.Core.Configuration;
using TunProxy.Core.Connections;
using TunProxy.Core.Metrics;

namespace TunProxy.CLI;

public class LocalProxyService : IProxyService
{
    private readonly AppConfig _config;
    private readonly ProxyMetrics _metrics = new();
    private GeoIpService? _geoIpService;
    private GfwListService? _gfwListService;
    private SystemProxyManager? _systemProxy;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private volatile int _activeConnections;
    private volatile int _downloadingCount;

    public LocalProxyService(AppConfig config)
    {
        _config = config;
        if (config.Route.EnableGeo)
            _geoIpService = new GeoIpService(config.Route.GeoIpDbPath);
        if (config.Route.EnableGfwList)
            _gfwListService = new GfwListService(config.Route.GfwListUrl, config.Route.GfwListPath);
    }

    public ServiceStatus GetStatus() => new()
    {
        Mode = "proxy",
        IsRunning = _cts != null && !_cts.IsCancellationRequested,
        IsDownloading = _downloadingCount > 0,
        ProxyHost = _config.Proxy.Host,
        ProxyPort = _config.Proxy.Port,
        ProxyType = _config.Proxy.Type,
        ActiveConnections = _activeConnections,
        Metrics = _metrics.GetSnapshot()
    };

    public IReadOnlyDictionary<string, string> GetDnsCache() => new Dictionary<string, string>();

    public IReadOnlyList<string> GetDirectIps() => [];

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var port = _config.LocalProxy.ListenPort;

        Log.Information("Starting local proxy mode on 127.0.0.1:{Port}", port);
        Log.Information("Upstream proxy: {Host}:{Port} ({Type})", _config.Proxy.Host, _config.Proxy.Port, _config.Proxy.Type);

        InitializeBackgroundServices();

        if (_config.LocalProxy.SetSystemProxy && OperatingSystem.IsWindows() && Environment.UserInteractive)
        {
            _systemProxy = new SystemProxyManager();
            _systemProxy.SetProxy($"127.0.0.1:{port}", _config.LocalProxy.BypassList);
        }

        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleClientAsync(client, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await StopAsync();
        }
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (OperatingSystem.IsWindows())
            _systemProxy?.Dispose();
        _systemProxy = null;
        while (_metrics.ActiveConnections > 0)
            _metrics.DecrementActiveConnections();
        Log.Information("Local proxy stopped");
        return Task.CompletedTask;
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeConnections);
        _metrics.IncrementTotalConnections();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();

            int totalRead = 0;
            int headerEnd = -1;
            while (totalRead < buffer.Length)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(30000);
                int n = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), readCts.Token);
                if (n == 0)
                    return;

                totalRead += n;
                headerEnd = FindHeaderEnd(buffer, totalRead);
                if (headerEnd >= 0)
                    break;
            }

            if (headerEnd < 0)
                return;

            var requestLine = ExtractFirstLine(buffer, totalRead);
            if (requestLine == null)
                return;

            if (requestLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                await HandleConnectAsync(stream, requestLine, ct);
            else
                await HandleHttpAsync(stream, requestLine, buffer, totalRead, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug("Proxy connection failed: {Message}", ex.Message);
            _metrics.IncrementFailedConnections();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            client.Close();
            Interlocked.Decrement(ref _activeConnections);
            if (_metrics.ActiveConnections > 0)
                _metrics.DecrementActiveConnections();
        }
    }

    private async Task HandleConnectAsync(NetworkStream clientStream, string requestLine, CancellationToken ct)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
            return;

        var hostPort = parts[1];
        var colonIdx = hostPort.LastIndexOf(':');
        if (colonIdx <= 0)
            return;

        var host = hostPort[..colonIdx];
        if (!int.TryParse(hostPort[(colonIdx + 1)..], out var port))
            return;

        bool shouldProxy = await ShouldProxyDomain(host);
        Log.Information("[CONN] {Host}:{Port}  {Route}  (CONNECT)", host, port, shouldProxy ? "PROXY" : "DIRECT");

        try
        {
            TcpClient upstream = shouldProxy
                ? await ConnectViaUpstreamProxy(host, port, ct)
                : await ConnectDirect(host, port, ct);

            using (upstream)
            {
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                await clientStream.WriteAsync(response, ct);

                using var upstreamStream = upstream.GetStream();
                await RelayAsync(clientStream, upstreamStream, ct);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[CONN] {Host}:{Port}  dropped  ({Message})", host, port, ex.Message);
            _metrics.IncrementFailedConnections();
            var errorResponse = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n");
            try
            {
                await clientStream.WriteAsync(errorResponse, ct);
            }
            catch
            {
            }
        }
    }

    private async Task HandleHttpAsync(NetworkStream clientStream, string requestLine, byte[] initialData, int initialLength, CancellationToken ct)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 3)
            return;

        var method = parts[0];
        var url = parts[1];
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return;

        var afterScheme = url[7..];
        var slashIdx = afterScheme.IndexOf('/');
        var hostPart = slashIdx >= 0 ? afterScheme[..slashIdx] : afterScheme;
        var path = slashIdx >= 0 ? afterScheme[slashIdx..] : "/";

        var colonIdx = hostPart.LastIndexOf(':');
        var host = colonIdx > 0 ? hostPart[..colonIdx] : hostPart;
        var port = colonIdx > 0 && int.TryParse(hostPart[(colonIdx + 1)..], out var parsedPort) ? parsedPort : 80;

        bool shouldProxy = await ShouldProxyDomain(host);
        var proxyType = _config.Proxy.GetProxyType();
        bool useAbsoluteUri = shouldProxy && proxyType == ProxyType.Http;
        Log.Information("[CONN] {Host}:{Port}  {Route}  (HTTP/{Method})", host, port, shouldProxy ? "PROXY" : "DIRECT", method);

        try
        {
            TcpClient upstream = shouldProxy
                ? await ConnectToUpstreamForHttp(host, port, ct)
                : await ConnectDirect(host, port, ct);

            using (upstream)
            using (var upstreamStream = upstream.GetStream())
            {
                var newRequestLine = useAbsoluteUri
                    ? $"{method} {url} {parts[2]}"
                    : $"{method} {path} {parts[2]}";

                var firstLineEnd = FindLineEnd(initialData, initialLength);
                var newHeader = Encoding.ASCII.GetBytes(newRequestLine + "\r\n");
                await upstreamStream.WriteAsync(newHeader, ct);
                if (firstLineEnd + 2 < initialLength)
                {
                    await upstreamStream.WriteAsync(
                        initialData.AsMemory(firstLineEnd + 2, initialLength - firstLineEnd - 2), ct);
                }

                _metrics.AddBytesSent(initialLength);
                await RelayAsync(clientStream, upstreamStream, ct);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[CONN] {Host}:{Port}  dropped  ({Message})", host, port, ex.Message);
            _metrics.IncrementFailedConnections();
        }
    }

    private async Task<bool> ShouldProxyDomain(string domain)
    {
        if (_gfwListService != null && _gfwListService.IsInGfwList(domain))
            return true;

        if (_config.Route.DirectDomains.Any(d => domain.EndsWith(d, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (_config.Route.ProxyDomains.Any(d => domain.EndsWith(d, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (_geoIpService != null)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(domain);
                var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 != null)
                    return _geoIpService.ShouldProxy(ipv4, _config.Route.GeoProxy, _config.Route.GeoDirect);
            }
            catch
            {
            }
        }

        return true;
    }

    private async Task<TcpClient> ConnectViaUpstreamProxy(string destHost, int destPort, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_config.Proxy.Host, _config.Proxy.Port, ct);
            var stream = client.GetStream();

            if (_config.Proxy.GetProxyType() == ProxyType.Socks5)
                await Socks5HandshakeAsync(stream, destHost, destPort, ct);
            else
                await HttpConnectHandshakeAsync(stream, destHost, destPort, ct);

            _metrics.IncrementActiveConnections();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<TcpClient> ConnectToUpstreamForHttp(string destHost, int destPort, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_config.Proxy.Host, _config.Proxy.Port, ct);
            if (_config.Proxy.GetProxyType() == ProxyType.Socks5)
            {
                var stream = client.GetStream();
                await Socks5HandshakeAsync(stream, destHost, destPort, ct);
            }

            _metrics.IncrementActiveConnections();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<TcpClient> ConnectDirect(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task Socks5HandshakeAsync(NetworkStream stream, string destHost, int destPort, CancellationToken ct)
    {
        bool hasAuth = !string.IsNullOrEmpty(_config.Proxy.Username);

        byte[] greeting = hasAuth ? [0x05, 0x02, 0x00, 0x02] : [0x05, 0x01, 0x00];
        await stream.WriteAsync(greeting, ct);

        var buf = new byte[512];
        int n = await stream.ReadAsync(buf.AsMemory(0, 2), ct);
        if (n < 2 || buf[0] != 0x05)
            throw new Exception("SOCKS5 handshake failed");

        if (buf[1] == 0x02 && hasAuth)
        {
            var username = Encoding.ASCII.GetBytes(_config.Proxy.Username!);
            var password = Encoding.ASCII.GetBytes(_config.Proxy.Password ?? "");
            var auth = new byte[3 + username.Length + password.Length];
            auth[0] = 0x01;
            auth[1] = (byte)username.Length;
            Buffer.BlockCopy(username, 0, auth, 2, username.Length);
            auth[2 + username.Length] = (byte)password.Length;
            Buffer.BlockCopy(password, 0, auth, 3 + username.Length, password.Length);
            await stream.WriteAsync(auth, ct);

            n = await stream.ReadAsync(buf.AsMemory(0, 2), ct);
            if (n < 2 || buf[1] != 0x00)
                throw new Exception("SOCKS5 authentication failed");
        }

        var hostBytes = Encoding.ASCII.GetBytes(destHost);
        var request = new byte[7 + hostBytes.Length];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x03;
        request[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);
        request[5 + hostBytes.Length] = (byte)(destPort >> 8);
        request[6 + hostBytes.Length] = (byte)(destPort & 0xFF);
        await stream.WriteAsync(request, ct);

        n = await stream.ReadAsync(buf.AsMemory(0, 10), ct);
        if (n < 4 || buf[1] != 0x00)
            throw new Exception($"SOCKS5 CONNECT rejected: 0x{buf[1]:X2}");
    }

    private async Task HttpConnectHandshakeAsync(NetworkStream stream, string destHost, int destPort, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"CONNECT {destHost}:{destPort} HTTP/1.1\r\n");
        sb.Append($"Host: {destHost}:{destPort}\r\n");

        if (!string.IsNullOrEmpty(_config.Proxy.Username))
        {
            var credentials = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_config.Proxy.Username}:{_config.Proxy.Password ?? ""}"));
            sb.Append($"Proxy-Authorization: Basic {credentials}\r\n");
        }

        sb.Append("\r\n");
        var requestBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(requestBytes, ct);

        var buf = new byte[1024];
        int totalRead = 0;
        while (totalRead < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(totalRead, buf.Length - totalRead), ct);
            if (n == 0)
                throw new Exception("HTTP CONNECT: proxy closed the connection");
            totalRead += n;

            if (FindHeaderEnd(buf, totalRead) >= 0)
                break;
        }

        var response = Encoding.ASCII.GetString(buf, 0, totalRead);
        if (!response.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
            !response.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
        {
            var statusLine = response.Split('\r')[0];
            throw new Exception($"HTTP CONNECT rejected: {statusLine}");
        }
    }

    private async Task RelayAsync(NetworkStream clientStream, NetworkStream upstreamStream, CancellationToken ct)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        relayCts.CancelAfter(TimeSpan.FromMinutes(5));

        var clientToUpstream = CopyStreamAsync(clientStream, upstreamStream, "C->S", relayCts.Token);
        var upstreamToClient = CopyStreamAsync(upstreamStream, clientStream, "S->C", relayCts.Token);

        await Task.WhenAny(clientToUpstream, upstreamToClient);
        relayCts.Cancel();
    }

    private async Task CopyStreamAsync(NetworkStream source, NetworkStream dest, string direction, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await source.ReadAsync(buffer, ct);
                if (bytesRead == 0)
                    break;

                await dest.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                if (direction == "C->S")
                    _metrics.AddBytesSent(bytesRead);
                else
                    _metrics.AddBytesReceived(bytesRead);
            }
        }
        catch
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void InitializeBackgroundServices()
    {
        string? httpUrl = _config.Proxy.GetProxyType() == ProxyType.Http
            ? $"http://{_config.Proxy.Host}:{_config.Proxy.Port}"
            : null;

        if (_config.Route.EnableGeo && _geoIpService != null)
        {
            _ = Task.Run(async () =>
            {
                Interlocked.Increment(ref _downloadingCount);
                try
                {
                    await _geoIpService.InitializeAsync(_cts!.Token, httpUrl);
                }
                finally
                {
                    Interlocked.Decrement(ref _downloadingCount);
                }
            });
        }

        if (_config.Route.EnableGfwList && _gfwListService != null)
        {
            _ = Task.Run(async () =>
            {
                Interlocked.Increment(ref _downloadingCount);
                try
                {
                    await _gfwListService.InitializeAsync(_cts!.Token, httpUrl);
                }
                finally
                {
                    Interlocked.Decrement(ref _downloadingCount);
                }
            });
        }
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (int i = 0; i < length - 3; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        }

        return -1;
    }

    private static int FindLineEnd(byte[] data, int length)
    {
        for (int i = 0; i < length - 1; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n')
                return i;
        }

        return -1;
    }

    private static string? ExtractFirstLine(byte[] data, int length)
    {
        var end = FindLineEnd(data, length);
        return end < 0 ? null : Encoding.ASCII.GetString(data, 0, end);
    }
}
