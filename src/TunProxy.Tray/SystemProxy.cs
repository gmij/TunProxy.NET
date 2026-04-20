using System.Runtime.InteropServices;
using Microsoft.Win32;

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
    private bool _applied;

    public bool IsApplied => _applied;

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
        if (!_applied)
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

            Notify();
            _applied = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveSnapshotIfNeeded(RegistryKey key)
    {
        if (_applied)
        {
            return;
        }

        _savedEnable = (int)(key.GetValue("ProxyEnable", 0) ?? 0);
        _savedServer = key.GetValue("ProxyServer") as string;
        _savedBypass = key.GetValue("ProxyOverride") as string;
        _savedAutoConfigUrl = key.GetValue("AutoConfigURL") as string;
    }

    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }
}
