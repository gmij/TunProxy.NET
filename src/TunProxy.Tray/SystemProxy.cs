using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using TunProxy.Core.Configuration;

namespace TunProxy.Tray;

internal sealed class SystemProxy
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int dwBufferLength);

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    private int _savedEnable;
    private string? _savedServer;
    private string? _savedBypass;
    private string? _savedAutoConfigUrl;
    private bool _saved;
    private bool _applied;
    private readonly string _configPath;

    public bool IsApplied => _applied;

    public SystemProxy(string? appDir = null)
    {
        _configPath = Path.Combine(appDir ?? AppContext.BaseDirectory, "tunproxy.json");
    }

    public bool Set(
        string proxyAddress,
        string bypassList = "localhost;127.*;10.*;172.16.*;192.168.*;<local>")
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key == null)
            {
                return false;
            }

            SaveSnapshotIfNeeded(key);

            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", proxyAddress, RegistryValueKind.String);
            key.SetValue("ProxyOverride", bypassList, RegistryValueKind.String);
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);

            Notify();
            _applied = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void DisableForTun()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key == null)
            {
                return;
            }

            var enabled = (int)(key.GetValue("ProxyEnable", 0) ?? 0);
            var autoConfigUrl = key.GetValue("AutoConfigURL") as string;
            if (enabled == 0 && string.IsNullOrWhiteSpace(autoConfigUrl))
            {
                return;
            }

            SaveSnapshotIfNeeded(key);
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            Notify();
            _applied = true;
        }
        catch
        {
        }
    }

    public bool Restore()
    {
        var backup = ReadPersistentBackup();
        if (!_applied && backup == null && !_saved)
        {
            return true;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key == null)
            {
                return false;
            }

            if (backup != null)
            {
                RestoreFromBackup(key, backup);
                ClearPersistentBackup();
            }
            else
            {
                RestoreFromMemory(key);
            }

            Notify();
            _applied = false;
            _saved = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveSnapshotIfNeeded(RegistryKey key)
    {
        var backup = CaptureBackup(key);
        SavePersistentBackupIfMissing(backup);
        var effectiveBackup = ReadPersistentBackup() ?? backup;

        if (_saved)
        {
            return;
        }

        _savedEnable = effectiveBackup.ProxyEnable;
        _savedServer = effectiveBackup.ProxyServer;
        _savedBypass = effectiveBackup.ProxyOverride;
        _savedAutoConfigUrl = effectiveBackup.AutoConfigUrl;
        _saved = true;
    }

    private static SystemProxyBackupConfig CaptureBackup(RegistryKey key) => new()
    {
        Captured = true,
        ProxyEnable = Convert.ToInt32(key.GetValue("ProxyEnable", 0) ?? 0),
        ProxyServer = key.GetValue("ProxyServer") as string,
        ProxyOverride = key.GetValue("ProxyOverride") as string,
        AutoConfigUrl = key.GetValue("AutoConfigURL") as string
    };

    private void RestoreFromMemory(RegistryKey key)
    {
        key.SetValue("ProxyEnable", _savedEnable, RegistryValueKind.DWord);

        if (_savedServer != null)
        {
            key.SetValue("ProxyServer", _savedServer, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        }

        if (_savedBypass != null)
        {
            key.SetValue("ProxyOverride", _savedBypass, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
        }

        if (_savedAutoConfigUrl != null)
        {
            key.SetValue("AutoConfigURL", _savedAutoConfigUrl, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        }
    }

    private static void RestoreFromBackup(RegistryKey key, SystemProxyBackupConfig backup)
    {
        key.SetValue("ProxyEnable", backup.ProxyEnable, RegistryValueKind.DWord);

        if (backup.ProxyServer != null)
        {
            key.SetValue("ProxyServer", backup.ProxyServer, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        }

        if (backup.ProxyOverride != null)
        {
            key.SetValue("ProxyOverride", backup.ProxyOverride, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
        }

        if (backup.AutoConfigUrl != null)
        {
            key.SetValue("AutoConfigURL", backup.AutoConfigUrl, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        }
    }

    private SystemProxyBackupConfig? ReadPersistentBackup()
    {
        var config = LoadConfig();
        return config?.LocalProxy.SystemProxyBackup.Captured == true
            ? config.LocalProxy.SystemProxyBackup.Clone()
            : null;
    }

    private void SavePersistentBackupIfMissing(SystemProxyBackupConfig backup)
    {
        var config = LoadConfig();
        if (config == null || config.LocalProxy.SystemProxyBackup.Captured)
        {
            return;
        }

        config.LocalProxy.SystemProxyBackup.ApplyFrom(backup);
        SaveConfig(config);
    }

    private void ClearPersistentBackup()
    {
        var config = LoadConfig();
        if (config == null)
        {
            return;
        }

        config.LocalProxy.SystemProxyBackup.Clear();
        SaveConfig(config);
    }

    private AppConfig? LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return null;
            }

            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize(json, TrayJsonContext.Default.AppConfig);
        }
        catch
        {
            return null;
        }
    }

    private void SaveConfig(AppConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_configPath, JsonSerializer.Serialize(config, TrayJsonContext.Default.AppConfig));
        }
        catch
        {
        }
    }

    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }
}
