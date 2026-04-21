using Microsoft.Win32;
using System.Text.Json;
using TunProxy.CLI;
using TunProxy.Core.Configuration;

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

    [Fact]
    public void SetProxy_PersistsOriginalSystemProxyBackupBeforeOverwriting()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var keyPath = $@"Software\TunProxy.Tests\{Guid.NewGuid():N}";
        var configPath = Path.Combine(Path.GetTempPath(), $"tunproxy-{Guid.NewGuid():N}.json");
        try
        {
            var config = new AppConfig();
            File.WriteAllText(configPath, JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig));

            using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
            {
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", "old:80", RegistryValueKind.String);
                key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
                key.SetValue("AutoConfigURL", "http://original/pac", RegistryValueKind.String);
            }

            var store = new SystemProxyBackupStore(config, configPath);
            var manager = new SystemProxyManager(Registry.CurrentUser, keyPath, store);
            manager.SetProxy("127.0.0.1:8080", "localhost");

            var saved = JsonSerializer.Deserialize(
                File.ReadAllText(configPath),
                AppJsonContext.Default.AppConfig);

            Assert.NotNull(saved);
            Assert.True(saved.LocalProxy.SystemProxyBackup.Captured);
            Assert.Equal(1, saved.LocalProxy.SystemProxyBackup.ProxyEnable);
            Assert.Equal("old:80", saved.LocalProxy.SystemProxyBackup.ProxyServer);
            Assert.Equal("<local>", saved.LocalProxy.SystemProxyBackup.ProxyOverride);
            Assert.Equal("http://original/pac", saved.LocalProxy.SystemProxyBackup.AutoConfigUrl);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            File.Delete(configPath);
        }
    }

    [Fact]
    public void RestoreProxy_RestoresPersistedBackupAndClearsIt()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var keyPath = $@"Software\TunProxy.Tests\{Guid.NewGuid():N}";
        var configPath = Path.Combine(Path.GetTempPath(), $"tunproxy-{Guid.NewGuid():N}.json");
        try
        {
            var config = new AppConfig();
            config.LocalProxy.SystemProxyBackup = new SystemProxyBackupConfig
            {
                Captured = true,
                ProxyEnable = 1,
                ProxyServer = "old:80",
                ProxyOverride = "<local>",
                AutoConfigUrl = "http://original/pac"
            };
            File.WriteAllText(configPath, JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig));

            using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
            {
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", "127.0.0.1:8080", RegistryValueKind.String);
                key.SetValue("ProxyOverride", "localhost", RegistryValueKind.String);
                key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            }

            var store = new SystemProxyBackupStore(config, configPath);
            var manager = new SystemProxyManager(Registry.CurrentUser, keyPath, store);
            manager.SetProxy("127.0.0.1:8080", "localhost");
            manager.RestoreProxy();

            using var restoredKey = Registry.CurrentUser.OpenSubKey(keyPath, writable: false)!;
            Assert.Equal(1, (int)(restoredKey.GetValue("ProxyEnable") ?? -1));
            Assert.Equal("old:80", restoredKey.GetValue("ProxyServer") as string);
            Assert.Equal("<local>", restoredKey.GetValue("ProxyOverride") as string);
            Assert.Equal("http://original/pac", restoredKey.GetValue("AutoConfigURL") as string);

            var saved = JsonSerializer.Deserialize(
                File.ReadAllText(configPath),
                AppJsonContext.Default.AppConfig);
            Assert.NotNull(saved);
            Assert.False(saved.LocalProxy.SystemProxyBackup.Captured);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            File.Delete(configPath);
        }
    }
}
