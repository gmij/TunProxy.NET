namespace TunProxy.CLI;

public sealed record DnsRouteRecord(
    string IpAddress,
    string Hostname,
    string Route,
    string Reason,
    long SeenCount,
    bool IsPrivateIp,
    bool IsDnsCached,
    DateTime? LastActiveUtc);

public sealed record DnsCacheRecord(
    string Hostname,
    string IpAddress,
    DateTime LastActiveUtc,
    DateTime ExpiresUtc);
