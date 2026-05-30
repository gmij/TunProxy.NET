using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

internal sealed class ConfigWorkflowService
{
    internal const string LegacyDefaultTunIpAddress = "10.0.0.1";
    internal const string CurrentDefaultTunIpAddress = "10.255.0.1";

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
        NormalizeTunDefaultsForSave(config);

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

    internal static void NormalizeTunDefaultsForSave(AppConfig config)
    {
        if (!config.Tun.Enabled ||
            !string.Equals(config.Tun.IpAddress, LegacyDefaultTunIpAddress, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        config.Tun.IpAddress = CurrentDefaultTunIpAddress;
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
