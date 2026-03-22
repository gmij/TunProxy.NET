using System.IO.Compression;
using System.Net;
using MaxMind.GeoIP2;

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
    /// 初始化 GeoIP 数据库
    /// </summary>
    public async Task InitializeAsync()
    {
        // 如果数据库不存在，自动下载
        if (!File.Exists(_dbPath))
        {
            await DownloadGeoIpDbAsync();
        }

        try
        {
            _reader = new DatabaseReader(_dbPath);
            Console.WriteLine($"[GEO] GeoIP 数据库加载成功：{_dbPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GEO] GeoIP 数据库加载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 下载 GeoIP 数据库（MaxMind GeoLite2）
    /// </summary>
    private async Task DownloadGeoIpDbAsync()
    {
        Console.WriteLine("[GEO] GeoIP 数据库不存在，开始下载...");
        
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);

            // MaxMind GeoLite2 Country 数据库
            var downloadUrl = "https://github.com/P3TERX/GeoLite.mmdb/raw/download/GeoLite2-Country.mmdb";
            
            var tempPath = _dbPath + ".tmp";
            var data = await client.GetByteArrayAsync(downloadUrl);
            await File.WriteAllBytesAsync(tempPath, data);
            
            // 移动文件
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
            File.Move(tempPath, _dbPath);
            
            Console.WriteLine($"[GEO] GeoIP 数据库下载完成：{_dbPath} ({data.Length / 1024 / 1024} MB)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GEO] GeoIP 数据库下载失败：{ex.Message}");
            Console.WriteLine("[GEO] 请手动下载：https://github.com/P3TERX/GeoLite.mmdb/raw/download/GeoLite2-Country.mmdb");
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
