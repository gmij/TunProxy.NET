using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

public class Program
{
    private const string SvcName = "TunProxyService";

    public static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "logs", "tunproxy-.log"), rollingInterval: RollingInterval.Day)
            .WriteTo.Sink(MemoryLogSink.Instance)
            .CreateLogger();

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (args.Contains("--install"))
                {
                    InstallService();
                    return;
                }

                if (args.Contains("--uninstall"))
                {
                    UninstallService();
                    return;
                }
            }

            var config = LoadConfig(args);

            var configuredTunMode = config.Tun.Enabled;
            var invalidRuleResources = configuredTunMode
                ? GetInvalidRuleResourceMessages(config)
                : [];
            var bootstrapProxyMode = configuredTunMode && invalidRuleResources.Count > 0;
            var tunMode = configuredTunMode && !bootstrapProxyMode;

            if (bootstrapProxyMode)
            {
                Log.Warning(
                    "TUN mode is configured but rule resources are missing or invalid. Starting local proxy setup mode first: {Resources}",
                    string.Join("; ", invalidRuleResources));
            }

            if (tunMode && !IsAdministrator())
            {
                Log.Information("TUN mode requires elevated privileges. Attempting automatic elevation...");
                if (OperatingSystem.IsWindows())
                {
                    RunAsAdministrator();
                }
                else
                {
                    Log.Error("Please run as root: sudo {Exe} [args]", Environment.ProcessPath);
                }

                return;
            }

            Log.Information("========================================");
            Log.Information("TunProxy .NET 8 - {Mode}", tunMode ? "TUN mode" : bootstrapProxyMode ? "Local Proxy setup mode" : "Local Proxy mode");
            Log.Information("Version: {Version}", typeof(Program).Assembly.GetName().Version);
            Log.Information("========================================");

            var logLevel = config.Logging.MinimumLevel?.ToLowerInvariant() switch
            {
                "debug" => Serilog.Events.LogEventLevel.Debug,
                "warning" => Serilog.Events.LogEventLevel.Warning,
                "error" => Serilog.Events.LogEventLevel.Error,
                _ => Serilog.Events.LogEventLevel.Information
            };

            await Log.CloseAndFlushAsync();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(logLevel)
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    ResolveLogFilePath(config.Logging.FilePath),
                    rollingInterval: RollingInterval.Day)
                .WriteTo.Sink(MemoryLogSink.Instance)
                .CreateLogger();
            Log.Information("File logging path: {Path}", ResolveLogFilePath(config.Logging.FilePath));

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });
            Log.Information("Web host builder created. ContentRoot={ContentRoot}", AppContext.BaseDirectory);

            if (OperatingSystem.IsWindows())
            {
                Log.Information("Configuring Windows service integration. UserInteractive={UserInteractive}", Environment.UserInteractive);
                builder.Host.UseWindowsService(options =>
                {
                    options.ServiceName = SvcName;
                });
            }

            builder.Host.UseSerilog();
            Log.Information("Serilog host integration configured.");

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenLocalhost(50000);
            });
            Log.Information("Kestrel configured on http://localhost:50000.");

            builder.Services.AddSingleton(config);
            builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

            if (tunMode)
            {
                Log.Information("Registering TUN proxy hosted service.");
                builder.Services.AddSingleton<TunProxyService>();
                builder.Services.AddSingleton<IProxyService>(sp => sp.GetRequiredService<TunProxyService>());
                builder.Services.AddHostedService<ProxyHostedService<TunProxyService>>();
            }
            else
            {
                Log.Information("Registering local proxy hosted service.");
                builder.Services.AddSingleton<LocalProxyService>();
                builder.Services.AddSingleton<IProxyService>(sp => sp.GetRequiredService<LocalProxyService>());
                builder.Services.AddHostedService<ProxyHostedService<LocalProxyService>>();
            }

            Log.Information("Building web application...");
            var app = builder.Build();
            Log.Information("Web application built. Mapping endpoints...");
            app.MapApiEndpoints();
            Log.Information("Endpoints mapped. Starting web application...");
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application exited unexpectedly");
            Environment.Exit(1);
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static AppConfig LoadConfig(string[] args)
    {
        var config = new AppConfig();
        var configPath = Path.Combine(AppContext.BaseDirectory, "tunproxy.json");

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var loaded = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig);
                if (loaded != null)
                {
                    config = loaded;
                }

                Log.Information("Configuration loaded from {Path}", configPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read configuration file. Using defaults.");
            }
        }
        else
        {
            Log.Information("Configuration file not found. Creating sample file at {Path}", configPath);
            ApplyWindowsSystemProxyDefaults(config);
            var json = JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig);
            File.WriteAllText(configPath, json);
        }

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--proxy" or "-p":
                    if (i + 1 < args.Length)
                    {
                        var parts = args[++i].Split(':');
                        config.Proxy.Host = parts[0];
                        if (parts.Length > 1)
                        {
                            config.Proxy.Port = int.Parse(parts[1]);
                        }
                    }
                    break;

                case "--type" or "-t":
                    if (i + 1 < args.Length)
                    {
                        config.Proxy.Type = args[++i];
                    }
                    break;

                case "--username" or "-u":
                    if (i + 1 < args.Length)
                    {
                        config.Proxy.Username = args[++i];
                    }
                    break;

                case "--password" or "-w":
                    if (i + 1 < args.Length)
                    {
                        config.Proxy.Password = args[++i];
                    }
                    break;
            }
        }

        return config;
    }

    private static string ResolveLogFilePath(string? configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? "logs/tunproxy-.log"
            : configuredPath;

        return AppPathResolver.ResolveAppFilePath(path);
    }

    private static void ApplyWindowsSystemProxyDefaults(AppConfig config)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var systemProxy = new WindowsSystemProxyConfig().ReadProxyConfig();
        if (systemProxy == null)
        {
            Log.Information("[CONFIG] Windows system proxy is not enabled or cannot be converted. Keeping default upstream proxy.");
            return;
        }

        config.Proxy.ApplyFrom(systemProxy);
        Log.Information(
            "[CONFIG] Initialized upstream proxy from Windows system proxy: {Type} {Host}:{Port}",
            config.Proxy.Type,
            config.Proxy.Host,
            config.Proxy.Port);
    }

    private static bool IsAdministrator()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        return Environment.GetEnvironmentVariable("USER") == "root"
            || Environment.GetEnvironmentVariable("LOGNAME") == "root";
    }

    private static void RunAsAdministrator()
    {
        var exePath = Environment.ProcessPath!;
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var processStartInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = string.Join(" ", args.Select(arg => arg.Contains(" ") ? $"\"{arg}\"" : arg)),
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            Process.Start(processStartInfo);
            Environment.Exit(0);
        }
        catch
        {
            Console.WriteLine("Elevation failed. Please run the application as administrator.");
            Environment.Exit(1);
        }
    }

    private static void InstallService()
    {
        var exePath = Environment.ProcessPath!;
        Log.Information("Installing Windows service from {Path}", exePath);

        var config = LoadConfig([]);
            var invalidRuleResources = GetInvalidRuleResourceMessages(config);
            if (invalidRuleResources.Count > 0)
            {
                Log.Warning(
                    "TUN service will be installed in setup mode because rule resources are missing or invalid: {Resources}",
                    string.Join("; ", invalidRuleResources));
            }

        RunSc($"create {SvcName} binPath= \"{exePath}\" start= auto DisplayName= \"TunProxy Service\"");
        RunSc($"description {SvcName} \"TUN-mode transparent proxy service for Windows\"");
        RunSc($"sdset {SvcName} D:(A;;CCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;LCRPWPCR;;;IU)");
        RunSc($"failure {SvcName} reset= 60 actions= restart/1000");
        RunSc($"failureflag {SvcName} 1");

        Log.Information("Service installation completed. Use sc start {Name} or the tray menu to start it.", SvcName);
        SetTunEnabledInConfig(true);
    }

    private static void UninstallService()
    {
        Log.Information("Uninstalling service {Name}...", SvcName);
        RunSc($"stop {SvcName}");
        Thread.Sleep(2000);
        RunSc($"delete {SvcName}");
        Log.Information("Service uninstalled.");

        SetTunEnabledInConfig(false);
    }

    private static IReadOnlyList<string> GetInvalidRuleResourceMessages(AppConfig config)
    {
        var invalid = new List<string>();
        if (config.Route.EnableGeo)
        {
            using var geo = new GeoIpService(config.Route.GeoIpDbPath);
            if (!geo.HasValidDatabase())
            {
                invalid.Add($"GEO database: {geo.DatabasePath}");
            }
        }

        if (config.Route.EnableGfwList)
        {
            var gfw = new GfwListService(config.Route.GfwListUrl, config.Route.GfwListPath);
            if (!gfw.HasValidListAsync().GetAwaiter().GetResult())
            {
                invalid.Add($"GFW list: {gfw.ListPath}");
            }
        }

        return invalid;
    }

    private static void SetTunEnabledInConfig(bool enabled)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "tunproxy.json");
        if (!File.Exists(configPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig) ?? new AppConfig();
            config.Tun.Enabled = enabled;
            File.WriteAllText(configPath, JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig));
            Log.Information("Updated tun.enabled to {Enabled}", enabled);
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to update configuration file: {Message}", ex.Message);
        }
    }

    private static void RunSc(string args)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        });
        process?.WaitForExit(10000);
    }
}

public class ProxyHostedService<T> : IHostedService where T : IProxyService
{
    private readonly T _proxyService;
    private CancellationTokenSource? _cts;
    private Task? _executingTask;

    public ProxyHostedService(T proxyService)
    {
        _proxyService = proxyService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        Log.Information("Hosted proxy service starting: {ServiceType}", typeof(T).Name);
        _executingTask = _proxyService.StartAsync(_cts.Token);
        _ = _executingTask.ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                {
                    Log.Error(task.Exception, "Hosted proxy service faulted: {ServiceType}", typeof(T).Name);
                }
                else if (task.IsCanceled)
                {
                    Log.Information("Hosted proxy service canceled: {ServiceType}", typeof(T).Name);
                }
                else
                {
                    Log.Information("Hosted proxy service completed: {ServiceType}", typeof(T).Name);
                }
            },
            TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("Hosted proxy service stopping: {ServiceType}", typeof(T).Name);
        if (_executingTask == null)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
        }
        finally
        {
            await Task.WhenAny(_executingTask, Task.Delay(Timeout.Infinite, cancellationToken));
            await _proxyService.StopAsync();
        }
    }
}
