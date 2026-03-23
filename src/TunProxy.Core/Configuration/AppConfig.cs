using TunProxy.Core.Connections;

namespace TunProxy.Core.Configuration;

/// <summary>
/// 应用程序完整配置（JSON 反序列化用）
/// </summary>
public class AppConfig
{
    public ProxyConfig Proxy { get; set; } = new();
    public TunConfig Tun { get; set; } = new();
    public RouteConfig Route { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
}

/// <summary>
/// 代理服务器配置
/// </summary>
public class ProxyConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7890;
    public string Type { get; set; } = "Socks5";
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>
    /// 将字符串 Type 转为 ProxyType 枚举
    /// </summary>
    public ProxyType GetProxyType() => Type?.ToLower() switch
    {
        "socks5" => ProxyType.Socks5,
        "http" => ProxyType.Http,
        _ => ProxyType.Socks5
    };
}

/// <summary>
/// TUN 接口配置
/// </summary>
public class TunConfig
{
    public string IpAddress { get; set; } = "10.0.0.1";
    public string SubnetMask { get; set; } = "255.255.255.0";
    public bool AddDefaultRoute { get; set; } = true;
}

/// <summary>
/// 路由规则配置
/// </summary>
public class RouteConfig
{
    public string Mode { get; set; } = "whitelist";
    public List<string> ProxyDomains { get; set; } = new();
    public List<string> DirectDomains { get; set; } = new();
    public bool EnableGeo { get; set; } = false;
    public List<string> GeoProxy { get; set; } = new();
    public List<string> GeoDirect { get; set; } = new();
    public string GeoIpDbPath { get; set; } = "GeoLite2-Country.mmdb";
    public bool EnableGfwList { get; set; } = false;
    public string GfwListUrl { get; set; } = "https://raw.githubusercontent.com/gfwlist/gfwlist/master/gfwlist.txt";
    public string GfwListPath { get; set; } = "gfwlist.txt";
    public string TunRouteMode { get; set; } = "global";
    public List<string> TunRouteApps { get; set; } = new();
    public bool AutoAddDefaultRoute { get; set; } = true;
}

/// <summary>
/// 日志配置
/// </summary>
public class LoggingConfig
{
    public string MinimumLevel { get; set; } = "Information";
    public string FilePath { get; set; } = "logs/tunproxy-.log";
}

/// <summary>
/// 服务状态信息（用于 Web API）
/// </summary>
public class ServiceStatus
{
    public bool IsRunning { get; set; }
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; }
    public string ProxyType { get; set; } = "";
    public int ActiveConnections { get; set; }
    public Metrics.MetricsSnapshot Metrics { get; set; } = new();
}
