using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using TunProxy.Core.Connections;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

public class Program
{
    public static async Task Main(string[] args)
    {
        // 先用 Information 级别初始化日志，读取配置后再更新
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File("logs/tunproxy-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Log.Error("当前版本仅支持 Windows 系统");
                return;
            }

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

            // 加载配置
            var config = LoadConfig(args);

            // 更新日志配置
            var logLevel = config.Logging.MinimumLevel?.ToLower() switch
            {
                "debug"   => Serilog.Events.LogEventLevel.Debug,
                "warning" => Serilog.Events.LogEventLevel.Warning,
                "error"   => Serilog.Events.LogEventLevel.Error,
                _         => Serilog.Events.LogEventLevel.Information
            };
            await Log.CloseAndFlushAsync();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(logLevel)
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(config.Logging.FilePath ?? "logs/tunproxy-.log", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            // 因为我们重构了 TunProxyService 构造函数接受 AppConfig
            var service = new TunProxyService(config);

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
    private static AppConfig LoadConfig(string[] args)
    {
        var config = new AppConfig();
        var configPath = "tunproxy.json";

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var loaded = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig);
                if (loaded != null) config = loaded;
                Log.Information("配置文件加载成功：{Path}", configPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取配置文件失败，使用默认配置");
            }
        }
        else
        {
            Log.Information("未找到配置文件，创建示例配置：{Path}", configPath);
            var json = JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig);
            File.WriteAllText(configPath, json);
        }

        // 命令行参数覆盖配置文件
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--proxy" or "-p":
                    if (i + 1 < args.Length)
                    {
                        var parts = args[++i].Split(':');
                        config.Proxy.Host = parts[0];
                        if (parts.Length > 1) config.Proxy.Port = int.Parse(parts[1]);
                    }
                    break;
                case "--type" or "-t":
                    if (i + 1 < args.Length) config.Proxy.Type = args[++i];
                    break;
                case "--username" or "-u":
                    if (i + 1 < args.Length) config.Proxy.Username = args[++i];
                    break;
                case "--password" or "-w":
                    if (i + 1 < args.Length) config.Proxy.Password = args[++i];
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        return config;
    }

    private static bool IsAdministrator()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

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
        try { Process.Start(psi); Environment.Exit(0); }
        catch { Console.WriteLine("提权失败，请手动以管理员身份运行本程序"); Environment.Exit(1); }
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
");
    }
}
