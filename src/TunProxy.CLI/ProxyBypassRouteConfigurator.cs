using System.Net;
using System.Net.Sockets;
using Serilog;
using TunProxy.Core.Configuration;
using TunProxy.Core.Route;

namespace TunProxy.CLI;

internal sealed class ProxyBypassRouteConfigurator
{
    private readonly Func<string, IPAddress[]> _resolveHost;

    public ProxyBypassRouteConfigurator(Func<string, IPAddress[]>? resolveHost = null)
    {
        _resolveHost = resolveHost ?? Dns.GetHostAddresses;
    }

    public IReadOnlyList<string> AddProxyBypassRoutes(ProxyConfig proxy, IRouteService routeService)
    {
        var addresses = ResolveProxyAddresses(proxy.Host);
        var added = new List<string>();

        foreach (var address in addresses)
        {
            var ipAddress = address.ToString();
            var ok = routeService.AddBypassRoute(ipAddress);
            if (ok)
            {
                added.Add(ipAddress);
            }

            Log.Information("[ROUTE] Proxy bypass route {Status}: {IP}", ok ? "ready" : "failed", ipAddress);
        }

        return added;
    }

    internal IReadOnlyList<IPAddress> ResolveProxyAddresses(string host)
    {
        if (IPAddress.TryParse(host, out var proxyIp))
        {
            return IsBypassRouteCandidate(proxyIp) ? [proxyIp] : [];
        }

        try
        {
            return _resolveHost(host)
                .Where(IsBypassRouteCandidate)
                .Distinct()
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning("[ROUTE] Failed to resolve upstream proxy host for bypass route: {Host}, {Message}",
                host,
                ex.Message);
            return [];
        }
    }

    internal static bool IsBypassRouteCandidate(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address);
}
