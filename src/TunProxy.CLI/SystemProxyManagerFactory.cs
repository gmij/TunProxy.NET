using System.Runtime.Versioning;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

internal static class SystemProxyManagerFactory
{
    [SupportedOSPlatform("windows")]
    public static SystemProxyManager Create(AppConfig config)
    {
        var backupStore = new SystemProxyBackupStore(config);
        if (!Environment.UserInteractive &&
            WindowsInteractiveUserRegistry.TryGetInternetSettingsPath(out var settingsPath))
        {
            return new SystemProxyManager(Microsoft.Win32.Registry.Users, settingsPath, backupStore);
        }

        return new SystemProxyManager(backupStore: backupStore);
    }
}
