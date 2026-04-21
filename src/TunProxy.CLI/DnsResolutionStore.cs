using System.Collections.Concurrent;
using System.Net;
using Serilog;
using TunProxy.Core.Dns;

namespace TunProxy.CLI;

/// <summary>
/// Tracks DNS response cache entries and observed IP-to-hostname mappings.
/// </summary>
public class DnsResolutionStore
{
    private readonly ConcurrentDictionary<DnsCacheKey, DnsCacheEntry> _dnsCache = new();
    private readonly ConcurrentDictionary<string, DnsObservedHostnameEntry> _observedHostnames =
        new(StringComparer.OrdinalIgnoreCase);

    private long _cacheHits;
    private long _cacheMisses;

    private static readonly TimeSpan DnsCacheIdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ObservedHostnameIdleTimeout = TimeSpan.FromMinutes(10);

    public long CacheHits => Interlocked.Read(ref _cacheHits);

    public long CacheMisses => Interlocked.Read(ref _cacheMisses);

    public int CacheEntryCount
    {
        get
        {
            var now = DateTime.UtcNow;
            return _dnsCache.Values.Sum(entry => entry.Answers.Count(answer => IsDnsCacheAnswerUsable(answer, now)));
        }
    }

    public void CleanupExpired(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        CleanupDnsCache(now);
        CleanupObservedHostnames(now);
    }

    public IReadOnlyList<DnsCacheRecord> GetCacheSnapshot()
    {
        var now = DateTime.UtcNow;
        return GetCacheSnapshot(now);
    }

    public IReadOnlyList<DnsResolutionSnapshot> GetResolutionSnapshot(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var dnsRecords = GetCacheSnapshot(now);
        var dnsByKey = dnsRecords
            .GroupBy(static record => MakeRecordKey(record.Hostname, record.IpAddress), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(record => record.LastActiveUtc).First(),
                StringComparer.OrdinalIgnoreCase);
        var records = new List<DnsResolutionSnapshot>(_observedHostnames.Count + dnsByKey.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (ip, observed) in _observedHostnames)
        {
            if (!IsObservedHostnameUsable(observed, now))
            {
                continue;
            }

            var key = MakeRecordKey(observed.Hostname, ip);
            dnsByKey.TryGetValue(key, out var dnsRecord);
            seen.Add(key);
            records.Add(new DnsResolutionSnapshot(
                observed.Hostname,
                ip,
                observed.Route,
                observed.Reason,
                observed.LastSeenUtc,
                observed.SeenCount,
                dnsRecord != null,
                dnsRecord?.LastActiveUtc,
                dnsRecord?.ExpiresUtc));
        }

        foreach (var dnsRecord in dnsRecords)
        {
            var key = MakeRecordKey(dnsRecord.Hostname, dnsRecord.IpAddress);
            if (seen.Contains(key))
            {
                continue;
            }

            records.Add(new DnsResolutionSnapshot(
                dnsRecord.Hostname,
                dnsRecord.IpAddress,
                "UNKNOWN",
                "DNS",
                dnsRecord.LastActiveUtc,
                0,
                true,
                dnsRecord.LastActiveUtc,
                dnsRecord.ExpiresUtc));
        }

        return records
            .OrderBy(static record => record.Hostname, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static record => record.Route)
            .ThenBy(static record => record.Reason, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static record => record.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal bool TryBuildCachedDnsResponse(DnsPacket query, out byte[] response, DateTime? nowUtc = null)
    {
        response = [];

        if (query.Questions.Count != 1)
        {
            return false;
        }

        var question = query.Questions[0];
        if (!TryCreateDnsCacheKey(question.Name, question.Type, question.Class, out var key))
        {
            return false;
        }

        if (!_dnsCache.TryGetValue(key, out var entry))
        {
            Interlocked.Increment(ref _cacheMisses);
            return false;
        }

        var now = nowUtc ?? DateTime.UtcNow;
        CleanupDnsCache(now);
        var answers = entry.Answers
            .Where(answer => IsDnsCacheAnswerUsable(answer, now))
            .Select(answer => answer with { LastActiveUtc = now })
            .ToList();

        if (answers.Count != entry.Answers.Count)
        {
            if (answers.Count == 0)
            {
                _dnsCache.TryRemove(key, out _);
            }
            else
            {
                _dnsCache[key] = entry with { Answers = answers };
            }
        }
        else
        {
            _dnsCache[key] = entry with { Answers = answers };
        }

        var dnsAnswers = answers
            .Select(answer => new DnsAnswer
            {
                Name = question.Name,
                Type = question.Type,
                Class = question.Class,
                TTL = GetRemainingTtlSeconds(answer.ExpiresUtc, now),
                Data = answer.Data.ToArray()
            })
            .ToList();

        if (dnsAnswers.Count == 0)
        {
            _dnsCache.TryRemove(key, out _);
            Interlocked.Increment(ref _cacheMisses);
            return false;
        }

        response = new DnsPacket
        {
            TransactionId = query.TransactionId,
            Flags = new DnsFlags(BuildCachedResponseFlags(query.Flags.Value)),
            Questions =
            [
                new DnsQuestion
                {
                    Name = question.Name,
                    Type = question.Type,
                    Class = question.Class
                }
            ],
            Answers = dnsAnswers
        }.Build();

        Interlocked.Increment(ref _cacheHits);
        return true;
    }

    internal void StoreDnsResponseInCache(DnsPacket response, DateTime? nowUtc = null)
    {
        if (response.Questions.Count == 0 || response.Answers.Count == 0)
        {
            return;
        }

        var now = nowUtc ?? DateTime.UtcNow;
        var cacheableQuestionKeys = response.Questions
            .Select(question => TryCreateDnsCacheKey(question.Name, question.Type, question.Class, out var key) ? key : (DnsCacheKey?)null)
            .Where(static key => key.HasValue)
            .Select(static key => key!.Value)
            .Distinct()
            .ToList();
        var updates = new Dictionary<DnsCacheKey, List<DnsCachedAnswer>>();

        foreach (var answer in response.Answers)
        {
            if (answer.Type != 1 || answer.Class != 1 || answer.Data.Length != 4 || answer.TTL == 0)
            {
                continue;
            }

            var cachedAnswer = new DnsCachedAnswer(answer.Data.ToArray(), now.AddSeconds(answer.TTL), now);
            if (TryCreateDnsCacheKey(answer.Name, answer.Type, answer.Class, out var answerKey))
            {
                AddCachedAnswer(updates, answerKey, cachedAnswer);
            }

            foreach (var questionKey in cacheableQuestionKeys)
            {
                AddCachedAnswer(updates, questionKey, cachedAnswer);
            }
        }

        foreach (var (key, answers) in updates)
        {
            _dnsCache[key] = new DnsCacheEntry(answers);
        }
    }

    internal bool MarkCachedAddressActive(string? domain, string ip)
    {
        if (!TryGetIpv4Bytes(ip, out var ipBytes))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        CleanupDnsCache(now);
        var updated = UpdateCachedAddress(domain, ipBytes, now, remove: false);
        if (updated)
        {
            TouchObservedHostname(domain, ip, now);
        }

        return updated;
    }

    internal bool RemoveCachedAddress(string? domain, string ip)
    {
        if (!TryGetIpv4Bytes(ip, out var ipBytes))
        {
            return false;
        }

        var removed = UpdateCachedAddress(domain, ipBytes, DateTime.UtcNow, remove: true);
        if (removed)
        {
            RemoveObservedHostname(domain, ip);
            Log.Debug("[DNS ] Removed cached address {IP} for {Domain}", ip, domain ?? "(any domain)");
        }

        return removed;
    }

    public void RecordObservedHostname(
        string ip,
        string hostname,
        string source = "Observed",
        string? route = null,
        string? reason = null,
        DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(hostname))
        {
            return;
        }

        var normalized = NormalizeDnsName(hostname);
        if (normalized == null)
        {
            return;
        }

        var normalizedRoute = string.IsNullOrWhiteSpace(route) ? "UNKNOWN" : route;
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "Observed" : reason;
        var now = nowUtc ?? DateTime.UtcNow;
        _observedHostnames.TryGetValue(ip, out var previous);
        _observedHostnames.AddOrUpdate(
            ip,
            _ => new DnsObservedHostnameEntry(normalized, normalizedRoute, normalizedReason, now, 1),
            (_, existing) => existing with
            {
                Hostname = normalized,
                Route = normalizedRoute,
                Reason = normalizedReason,
                LastSeenUtc = now,
                SeenCount = existing.SeenCount + 1
            });

        if (previous == null)
        {
            LogDnsMapping(normalized, ip, source, normalizedRoute, normalizedReason, previousHostname: null);
        }
        else if (!previous.Hostname.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                 !previous.Route.Equals(normalizedRoute, StringComparison.OrdinalIgnoreCase) ||
                 !previous.Reason.Equals(normalizedReason, StringComparison.OrdinalIgnoreCase))
        {
            LogDnsMapping(
                normalized,
                ip,
                source,
                normalizedRoute,
                normalizedReason,
                previous.Hostname.Equals(normalized, StringComparison.OrdinalIgnoreCase) ? null : previous.Hostname);
        }
    }

    public string? GetCachedHostname(string ip)
    {
        var now = DateTime.UtcNow;

        if (_observedHostnames.TryGetValue(ip, out var entry) &&
            IsObservedHostnameUsable(entry, now))
        {
            return entry.Hostname;
        }

        if (!TryGetIpv4Bytes(ip, out var ipBytes))
        {
            return null;
        }

        return _dnsCache
            .SelectMany(item => item.Value.Answers
                .Where(answer => IsDnsCacheAnswerUsable(answer, now) && answer.Data.SequenceEqual(ipBytes))
                .Select(answer => new { item.Key.Domain, answer.LastActiveUtc }))
            .OrderByDescending(item => item.LastActiveUtc)
            .Select(item => item.Domain)
            .FirstOrDefault();
    }

    private IReadOnlyList<DnsCacheRecord> GetCacheSnapshot(DateTime now)
    {
        var records = new List<DnsCacheRecord>();

        foreach (var (key, entry) in _dnsCache)
        {
            foreach (var answer in entry.Answers.Where(answer => IsDnsCacheAnswerUsable(answer, now)))
            {
                records.Add(new DnsCacheRecord(
                    key.Domain,
                    new IPAddress(answer.Data).ToString(),
                    answer.LastActiveUtc,
                    answer.ExpiresUtc));
            }
        }

        return records
            .OrderBy(static record => record.Hostname, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static record => record.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool UpdateCachedAddress(string? domain, byte[] ipBytes, DateTime now, bool remove)
    {
        var normalizedDomain = domain == null ? null : NormalizeDnsName(domain);
        if (normalizedDomain != null)
        {
            var key = new DnsCacheKey(normalizedDomain, 1, 1);
            return UpdateCachedAddressForKey(key, ipBytes, now, remove);
        }

        var updated = false;
        foreach (var key in _dnsCache.Keys)
        {
            updated |= UpdateCachedAddressForKey(key, ipBytes, now, remove);
        }

        return updated;
    }

    private bool UpdateCachedAddressForKey(DnsCacheKey key, byte[] ipBytes, DateTime now, bool remove)
    {
        if (!_dnsCache.TryGetValue(key, out var entry))
        {
            return false;
        }

        var found = false;
        var answers = new List<DnsCachedAnswer>(entry.Answers.Count);
        foreach (var answer in entry.Answers.Where(answer => IsDnsCacheAnswerUsable(answer, now)))
        {
            if (answer.Data.SequenceEqual(ipBytes))
            {
                found = true;
                if (!remove)
                {
                    answers.Add(answer with { LastActiveUtc = now });
                }

                continue;
            }

            answers.Add(answer);
        }

        if (!found)
        {
            if (answers.Count != entry.Answers.Count)
            {
                if (answers.Count == 0)
                {
                    _dnsCache.TryRemove(key, out _);
                }
                else
                {
                    _dnsCache[key] = entry with { Answers = answers };
                }
            }

            return false;
        }

        if (answers.Count == 0)
        {
            _dnsCache.TryRemove(key, out _);
        }
        else
        {
            _dnsCache[key] = entry with { Answers = answers };
        }

        return true;
    }

    private void CleanupDnsCache(DateTime now)
    {
        foreach (var (key, entry) in _dnsCache)
        {
            var answers = entry.Answers
                .Where(answer => IsDnsCacheAnswerUsable(answer, now))
                .ToList();
            if (answers.Count == 0)
            {
                _dnsCache.TryRemove(key, out _);
            }
            else if (answers.Count != entry.Answers.Count)
            {
                _dnsCache[key] = entry with { Answers = answers };
            }
        }
    }

    private void CleanupObservedHostnames(DateTime now)
    {
        foreach (var (ip, entry) in _observedHostnames)
        {
            if (!IsObservedHostnameUsable(entry, now))
            {
                _observedHostnames.TryRemove(ip, out _);
            }
        }
    }

    private void TouchObservedHostname(string? domain, string ip, DateTime now)
    {
        var normalizedDomain = domain == null ? null : NormalizeDnsName(domain);
        if (normalizedDomain == null)
        {
            return;
        }

        _observedHostnames.AddOrUpdate(
            ip,
            _ => new DnsObservedHostnameEntry(normalizedDomain, "UNKNOWN", "DNS", now, 1),
            (_, existing) => existing.Hostname.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase)
                ? existing with { LastSeenUtc = now, SeenCount = existing.SeenCount + 1 }
                : existing);
    }

    private void RemoveObservedHostname(string? domain, string ip)
    {
        if (!_observedHostnames.TryGetValue(ip, out var entry))
        {
            return;
        }

        var normalizedDomain = domain == null ? null : NormalizeDnsName(domain);
        if (normalizedDomain == null ||
            entry.Hostname.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase))
        {
            _observedHostnames.TryRemove(ip, out _);
        }
    }

    private static void AddCachedAnswer(
        Dictionary<DnsCacheKey, List<DnsCachedAnswer>> updates,
        DnsCacheKey key,
        DnsCachedAnswer answer)
    {
        if (!updates.TryGetValue(key, out var answers))
        {
            answers = [];
            updates[key] = answers;
        }

        if (!answers.Any(existing => existing.Data.SequenceEqual(answer.Data)))
        {
            answers.Add(answer);
        }
    }

    private static bool TryCreateDnsCacheKey(string domain, ushort type, ushort @class, out DnsCacheKey key)
    {
        key = default;
        if (type != 1 || @class != 1)
        {
            return false;
        }

        var normalizedDomain = NormalizeDnsName(domain);
        if (normalizedDomain == null)
        {
            return false;
        }

        key = new DnsCacheKey(normalizedDomain, type, @class);
        return true;
    }

    private static string? NormalizeDnsName(string domain)
    {
        var value = domain.Trim().TrimEnd('.');
        return value.Length == 0 ? null : value.ToLowerInvariant();
    }

    private static bool TryGetIpv4Bytes(string ip, out byte[] bytes)
    {
        bytes = [];
        if (!IPAddress.TryParse(ip, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        bytes = address.GetAddressBytes();
        return true;
    }

    private static bool IsDnsCacheAnswerUsable(DnsCachedAnswer answer, DateTime now) =>
        answer.ExpiresUtc > now &&
        now - answer.LastActiveUtc <= DnsCacheIdleTimeout;

    private static bool IsObservedHostnameUsable(DnsObservedHostnameEntry entry, DateTime now) =>
        now - entry.LastSeenUtc <= ObservedHostnameIdleTimeout;

    private static uint GetRemainingTtlSeconds(DateTime expiresUtc, DateTime now)
    {
        var remaining = (uint)Math.Ceiling((expiresUtc - now).TotalSeconds);
        return Math.Max(remaining, 1);
    }

    private static ushort BuildCachedResponseFlags(ushort queryFlags) =>
        (ushort)(0x8000 | 0x0080 | (queryFlags & 0x0100));

    private static string MakeRecordKey(string hostname, string ipAddress) =>
        $"{hostname.Trim().TrimEnd('.')}|{ipAddress}";

    private static void LogDnsMapping(
        string hostname,
        string ip,
        string source,
        string? route,
        string? reason,
        string? previousHostname)
    {
        if (previousHostname == null)
        {
            Log.Information(
                "[DNS ] {Hostname} -> {IP} -> {Route}/{Reason} ({Source})",
                hostname,
                ip,
                route,
                reason,
                source);
            return;
        }

        Log.Information(
            "[DNS ] {Hostname} -> {IP} -> {Route}/{Reason} ({Source}; was {PreviousHostname})",
            hostname,
            ip,
            route,
            reason,
            source,
            previousHostname);
    }
}

public sealed record DnsResolutionSnapshot(
    string Hostname,
    string IpAddress,
    string Route,
    string Reason,
    DateTime LastSeenUtc,
    long SeenCount,
    bool IsDnsCached,
    DateTime? DnsLastActiveUtc,
    DateTime? DnsExpiresUtc);

internal readonly record struct DnsCacheKey(string Domain, ushort Type, ushort Class);

internal sealed record DnsCacheEntry(IReadOnlyList<DnsCachedAnswer> Answers);

internal sealed record DnsCachedAnswer(byte[] Data, DateTime ExpiresUtc, DateTime LastActiveUtc);

internal sealed record DnsObservedHostnameEntry(
    string Hostname,
    string Route,
    string Reason,
    DateTime LastSeenUtc,
    long SeenCount);
