using System.Net;
using System.Net.Sockets;

namespace TunProxy.CLI;

/// <summary>
/// Manages the fake-IP address pool used in FakeIP DNS mode.
/// Allocates addresses from the 198.18.0.0/16 benchmarking block (RFC 2544)
/// and maintains persistent domain ↔ fake-IP mappings with LRU eviction.
/// </summary>
public sealed class FakeIpPool
{
    /// <summary>First and second octets of the pool block: 198.18.x.x</summary>
    private const byte FirstOctet = 198;
    private const byte SecondOctet = 18;

    // Indices 1..65534 (skip .0.0 network address and .255.255 broadcast)
    private const uint MinIndex = 1;
    private const uint MaxIndex = 65534;
    private const int DefaultCapacity = (int)(MaxIndex - MinIndex + 1);

    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Dictionary<string, FakeIpEntry> _byDomain = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _byIndex = new();
    private uint _nextSlot = MinIndex;

    public FakeIpPool(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = Math.Min(capacity, DefaultCapacity);
    }

    /// <summary>
    /// Returns the number of currently allocated entries.
    /// </summary>
    public int Count
    {
        get { lock (_lock) return _byDomain.Count; }
    }

    /// <summary>
    /// Returns true if <paramref name="ip"/> falls within the 198.18.0.0/16 fake-IP block.
    /// </summary>
    public static bool IsFakeIp(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = ip.GetAddressBytes();
        return b[0] == FirstOctet && b[1] == SecondOctet;
    }

    /// <summary>
    /// Returns the fake IP assigned to <paramref name="domain"/>, allocating a new one if needed.
    /// Updates the last-used timestamp for existing allocations.
    /// </summary>
    public IPAddress AllocateOrGet(string domain)
    {
        var key = NormalizeDomain(domain);
        lock (_lock)
        {
            if (_byDomain.TryGetValue(key, out var existing))
            {
                existing.LastUsedUtc = DateTime.UtcNow;
                return IndexToIp(existing.Index);
            }

            var slot = FindFreeSlot();
            var entry = new FakeIpEntry(slot, DateTime.UtcNow);
            _byDomain[key] = entry;
            _byIndex[slot] = key;
            return IndexToIp(slot);
        }
    }

    /// <summary>
    /// Returns the domain mapped to <paramref name="fakeIp"/>, or null if not found.
    /// Updates the last-used timestamp when a mapping is found.
    /// </summary>
    public string? GetDomain(IPAddress fakeIp)
    {
        if (!IsFakeIp(fakeIp))
        {
            return null;
        }

        var b = fakeIp.GetAddressBytes();
        var index = ((uint)b[2] << 8) | b[3];
        lock (_lock)
        {
            if (!_byIndex.TryGetValue(index, out var domain))
            {
                return null;
            }

            if (_byDomain.TryGetValue(domain, out var entry))
            {
                entry.LastUsedUtc = DateTime.UtcNow;
            }

            return domain;
        }
    }

    /// <summary>
    /// Removes entries that have not been accessed within <paramref name="idleTimeout"/>.
    /// </summary>
    public void CleanupExpired(TimeSpan idleTimeout, DateTime? nowUtc = null)
    {
        var cutoff = (nowUtc ?? DateTime.UtcNow) - idleTimeout;
        lock (_lock)
        {
            var toRemove = _byDomain
                .Where(kv => kv.Value.LastUsedUtc < cutoff)
                .Select(kv => (kv.Key, kv.Value.Index))
                .ToList();

            foreach (var (domain, index) in toRemove)
            {
                _byDomain.Remove(domain);
                _byIndex.Remove(index);
            }
        }
    }

    // ── private helpers ──────────────────────────────────────────────────────

    private uint MaxSlot => MinIndex + (uint)_capacity - 1;

    private uint FindFreeSlot()
    {
        // Advance the circular pointer within [MinIndex, MaxSlot] until a free slot is found.
        var maxSlot = MaxSlot;
        var start = _nextSlot;
        while (true)
        {
            if (!_byIndex.ContainsKey(_nextSlot))
            {
                var slot = _nextSlot;
                _nextSlot = _nextSlot >= maxSlot ? MinIndex : _nextSlot + 1;
                return slot;
            }

            _nextSlot = _nextSlot >= maxSlot ? MinIndex : _nextSlot + 1;
            if (_nextSlot == start)
            {
                // All slots within capacity are occupied – evict the least recently used entry.
                return EvictLru();
            }
        }
    }

    private uint EvictLru()
    {
        var lruDomain = _byDomain
            .OrderBy(kv => kv.Value.LastUsedUtc)
            .Select(kv => kv.Key)
            .First();

        var index = _byDomain[lruDomain].Index;
        _byDomain.Remove(lruDomain);
        _byIndex.Remove(index);
        return index;
    }

    private static IPAddress IndexToIp(uint index) =>
        new([FirstOctet, SecondOctet, (byte)(index >> 8), (byte)(index & 0xFF)]);

    private static string NormalizeDomain(string domain) =>
        domain.Trim().TrimEnd('.').ToLowerInvariant();

    private sealed class FakeIpEntry(uint index, DateTime lastUsedUtc)
    {
        public uint Index { get; } = index;
        public DateTime LastUsedUtc { get; set; } = lastUsedUtc;
    }
}
