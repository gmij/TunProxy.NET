using Serilog;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

internal sealed class RuleResourceInitializer
{
    private static readonly TimeSpan BackgroundRetryDelay = TimeSpan.FromSeconds(60);

    private readonly AppConfig _config;
    private readonly GeoIpService? _geoIpService;
    private readonly GfwListService? _gfwListService;
    private readonly Action _incrementDownloading;
    private readonly Action _decrementDownloading;
    private readonly Func<Task>? _proxyReady;

    public RuleResourceInitializer(
        AppConfig config,
        GeoIpService? geoIpService,
        GfwListService? gfwListService,
        Action incrementDownloading,
        Action decrementDownloading,
        Func<Task>? proxyReady = null)
    {
        _config = config;
        _geoIpService = geoIpService;
        _gfwListService = gfwListService;
        _incrementDownloading = incrementDownloading;
        _decrementDownloading = decrementDownloading;
        _proxyReady = proxyReady;
    }

    public bool NeedsInitialization() =>
        BuildInitializationPlan(
            _config,
            _geoIpService?.IsInitialized == true,
            _gfwListService?.IsInitialized == true,
            includeAlreadyInitialized: false).Count > 0;

    public async Task<bool> InitializeEnabledAsync(
        CancellationToken ct,
        ProxyConfig? proxyConfig,
        bool waitForProxyReadyWhenNeeded,
        bool downloadIfMissing,
        bool includeAlreadyInitialized = false)
    {
        var ready = true;
        foreach (var resource in GetResources(includeAlreadyInitialized))
        {
            ready &= await InitializeResourceWithCounterAsync(
                resource,
                ct,
                proxyConfig,
                waitForProxyReadyWhenNeeded,
                downloadIfMissing);
        }

        return ready;
    }

    public void StartBackgroundRetry(CancellationToken ct, ProxyConfig proxyConfig)
    {
        foreach (var resource in GetResources(includeAlreadyInitialized: false))
        {
            _ = Task.Run(
                () => InitializeResourceWithRetryAsync(resource, proxyConfig, ct),
                ct);
        }
    }

    public IReadOnlyList<Task<bool>> StartSetupModeInitialization(CancellationToken ct, ProxyConfig proxyConfig)
    {
        var tasks = GetResources(includeAlreadyInitialized: true)
            .Select(resource => Task.Run(
                () => InitializeSetupModeResourceAsync(resource, proxyConfig, ct),
                ct))
            .ToList();

        if (tasks.Count > 0)
        {
            _ = ObserveSetupModeInitializationAsync(tasks);
        }

        return tasks;
    }

    internal static bool ShouldInitializeResource(
        bool enabled,
        bool isInitialized,
        bool includeAlreadyInitialized) =>
        enabled && (includeAlreadyInitialized || !isInitialized);

    internal static bool ShouldWaitForProxyReady(
        ProxyConfig? proxyConfig,
        bool waitForProxyReadyWhenNeeded) =>
        waitForProxyReadyWhenNeeded && ProxyHttpClientFactory.BuildProxyUri(proxyConfig) == null;

    internal static IReadOnlyList<RuleResourceKind> BuildInitializationPlan(
        AppConfig config,
        bool geoReady,
        bool gfwReady,
        bool includeAlreadyInitialized)
    {
        var resources = new List<RuleResourceKind>(2);
        if (ShouldInitializeResource(config.Route.EnableGeo, geoReady, includeAlreadyInitialized))
        {
            resources.Add(RuleResourceKind.Geo);
        }

        if (ShouldInitializeResource(config.Route.EnableGfwList, gfwReady, includeAlreadyInitialized))
        {
            resources.Add(RuleResourceKind.Gfw);
        }

        return resources;
    }

    private IEnumerable<RuleResource> GetResources(bool includeAlreadyInitialized)
    {
        var plan = BuildInitializationPlan(
            _config,
            _geoIpService?.IsInitialized == true,
            _gfwListService?.IsInitialized == true,
            includeAlreadyInitialized);

        foreach (var kind in plan)
        {
            switch (kind)
            {
                case RuleResourceKind.Geo when _geoIpService != null:
                    yield return new RuleResource(
                        "GEO",
                        () => _geoIpService.IsInitialized,
                        (ct, proxyConfig, downloadIfMissing) =>
                            _geoIpService.InitializeAsync(ct, proxyConfig, downloadIfMissing));
                    break;
                case RuleResourceKind.Gfw when _gfwListService != null:
                    yield return new RuleResource(
                        "GFW",
                        () => _gfwListService.IsInitialized,
                        (ct, proxyConfig, downloadIfMissing) =>
                            _gfwListService.InitializeAsync(ct, proxyConfig, downloadIfMissing));
                    break;
            }
        }
    }

    private async Task<bool> InitializeResourceWithCounterAsync(
        RuleResource resource,
        CancellationToken ct,
        ProxyConfig? proxyConfig,
        bool waitForProxyReadyWhenNeeded,
        bool downloadIfMissing)
    {
        _incrementDownloading();
        try
        {
            await WaitForProxyReadyIfNeededAsync(proxyConfig, waitForProxyReadyWhenNeeded);
            return await resource.Initialize(ct, proxyConfig, downloadIfMissing);
        }
        finally
        {
            _decrementDownloading();
        }
    }

    private async Task InitializeResourceWithRetryAsync(
        RuleResource resource,
        ProxyConfig proxyConfig,
        CancellationToken ct)
    {
        if (resource.IsReady())
        {
            return;
        }

        _incrementDownloading();
        try
        {
            var attempt = 0;
            while (!ct.IsCancellationRequested && !resource.IsReady())
            {
                attempt++;
                try
                {
                    await WaitForProxyReadyIfNeededAsync(proxyConfig, waitForProxyReadyWhenNeeded: true);
                    if (await resource.Initialize(ct, proxyConfig, false))
                    {
                        Log.Information("[{Name}] Rule resource ready after background attempt {Attempt}.", resource.Name, attempt);
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[{Name}] Background initialization attempt {Attempt} failed.", resource.Name, attempt);
                }

                Log.Warning("[{Name}] Rule resource is not ready; retrying in 60 seconds.", resource.Name);
                await Task.Delay(BackgroundRetryDelay, ct);
            }
        }
        finally
        {
            _decrementDownloading();
        }
    }

    private async Task<bool> InitializeSetupModeResourceAsync(
        RuleResource resource,
        ProxyConfig proxyConfig,
        CancellationToken ct)
    {
        _incrementDownloading();
        try
        {
            return await resource.Initialize(ct, proxyConfig, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[{Name}] Rule resource initialization failed in local proxy setup mode.", resource.Name);
            return false;
        }
        finally
        {
            _decrementDownloading();
        }
    }

    private async Task WaitForProxyReadyIfNeededAsync(
        ProxyConfig? proxyConfig,
        bool waitForProxyReadyWhenNeeded)
    {
        if (!ShouldWaitForProxyReady(proxyConfig, waitForProxyReadyWhenNeeded))
        {
            return;
        }

        if (_proxyReady != null)
        {
            await _proxyReady();
        }
    }

    private static async Task ObserveSetupModeInitializationAsync(IReadOnlyList<Task<bool>> initializationTasks)
    {
        try
        {
            var results = await Task.WhenAll(initializationTasks);
            if (!results.All(static ready => ready))
            {
                Log.Warning("[SETUP] Rule resources are not ready yet; staying in the current proxy mode.");
                return;
            }

            Log.Information("[SETUP] Rule resources are ready. GFW/GEO routing can be activated after the next mode restart.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SETUP] Failed while waiting for rule resources.");
        }
    }

    private sealed record RuleResource(
        string Name,
        Func<bool> IsReady,
        Func<CancellationToken, ProxyConfig?, bool, Task<bool>> Initialize);
}

internal enum RuleResourceKind
{
    Geo,
    Gfw
}
