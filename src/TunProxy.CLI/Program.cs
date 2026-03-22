using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Serilog;
using TunProxy.Core.Connections;
using TunProxy.Core.Packets;
using TunProxy.Core.Wintun;

namespace TunProxy.CLI;

public class Program
{
    public static async Task Main(string[] args)
    {
        // 配置 Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File("logs/tunproxy-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            // 检查管理员权限
            if (!IsAdministrator())
            {
                Log.Information("未检测到管理员权限，尝试自动提权...");
                RunAsAdministrator();
                return;
            }

            Log.Information("========================================");
            Log.Information("TunProxy .NET 8 TUN 代理");
            Log.Information("版本：{Version}", typeof(Program).Assembly.GetName().Version);
            Log.Information("========================================");

            // 加载配置文件
            var config = LoadConfig(args);

            Log.Information("代理配置：{Host}:{Port} ({Type})", 
                config.ProxyHost, config.ProxyPort, config.ProxyType);

            // 加载路由配置
            var routeConfig = new RouteConfig();
            var configPath = "tunproxy.json";
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    var appConfig = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json);
                    routeConfig = appConfig?.Route ?? routeConfig;
                }
                catch { }
            }

            var service = new TunProxyService(
                config.ProxyHost, 
                config.ProxyPort, 
                config.ProxyType, 
                config.Username, 
                config.Password,
                routeConfig.GeoProxy,
                routeConfig.GeoDirect,
                routeConfig.GeoIpDbPath,
                routeConfig.EnableGfwList,
                routeConfig.GfwListUrl,
                routeConfig.GfwListPath);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            await service.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "程序异常退出");
            Console.WriteLine($"错误：{ex.Message}");
            Environment.Exit(1);
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// 加载配置（配置文件 + 命令行参数）
    /// </summary>
    private static Config LoadConfig(string[] args)
    {
        var config = new Config();

        // 1. 读取配置文件
        var configPath = "tunproxy.json";
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var appConfig = JsonSerializer.Deserialize<AppConfig>(json);
                if (appConfig?.Proxy != null)
                {
                    config.ProxyHost = appConfig.Proxy.Host ?? config.ProxyHost;
                    config.ProxyPort = appConfig.Proxy.Port;
                    config.ProxyType = appConfig.Proxy.Type?.ToLower() switch
                    {
                        "socks5" => TunProxy.Core.Connections.ProxyType.Socks5,
                        "http" => TunProxy.Core.Connections.ProxyType.Http,
                        _ => TunProxy.Core.Connections.ProxyType.Socks5
                    };
                    config.Username = appConfig.Proxy.Username;
                    config.Password = appConfig.Proxy.Password;
                }

                Log.Information("配置文件加载成功：{Path}", configPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取配置文件失败，使用默认配置");
            }
        }
        else
        {
            Log.Information("未找到配置文件，使用默认配置");
            Log.Information("创建示例配置文件：{Path}", configPath);
            CreateSampleConfig(configPath);
        }

        // 2. 命令行参数覆盖配置文件
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--proxy" or "-p":
                    if (i + 1 < args.Length)
                    {
                        var parts = args[++i].Split(':');
                        config.ProxyHost = parts[0];
                        if (parts.Length > 1)
                            config.ProxyPort = int.Parse(parts[1]);
                    }
                    break;
                case "--type" or "-t":
                    if (i + 1 < args.Length)
                    {
                        config.ProxyType = args[++i].ToLower() switch
                        {
                            "socks5" => TunProxy.Core.Connections.ProxyType.Socks5,
                            "http" => TunProxy.Core.Connections.ProxyType.Http,
                            _ => TunProxy.Core.Connections.ProxyType.Socks5
                        };
                    }
                    break;
                case "--username" or "-u":
                    if (i + 1 < args.Length)
                        config.Username = args[++i];
                    break;
                case "--password" or "-w":
                    if (i + 1 < args.Length)
                        config.Password = args[++i];
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                case "--diagnose" or "-d":
                    // 路由诊断模式
                    RunRouteDiagnosis();
                    Environment.Exit(0);
                    break;
            }
        }

        return config;
    }

    /// <summary>
    /// 创建示例配置文件
    /// </summary>
    private static void CreateSampleConfig(string path)
    {
        var config = new AppConfig
        {
            Proxy = new ProxyConfig
            {
                Host = "127.0.0.1",
                Port = 7890,
                Type = "Socks5",
                Username = null,
                Password = null
            },
            Tun = new TunConfig
            {
                IpAddress = "10.0.0.1",
                SubnetMask = "255.255.255.0",
                AddDefaultRoute = true
            },
            Route = new RouteConfig
            {
                Mode = "whitelist",
                ProxyDomains = new List<string> { "google.com", "github.com", "stackoverflow.com" },
                DirectDomains = new List<string> { "cn", "com.cn", "163.com", "qq.com" },
                GeoProxy = new List<string> { "US", "JP", "SG", "HK" }, // 这些国家走代理
                GeoDirect = new List<string> { "CN" }, // 中国直连
                GeoIpDbPath = "GeoLite2-Country.mmdb",
                EnableGfwList = true, // 启用 GFWList
                GfwListUrl = "https://raw.githubusercontent.com/gfwlist/gfwlist/master/gfwlist.txt",
                GfwListPath = "gfwlist.txt",
                TunRouteMode = "global", // global, smart, manual
                TunRouteApps = new List<string> { "chrome.exe", "firefox.exe", "msedge.exe" },
                AutoAddDefaultRoute = true
            },
            Logging = new LoggingConfig
            {
                MinimumLevel = "Information",
                FilePath = "logs/tunproxy-.log"
            }
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(config, options);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// 检查是否为管理员
    /// </summary>
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// 以管理员身份重新启动
    /// </summary>
    private static void RunAsAdministrator()
    {
        var exePath = Environment.ProcessPath!;
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = string.Join(" ", args.Select(a => a.Contains(" ") ? $"\"{a}\"" : a)),
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            Process.Start(psi);
            Log.Information("已启动管理员进程");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "提权失败，请手动以管理员身份运行");
            Console.WriteLine("提权失败，请手动以管理员身份运行本程序");
            Environment.Exit(1);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"
TunProxy - .NET 8 TUN 代理

用法：TunProxy.CLI [选项]

配置文件：
  tunproxy.json - 首次运行自动创建，可修改代理配置

选项:
  -p, --proxy <host:port>     代理服务器地址 (覆盖配置文件)
  -t, --type <type>           代理类型：socks5, http (覆盖配置文件)
  -u, --username <username>   代理用户名 (可选)
  -w, --password <password>   代理密码 (可选)
  -h, --help                  显示帮助

示例:
  # 使用配置文件启动
  TunProxy.CLI.exe

  # 命令行覆盖配置
  TunProxy.CLI.exe -p 192.168.1.100:8080 -t http
  TunProxy.CLI.exe -p 127.0.0.1:7890 -t socks5 -u user -w pass

注意：需要管理员权限运行
");
    }
}

/// <summary>
/// 运行时配置类
/// </summary>
public class Config
{
    public string ProxyHost { get; set; } = "127.0.0.1";
    public int ProxyPort { get; set; } = 7890;
    public TunProxy.Core.Connections.ProxyType ProxyType { get; set; } = TunProxy.Core.Connections.ProxyType.Socks5;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

/// <summary>
/// 配置文件结构（用于序列化）
/// </summary>
public class AppConfig
{
    public ProxyConfig Proxy { get; set; } = new();
    public TunConfig Tun { get; set; } = new();
    public RouteConfig Route { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
}

public class ProxyConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7890;
    public string Type { get; set; } = "Socks5";
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public class RouteRule
{
    public string Mode { get; set; } = "whitelist"; // whitelist or blacklist
    public List<string> Domains { get; set; } = new();
    public List<string> IpRanges { get; set; } = new();
}

public class TunConfig
{
    public string IpAddress { get; set; } = "10.0.0.1";
    public string SubnetMask { get; set; } = "255.255.255.0";
    public bool AddDefaultRoute { get; set; } = true;
}

public class RouteConfig
{
    public string Mode { get; set; } = "whitelist"; // whitelist, blacklist, or all
    public List<string> ProxyDomains { get; set; } = new(); // 走代理的域名
    public List<string> DirectDomains { get; set; } = new(); // 直连的域名
    public List<string> GeoProxy { get; set; } = new(); // 走代理的国家代码
    public List<string> GeoDirect { get; set; } = new(); // 直连的国家代码
    public string GeoIpDbPath { get; set; } = "GeoLite2-Country.mmdb"; // GeoIP 数据库路径
    public bool EnableGfwList { get; set; } = false; // 是否启用 GFWList
    public string GfwListUrl { get; set; } = "https://raw.githubusercontent.com/gfwlist/gfwlist/master/gfwlist.txt";
    public string GfwListPath { get; set; } = "gfwlist.txt";
    public string TunRouteMode { get; set; } = "global"; // global, smart, manual
    public List<string> TunRouteApps { get; set; } = new(); // 指定应用（进程名）
    public bool AutoAddDefaultRoute { get; set; } = true; // 是否自动添加默认路由
}

public class LoggingConfig
{
    public string MinimumLevel { get; set; } = "Information";
    public string FilePath { get; set; } = "logs/tunproxy-.log";
}
