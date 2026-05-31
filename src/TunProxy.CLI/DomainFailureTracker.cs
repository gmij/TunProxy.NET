using System.Collections.Concurrent;

namespace TunProxy.CLI;

internal sealed class DomainFailureTracker
{
    internal const int DefaultDirectFailureThreshold = 3;
    internal static readonly TimeSpan DefaultFailureWindow = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan DefaultProxyFallbackTtl = TimeSpan.FromMinutes(15);

    private readonly int _directFailureThreshold;
    private readonly TimeSpan _failureWindow;
    private readonly TimeSpan _proxyFallbackTtl;
    private readonly Func<DateTime> _getUtcNow;
    private readonly ConcurrentDictionary<string, DomainFailureEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public DomainFailureTracker(
        int directFailureThreshold = DefaultDirectFailureThreshold,
        TimeSpan? failureWindow = null,
        TimeSpan? proxyFallbackTtl = null,
        Func<DateTime>? getUtcNow = null)
    {
        if (directFailureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(directFailureThreshold), "Threshold must be greater than zero.");
        }

        _directFailureThreshold = directFailureThreshold;
        _failureWindow = failureWindow ?? DefaultFailureWindow;
        _proxyFallbackTtl = proxyFallbackTtl ?? DefaultProxyFallbackTtl;
        _getUtcNow = getUtcNow ?? (() => DateTime.UtcNow);
    }

    public bool IsProxyFallbackActive(string? domain)
    {
        var normalized = RouteDecisionService.NormalizeDomain(domain);
        if (normalized == null || !_entries.TryGetValue(normalized, out var entry))
        {
            return false;
        }

        var now = _getUtcNow();
        if (entry.ProxyFallbackUntilUtc > now)
        {
            return true;
        }

        if (entry.FailureWindowStartedUtc + _failureWindow <= now)
        {
            _entries.TryRemove(normalized, out _);
        }

        return false;
    }

    public DomainFailureRecordResult RecordDirectFailure(string? domain, string reason)
    {
        var normalized = RouteDecisionService.NormalizeDomain(domain);
        if (normalized == null)
        {
            return DomainFailureRecordResult.Ignored;
        }

        var now = _getUtcNow();
        var previousWasActive = IsProxyFallbackActive(normalized);
        var entry = _entries.AddOrUpdate(
            normalized,
            _ => CreateFailureEntry(now, reason, 1),
            (_, current) =>
            {
                var count = current.FailureWindowStartedUtc + _failureWindow <= now
                    ? 1
                    : current.DirectFailureCount + 1;
                var windowStartedUtc = count == 1 ? now : current.FailureWindowStartedUtc;
                return CreateFailureEntry(now, reason, count, windowStartedUtc, current.ProxyFallbackUntilUtc);
            });

        var isActive = entry.ProxyFallbackUntilUtc > now;
        return new DomainFailureRecordResult(
            normalized,
            entry.DirectFailureCount,
            _directFailureThreshold,
            isActive,
            isActive && !previousWasActive,
            isActive ? entry.ProxyFallbackUntilUtc : null);
    }

    public void RecordDirectSuccess(string? domain)
    {
        var normalized = RouteDecisionService.NormalizeDomain(domain);
        if (normalized != null)
        {
            _entries.TryRemove(normalized, out _);
        }
    }

    public void CleanupExpired()
    {
        var now = _getUtcNow();
        foreach (var (domain, entry) in _entries)
        {
            if (entry.ProxyFallbackUntilUtc <= now &&
                entry.FailureWindowStartedUtc + _failureWindow <= now)
            {
                _entries.TryRemove(domain, out _);
            }
        }
    }

    private DomainFailureEntry CreateFailureEntry(
        DateTime now,
        string reason,
        int count,
        DateTime? windowStartedUtc = null,
        DateTime? existingFallbackUntilUtc = null)
    {
        var fallbackUntilUtc = existingFallbackUntilUtc > now
            ? existingFallbackUntilUtc.Value
            : count >= _directFailureThreshold
                ? now.Add(_proxyFallbackTtl)
                : DateTime.MinValue;

        return new DomainFailureEntry(
            count,
            windowStartedUtc ?? now,
            now,
            fallbackUntilUtc,
            reason);
    }
}

internal sealed record DomainFailureRecordResult(
    string? Domain,
    int DirectFailureCount,
    int DirectFailureThreshold,
    bool IsProxyFallbackActive,
    bool ActivatedProxyFallback,
    DateTime? ProxyFallbackUntilUtc)
{
    public static DomainFailureRecordResult Ignored { get; } =
        new(null, 0, 0, false, false, null);
}

internal sealed record DomainFailureEntry(
    int DirectFailureCount,
    DateTime FailureWindowStartedUtc,
    DateTime LastFailureUtc,
    DateTime ProxyFallbackUntilUtc,
    string LastReason);
