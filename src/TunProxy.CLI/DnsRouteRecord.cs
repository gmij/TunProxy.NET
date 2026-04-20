namespace TunProxy.CLI;

public sealed record DnsRouteRecord(
    string IpAddress,
    string Hostname,
    string Route,
    string Reason,
    bool IsPrivateIp,
    bool IsDirectBypass);
