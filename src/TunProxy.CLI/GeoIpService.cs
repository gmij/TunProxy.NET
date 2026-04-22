using System.Diagnostics.CodeAnalysis;
using System.Net;
using MaxMind.GeoIP2;
using Serilog;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

public class GeoIpService : IDisposable
{
    private readonly string _dbPath;
    private DatabaseReader? _reader;
    private bool _disposed;
    private long _emptyLookupLogCount;
    private long _failedLookupLogCount;

    internal string DatabasePath => _dbPath;
    internal bool IsInitialized => _reader != null;

    public GeoIpService(string dbPath)
    {
        _dbPath = AppPathResolver.ResolveAppFilePath(dbPath);
    }

    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors,
        typeof(MaxMind.Db.Metadata))]
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.NonPublicConstructors |
        DynamicallyAccessedMemberTypes.PublicProperties |
        DynamicallyAccessedMemberTypes.NonPublicProperties,
        typeof(MaxMind.GeoIP2.Responses.CountryResponse))]
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.NonPublicConstructors |
        DynamicallyAccessedMemberTypes.PublicProperties |
        DynamicallyAccessedMemberTypes.NonPublicProperties,
        typeof(MaxMind.GeoIP2.Model.Country))]
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.NonPublicConstructors |
        DynamicallyAccessedMemberTypes.PublicProperties |
        DynamicallyAccessedMemberTypes.NonPublicProperties,
        typeof(MaxMind.GeoIP2.Model.Continent))]
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.NonPublicConstructors |
        DynamicallyAccessedMemberTypes.PublicProperties |
        DynamicallyAccessedMemberTypes.NonPublicProperties,
        typeof(MaxMind.GeoIP2.Model.MaxMind))]
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.NonPublicConstructors |
        DynamicallyAccessedMemberTypes.PublicProperties |
        DynamicallyAccessedMemberTypes.NonPublicProperties,
        typeof(MaxMind.GeoIP2.Model.RepresentedCountry))]
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.NonPublicConstructors |
        DynamicallyAccessedMemberTypes.PublicProperties |
        DynamicallyAccessedMemberTypes.NonPublicProperties,
        typeof(MaxMind.GeoIP2.Model.Traits))]
    public async Task<bool> InitializeAsync(
        CancellationToken ct = default,
        ProxyConfig? proxyConfig = null,
        bool downloadIfMissing = true)
    {
        if (!File.Exists(_dbPath) && downloadIfMissing)
        {
            await DownloadGeoIpDbAsync(ct, proxyConfig);
        }

        if (!File.Exists(_dbPath))
        {
            Log.Warning("[GEO] Database file is missing; GeoIP routing is disabled: {Path}", _dbPath);
            return false;
        }

        try
        {
            _reader = new DatabaseReader(_dbPath);
            var metadata = _reader.Metadata;
            Log.Information(
                "[GEO] Database loaded: {Path} ({Size} bytes), type={Type}, ipVersion={IPVersion}, build={BuildDate}",
                _dbPath,
                new FileInfo(_dbPath).Length,
                metadata.DatabaseType,
                metadata.IPVersion,
                metadata.BuildDate);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GEO] Failed to load database: {Path}", _dbPath);
            return false;
        }
    }

    internal bool HasValidDatabase()
    {
        if (!File.Exists(_dbPath))
        {
            return false;
        }

        try
        {
            using var reader = new DatabaseReader(_dbPath);
            _ = reader.Metadata;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GEO] Database validation failed: {Path}", _dbPath);
            return false;
        }
    }

    private async Task DownloadGeoIpDbAsync(CancellationToken ct = default, ProxyConfig? proxyConfig = null)
    {
        var proxyUri = ProxyHttpClientFactory.BuildProxyUri(proxyConfig);
        Log.Information("[GEO] Database is missing; downloading... {Via}",
            proxyUri != null ? $"via {proxyUri}" : "direct");

        try
        {
            using var client = ProxyHttpClientFactory.Create(proxyConfig, TimeSpan.FromMinutes(10));

            const string downloadUrl = "https://github.com/P3TERX/GeoLite.mmdb/raw/download/GeoLite2-Country.mmdb";

            var tempPath = _dbPath + ".tmp";
            var data = await client.GetByteArrayAsync(downloadUrl, ct);
            await File.WriteAllBytesAsync(tempPath, data, ct);

            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }

            File.Move(tempPath, _dbPath);

            Log.Information("[GEO] Database downloaded: {Path} ({Size} MB)", _dbPath, data.Length / 1024 / 1024);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "[GEO] Database download failed. Please download it manually: https://github.com/P3TERX/GeoLite.mmdb/raw/download/GeoLite2-Country.mmdb");
        }
    }

    public string? GetCountryCode(IPAddress ipAddress)
    {
        if (_reader == null)
        {
            LogGeoLookupIssue(
                ref _failedLookupLogCount,
                "[GEO] Lookup skipped because database reader is not initialized: {IP}",
                ipAddress);
            return null;
        }

        try
        {
            if (!_reader.TryCountry(ipAddress, out var response))
            {
                LogGeoLookupIssue(
                    ref _emptyLookupLogCount,
                    "[GEO] Lookup found no record: {IP}",
                    ipAddress);
                return null;
            }

            var country = FirstNonEmpty(
                response?.Country?.IsoCode,
                response?.RegisteredCountry?.IsoCode,
                response?.RepresentedCountry?.IsoCode);
            if (country == null)
            {
                LogGeoLookupIssue(
                    ref _emptyLookupLogCount,
                    "[GEO] Lookup returned no country: {IP}",
                    ipAddress);
            }

            return country;
        }
        catch (Exception ex)
        {
            LogGeoLookupFailure(ipAddress, ex);
            return null;
        }
    }

    public bool ShouldProxy(IPAddress ipAddress, List<string> geoProxy, List<string> geoDirect)
    {
        var country = GetCountryCode(ipAddress);
        return RouteDecisionService.ShouldProxyByGeo(country, geoProxy, geoDirect);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static void LogGeoLookupIssue(ref long counter, string messageTemplate, IPAddress ipAddress)
    {
        var count = Interlocked.Increment(ref counter);
        if (count <= 10 || count % 1000 == 0)
        {
            Log.Warning(messageTemplate + " (count={Count})", ipAddress, count);
        }
    }

    private void LogGeoLookupFailure(IPAddress ipAddress, Exception ex)
    {
        var count = Interlocked.Increment(ref _failedLookupLogCount);
        if (count <= 10 || count % 1000 == 0)
        {
            Log.Warning(
                ex,
                "[GEO] Lookup failed: {IP}, database={Path} (count={Count})",
                ipAddress,
                _dbPath,
                count);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _reader?.Dispose();
        _disposed = true;
    }
}
