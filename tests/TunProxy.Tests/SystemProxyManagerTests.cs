using Microsoft.Win32;
using TunProxy.CLI;

namespace TunProxy.Tests;

public class SystemProxyManagerTests
{
    [Fact]
    public void RestoreProxy_RestoresOriginalAutoConfigUrl()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var keyPath = $@"Software\TunProxy.Tests\{Guid.NewGuid():N}";
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", "old:80", RegistryValueKind.String);
                key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
                key.SetValue("AutoConfigURL", "http://original/pac", RegistryValueKind.String);
            }

            var manager = new SystemProxyManager(Registry.CurrentUser, keyPath);
            manager.SetProxy("127.0.0.1:8080", "localhost");
            manager.RestoreProxy();

            using var restoredKey = Registry.CurrentUser.OpenSubKey(keyPath, writable: false)!;
            Assert.Equal(0, (int)(restoredKey.GetValue("ProxyEnable") ?? -1));
            Assert.Equal("old:80", restoredKey.GetValue("ProxyServer") as string);
            Assert.Equal("<local>", restoredKey.GetValue("ProxyOverride") as string);
            Assert.Equal("http://original/pac", restoredKey.GetValue("AutoConfigURL") as string);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }
}
