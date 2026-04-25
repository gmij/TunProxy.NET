using System.Net;
using Serilog;
using TunProxy.Core.Route;

namespace TunProxy.CLI;

internal sealed class DirectBypassRouteScheduler
{
    private readonly IRouteService? _routeService;
    private readonly DirectBypassRouteManager _routes;
    private readonly string _tunIpAddress;
    private readonly Func<IEnumerable<string>> _getProxyBypassRoutes;

    public DirectBypassRouteScheduler(
        IRouteService? routeService,
        DirectBypassRouteManager routes,
        string tunIpAddress,
        Func<IEnumerable<string>> getProxyBypassRoutes)
    {
        _routeService = routeService;
        _routes = routes;
        _tunIpAddress = tunIpAddress;
        _getProxyBypassRoutes = getProxyBypassRoutes;
    }

    public Task ScheduleAsync(
        IPAddress destinationAddress,
        RouteDecision decision,
        CancellationToken ct) =>
        Task.Run(async () =>
        {
            try
            {
                await EnsureAsync(destinationAddress, decision, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[ROUTE] Failed to schedule direct bypass route for {IP}", destinationAddress);
            }
        }, ct);

    public async Task EnsureAsync(
        IPAddress destinationAddress,
        RouteDecision decision,
        CancellationToken ct)
    {
        var destIp = destinationAddress.ToString();
        var isProxyBypassRoute = _getProxyBypassRoutes()
            .Contains(destIp, StringComparer.OrdinalIgnoreCase);

        if (!ShouldEnsureDirectBypassRoute(
                destinationAddress,
                _routeService != null,
                isProxyBypassRoute,
                _tunIpAddress,
                _routeService?.GetOriginalDefaultGateway()))
        {
            return;
        }

        await _routes.EnsureRouteAsync(destIp, decision, ct);
    }

    internal static bool ShouldEnsureDirectBypassRoute(
        IPAddress destinationAddress,
        bool hasRouteService,
        bool isProxyBypassRoute,
        string? tunIpAddress,
        string? originalDefaultGateway)
    {
        if (!hasRouteService || isProxyBypassRoute)
        {
            return false;
        }

        return !TunPacketDecisions.ShouldSkipDirectBypassRoute(
            destinationAddress,
            tunIpAddress,
            originalDefaultGateway);
    }
}
