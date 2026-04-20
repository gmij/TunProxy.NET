using System.Runtime.Versioning;
using Microsoft.Win32;
using Serilog;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

internal sealed class WindowsSystemProxyConfig
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private readonly RegistryKey? _registryRoot;
    private readonly string _settingsPath;

    public WindowsSystemProxyConfig(RegistryKey? registryRoot = null, string settingsPath = InternetSettingsKey)
    {
        _registryRoot = registryRoot;
        _settingsPath = settingsPath;
    }

    [SupportedOSPlatform("windows")]
    public ProxyConfig? ReadProxyConfig()
    {
        try
        {
            var root = _registryRoot ?? Registry.CurrentUser;
            using var key = root.OpenSubKey(_settingsPath, writable: false);
            if (key == null)
            {
                return null;
            }

            var enabled = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0;
            if (!enabled)
            {
                return null;
            }

            var proxyServer = key.GetValue("ProxyServer") as string;
            return ParseProxyServer(proxyServer);
        }
        catch (Exception ex)
        {
            Log.Warning("[CONFIG] Failed to read Windows system proxy settings: {Message}", ex.Message);
            return null;
        }
    }

    internal static ProxyConfig? ParseProxyServer(string? proxyServer)
    {
        if (string.IsNullOrWhiteSpace(proxyServer))
        {
            return null;
        }

        var entries = proxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
        {
            return null;
        }

        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = entry[..separator].Trim();
            var value = entry[(separator + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                mapped[key] = value;
            }
        }

        if (mapped.TryGetValue("socks", out var socksEndpoint) &&
            TryParseEndpoint(socksEndpoint, "Socks5", out var socksConfig))
        {
            return socksConfig;
        }

        if (mapped.TryGetValue("socks5", out var socks5Endpoint) &&
            TryParseEndpoint(socks5Endpoint, "Socks5", out var socks5Config))
        {
            return socks5Config;
        }

        if (mapped.TryGetValue("http", out var httpEndpoint) &&
            TryParseEndpoint(httpEndpoint, "Http", out var httpConfig))
        {
            return httpConfig;
        }

        if (mapped.TryGetValue("https", out var httpsEndpoint) &&
            TryParseEndpoint(httpsEndpoint, "Http", out var httpsConfig))
        {
            return httpsConfig;
        }

        if (mapped.Count > 0)
        {
            return null;
        }

        return TryParseEndpoint(entries[0], InferProxyType(entries[0]), out var config)
            ? config
            : null;
    }

    private static string InferProxyType(string endpoint)
    {
        if (endpoint.StartsWith("socks://", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
        {
            return "Socks5";
        }

        return "Http";
    }

    private static bool TryParseEndpoint(string endpoint, string proxyType, out ProxyConfig config)
    {
        config = new ProxyConfig();
        var normalized = endpoint.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var scheme = proxyType.Equals("Socks5", StringComparison.OrdinalIgnoreCase) ? "socks5" : "http";
        var uriText = normalized.Contains("://", StringComparison.Ordinal)
            ? normalized
            : $"{scheme}://{normalized}";

        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var type = uri.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase)
            ? "Socks5"
            : proxyType;
        var port = uri.IsDefaultPort
            ? type.Equals("Socks5", StringComparison.OrdinalIgnoreCase) ? 1080 : 80
            : uri.Port;

        if (port <= 0 || port > ushort.MaxValue)
        {
            return false;
        }

        config.Host = uri.Host;
        config.Port = port;
        config.Type = type;
        return true;
    }
}
