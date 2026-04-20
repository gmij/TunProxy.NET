using System.Collections.Concurrent;
using Serilog;
using TunProxy.Core.Route;

namespace TunProxy.CLI;

/// <summary>
/// Tracks runtime IP routing state and observed IP-to-hostname mappings.
/// </summary>
public class IpCacheManager
{
    private readonly ConcurrentDictionary<string, int> _directBypassedIps = new();
    private readonly ConcurrentDictionary<string, bool> _proxyBlockedIps = new();
    private readonly ConcurrentDictionary<string, DateTime> _connectFailedIps = new();
    private readonly ConcurrentDictionary<string, HostnameCacheEntry> _ipHostnameCache = new();

    private const string DirectIpCacheFile = "direct_ip_cache.txt";
    private const string BlockedIpCacheFile = "blocked_ip_cache.txt";
    private static string DirectIpCachePath => AppPathResolver.ResolveAppFilePath(DirectIpCacheFile);
    private static string BlockedIpCachePath => AppPathResolver.ResolveAppFilePath(BlockedIpCacheFile);

    private readonly IRouteService? _routeService;

    public IpCacheManager(IRouteService? routeService)
    {
        _routeService = routeService;
    }

    public void RetireDirectIpCache()
    {
        if (!File.Exists(DirectIpCachePath))
        {
            return;
        }

        try
        {
            var ips = File.ReadAllLines(DirectIpCachePath)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0 && !line.StartsWith('#'))
                .Distinct()
                .ToList();

            foreach (var ip in ips)
            {
                _routeService?.RemoveBypassRoute(ip);
                _directBypassedIps.TryRemove(ip, out _);
            }

            File.Delete(DirectIpCachePath);
            if (ips.Count > 0)
            {
                Log.Information("[ROUTE] Retired legacy direct-route cache entries: {Count}", ips.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ROUTE] Failed to retire legacy direct-route cache");
        }
    }

    public void LoadAndApplyDirectIpCache()
    {
        if (!File.Exists(DirectIpCachePath))
        {
            return;
        }

        try
        {
            var ips = File.ReadAllLines(DirectIpCachePath)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0 && !line.StartsWith('#'))
                .Distinct()
                .ToList();
            if (ips.Count == 0)
            {
                return;
            }

            var added = 0;
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
                    {
                        _directBypassedIps.TryRemove(ip, out _);
                    }
                }
            }

            Log.Information("[ROUTE] Loaded direct IP cache: {Total} entries, {Added} routes applied", ips.Count, added);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ROUTE] Failed to load direct IP cache; it will be rebuilt at runtime");
        }
    }

    public void LoadBlockedIpCache()
    {
        if (!File.Exists(BlockedIpCachePath))
        {
            return;
        }

        try
        {
            var ips = File.ReadAllLines(BlockedIpCachePath)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0 && !line.StartsWith('#'))
                .Distinct()
                .ToList();

            foreach (var ip in ips)
            {
                _proxyBlockedIps.TryAdd(ip, true);
            }

            if (ips.Count > 0)
            {
                Log.Information("[ROUTE] Loaded proxy-blocked IP cache: {Count} entries", ips.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ROUTE] Failed to load proxy-blocked IP cache");
        }
    }

    public bool TryAddDirectBypass(string net24)
    {
        if (!_directBypassedIps.TryAdd(net24, 0))
        {
            return false;
        }

        _ = Task.Run(() =>
        {
            if (_routeService?.AddBypassRoute(net24, 24) == true)
            {
                _directBypassedIps.TryUpdate(net24, 1, 0);
                AppendDirectIpCache(net24);
            }
            else
            {
                _directBypassedIps.TryRemove(net24, out _);
            }
        });
        return true;
    }

    public bool IsProxyBlocked(string ip) => _proxyBlockedIps.ContainsKey(ip);

    public bool TryAddProxyBlocked(string ip)
    {
        if (!_proxyBlockedIps.TryAdd(ip, true))
        {
            return false;
        }

        _ = Task.Run(() => AppendBlockedIpCache(ip));
        return true;
    }

    public bool IsConnectFailed(string ip) =>
        _connectFailedIps.TryGetValue(ip, out var failedAt) &&
        (DateTime.UtcNow - failedAt).TotalMinutes < 5;

    public void RecordConnectFailed(string ip)
    {
        _connectFailedIps[ip] = DateTime.UtcNow;
    }

    public void CacheHostname(string ip, string hostname)
    {
        if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(hostname))
        {
            return;
        }

        var normalized = hostname.Trim().TrimEnd('.');
        var now = DateTime.UtcNow;
        _ipHostnameCache.AddOrUpdate(
            ip,
            _ => new HostnameCacheEntry(normalized, now, 1),
            (_, existing) => existing with
            {
                Hostname = normalized,
                LastSeenUtc = now,
                SeenCount = existing.SeenCount + 1
            });
    }

    public string? GetCachedHostname(string ip)
    {
        return _ipHostnameCache.TryGetValue(ip, out var entry) ? entry.Hostname : null;
    }

    public void CleanupBypassRoutes()
    {
        foreach (var ip in _directBypassedIps.Keys)
        {
            _routeService?.RemoveBypassRoute(ip);
        }

        if (_directBypassedIps.Count > 0)
        {
            Log.Information("[ROUTE] Removed direct bypass routes: {Count}", _directBypassedIps.Count);
        }
    }

    public IReadOnlyDictionary<string, string> GetHostnameCacheSnapshot() =>
        _ipHostnameCache
            .OrderByDescending(static item => item.Value.LastSeenUtc)
            .ToDictionary(static item => item.Key, static item => item.Value.Hostname);

    public IReadOnlyList<string> GetDirectIpSnapshot() =>
        _directBypassedIps.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static void AppendDirectIpCache(string ip)
    {
        try
        {
            File.AppendAllText(DirectIpCachePath, ip + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static void AppendBlockedIpCache(string ip)
    {
        try
        {
            File.AppendAllText(BlockedIpCachePath, ip + Environment.NewLine);
        }
        catch
        {
        }
    }
}

internal sealed record HostnameCacheEntry(string Hostname, DateTime LastSeenUtc, long SeenCount);
