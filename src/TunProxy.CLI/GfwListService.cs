using System.Text;

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

    public GfwListService(string gfwListUrl, string gfwListPath)
    {
        _gfwListUrl = gfwListUrl;
        _gfwListPath = gfwListPath;
    }

    /// <summary>
    /// 初始化 GFWList
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        // 如果本地文件不存在，下载
        if (!File.Exists(_gfwListPath))
        {
            await DownloadGfwListAsync();
        }

        // 解析 GFWList
        await ParseGfwListAsync();
        
        Console.WriteLine($"[GFW] GFWList 加载完成，共 {_domains.Count} 条规则");
        _initialized = true;
    }

    /// <summary>
    /// 下载 GFWList
    /// </summary>
    private async Task DownloadGfwListAsync()
    {
        Console.WriteLine("[GFW] GFWList 不存在，开始下载...");
        
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(5);
            
            var data = await client.GetByteArrayAsync(_gfwListUrl);
            await File.WriteAllBytesAsync(_gfwListPath, data);
            
            Console.WriteLine($"[GFW] GFWList 下载完成：{_gfwListPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GFW] GFWList 下载失败：{ex.Message}");
            Console.WriteLine($"[GFW] 请手动下载：{_gfwListUrl}");
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
    public async Task ReloadAsync()
    {
        _domains.Clear();
        _initialized = false;
        await InitializeAsync();
    }
}
