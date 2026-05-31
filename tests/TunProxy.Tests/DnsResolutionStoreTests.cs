using System.Net;
using TunProxy.CLI;
using TunProxy.Core.Dns;

namespace TunProxy.Tests;

public class DnsResolutionStoreTests
{
    [Fact]
    public void GetResolutionSnapshot_MergesObservedHostnameWithActiveDnsCache()
    {
        var store = new DnsResolutionStore();
        var now = DateTime.UtcNow;
        var queryBytes = BuildDnsQuery("Example.COM.", 0x1111);
        var response = DnsPacket.Parse(BuildDnsResponse(queryBytes, 600, [1, 2, 3, 4]))!;

        store.StoreDnsResponseInCache(response, now);
        store.RecordObservedHostname("1.2.3.4", "Example.COM.", "DNS", "PROXY", "GFW");

        var record = Assert.Single(store.GetResolutionSnapshot());
        Assert.Equal("example.com", record.Hostname);
        Assert.Equal("1.2.3.4", record.IpAddress);
        Assert.Equal("PROXY", record.Route);
        Assert.Equal("GFW", record.Reason);
        Assert.True(record.IsDnsCached);
        Assert.NotNull(record.DnsLastActiveUtc);
        Assert.NotNull(record.DnsExpiresUtc);
        Assert.True(record.SeenCount > 0);
    }

    [Fact]
    public void RemoveCachedAddress_ClearsDnsCacheAndObservedHostname()
    {
        var store = new DnsResolutionStore();
        var now = DateTime.UtcNow;
        var queryBytes = BuildDnsQuery("example.com", 0x1111);
        var response = DnsPacket.Parse(BuildDnsResponse(queryBytes, 600, [1, 2, 3, 4]))!;

        store.StoreDnsResponseInCache(response, now);
        store.RecordObservedHostname("1.2.3.4", "example.com", "DNS", "PROXY", "GFW");

        Assert.True(store.RemoveCachedAddress("example.com", "1.2.3.4"));

        Assert.Empty(store.GetResolutionSnapshot());
    }

    [Fact]
    public void GetResolutionSnapshot_HidesStaleObservedHostnameWithoutCleanup()
    {
        var store = new DnsResolutionStore();
        var now = DateTime.UtcNow;

        store.RecordObservedHostname("1.2.3.4", "example.com", "DNS", "PROXY", "GFW", nowUtc: now);

        Assert.Empty(store.GetResolutionSnapshot(now.AddMinutes(10).AddSeconds(1)));

        var record = Assert.Single(store.GetResolutionSnapshot(now));
        Assert.Equal("example.com", record.Hostname);
        Assert.False(record.IsDnsCached);
    }

    [Fact]
    public void GetResolutionSnapshot_HidesExpiredDnsCacheWithoutCleanup()
    {
        var store = new DnsResolutionStore();
        var now = DateTime.UtcNow;
        var queryBytes = BuildDnsQuery("example.com", 0x1111);
        var response = DnsPacket.Parse(BuildDnsResponse(queryBytes, 60, [1, 2, 3, 4]))!;

        store.StoreDnsResponseInCache(response, now);

        Assert.Empty(store.GetResolutionSnapshot(now.AddSeconds(61)));

        var record = Assert.Single(store.GetResolutionSnapshot(now));
        Assert.Equal("example.com", record.Hostname);
        Assert.True(record.IsDnsCached);
    }

    [Fact]
    public void CleanupExpired_RemovesExpiredDnsAndObservedRecords()
    {
        var store = new DnsResolutionStore();
        var now = DateTime.UtcNow;
        var queryBytes = BuildDnsQuery("example.com", 0x1111);
        var response = DnsPacket.Parse(BuildDnsResponse(queryBytes, 60, [1, 2, 3, 4]))!;

        store.StoreDnsResponseInCache(response, now);
        store.RecordObservedHostname("1.2.3.4", "example.com", "DNS", "PROXY", "GFW", nowUtc: now);

        store.CleanupExpired(now.AddMinutes(10).AddSeconds(1));

        Assert.Equal(0, store.CacheEntryCount);
        Assert.Empty(store.GetResolutionSnapshot(now));
    }

    [Fact]
    public void GetMostRecentAddressForHostname_PrefersObservedHostname()
    {
        var store = new DnsResolutionStore();
        var now = DateTime.UtcNow;

        store.RecordObservedHostname("1.2.3.4", "example.com", nowUtc: now.AddSeconds(-5));
        store.RecordObservedHostname("5.6.7.8", "example.com", nowUtc: now);

        var address = store.GetMostRecentAddressForHostname("example.com");

        Assert.Equal(IPAddress.Parse("5.6.7.8"), address);
    }

    [Fact]
    public void GetMostRecentAddressForHostname_FallsBackToDnsCache()
    {
        var store = new DnsResolutionStore();
        var now = DateTime.UtcNow;
        var firstQuery = BuildDnsQuery("example.com", 0x1111);
        var firstResponse = DnsPacket.Parse(BuildDnsResponse(firstQuery, 600, [1, 2, 3, 4]))!;
        var secondQuery = BuildDnsQuery("example.com", 0x2222);
        var secondResponse = DnsPacket.Parse(BuildDnsResponse(secondQuery, 600, [5, 6, 7, 8]))!;

        store.StoreDnsResponseInCache(firstResponse, now.AddSeconds(-10));
        store.StoreDnsResponseInCache(secondResponse, now);

        var address = store.GetMostRecentAddressForHostname("example.com");

        Assert.Equal(IPAddress.Parse("5.6.7.8"), address);
    }

    [Fact]
    public void GetMostRecentAddressForHostname_AppliesAddressPredicateToObservedAndDnsCache()
    {
        var store = new DnsResolutionStore();
        var now = DateTime.UtcNow;
        var fakeIp = IPAddress.Parse("198.18.0.42");
        var realIp = IPAddress.Parse("203.0.113.42");
        var query = BuildDnsQuery("example.com", 0x1111);
        var response = DnsPacket.Parse(BuildDnsResponse(query, 600, realIp.GetAddressBytes()))!;

        store.StoreDnsResponseInCache(response, now.AddSeconds(-5));
        store.RecordObservedHostname(realIp.ToString(), "example.com", nowUtc: now.AddSeconds(-4));
        store.RecordObservedHostname(fakeIp.ToString(), "example.com", nowUtc: now);

        var address = store.GetMostRecentAddressForHostname(
            "example.com",
            candidate => !FakeIpPool.IsFakeIp(candidate));

        Assert.Equal(realIp, address);
    }

    private static byte[] BuildDnsQuery(string domain, ushort transactionId)
    {
        return new DnsPacket
        {
            TransactionId = transactionId,
            Flags = new DnsFlags(0x0100),
            Questions =
            [
                new DnsQuestion
                {
                    Name = domain,
                    Type = 1,
                    Class = 1
                }
            ]
        }.Build();
    }

    private static byte[] BuildDnsResponse(byte[] queryBytes, uint ttl, byte[] data)
    {
        var query = DnsPacket.Parse(queryBytes)!;
        return new DnsPacket
        {
            TransactionId = query.TransactionId,
            Flags = new DnsFlags(0x8180),
            Questions = query.Questions,
            Answers =
            [
                new DnsAnswer
                {
                    Name = query.Questions[0].Name,
                    Type = 1,
                    Class = 1,
                    TTL = ttl,
                    Data = data
                }
            ]
        }.Build();
    }
}
