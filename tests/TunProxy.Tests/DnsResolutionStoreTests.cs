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

        store.RecordObservedHostname("1.2.3.4", "example.com", "DNS", "PROXY", "GFW", now);

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
        store.RecordObservedHostname("1.2.3.4", "example.com", "DNS", "PROXY", "GFW", now);

        store.CleanupExpired(now.AddMinutes(10).AddSeconds(1));

        Assert.Equal(0, store.CacheEntryCount);
        Assert.Empty(store.GetResolutionSnapshot(now));
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
