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
    public void RemoveCachedAddress_ClearsDnsCacheButKeepsObservedHostname()
    {
        var store = new DnsResolutionStore();
        var now = DateTime.UtcNow;
        var queryBytes = BuildDnsQuery("example.com", 0x1111);
        var response = DnsPacket.Parse(BuildDnsResponse(queryBytes, 600, [1, 2, 3, 4]))!;

        store.StoreDnsResponseInCache(response, now);
        store.RecordObservedHostname("1.2.3.4", "example.com", "DNS", "PROXY", "GFW");

        Assert.True(store.RemoveCachedAddress("example.com", "1.2.3.4"));

        var record = Assert.Single(store.GetResolutionSnapshot());
        Assert.Equal("example.com", record.Hostname);
        Assert.Equal("1.2.3.4", record.IpAddress);
        Assert.False(record.IsDnsCached);
        Assert.Null(record.DnsLastActiveUtc);
        Assert.Null(record.DnsExpiresUtc);
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
