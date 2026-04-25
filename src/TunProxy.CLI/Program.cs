using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using TunProxy.Core;
using TunProxy.Core.Configuration;
using TunProxy.Core.WindowsServices;

namespace TunProxy.CLI;

public class Program
{
    public static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(AppPaths.DefaultLogFilePath, rollingInterval: RollingInterval.Day)
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

            var tunMode = config.Tun.Enabled;

            if (OperatingSystem.IsWindows() && tunMode && Environment.UserInteractive)
            {
                Log.Information("TUN mode is configured. Starting the Windows service instead of running TUN in an interactive process...");
                DisableSystemProxyForTun(config);
                if (!EnsureTunWindowsService())
                {
                    Log.Error("Failed to start or install the Windows service for TUN mode.");
                }

                return;
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
            Log.Information("TunProxy .NET 8 - {Mode}", tunMode ? "TUN mode" : "Local Proxy mode");
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
                    options.ServiceName = TunProxyProduct.ServiceName;
                });
            }

            builder.Host.UseSerilog();
            Log.Information("Serilog host integration configured.");

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenLocalhost(TunProxyProduct.ApiPort);
            });
            Log.Information("Kestrel configured on {ApiBaseUrl}.", TunProxyProduct.ApiBaseUrl);

            builder.Services.AddSingleton(config);
            builder.Services.AddSingleton<AppConfigStore>();
            builder.Services.AddSingleton<ConfigWorkflowService>();
            builder.Services.AddSingleton<RuleResourceService>();
            builder.Services.AddSingleton(sp =>
                new RestartCoordinator(sp.GetRequiredService<IHostApplicationLifetime>()));
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
        var config = new AppConfigStore().LoadOrCreate(ApplyWindowsSystemProxyDefaults);

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

    [SupportedOSPlatform("windows")]
    private static void InstallService()
    {
        var exePath = Environment.ProcessPath!;
        Log.Information("Installing Windows service from {Path}", exePath);

        WindowsServiceManager.Install(exePath);

        Log.Information("Service installation completed. Use sc start {Name} or the tray menu to start it.", TunProxyProduct.ServiceName);
        SetTunEnabledInConfig(true);
        WindowsServiceManager.StartInstalledService();
    }

    [SupportedOSPlatform("windows")]
    private static void UninstallService()
    {
        Log.Information("Uninstalling service {Name}...", TunProxyProduct.ServiceName);
        WindowsServiceManager.Uninstall();
        Log.Information("Service uninstalled.");

        SetTunEnabledInConfig(false);
    }

    private static void SetTunEnabledInConfig(bool enabled)
    {
        var store = new AppConfigStore();
        if (!File.Exists(store.ConfigPath))
        {
            return;
        }

        try
        {
            var config = store.LoadOrCreate();
            config.Tun.Enabled = enabled;
            if (enabled)
            {
                config.LocalProxy.SystemProxyMode = SystemProxyModes.Tun;
            }
            else if (config.LocalProxy.SystemProxyMode == SystemProxyModes.Tun)
            {
                config.LocalProxy.SystemProxyMode = SystemProxyModes.None;
            }

            store.Save(config);
            Log.Information("Updated tun.enabled to {Enabled}", enabled);
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to update configuration file: {Message}", ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool EnsureTunWindowsService()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (WindowsServiceManager.IsInstalled())
        {
            Log.Information("Windows service is already installed. Starting {Name}...", TunProxyProduct.ServiceName);
            return WindowsServiceManager.StartInstalledService();
        }

        if (IsAdministrator())
        {
            InstallService();
            return true;
        }

        return TryRunElevatedServiceInstaller();
    }

    private static bool TryRunElevatedServiceInstaller()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = "--install",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to start elevated service installer: {Message}", ex.Message);
            return false;
        }
    }

    private static void DisableSystemProxyForTun(AppConfig config)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            SystemProxyManagerFactory.Create(config).DisableForTun();
            new AppConfigStore().Save(config);
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to disable system proxy for TUN mode: {Message}", ex.Message);
        }
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
