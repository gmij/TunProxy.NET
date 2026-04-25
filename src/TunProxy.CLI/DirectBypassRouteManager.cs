using System.Collections.Concurrent;
using System.Net;
using Serilog;
using TunProxy.Core.Route;

namespace TunProxy.CLI;

internal sealed class DirectBypassRouteManager
{
    private const int DefaultMaxRouteCount = 512;
    private readonly IRouteService? _routeService;
    private readonly TimeSpan _idleTimeout;
    private readonly int _maxRouteCount;
    private readonly ConcurrentDictionary<string, DirectBypassRouteEntry> _routes =
        new(StringComparer.OrdinalIgnoreCase);

    public DirectBypassRouteManager(
        IRouteService? routeService,
        TimeSpan? idleTimeout = null,
        int maxRouteCount = DefaultMaxRouteCount)
    {
        if (maxRouteCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRouteCount), "Maximum route count must be greater than zero.");
        }

        _routeService = routeService;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(10);
        _maxRouteCount = maxRouteCount;
    }

    public int Count => _routes.Count;

    public List<string> GetSnapshot() =>
        _routes.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public Task EnsureRouteAsync(string destIP, RouteDecision decision, CancellationToken ct)
    {
        if (_routeService == null)
        {
            return Task.CompletedTask;
        }

        ct.ThrowIfCancellationRequested();
        var entry = _routes.GetOrAdd(
            destIP,
            static (ip, state) => new DirectBypassRouteEntry(
                () => state.manager.AddRouteAsync(ip, state.decision),
                DateTime.UtcNow),
            (manager: this, decision));

        entry.Touch();
        return AwaitRouteAsync(destIP, entry, ct);
    }

    public void Touch(string? destIP)
    {
        if (!string.IsNullOrWhiteSpace(destIP) &&
            _routes.TryGetValue(destIP, out var entry))
        {
            entry.Touch();
        }
    }

    public void CleanupExpired(DateTime? nowUtc = null)
    {
        if (_routeService == null)
        {
            _routes.Clear();
            return;
        }

        var now = nowUtc ?? DateTime.UtcNow;
        foreach (var (ip, entry) in _routes)
        {
            if (now - entry.LastUsedUtc <= _idleTimeout)
            {
                continue;
            }

            if (entry.AddTask.IsValueCreated && !entry.AddTask.Value.IsCompleted)
            {
                continue;
            }

            if (!TryRemoveEntry(ip, entry))
            {
                continue;
            }

            if (entry.AddTask.IsValueCreated &&
                entry.AddTask.Value.Status == TaskStatus.RanToCompletion &&
                entry.AddTask.Value.Result)
            {
                RemoveTrackedRoute(ip, "expired");
            }
        }
    }

    private async Task AwaitRouteAsync(string destIP, DirectBypassRouteEntry entry, CancellationToken ct)
    {
        try
        {
            var ok = await entry.AddTask.Value;
            ct.ThrowIfCancellationRequested();
            if (!ok)
            {
                TryRemoveEntry(destIP, entry);
                return;
            }

            TrimToLimit(destIP);
        }
        catch
        {
            TryRemoveEntry(destIP, entry);
            throw;
        }
    }

    private bool TryRemoveEntry(string ip, DirectBypassRouteEntry entry)
    {
        return _routes.TryGetValue(ip, out var current) &&
               ReferenceEquals(current, entry) &&
               _routes.TryRemove(ip, out _);
    }

    private void TrimToLimit(string protectedIp)
    {
        if (_routeService == null || _routes.Count <= _maxRouteCount)
        {
            return;
        }

        var removable = _routes
            .Where(item =>
                !item.Key.Equals(protectedIp, StringComparison.OrdinalIgnoreCase) &&
                item.Value.AddTask.IsValueCreated &&
                item.Value.AddTask.Value.Status == TaskStatus.RanToCompletion)
            .OrderBy(item => item.Value.LastUsedUtc)
            .ToList();

        foreach (var (ip, entry) in removable)
        {
            if (_routes.Count <= _maxRouteCount)
            {
                return;
            }

            if (!TryRemoveEntry(ip, entry))
            {
                continue;
            }

            if (entry.AddTask.Value.Result)
            {
                RemoveTrackedRoute(ip, "evicted");
            }
        }
    }

    private void RemoveTrackedRoute(string ip, string reason)
    {
        var removed = _routeService?.RemoveTrackedBypassRoute(ip) == true;
        Log.Information("[ROUTE] Direct bypass route {Reason}: {IP} (removed={Removed})", reason, ip, removed);
    }

    private Task<bool> AddRouteAsync(string destIP, RouteDecision decision) =>
        Task.Run(() =>
        {
            var ok = _routeService?.AddBypassRoute(destIP) == true;
            Log.Information("[ROUTE] Direct bypass route {Status}: {IP} ({Reason})",
                ok ? "ready" : "failed",
                destIP,
                decision.Reason);
            return ok;
        });

    private sealed class DirectBypassRouteEntry
    {
        private long _lastUsedTicks;

        public DirectBypassRouteEntry(Func<Task<bool>> addRoute, DateTime nowUtc)
        {
            AddTask = new Lazy<Task<bool>>(addRoute, LazyThreadSafetyMode.ExecutionAndPublication);
            _lastUsedTicks = nowUtc.Ticks;
        }

        public Lazy<Task<bool>> AddTask { get; }

        public DateTime LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);

        public void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
    }
}
