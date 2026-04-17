using System.Collections.Concurrent;
using Serilog;
using TunProxy.Core.Route;

namespace TunProxy.CLI;

/// <summary>
/// IP 缓存管理器
/// 管理直连 IP 绕过路由缓存和代理封锁 IP 缓存
/// 从 TunProxyService 中提取，符合 SRP 原则
/// </summary>
public class IpCacheManager
{
    private readonly ConcurrentDictionary<string, int> _directBypassedIps = new();       // 直连 IP 绕过路由状态：0=添加中，1=已确认
    private readonly ConcurrentDictionary<string, bool> _proxyBlockedIps = new();         // 代理封锁的 IP（超时/拒绝），SYN 直接 RST
    private readonly ConcurrentDictionary<string, DateTime> _connectFailedIps = new();     // CONNECT_FAILED IP 临时记录（5分钟内快速失败）
    private readonly ConcurrentDictionary<string, string> _ipHostnameCache = new();       // IP → 域名，用于 CONNECT

    private const string DirectIpCacheFile = "direct_ip_cache.txt";
    private const string BlockedIpCacheFile = "blocked_ip_cache.txt";
    private static string DirectIpCachePath => AppPathResolver.ResolveAppFilePath(DirectIpCacheFile);
    private static string BlockedIpCachePath => AppPathResolver.ResolveAppFilePath(BlockedIpCacheFile);

    private readonly IRouteService? _routeService;

    public IpCacheManager(IRouteService? routeService)
    {
        _routeService = routeService;
    }

    /// <summary>
    /// 启动时加载直连 IP 缓存并批量添加绕过路由
    /// </summary>
    public void LoadAndApplyDirectIpCache()
    {
        if (!File.Exists(DirectIpCachePath)) return;
        try
        {
            var ips = File.ReadAllLines(DirectIpCachePath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Distinct()
                .ToList();
            if (ips.Count == 0) return;

            int added = 0;
            foreach (var ip in ips)
            {
                if (_directBypassedIps.TryAdd(ip, 0))
                {
                    if (_routeService?.AddBypassRoute(ip, 24) == true)
                    {
                        _directBypassedIps.TryUpdate(ip, 1, 0);
                        added++;
                    }
                    else
                        _directBypassedIps.TryRemove(ip, out _);
                }
            }
            Log.Information("直连 IP 缓存加载：{Total} 条记录，成功添加 {Added} 条绕过路由", ips.Count, added);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载直连 IP 缓存失败，将在运行时重建");
        }
    }

    /// <summary>
    /// 启动时加载代理封锁 IP 缓存
    /// </summary>
    public void LoadBlockedIpCache()
    {
        if (!File.Exists(BlockedIpCachePath)) return;
        try
        {
            var ips = File.ReadAllLines(BlockedIpCachePath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Distinct()
                .ToList();
            foreach (var ip in ips)
                _proxyBlockedIps.TryAdd(ip, true);
            if (ips.Count > 0)
                Log.Information("代理封锁 IP 缓存加载：{Count} 条", ips.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载代理封锁 IP 缓存失败");
        }
    }

    /// <summary>
    /// 尝试添加直连 IP 绕过路由（/24 子网），返回是否为首次添加
    /// </summary>
    public bool TryAddDirectBypass(string net24)
    {
        if (!_directBypassedIps.TryAdd(net24, 0)) return false;

        var subnet = net24;
        _ = Task.Run(() =>
        {
            if (_routeService?.AddBypassRoute(subnet, 24) == true)
            {
                _directBypassedIps.TryUpdate(subnet, 1, 0);
                AppendDirectIpCache(subnet);
            }
            else
                _directBypassedIps.TryRemove(subnet, out _);
        });
        return true;
    }

    /// <summary>
    /// IP 是否被代理封锁
    /// </summary>
    public bool IsProxyBlocked(string ip) => _proxyBlockedIps.ContainsKey(ip);

    /// <summary>
    /// 添加代理封锁 IP
    /// </summary>
    public bool TryAddProxyBlocked(string ip)
    {
        if (!_proxyBlockedIps.TryAdd(ip, true)) return false;
        _ = Task.Run(() => AppendBlockedIpCache(ip));
        return true;
    }

    /// <summary>
    /// IP 是否在 CONNECT_FAILED 临时屏蔽中（5分钟内）
    /// </summary>
    public bool IsConnectFailed(string ip)
    {
        return _connectFailedIps.TryGetValue(ip, out var failedAt) &&
            (DateTime.UtcNow - failedAt).TotalMinutes < 5;
    }

    /// <summary>
    /// 记录 CONNECT_FAILED IP
    /// </summary>
    public void RecordConnectFailed(string ip)
    {
        _connectFailedIps[ip] = DateTime.UtcNow;
    }

    /// <summary>
    /// 缓存 IP → 域名映射
    /// </summary>
    public void CacheHostname(string ip, string hostname)
    {
        _ipHostnameCache.TryAdd(ip, hostname);
    }

    /// <summary>
    /// 获取缓存的域名
    /// </summary>
    public string? GetCachedHostname(string ip)
    {
        return _ipHostnameCache.TryGetValue(ip, out var hostname) ? hostname : null;
    }

    /// <summary>
    /// 清理停止时的直连 IP 绕过路由
    /// </summary>
    public void CleanupBypassRoutes()
    {
        foreach (var ip in _directBypassedIps.Keys)
        {
            _routeService?.RemoveBypassRoute(ip);
        }
        if (_directBypassedIps.Count > 0)
            Log.Information("直连 IP 绕过路由已删除（{Count} 条）", _directBypassedIps.Count);
    }

    private static void AppendDirectIpCache(string ip)
    {
        try { File.AppendAllText(DirectIpCachePath, ip + Environment.NewLine); }
        catch { }
    }

    private static void AppendBlockedIpCache(string ip)
    {
        try { File.AppendAllText(BlockedIpCachePath, ip + Environment.NewLine); }
        catch { }
    }

    /// <summary>获取 IP→域名 缓存快照（供 Dashboard 展示）</summary>
    public IReadOnlyDictionary<string, string> GetHostnameCacheSnapshot() =>
        new Dictionary<string, string>(_ipHostnameCache);

    /// <summary>获取直连 IP 列表快照</summary>
    public IReadOnlyList<string> GetDirectIpSnapshot() =>
        _directBypassedIps.Keys.ToList();
}
