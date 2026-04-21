using System.Text;
using Serilog;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

public class GfwListService
{
    private readonly string _gfwListUrl;
    private readonly string _gfwListPath;
    private HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    internal string ListPath => _gfwListPath;
    internal bool IsInitialized => _initialized;

    public GfwListService(string gfwListUrl, string gfwListPath)
    {
        _gfwListUrl = gfwListUrl;
        _gfwListPath = AppPathResolver.ResolveAppFilePath(gfwListPath);
    }

    public async Task<bool> InitializeAsync(CancellationToken ct = default, ProxyConfig? proxyConfig = null)
    {
        if (_initialized)
        {
            return true;
        }

        if (!File.Exists(_gfwListPath))
        {
            await DownloadGfwListAsync(ct, proxyConfig);
        }

        if (!File.Exists(_gfwListPath))
        {
            Log.Warning("[GFW] List file is missing; GFWList routing is unavailable: {Path}", _gfwListPath);
            return false;
        }

        if (!await ParseGfwListAsync())
        {
            Log.Warning("[GFW] List file did not produce usable rules: {Path}", _gfwListPath);
            return false;
        }

        Log.Information("[GFW] Loaded {Count} rules from {Path}", _domains.Count, _gfwListPath);
        _initialized = true;
        return true;
    }

    internal async Task<bool> HasValidListAsync()
    {
        if (!File.Exists(_gfwListPath))
        {
            return false;
        }

        return await ParseGfwListAsync();
    }

    private async Task DownloadGfwListAsync(CancellationToken ct = default, ProxyConfig? proxyConfig = null)
    {
        var proxyUri = ProxyHttpClientFactory.BuildProxyUri(proxyConfig);
        Log.Information("[GFW] List is missing; downloading {Via}",
            proxyUri != null ? $"via {proxyUri}" : "direct");

        try
        {
            using var client = ProxyHttpClientFactory.Create(proxyConfig, TimeSpan.FromMinutes(5));
            var data = await client.GetByteArrayAsync(_gfwListUrl, ct);
            await File.WriteAllBytesAsync(_gfwListPath, data, ct);

            Log.Information("[GFW] Downloaded list: {Path}", _gfwListPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GFW] Download failed: {Url}", _gfwListUrl);
        }
    }

    private async Task<bool> ParseGfwListAsync()
    {
        try
        {
            var base64 = await File.ReadAllTextAsync(_gfwListPath);
            var bytes = Convert.FromBase64String(base64);
            var content = Encoding.UTF8.GetString(bytes);
            var parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("!", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    continue;
                }

                var domain = ParseRule(trimmed);
                if (!string.IsNullOrEmpty(domain))
                {
                    parsed.Add(domain);
                }
            }

            if (parsed.Count == 0)
            {
                return false;
            }

            _domains = parsed;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GFW] Failed to parse list: {Path}", _gfwListPath);
            return false;
        }
    }

    private static string? ParseRule(string rule)
    {
        if (rule.StartsWith("||", StringComparison.Ordinal))
        {
            return rule.TrimStart('|').Split('^')[0];
        }

        if (rule.StartsWith("|", StringComparison.Ordinal) ||
            rule.StartsWith("@@", StringComparison.Ordinal) ||
            rule.Contains('*', StringComparison.Ordinal) ||
            rule.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        return rule;
    }

    public bool IsInGfwList(string domain)
    {
        if (!_initialized || _domains.Count == 0)
        {
            return false;
        }

        if (_domains.Contains(domain))
        {
            return true;
        }

        foreach (var gfwDomain in _domains)
        {
            if (domain.EndsWith("." + gfwDomain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<bool> ReloadAsync(CancellationToken ct = default, ProxyConfig? proxyConfig = null)
    {
        _domains.Clear();
        _initialized = false;
        return await InitializeAsync(ct, proxyConfig);
    }
}
