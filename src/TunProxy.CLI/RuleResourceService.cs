using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

internal sealed class RuleResourceService
{
    public async Task<RuleResourcesStatus> GetStatusAsync(AppConfig config)
    {
        using var geo = new GeoIpService(config.Route.GeoIpDbPath);
        var gfw = new GfwListService(config.Route.GfwListUrl, config.Route.GfwListPath);
        var geoExists = File.Exists(geo.DatabasePath);
        var gfwExists = File.Exists(gfw.ListPath);

        return new RuleResourcesStatus(
            new RuleResourceStatus(
                "geo",
                config.Route.EnableGeo,
                geo.DatabasePath,
                geoExists,
                geoExists && geo.HasValidDatabase()),
            new RuleResourceStatus(
                "gfw",
                config.Route.EnableGfwList,
                gfw.ListPath,
                gfwExists,
                gfwExists && await gfw.HasValidListAsync()));
    }

    public async Task<RuleResourcePreparationResult> PrepareEnabledAsync(
        AppConfig config,
        IProxyService proxyService,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allReady = true;

        if (config.Route.EnableGeo)
        {
            var ready = await PrepareGeoResourceAsync(config, ct);
            result["geo"] = ready ? "ready" : "failed";
            allReady &= ready;
        }
        else
        {
            result["geo"] = "disabled";
        }

        if (config.Route.EnableGfwList)
        {
            var ready = await PrepareGfwResourceAsync(config, ct);
            result["gfw"] = ready ? "ready" : "failed";
            allReady &= ready;
        }
        else
        {
            result["gfw"] = "disabled";
        }

        if (allReady)
        {
            await proxyService.RefreshRuleResourcesAsync(ct);
        }

        return new RuleResourcePreparationResult(allReady, result);
    }

    public async Task<RuleResourceDownloadResult> DownloadAsync(
        AppConfig config,
        string? resource,
        IProxyService proxyService,
        CancellationToken ct)
    {
        var ok = await PrepareRequestedResourceAsync(config, resource, ct);
        if (ok)
        {
            await proxyService.RefreshRuleResourcesAsync(ct);
        }

        return new RuleResourceDownloadResult(ok, ok ? await GetStatusAsync(config) : null);
    }

    internal static string NormalizeResourceName(string? resource) =>
        string.IsNullOrWhiteSpace(resource) ? "all" : resource.Trim().ToLowerInvariant();

    private static async Task<bool> PrepareRequestedResourceAsync(
        AppConfig config,
        string? resource,
        CancellationToken ct)
    {
        switch (NormalizeResourceName(resource))
        {
            case "geo":
                return await PrepareGeoResourceAsync(config, ct);
            case "gfw":
                return await PrepareGfwResourceAsync(config, ct);
            case "all":
                var geoReady = await PrepareGeoResourceAsync(config, ct);
                var gfwReady = await PrepareGfwResourceAsync(config, ct);
                return geoReady && gfwReady;
            default:
                return false;
        }
    }

    private static async Task<bool> PrepareGeoResourceAsync(AppConfig config, CancellationToken ct)
    {
        using var geo = new GeoIpService(config.Route.GeoIpDbPath);
        return await geo.InitializeAsync(ct, config.Proxy);
    }

    private static async Task<bool> PrepareGfwResourceAsync(AppConfig config, CancellationToken ct)
    {
        var gfw = new GfwListService(config.Route.GfwListUrl, config.Route.GfwListPath);
        return await gfw.InitializeAsync(ct, config.Proxy);
    }
}

internal sealed record RuleResourcePreparationResult(
    bool AllReady,
    Dictionary<string, string> StatusByResource);

internal sealed record RuleResourceDownloadResult(
    bool Succeeded,
    RuleResourcesStatus? Status);
