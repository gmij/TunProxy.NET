using TunProxy.Core.Route;

namespace TunProxy.CLI;

internal static class TunRouteDiagnosticsBuilder
{
    public static RouteDiagnosticsSnapshot Build(
        IRouteService? routeService,
        string tunIpAddress,
        IEnumerable<string> proxyBypassRoutes,
        IReadOnlyCollection<string> directBypassRoutes,
        int directBypassRouteCount)
    {
        var route = new RouteDiagnosticsSnapshot
        {
            ProxyBypassRoutes = proxyBypassRoutes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DirectBypassRoutes = directBypassRoutes.ToList(),
            DirectBypassRouteCount = directBypassRouteCount
        };

        try
        {
            route.OriginalDefaultGateway = routeService?.GetOriginalDefaultGateway();
            if (routeService is WindowsRouteService windowsRouteService)
            {
                ApplyWindowsRouteTableDiagnostics(
                    route,
                    tunIpAddress,
                    windowsRouteService.GetRouteTable(),
                    windowsRouteService.GetTunDefaultRoute());
            }
        }
        catch (Exception ex)
        {
            route.Issues.Add($"Route diagnostics failed: {ex.GetType().Name}: {ex.Message}");
        }

        return route;
    }

    internal static void ApplyWindowsRouteTableDiagnostics(
        RouteDiagnosticsSnapshot route,
        string tunIpAddress,
        IReadOnlyCollection<RouteEntry> routes,
        RouteEntry? tunDefault)
    {
        route.HasTunDefaultRoute = tunDefault != null;
        route.TunDefaultMetric = tunDefault?.Metric;

        if (tunDefault == null)
        {
            route.Issues.Add("TUN default route is missing.");
            return;
        }

        if (!int.TryParse(tunDefault.Metric, out var tunMetric))
        {
            return;
        }

        var hasHigherPriorityCompetingDefault = routes.Any(routeEntry =>
            routeEntry.Network == "0.0.0.0" &&
            !WindowsRouteService.IsTunDefaultRoute(routeEntry, tunIpAddress) &&
            int.TryParse(routeEntry.Metric, out var metric) &&
            metric < tunMetric);

        if (hasHigherPriorityCompetingDefault)
        {
            route.Issues.Add($"Another default route has a higher priority than TUN metric {tunMetric}.");
        }
    }
}
