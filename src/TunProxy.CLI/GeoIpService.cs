using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net;
using MaxMind.GeoIP2;
using Serilog;

namespace TunProxy.CLI;

/// <summary>
/// GeoIP 服务
/// </summary>
public class GeoIpService : IDisposable
{
    private readonly string _dbPath;
    private DatabaseReader? _reader;
    private bool _disposed;

    public GeoIpService(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>
    /// 初始化 GeoIP 数据库（若不存在则自动下载，需要 TUN 代理已运行）
    /// </summary>
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors,
        typeof(MaxMind.Db.Metadata))]
    public async Task InitializeAsync(CancellationToken ct = default, string? proxyUrl = null)
    {
        // 如果数据库不存在，通过代理自动下载
        if (!File.Exists(_dbPath))
        {
            await DownloadGeoIpDbAsync(ct, proxyUrl);
        }

        if (!File.Exists(_dbPath))
        {
            Log.Warning("[GEO] 数据库文件不存在，GEO 路由不可用：{Path}", _dbPath);
            return;
        }

        try
        {
            _reader = new DatabaseReader(_dbPath);
            Log.Information("[GEO] 数据库加载成功：{Path}", _dbPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GEO] 数据库加载失败：{Path}", _dbPath);
        }
    }

    /// <summary>
    /// 下载 GeoIP 数据库（通过代理，绕过系统 DNS 限制）
    /// </summary>
    private async Task DownloadGeoIpDbAsync(CancellationToken ct = default, string? proxyUrl = null)
    {
        Log.Information("[GEO] 数据库不存在，开始下载...{Via}",
            proxyUrl != null ? $"（经由 {proxyUrl}）" : "（直连）");

        try
        {
            using var handler = new HttpClientHandler();
            if (proxyUrl != null)
            {
                handler.Proxy = new System.Net.WebProxy(proxyUrl);
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

            var downloadUrl = "https://github.com/P3TERX/GeoLite.mmdb/raw/download/GeoLite2-Country.mmdb";

            var tempPath = _dbPath + ".tmp";
            var data = await client.GetByteArrayAsync(downloadUrl, ct);
            await File.WriteAllBytesAsync(tempPath, data, ct);

            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
            File.Move(tempPath, _dbPath);

            Log.Information("[GEO] 数据库下载完成：{Path}（{Size} MB）", _dbPath, data.Length / 1024 / 1024);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GEO] 数据库下载失败，请手动下载放到程序目录：https://github.com/P3TERX/GeoLite.mmdb/raw/download/GeoLite2-Country.mmdb");
        }
    }

    /// <summary>
    /// 查询 IP 所属国家代码
    /// </summary>
    public string? GetCountryCode(IPAddress ipAddress)
    {
        if (_reader == null)
            return null;

        try
        {
            var response = _reader.Country(ipAddress);
            return response.Country.IsoCode;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 判断 IP 是否应该走代理
    /// </summary>
    public bool ShouldProxy(IPAddress ipAddress, List<string> geoProxy, List<string> geoDirect)
    {
        var country = GetCountryCode(ipAddress);

        if (string.IsNullOrEmpty(country))
            return true; // 无法判断，默认走代理（安全起见）

        // 如果在直连列表中，不走代理
        if (geoDirect.Contains(country, StringComparer.OrdinalIgnoreCase))
            return false;

        // 如果在代理列表中，走代理
        if (geoProxy.Contains(country, StringComparer.OrdinalIgnoreCase))
            return true;

        // 默认行为：如果有直连列表但不在其中，走代理；如果没有直连列表，走代理
        return true;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _reader?.Dispose();
            _disposed = true;
        }
    }
}
