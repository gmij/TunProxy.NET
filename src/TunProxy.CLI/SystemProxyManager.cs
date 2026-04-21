using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Serilog;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

[SupportedOSPlatform("windows")]
internal sealed class SystemProxyManager : IDisposable
{
    private int _originalProxyEnable;
    private string? _originalProxyServer;
    private string? _originalBypassList;
    private string? _originalAutoConfigUrl;
    private bool _originalCaptured;
    private bool _applied;

    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private readonly RegistryKey _registryRoot;
    private readonly string _settingsPath;
    private readonly SystemProxyBackupStore? _backupStore;

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    internal SystemProxyManager(
        RegistryKey? registryRoot = null,
        string settingsPath = InternetSettingsKey,
        SystemProxyBackupStore? backupStore = null)
    {
        _registryRoot = registryRoot ?? Registry.CurrentUser;
        _settingsPath = settingsPath;
        _backupStore = backupStore;
    }

    public void SetProxy(string proxyAddress, string bypassList)
    {
        try
        {
            using var key = _registryRoot.CreateSubKey(_settingsPath, writable: true);
            if (key == null)
            {
                Log.Warning("Failed to open the registry key for system proxy settings");
                return;
            }

            CaptureOriginalSettingsIfNeeded(key);

            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", proxyAddress, RegistryValueKind.String);
            key.SetValue("ProxyOverride", bypassList, RegistryValueKind.String);
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);

            NotifyProxyChanged();
            _applied = true;
            Log.Information("System proxy set to {Proxy} with bypass list {Bypass}", proxyAddress, bypassList);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set system proxy");
        }
    }

    public static void SetPacUrl(string pacUrl)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key == null)
            {
                Log.Warning("Failed to open the registry key for PAC settings");
                return;
            }

            key.SetValue("AutoConfigURL", pacUrl, RegistryValueKind.String);
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);

            NotifyProxyChanged();
            Log.Information("System PAC URL set to {Url}", pacUrl);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set PAC URL");
        }
    }

    public static void ClearPacUrl()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key == null)
                return;

            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            NotifyProxyChanged();
            Log.Information("System PAC URL cleared");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clear PAC URL");
        }
    }

    public void RestoreProxy()
    {
        var backup = _backupStore?.GetBackup();
        if (!_applied && backup == null && !_originalCaptured)
            return;

        try
        {
            using var key = _registryRoot.CreateSubKey(_settingsPath, writable: true);
            if (key == null)
                return;

            if (backup != null)
            {
                RestoreFromBackup(key, backup);
                _backupStore?.Clear();
            }
            else
            {
                RestoreFromMemory(key);
            }

            NotifyProxyChanged();
            _applied = false;
            _originalCaptured = false;
            Log.Information("System proxy restored");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore system proxy");
        }
    }

    private static void NotifyProxyChanged()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    private void CaptureOriginalSettingsIfNeeded(RegistryKey key)
    {
        var backup = CaptureBackup(key);
        _backupStore?.SaveIfMissing(backup);
        var effectiveBackup = _backupStore?.GetBackup() ?? backup;

        if (_originalCaptured)
        {
            return;
        }

        _originalProxyEnable = effectiveBackup.ProxyEnable;
        _originalProxyServer = effectiveBackup.ProxyServer;
        _originalBypassList = effectiveBackup.ProxyOverride;
        _originalAutoConfigUrl = effectiveBackup.AutoConfigUrl;
        _originalCaptured = true;
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
        key.SetValue("ProxyEnable", _originalProxyEnable, RegistryValueKind.DWord);

        if (_originalProxyServer != null)
            key.SetValue("ProxyServer", _originalProxyServer, RegistryValueKind.String);
        else
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);

        if (_originalBypassList != null)
            key.SetValue("ProxyOverride", _originalBypassList, RegistryValueKind.String);
        else
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);

        if (_originalAutoConfigUrl != null)
            key.SetValue("AutoConfigURL", _originalAutoConfigUrl, RegistryValueKind.String);
        else
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
    }

    private static void RestoreFromBackup(RegistryKey key, SystemProxyBackupConfig backup)
    {
        key.SetValue("ProxyEnable", backup.ProxyEnable, RegistryValueKind.DWord);

        if (backup.ProxyServer != null)
            key.SetValue("ProxyServer", backup.ProxyServer, RegistryValueKind.String);
        else
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);

        if (backup.ProxyOverride != null)
            key.SetValue("ProxyOverride", backup.ProxyOverride, RegistryValueKind.String);
        else
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);

        if (backup.AutoConfigUrl != null)
            key.SetValue("AutoConfigURL", backup.AutoConfigUrl, RegistryValueKind.String);
        else
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
    }

    public void Dispose() => RestoreProxy();
}
