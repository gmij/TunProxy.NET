using TunProxy.Core.Connections;

namespace TunProxy.Core.Configuration;

public class AppConfig
{
    public ProxyConfig Proxy { get; set; } = new();
    public TunConfig Tun { get; set; } = new();
    public LocalProxyConfig LocalProxy { get; set; } = new();
    public RouteConfig Route { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();

    public void ApplyFrom(AppConfig other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Proxy.ApplyFrom(other.Proxy);
        Tun.ApplyFrom(other.Tun);
        LocalProxy.ApplyFrom(other.LocalProxy);
        Route.ApplyFrom(other.Route);
        Logging.ApplyFrom(other.Logging);
    }
}

public class ProxyConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7890;
    public string Type { get; set; } = "Socks5";
    public string? Username { get; set; }
    public string? Password { get; set; }

    public void ApplyFrom(ProxyConfig other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Host = other.Host;
        Port = other.Port;
        Type = other.Type;
        Username = other.Username;
        Password = other.Password;
    }

    public ProxyType GetProxyType() => Type?.ToLowerInvariant() switch
    {
        "socks5" => ProxyType.Socks5,
        "http" => ProxyType.Http,
        _ => ProxyType.Socks5
    };
}

public class TunConfig
{
    public bool Enabled { get; set; }
    public string IpAddress { get; set; } = "10.0.0.1";
    public string SubnetMask { get; set; } = "255.255.255.0";
    public bool AddDefaultRoute { get; set; } = true;
    public string DnsServer { get; set; } = "8.8.8.8";

    /// <summary>
    /// When true the DNS proxy returns fake IPs from the 198.18.0.0/16 block instead of the
    /// real upstream addresses.  The TUN layer intercepts connections to those fake IPs and
    /// forwards them to the correct upstream host via the configured proxy.
    /// Requires <see cref="AddDefaultRoute"/> = true (or a manual 198.18.0.0/16 → TUN route).
    /// </summary>
    public bool FakeIpMode { get; set; } = true;

    public void ApplyFrom(TunConfig other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Enabled = other.Enabled;
        IpAddress = other.IpAddress;
        SubnetMask = other.SubnetMask;
        AddDefaultRoute = other.AddDefaultRoute;
        DnsServer = other.DnsServer;
        FakeIpMode = other.FakeIpMode;
    }
}

public class LocalProxyConfig
{
    private string _systemProxyMode = SystemProxyModes.None;

    public int ListenPort { get; set; } = 8080;
    public bool SetSystemProxy
    {
        get => SystemProxyMode is SystemProxyModes.Pac or SystemProxyModes.Global;
        set
        {
            if (!value)
            {
                SystemProxyMode = SystemProxyModes.None;
            }
            else if (SystemProxyMode == SystemProxyModes.None)
            {
                SystemProxyMode = SystemProxyModes.Pac;
            }
        }
    }

    public string SystemProxyMode
    {
        get => _systemProxyMode;
        set => _systemProxyMode = SystemProxyModes.Normalize(value);
    }

    public string BypassList { get; set; } = "<local>;localhost;127.0.0.1;10.*;192.168.*";
    public SystemProxyBackupConfig SystemProxyBackup { get; set; } = new();

    public void ApplyFrom(LocalProxyConfig other)
    {
        ArgumentNullException.ThrowIfNull(other);

        ListenPort = other.ListenPort;
        SystemProxyMode = other.SystemProxyMode;
        BypassList = other.BypassList;
        SystemProxyBackup.ApplyFrom(other.SystemProxyBackup);
    }
}

public static class SystemProxyModes
{
    public const string Pac = "pac";
    public const string Global = "global";
    public const string Manual = "manual";
    public const string Tun = "tun";
    public const string None = "none";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Pac => Pac,
        Global or Manual => Global,
        Tun => Tun,
        None => None,
        _ => None
    };
}

public class SystemProxyBackupConfig
{
    public bool Captured { get; set; }
    public int ProxyEnable { get; set; }
    public string? ProxyServer { get; set; }
    public string? ProxyOverride { get; set; }
    public string? AutoConfigUrl { get; set; }

    public void ApplyFrom(SystemProxyBackupConfig? other)
    {
        if (other == null)
        {
            Clear();
            return;
        }

        Captured = other.Captured;
        ProxyEnable = other.ProxyEnable;
        ProxyServer = other.ProxyServer;
        ProxyOverride = other.ProxyOverride;
        AutoConfigUrl = other.AutoConfigUrl;
    }

    public SystemProxyBackupConfig Clone() => new()
    {
        Captured = Captured,
        ProxyEnable = ProxyEnable,
        ProxyServer = ProxyServer,
        ProxyOverride = ProxyOverride,
        AutoConfigUrl = AutoConfigUrl
    };

    public void Clear()
    {
        Captured = false;
        ProxyEnable = 0;
        ProxyServer = null;
        ProxyOverride = null;
        AutoConfigUrl = null;
    }
}

public class RouteConfig
{
    public string Mode { get; set; } = "smart";
    public List<string> ProxyDomains { get; set; } = new();
    public List<string> DirectDomains { get; set; } = new();
    public bool EnableGeo { get; set; } = true;
    public List<string> GeoProxy { get; set; } = new();
    public List<string> GeoDirect { get; set; } = new();
    public string GeoIpDbPath { get; set; } = "GeoLite2-Country.mmdb";
    public bool EnableGfwList { get; set; } = true;
    public string GfwListUrl { get; set; } = "https://raw.githubusercontent.com/gfwlist/gfwlist/master/gfwlist.txt";
    public string GfwListPath { get; set; } = "gfwlist.txt";
    public string TunRouteMode { get; set; } = "global";
    public List<string> TunRouteApps { get; set; } = new();
    public bool AutoAddDefaultRoute { get; set; } = true;

    public void ApplyFrom(RouteConfig other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Mode = other.Mode;
        ProxyDomains = [.. other.ProxyDomains];
        DirectDomains = [.. other.DirectDomains];
        EnableGeo = other.EnableGeo;
        GeoProxy = [.. other.GeoProxy];
        GeoDirect = [.. other.GeoDirect];
        GeoIpDbPath = other.GeoIpDbPath;
        EnableGfwList = other.EnableGfwList;
        GfwListUrl = other.GfwListUrl;
        GfwListPath = other.GfwListPath;
        TunRouteMode = other.TunRouteMode;
        TunRouteApps = [.. other.TunRouteApps];
        AutoAddDefaultRoute = other.AutoAddDefaultRoute;
    }
}

public class LoggingConfig
{
    public string MinimumLevel { get; set; } = "Information";
    public string FilePath { get; set; } = "logs/tunproxy-.log";

    public void ApplyFrom(LoggingConfig other)
    {
        ArgumentNullException.ThrowIfNull(other);

        MinimumLevel = other.MinimumLevel;
        FilePath = other.FilePath;
    }
}

public class ServiceStatus
{
    public string Mode { get; set; } = "proxy";
    public bool IsRunning { get; set; }
    public bool IsDownloading { get; set; }
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; }
    public string ProxyType { get; set; } = "";
    public int ActiveConnections { get; set; }
    public Metrics.MetricsSnapshot Metrics { get; set; } = new();
}
