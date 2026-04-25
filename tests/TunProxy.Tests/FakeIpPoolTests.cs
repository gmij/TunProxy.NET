using System.Net;
using TunProxy.CLI;

namespace TunProxy.Tests;

public class FakeIpPoolTests
{
    [Fact]
    public void AllocateOrGet_ReturnsAddressInExpectedRange()
    {
        var pool = new FakeIpPool();
        var ip = pool.AllocateOrGet("example.com");

        Assert.True(FakeIpPool.IsFakeIp(ip));
    }

    [Fact]
    public void AllocateOrGet_ReturnsSameAddressForSameDomain()
    {
        var pool = new FakeIpPool();
        var first = pool.AllocateOrGet("example.com");
        var second = pool.AllocateOrGet("example.com");

        Assert.Equal(first, second);
    }

    [Fact]
    public void AllocateOrGet_ReturnsDifferentAddressesForDifferentDomains()
    {
        var pool = new FakeIpPool();
        var a = pool.AllocateOrGet("example.com");
        var b = pool.AllocateOrGet("other.com");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void AllocateOrGet_IsCaseInsensitive()
    {
        var pool = new FakeIpPool();
        var lower = pool.AllocateOrGet("example.com");
        var upper = pool.AllocateOrGet("EXAMPLE.COM");

        Assert.Equal(lower, upper);
    }

    [Fact]
    public void AllocateOrGet_NormalizesTrailingDot()
    {
        var pool = new FakeIpPool();
        var withDot = pool.AllocateOrGet("example.com.");
        var withoutDot = pool.AllocateOrGet("example.com");

        Assert.Equal(withDot, withoutDot);
    }

    [Fact]
    public void GetDomain_ReturnsNullForRealIp()
    {
        var pool = new FakeIpPool();
        var result = pool.GetDomain(IPAddress.Parse("1.2.3.4"));

        Assert.Null(result);
    }

    [Fact]
    public void GetDomain_ReturnsDomainForAllocatedFakeIp()
    {
        var pool = new FakeIpPool();
        var fakeIp = pool.AllocateOrGet("example.com");
        var domain = pool.GetDomain(fakeIp);

        Assert.Equal("example.com", domain);
    }

    [Fact]
    public void GetDomain_ReturnsNullForUnallocatedFakeIp()
    {
        var pool = new FakeIpPool();
        // 198.18.0.1 has not been allocated
        var result = pool.GetDomain(IPAddress.Parse("198.18.0.1"));

        Assert.Null(result);
    }

    [Theory]
    [InlineData("198.18.0.1", true)]
    [InlineData("198.18.255.254", true)]
    [InlineData("198.18.100.200", true)]
    [InlineData("198.19.0.1", false)]    // outside /16
    [InlineData("192.168.1.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("10.0.0.1", false)]
    public void IsFakeIp_DetectsRange(string ipStr, bool expected)
    {
        Assert.Equal(expected, FakeIpPool.IsFakeIp(IPAddress.Parse(ipStr)));
    }

    [Fact]
    public void IsFakeIp_ReturnsFalseForIPv6()
    {
        Assert.False(FakeIpPool.IsFakeIp(IPAddress.Parse("::1")));
    }

    [Fact]
    public void CleanupExpired_RemovesStaleEntries()
    {
        var pool = new FakeIpPool();
        pool.AllocateOrGet("old.com");

        // Advance simulated time well past idle window.
        var future = DateTime.UtcNow.Add(TimeSpan.FromHours(2));
        pool.CleanupExpired(TimeSpan.FromHours(1), nowUtc: future);

        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void CleanupExpired_KeepsRecentEntries()
    {
        var pool = new FakeIpPool();
        pool.AllocateOrGet("recent.com");

        // Cleanup with a 1-hour window but only 30 minutes have elapsed.
        var future = DateTime.UtcNow.Add(TimeSpan.FromMinutes(30));
        pool.CleanupExpired(TimeSpan.FromHours(1), nowUtc: future);

        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void CleanupExpired_ReleasesSlotForReuse()
    {
        var pool = new FakeIpPool();
        var originalIp = pool.AllocateOrGet("old.com");
        var future = DateTime.UtcNow.Add(TimeSpan.FromHours(2));
        pool.CleanupExpired(TimeSpan.FromHours(1), nowUtc: future);

        // The slot should now be available; new allocation should not conflict.
        var newIp = pool.AllocateOrGet("new.com");
        Assert.True(FakeIpPool.IsFakeIp(newIp));
        // The old domain should now be gone.
        Assert.Null(pool.GetDomain(originalIp));
    }

    [Fact]
    public void Pool_AllocatesUniqueAddressesUpToCapacity()
    {
        const int capacity = 10;
        var pool = new FakeIpPool(capacity);
        var allocated = new HashSet<string>();

        for (var i = 0; i < capacity; i++)
        {
            var ip = pool.AllocateOrGet($"domain{i}.test");
            allocated.Add(ip.ToString());
        }

        Assert.Equal(capacity, allocated.Count);
        Assert.Equal(capacity, pool.Count);
    }

    [Fact]
    public void Pool_EvictsLruEntryWhenFull()
    {
        const int capacity = 3;
        var pool = new FakeIpPool(capacity);

        // Allocate to fill the pool; domain0 is oldest (LRU).
        pool.AllocateOrGet("domain0.test");
        pool.AllocateOrGet("domain1.test");
        pool.AllocateOrGet("domain2.test");

        // Allocating a 4th domain must evict domain0 (LRU) and reuse its slot.
        pool.AllocateOrGet("domain3.test");

        // Pool stays at capacity.
        Assert.Equal(capacity, pool.Count);
    }

    [Fact]
    public void Pool_EvictsLru_PreservesRecentlyUsed()
    {
        const int capacity = 3;
        var pool = new FakeIpPool(capacity);

        pool.AllocateOrGet("domain0.test"); // oldest
        var ip1 = pool.AllocateOrGet("domain1.test");
        pool.AllocateOrGet("domain2.test");

        // Re-access domain0 to make it recently used (domain1 is now LRU).
        pool.AllocateOrGet("domain0.test");

        // Allocating a 4th domain must evict domain1 (now LRU).
        pool.AllocateOrGet("domain3.test");

        // Pool stays at capacity.
        Assert.Equal(capacity, pool.Count);

        // domain1's old IP slot is now reused by domain3 – GetDomain returns the new owner.
        var currentMappingForIp1 = pool.GetDomain(ip1);
        Assert.NotEqual("domain1.test", currentMappingForIp1);

        // domain0 survives because it was recently used.
        var domain0Ip = pool.AllocateOrGet("domain0.test");
        Assert.True(FakeIpPool.IsFakeIp(domain0Ip));
    }
}
