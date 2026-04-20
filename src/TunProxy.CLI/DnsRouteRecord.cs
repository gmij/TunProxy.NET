namespace TunProxy.CLI;

public sealed record DnsRouteRecord(
    string IpAddress,
    string Hostname,
    string Route,
    string Reason,
    bool IsPrivateIp,
    bool IsDirectBypass,
    bool IsDnsCached,
    DateTime? LastActiveUtc,
    DateTime? DnsExpiresUtc);

public sealed record DnsCacheRecord(
    string Hostname,
    string IpAddress,
    DateTime LastActiveUtc,
    DateTime ExpiresUtc);
