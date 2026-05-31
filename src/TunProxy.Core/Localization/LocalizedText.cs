using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace TunProxy.Core.Localization;

public static class LocalizedText
{
    private const string DefaultCultureName = "en";
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Catalogs =
        new(LoadCatalogs);

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
        "Shared.On",
        "Shared.Off",
        "Shared.Healthy",
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
        "Page.Status.ModeTunDescription",
        "Page.Status.ProxyServer",
        "Page.Status.ActiveConnections",
        "Page.Status.Uptime",
        "Page.Status.Traffic",
        "Page.Status.FakeIp",
        "Page.Status.PacketRate",
        "Page.Status.RollingWindow",
        "Page.Status.TrafficChartTitle",
        "Page.Status.TrafficChartDescription",
        "Page.Status.Wintun",
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
        "Page.Status.TunSendAllocationRetryAttempts",
        "Page.Status.TunSendAllocationDrops",
        "Page.Status.ConnectIssue.Title",
        "Page.Status.ConnectIssue.OccurredAt",
        "Page.Status.ConnectIssue.Reason.ProxyDenied",
        "Page.Status.ConnectIssue.Reason.ConnectFailed",
        "Page.Status.ConnectIssue.Reason.DnsFailed",
        "Page.Status.ConnectIssue.Reason.Generic",
        "Page.Status.ConnectIssue.Hint.ProxyDenied",
        "Page.Status.ConnectIssue.Hint.ConnectFailed",
        "Page.Status.ConnectIssue.Hint.DnsFailed",
        "Page.Status.ConnectIssue.Hint.Generic",
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
        "Page.Config.SystemProxyMode.TunDescription",
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
        "Page.Config.ProxyStatus.CheckCompleted",
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
        "Page.Config.RuleResource.AlreadyLatest",
        "Page.Config.RuleResource.Refreshed",
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
        "Page.Dns.DnsCache",
        "Page.Dns.EmptyApi",
        "Page.Dns.ClearCache",
        "Page.Dns.RouteStepCache",
        "Page.Dns.RouteStepDirect",
        "Page.Dns.RouteStepProxy",
        "Page.Dns.Records",
        "Page.Dns.RecordsSummary",
        "Page.Dns.LegendDirect",
        "Page.Dns.LegendGfw",
        "Page.Dns.LegendGeo",
        "Page.Dns.LegendDefault",
        "Page.Dns.Empty",
        "Page.Dns.DoHNoticeTitle",
        "Page.Dns.DoHNoticeBody",
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
        "Page.Logs.Reconnecting",
        "Page.Logs.TerminalMeta"
    ];

    public static string NormalizeCultureName(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return DefaultCultureName;
        }

        return cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : DefaultCultureName;
    }

    public static CultureInfo ResolveCulture(string? cultureName)
    {
        var normalized = NormalizeCultureName(cultureName);
        return CultureInfo.GetCultureInfo(normalized);
    }

    public static string Get(string key, string? cultureName = null)
    {
        var cultureNameOrDefault = NormalizeCultureName(cultureName);
        return ResolveString(key, cultureNameOrDefault);
    }

    public static string GetCurrent(string key)
    {
        return Get(key, CultureInfo.CurrentUICulture.Name);
    }

    public static string Format(string key, string? cultureName, params object[] args)
    {
        var culture = ResolveCulture(cultureName);
        var template = ResolveString(key, culture.Name);
        return string.Format(culture, template, args);
    }

    public static string FormatCurrent(string key, params object[] args)
    {
        return Format(key, CultureInfo.CurrentUICulture.Name, args);
    }

    public static Dictionary<string, string> GetFrontendCatalog(string? cultureName)
    {
        var cultureNameOrDefault = NormalizeCultureName(cultureName);
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in FrontendKeys)
        {
            catalog[key] = ResolveString(key, cultureNameOrDefault);
        }

        return catalog;
    }

    private static string ResolveString(string key, string cultureName)
    {
        return TryGetString(key, cultureName)
            ?? TryGetString(key, DefaultCultureName)
            ?? key;
    }

    private static string? TryGetString(string key, string cultureName)
    {
        return Catalogs.Value.TryGetValue(cultureName, out var catalog) && catalog.TryGetValue(key, out var value)
            ? value
            : null;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadCatalogs()
    {
        var assembly = typeof(LocalizedText).Assembly;
        var catalogs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cultureName in SupportedCultures)
        {
            var fileName = cultureName == DefaultCultureName
                ? "Strings.json"
                : $"Strings.{cultureName}.json";
            using var stream = OpenCatalogStream(assembly, fileName)
                ?? throw new InvalidOperationException($"Embedded localization catalog '{fileName}' was not found.");

            catalogs[cultureName] = ReadCatalog(stream, fileName);
        }

        return catalogs;
    }

    private static Stream? OpenCatalogStream(Assembly assembly, string fileName)
    {
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith($".Localization.{fileName}", StringComparison.Ordinal));

        return resourceName is null ? null : assembly.GetManifestResourceStream(resourceName);
    }

    private static IReadOnlyDictionary<string, string> ReadCatalog(Stream stream, string fileName)
    {
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Localization catalog '{fileName}' must be a JSON object.");
        }

        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        FlattenCatalogObject(document.RootElement, null, catalog, fileName);

        return catalog;
    }

    private static void FlattenCatalogObject(
        JsonElement element,
        string? prefix,
        Dictionary<string, string> catalog,
        string fileName)
    {
        foreach (var property in element.EnumerateObject())
        {
            var isNodeValue = property.Name == "$value";
            var key = isNodeValue ? prefix : prefix is null ? property.Name : $"{prefix}.{property.Name}";
            if (key is null)
            {
                throw new InvalidDataException($"Localization catalog '{fileName}' cannot define a root $value.");
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (isNodeValue)
                    {
                        throw new InvalidDataException(
                            $"Localization value '{key}' in '{fileName}' must be a string.");
                    }

                    FlattenCatalogObject(property.Value, key, catalog, fileName);
                    break;
                case JsonValueKind.String:
                    if (!catalog.TryAdd(key, property.Value.GetString() ?? string.Empty))
                    {
                        throw new InvalidDataException(
                            $"Localization key '{key}' is duplicated in '{fileName}'.");
                    }

                    break;
                default:
                    throw new InvalidDataException(
                        $"Localization value '{key}' in '{fileName}' must be a string or object.");
            }
        }
    }
}
