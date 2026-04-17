using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Serilog;

namespace TunProxy.CLI;

[SupportedOSPlatform("windows")]
internal sealed class SystemProxyManager : IDisposable
{
    private int _originalProxyEnable;
    private string? _originalProxyServer;
    private string? _originalBypassList;
    private string? _originalAutoConfigUrl;
    private bool _applied;

    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private readonly RegistryKey _registryRoot;
    private readonly string _settingsPath;

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    internal SystemProxyManager(RegistryKey? registryRoot = null, string settingsPath = InternetSettingsKey)
    {
        _registryRoot = registryRoot ?? Registry.CurrentUser;
        _settingsPath = settingsPath;
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

            _originalProxyEnable = (int)(key.GetValue("ProxyEnable", 0) ?? 0);
            _originalProxyServer = key.GetValue("ProxyServer") as string;
            _originalBypassList = key.GetValue("ProxyOverride") as string;
            _originalAutoConfigUrl = key.GetValue("AutoConfigURL") as string;

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
        if (!_applied)
            return;

        try
        {
            using var key = _registryRoot.CreateSubKey(_settingsPath, writable: true);
            if (key == null)
                return;

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

            NotifyProxyChanged();
            _applied = false;
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

    public void Dispose() => RestoreProxy();
}
