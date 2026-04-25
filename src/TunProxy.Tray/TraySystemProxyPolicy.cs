using TunProxy.Core;
using TunProxy.Core.Configuration;

namespace TunProxy.Tray;

internal enum TraySystemProxyActionKind
{
    None,
    Restore,
    SetPac,
    SetGlobal,
    DisableForTun
}

internal readonly record struct TraySystemProxyAction(
    TraySystemProxyActionKind Kind,
    string? PacUrl = null,
    string? ProxyAddress = null,
    string? BypassList = null);

internal static class TraySystemProxyPolicy
{
    public static TraySystemProxyAction Resolve(
        ServiceState newState,
        ServiceState previousState,
        string? runtimeMode,
        AppConfigDto? config,
        bool systemProxyApplied,
        string apiBase)
    {
        if (newState == ServiceState.Running)
        {
            if (string.Equals(runtimeMode, "tun", StringComparison.OrdinalIgnoreCase))
            {
                return new TraySystemProxyAction(TraySystemProxyActionKind.DisableForTun);
            }

            return ResolveLocalProxyAction(config, apiBase);
        }

        return previousState == ServiceState.Running && systemProxyApplied
            ? new TraySystemProxyAction(TraySystemProxyActionKind.Restore)
            : new TraySystemProxyAction(TraySystemProxyActionKind.None);
    }

    private static TraySystemProxyAction ResolveLocalProxyAction(AppConfigDto? config, string apiBase)
    {
        if (config == null)
        {
            return new TraySystemProxyAction(TraySystemProxyActionKind.None);
        }

        var systemProxyMode = string.IsNullOrWhiteSpace(config.LocalProxy.SystemProxyMode)
            ? config.LocalProxy.SetSystemProxy ? SystemProxyModes.Pac : SystemProxyModes.None
            : SystemProxyModes.Normalize(config.LocalProxy.SystemProxyMode);

        return systemProxyMode switch
        {
            SystemProxyModes.Pac => new TraySystemProxyAction(
                TraySystemProxyActionKind.SetPac,
                PacUrl: $"{apiBase}/proxy.pac"),
            SystemProxyModes.Global => new TraySystemProxyAction(
                TraySystemProxyActionKind.SetGlobal,
                ProxyAddress: $"127.0.0.1:{config.LocalProxy.ListenPort}",
                BypassList: config.LocalProxy.BypassList),
            _ => new TraySystemProxyAction(TraySystemProxyActionKind.Restore)
        };
    }
}
