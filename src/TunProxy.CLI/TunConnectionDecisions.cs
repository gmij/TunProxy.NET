using System.Net;
using TunProxy.Core.Packets;

namespace TunProxy.CLI;

internal static class TunConnectionDecisions
{
    public static TunConnectionTarget SelectTarget(
        int destPort,
        string destIp,
        byte[] initialPayload,
        string? cachedHostname)
    {
        if (destPort == 443)
        {
            var sni = ProtocolInspector.ExtractSni(initialPayload);
            return sni != null
                ? new TunConnectionTarget(sni, "SNI", destIp)
                : FromCachedHostnameOrIp(cachedHostname, destIp);
        }

        if (destPort == 80)
        {
            var host = ProtocolInspector.ExtractHttpHost(initialPayload);
            return host != null
                ? new TunConnectionTarget(host, "Host", destIp)
                : FromCachedHostnameOrIp(cachedHostname, destIp);
        }

        return FromCachedHostnameOrIp(cachedHostname, destIp);
    }

    public static string SelectUpstreamHost(
        bool shouldProxy,
        RouteDecision decision,
        TunConnectionTarget target) =>
        shouldProxy
            ? target.ConnectHost
            : decision.EvaluatedIp?.ToString() ?? target.ConnectHost;

    public static TunConnectionFailure ClassifyFailure(Exception ex, string routeLabel)
    {
        var proxyDenied = ex.Message.Contains("PROXY_DENIED", StringComparison.Ordinal);
        var connectFailed = ex.Message.Contains("CONNECT_FAILED", StringComparison.Ordinal);

        var reason = proxyDenied ? "proxy denied"
                   : connectFailed ? "connect failed"
                   : "error";

        return new TunConnectionFailure(
            reason,
            ShouldRecordProxyBlocked: proxyDenied,
            ShouldRecordProxyConnectFailed: routeLabel == "PROXY" && connectFailed);
    }

    private static TunConnectionTarget FromCachedHostnameOrIp(string? cachedHostname, string destIp) =>
        cachedHostname != null
            ? new TunConnectionTarget(cachedHostname, "DNS", destIp)
            : new TunConnectionTarget(destIp, "IP", destIp);
}

internal sealed record TunConnectionTarget(
    string ConnectHost,
    string DomainSource,
    string DestIp)
{
    public bool HasDomainHint => ConnectHost != DestIp && !IPAddress.TryParse(ConnectHost, out _);

    public string? DomainHint => HasDomainHint ? ConnectHost : null;

    public string? DnsCacheHost => HasDomainHint ? ConnectHost : null;
}

internal sealed record TunConnectionFailure(
    string Reason,
    bool ShouldRecordProxyBlocked,
    bool ShouldRecordProxyConnectFailed);
