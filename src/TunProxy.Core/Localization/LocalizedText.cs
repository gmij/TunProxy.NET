using System.Globalization;
using System.Resources;

namespace TunProxy.Core.Localization;

public static class LocalizedText
{
    private static readonly ResourceManager ResourceManager =
        new("TunProxy.Core.Localization.Strings", typeof(LocalizedText).Assembly);

    public static readonly string[] SupportedCultures = ["en", "zh-CN"];

    public static readonly string[] FrontendKeys =
    [
        "Nav.Status",
        "Nav.Config",
        "Nav.Dns",
        "Nav.Logs",
        "Nav.Language",
        "Shared.Loading",
        "Shared.RefreshAt",
        "Shared.Unknown",
        "Shared.None",
        "Mode.Proxy",
        "Mode.Tun",
        "Page.Status.Title",
        "Page.Status.Heading",
        "Page.Status.Badge.Loading",
        "Page.Status.Badge.Running",
        "Page.Status.Badge.Stopped",
        "Page.Status.Badge.Unreachable",
        "Page.Status.Badge.Downloading",
        "Page.Status.ServiceRestart",
        "Page.Status.ServiceStop",
        "Page.Status.ServiceRestarting",
        "Page.Status.ServiceStopping",
        "Page.Status.ServiceActionFailed",
        "Page.Status.ServiceUnavailableHint",
        "Page.Status.ProxyServer",
        "Page.Status.ActiveConnections",
        "Page.Status.Uptime",
        "Page.Status.Traffic",
        "Page.Status.BytesSent",
        "Page.Status.BytesReceived",
        "Page.Status.TotalConnections",
        "Page.Status.FailedConnections",
        "Page.Status.TunDiagnostics",
        "Page.Status.RawPackets",
        "Page.Status.Ipv6Packets",
        "Page.Status.ParseFailures",
        "Page.Status.PortFiltered",
        "Page.Status.DirectRouted",
        "Page.Status.DnsQueries",
        "Page.Status.DnsFailures",
        "Page.Config.Title",
        "Page.Config.AlertSavedHtml",
        "Page.Config.AlertRestarting",
        "Page.Config.CurrentMode",
        "Page.Config.ModeHintProxyHtml",
        "Page.Config.ModeHintTunHtml",
        "Page.Config.LocalProxyPort",
        "Page.Config.SetSystemProxy",
        "Page.Config.SystemProxyMode",
        "Page.Config.SystemProxyMode.Pac",
        "Page.Config.SystemProxyMode.Global",
        "Page.Config.SystemProxyMode.Tun",
        "Page.Config.SystemProxyMode.None",
        "Page.Config.StepUpstream",
        "Page.Config.StepUpstreamHint",
        "Page.Config.StepRoutingResources",
        "Page.Config.StepRoutingResourcesHint",
        "Page.Config.StepProxyMode",
        "Page.Config.StepProxyModeHint",
        "Page.Config.ProxyServer",
        "Page.Config.ProxyHost",
        "Page.Config.ProxyPort",
        "Page.Config.ProxyType",
        "Page.Config.ProxyUsername",
        "Page.Config.ProxyPassword",
        "Page.Config.ProxyCheck",
        "Page.Config.ProxyStatus.Unknown",
        "Page.Config.ProxyStatus.Hint",
        "Page.Config.ProxyStatus.Checking",
        "Page.Config.ProxyStatus.Available",
        "Page.Config.ProxyStatus.AvailableHint",
        "Page.Config.ProxyStatus.Unavailable",
        "Page.Config.ProxyStatus.UnavailableHint",
        "Page.Config.ProxyStatus.TargetOk",
        "Page.Config.ProxyStatus.TargetFailed",
        "Page.Config.ProxyStatus.CheckFailed",
        "Page.Config.ProxyStatus.RequiredBeforeSave",
        "Page.Config.RoutingRules",
        "Page.Config.RoutingLocked",
        "Page.Config.SmartRoutingDescription",
        "Page.Config.DnsCacheHint",
        "Page.Config.RouteMode",
        "Page.Config.Route.Global",
        "Page.Config.Route.Whitelist",
        "Page.Config.Route.Blacklist",
        "Page.Config.ProxyDomains",
        "Page.Config.DirectDomains",
        "Page.Config.EnableGeo",
        "Page.Config.EnableGfw",
        "Page.Config.RuleResource.Disabled",
        "Page.Config.RuleResource.Download",
        "Page.Config.RuleResource.Downloading",
        "Page.Config.RuleResource.Missing",
        "Page.Config.RuleResource.Preparing",
        "Page.Config.RuleResource.Ready",
        "Page.Config.RuleResource.Retry",
        "Page.Config.Save",
        "Page.Config.SaveRestart",
        "Page.Config.SaveStatus.EndpointRequired",
        "Page.Config.SaveStatus.NoChanges",
        "Page.Config.SaveStatus.Ready",
        "Page.Config.SaveStatus.Saving",
        "Page.Config.ConfigPath",
        "Page.Config.PacHeading",
        "Page.Config.PacDescriptionHtml",
        "Page.Config.CopyAddress",
        "Page.Config.PreviewPac",
        "Page.Config.ApplyPac",
        "Page.Config.ClearPac",
        "Page.Config.PacSet",
        "Page.Config.PacSetFailed",
        "Page.Config.PacCleared",
        "Page.Config.ActionFailed",
        "Page.Config.SaveFailed",
        "Page.Config.ModeUnknown",
        "Page.Dns.Title",
        "Page.Dns.ProxyModeNoticeHtml",
        "Page.Dns.Heading",
        "Page.Dns.SearchPlaceholder",
        "Page.Dns.ClearSearch",
        "Page.Dns.IpAddress",
        "Page.Dns.ResolvedDomain",
        "Page.Dns.SeenCount",
        "Page.Dns.Route",
        "Page.Dns.Reason",
        "Page.Dns.LastActive",
        "Page.Dns.RouteDirect",
        "Page.Dns.RouteProxy",
        "Page.Dns.RouteUnknown",
        "Page.Dns.PrivateIp",
        "Page.Dns.LegendDirect",
        "Page.Dns.LegendGfw",
        "Page.Dns.LegendGeo",
        "Page.Dns.LegendDefault",
        "Page.Dns.Empty",
        "Page.Logs.Title",
        "Page.Logs.Heading",
        "Page.Logs.Pause",
        "Page.Logs.Resume",
        "Page.Logs.Clear",
        "Page.Logs.FilterAll",
        "Page.Logs.FilterConnections",
        "Page.Logs.FilterDns",
        "Page.Logs.FilterWarnings",
        "Page.Logs.FilterErrors",
        "Page.Logs.FilterPlaceholder",
        "Page.Logs.Waiting",
        "Page.Logs.EmptyHtml",
        "Page.Logs.ScrollToLatest",
        "Page.Logs.UpdatedAt",
        "Page.Logs.Reconnecting"
    ];

    public static string NormalizeCultureName(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return "en";
        }

        return cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : "en";
    }

    public static CultureInfo ResolveCulture(string? cultureName)
    {
        var normalized = NormalizeCultureName(cultureName);
        return CultureInfo.GetCultureInfo(normalized);
    }

    public static string Get(string key, string? cultureName = null)
    {
        var culture = ResolveCulture(cultureName);
        return ResourceManager.GetString(key, culture) ?? key;
    }

    public static string GetCurrent(string key)
    {
        return Get(key, CultureInfo.CurrentUICulture.Name);
    }

    public static string Format(string key, string? cultureName, params object[] args)
    {
        var culture = ResolveCulture(cultureName);
        var template = ResourceManager.GetString(key, culture) ?? key;
        return string.Format(culture, template, args);
    }

    public static string FormatCurrent(string key, params object[] args)
    {
        return Format(key, CultureInfo.CurrentUICulture.Name, args);
    }

    public static Dictionary<string, string> GetFrontendCatalog(string? cultureName)
    {
        var culture = ResolveCulture(cultureName);
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in FrontendKeys)
        {
            catalog[key] = ResourceManager.GetString(key, culture) ?? key;
        }

        return catalog;
    }
}
