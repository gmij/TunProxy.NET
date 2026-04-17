using System.Text;
using Serilog;

namespace TunProxy.CLI;

/// <summary>
/// GFWList 服务
/// </summary>
public class GfwListService
{
    private readonly string _gfwListUrl;
    private readonly string _gfwListPath;
    private HashSet<string> _domains = new();
    private bool _initialized;

    internal string ListPath => _gfwListPath;

    public GfwListService(string gfwListUrl, string gfwListPath)
    {
        _gfwListUrl = gfwListUrl;
        _gfwListPath = AppPathResolver.ResolveAppFilePath(gfwListPath);
    }

    /// <summary>
    /// 初始化 GFWList（若不存在则自动下载，需要 TUN 代理已运行）
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default, string? proxyUrl = null)
    {
        if (_initialized)
            return;

        if (!File.Exists(_gfwListPath))
        {
            await DownloadGfwListAsync(ct, proxyUrl);
        }

        if (!File.Exists(_gfwListPath))
        {
            Log.Warning("[GFW] 列表文件不存在，GFWList 路由不可用：{Path}", _gfwListPath);
            return;
        }

        await ParseGfwListAsync();
        Log.Information("[GFW] 加载完成，共 {Count} 条规则", _domains.Count);
        _initialized = true;
    }

    /// <summary>
    /// 下载 GFWList（通过代理，绕过系统 DNS 限制）
    /// </summary>
    private async Task DownloadGfwListAsync(CancellationToken ct = default, string? proxyUrl = null)
    {
        Log.Information("[GFW] 列表不存在，开始下载...{Via}",
            proxyUrl != null ? $"（经由 {proxyUrl}）" : "（直连）");

        try
        {
            using var handler = new HttpClientHandler();
            if (proxyUrl != null)
            {
                handler.Proxy = new System.Net.WebProxy(proxyUrl);
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };

            var data = await client.GetByteArrayAsync(_gfwListUrl, ct);
            await File.WriteAllBytesAsync(_gfwListPath, data, ct);

            Log.Information("[GFW] 下载完成：{Path}", _gfwListPath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GFW] 下载失败，请手动下载：{Url}", _gfwListUrl);
        }
    }

    /// <summary>
    /// 解析 GFWList（Base64 编码）
    /// </summary>
    private async Task ParseGfwListAsync()
    {
        try
        {
            var base64 = await File.ReadAllTextAsync(_gfwListPath);
            var bytes = Convert.FromBase64String(base64);
            var content = Encoding.UTF8.GetString(bytes);
            
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                // 跳过注释和空行
                var trimmed = line.Trim();
                if (trimmed.StartsWith("!") || trimmed.StartsWith("["))
                    continue;
                
                // 解析规则
                var domain = ParseRule(trimmed);
                if (!string.IsNullOrEmpty(domain))
                    _domains.Add(domain.ToLower());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GFW] GFWList 解析失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 解析单条规则
    /// </summary>
    private static string? ParseRule(string rule)
    {
        // 处理各种规则格式
        if (rule.StartsWith("||"))
        {
            // ||domain.com^
            return rule.TrimStart('|').Split('^')[0];
        }
        else if (rule.StartsWith("|"))
        {
            // |http://...
            return null; // URL 规则，跳过
        }
        else if (rule.StartsWith("@@"))
        {
            // @@ 白名单，跳过
            return null;
        }
        else if (rule.Contains("*"))
        {
            // 通配符规则，跳过
            return null;
        }
        else if (rule.Contains("/"))
        {
            // 正则规则，跳过
            return null;
        }
        else
        {
            // 纯域名
            return rule;
        }
    }

    /// <summary>
    /// 判断域名是否在 GFWList 中
    /// </summary>
    public bool IsInGfwList(string domain)
    {
        if (!_initialized || _domains.Count == 0)
            return false;

        // 完全匹配
        if (_domains.Contains(domain.ToLower()))
            return true;

        // 子域名匹配
        foreach (var gfwDomain in _domains)
        {
            if (domain.EndsWith("." + gfwDomain))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 重新加载 GFWList
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default, string? proxyUrl = null)
    {
        _domains.Clear();
        _initialized = false;
        await InitializeAsync(ct, proxyUrl);
    }
}
