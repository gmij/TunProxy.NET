using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

internal sealed class ConfigWorkflowService
{
    private readonly AppConfigStore _configStore;
    private readonly Action<AppConfig> _disableSystemProxyForTun;

    public ConfigWorkflowService(
        AppConfigStore? configStore = null,
        Action<AppConfig>? disableSystemProxyForTun = null)
    {
        _configStore = configStore ?? new AppConfigStore();
        _disableSystemProxyForTun = disableSystemProxyForTun ?? DisableSystemProxyForTun;
    }

    public async Task ApplyAndSaveAsync(
        AppConfig activeConfig,
        AppConfig incomingConfig,
        IProxyService proxyService,
        CancellationToken ct)
    {
        ApplyIncomingConfigPreservingSystemProxyBackup(activeConfig, incomingConfig);
        await SaveCurrentAsync(activeConfig, ct);
        await proxyService.RefreshRuleResourcesAsync(ct);
    }

    public async Task SaveCurrentAsync(AppConfig config, CancellationToken ct)
    {
        if (config.Tun.Enabled && OperatingSystem.IsWindows())
        {
            _disableSystemProxyForTun(config);
        }

        await _configStore.SaveAsync(config, ct);
    }

    public async Task EnableTunAsync(AppConfig config, CancellationToken ct)
    {
        config.Tun.Enabled = true;
        config.LocalProxy.SystemProxyMode = SystemProxyModes.Tun;
        await SaveCurrentAsync(config, ct);
    }

    internal static void ApplyIncomingConfigPreservingSystemProxyBackup(
        AppConfig activeConfig,
        AppConfig incomingConfig)
    {
        var activeSystemProxyBackup = activeConfig.LocalProxy.SystemProxyBackup.Clone();
        activeConfig.ApplyFrom(incomingConfig);
        if (activeSystemProxyBackup.Captured && !incomingConfig.LocalProxy.SystemProxyBackup.Captured)
        {
            activeConfig.LocalProxy.SystemProxyBackup.ApplyFrom(activeSystemProxyBackup);
        }
    }

    private static void DisableSystemProxyForTun(AppConfig config)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SystemProxyManagerFactory.Create(config).DisableForTun();
    }
}
