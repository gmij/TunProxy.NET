using Serilog;
using Serilog.Events;
using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using TunProxy.Core;
using TunProxy.Core.Configuration;
using TunProxy.Core.WindowsServices;

namespace TunProxy.CLI;

public class Program
{
    private const string DefaultConsoleOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static async Task Main(string[] args)
    {
        var serilogConfiguration = EmbeddedSerilogConfiguration.Build();
        Log.Logger = CreateLogger(serilogConfiguration);

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

            if (ShouldRunInBackground(args))
            {
                if (TryStartInBackground(args, config))
                {
                    return;
                }
            }

            Log.Information("========================================");
            Log.Information("TunProxy .NET 8 - {Mode}", tunMode ? "TUN mode" : "Local Proxy mode");
            Log.Information("Version: {Version}", typeof(Program).Assembly.GetName().Version);
            Log.Information("========================================");

            await Log.CloseAndFlushAsync();
            serilogConfiguration = EmbeddedSerilogConfiguration.Build(
                config.Logging.FilePath,
                config.Logging.MinimumLevel);
            Log.Logger = CreateLogger(serilogConfiguration);
            Log.Information("File logging path: {Path}", EmbeddedSerilogConfiguration.GetFileSinkPath(serilogConfiguration));

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

            var apiListenHost = ResolveApiListenHost(args);
            builder.WebHost.ConfigureKestrel(options =>
            {
                ConfigureApiListener(options, apiListenHost, TunProxyProduct.ApiPort);
            });
            LogApiBinding(apiListenHost, TunProxyProduct.ApiPort);

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

    internal static bool ShouldRunInBackground(string[] args) =>
        args.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));

    internal static string[] BuildForegroundArguments(string[] args) =>
        args.Where(arg => !string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase)).ToArray();

    internal static string ResolveApiListenHost(string[] args) =>
        ResolveApiListenHost(args, OperatingSystem.IsWindows());

    internal static string ResolveApiListenHost(string[] args, bool isWindows)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--api-host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                throw new InvalidOperationException("--api-host requires a value.");
            }

            return args[i + 1].Trim();
        }

        return isWindows ? "127.0.0.1" : "0.0.0.0";
    }

    internal static string BuildBackgroundLaunchCommand(string executablePath, string[] args)
    {
        var commandParts = new[] { "nohup", QuoteShellArgument(executablePath) }
            .Concat(BuildForegroundArguments(args).Select(QuoteShellArgument));

        return string.Join(" ", commandParts) + " >/dev/null 2>&1 < /dev/null & echo $!";
    }

    internal static Serilog.ILogger CreateLogger(IConfiguration serilogConfiguration)
    {
        var loggerConfiguration = new LoggerConfiguration();
        ApplyMinimumLevels(loggerConfiguration, serilogConfiguration);
        ApplyConfiguredSinks(loggerConfiguration, serilogConfiguration);

        return loggerConfiguration
            .WriteTo.Sink(MemoryLogSink.Instance)
            .CreateLogger();
    }

    private static void ApplyMinimumLevels(LoggerConfiguration loggerConfiguration, IConfiguration configuration)
    {
        var defaultLevel = ParseLogEventLevel(configuration["Serilog:MinimumLevel:Default"]);
        loggerConfiguration.MinimumLevel.Is(defaultLevel);

        foreach (var overrideSection in configuration.GetSection("Serilog:MinimumLevel:Override").GetChildren())
        {
            var level = ParseLogEventLevel(overrideSection.Value);
            loggerConfiguration.MinimumLevel.Override(overrideSection.Key, level);
        }
    }

    private static void ApplyConfiguredSinks(LoggerConfiguration loggerConfiguration, IConfiguration configuration)
    {
        var writeToSection = configuration.GetSection("Serilog:WriteTo");
        var hasConfiguredSink = false;

        foreach (var sinkSection in writeToSection.GetChildren())
        {
            var sinkName = sinkSection["Name"];
            if (string.Equals(sinkName, "Console", StringComparison.OrdinalIgnoreCase))
            {
                loggerConfiguration.WriteTo.Console(
                    outputTemplate: sinkSection["Args:outputTemplate"] ?? DefaultConsoleOutputTemplate);
                hasConfiguredSink = true;
                continue;
            }

            if (string.Equals(sinkName, "File", StringComparison.OrdinalIgnoreCase))
            {
                var path = sinkSection["Args:path"];
                if (!string.IsNullOrWhiteSpace(path))
                {
                    loggerConfiguration.WriteTo.File(
                        path: path,
                        rollingInterval: ParseRollingInterval(sinkSection["Args:rollingInterval"]),
                        retainedFileCountLimit: ParseNullableInt(sinkSection["Args:retainedFileCountLimit"]));
                    hasConfiguredSink = true;
                }
            }
        }

        if (!hasConfiguredSink)
        {
            loggerConfiguration.WriteTo.Console();
        }
    }

    private static LogEventLevel ParseLogEventLevel(string? value) =>
        Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level)
            ? level
            : LogEventLevel.Information;

    private static RollingInterval ParseRollingInterval(string? value) =>
        Enum.TryParse<RollingInterval>(value, ignoreCase: true, out var interval)
            ? interval
            : RollingInterval.Day;

    private static int? ParseNullableInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;

    private static bool TryStartInBackground(string[] args, AppConfig config)
    {
        if (OperatingSystem.IsWindows())
        {
            Log.Warning("--background is only supported on Linux/macOS. Continuing in the foreground.");
            return false;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            Log.Error("Unable to determine the executable path for background launch.");
            return false;
        }

        var command = BuildBackgroundLaunchCommand(executablePath, args);
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            Log.Error("Failed to start the background launcher process.");
            return false;
        }

        process.WaitForExit();
        var launcherOutput = process.StandardOutput.ReadToEnd().Trim();
        var launcherError = process.StandardError.ReadToEnd().Trim();

        if (process.ExitCode != 0)
        {
            Log.Error(
                "Background launch failed with exit code {ExitCode}. {Error}",
                process.ExitCode,
                string.IsNullOrWhiteSpace(launcherError) ? "No error output was captured." : launcherError);
            return false;
        }

        Log.Information(
            "TunProxy is running in the background{PidHint}. Logs will be written to {LogPath}.",
            string.IsNullOrWhiteSpace(launcherOutput) ? string.Empty : $" (pid {launcherOutput})",
            config.Logging.FilePath);
        return true;
    }

    private static void ConfigureApiListener(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions options, string host, int port)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            options.ListenLocalhost(port);
            return;
        }

        if (IsAnyIpHost(host))
        {
            options.ListenAnyIP(port);
            return;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            options.Listen(address, port);
            return;
        }

        throw new InvalidOperationException(
            $"Unsupported --api-host value '{host}'. Use localhost, 0.0.0.0, or a specific IP address.");
    }

    private static void LogApiBinding(string host, int port)
    {
        if (IsAnyIpHost(host))
        {
            Log.Information(
                "Kestrel configured on 0.0.0.0:{Port}. Access locally via http://127.0.0.1:{Port} or remotely via http://<server-ip>:{Port}.",
                port);
            return;
        }

        Log.Information("Kestrel configured on http://{Host}:{Port}.", host, port);
    }

    private static bool IsAnyIpHost(string host) =>
        string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "*", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "::", StringComparison.OrdinalIgnoreCase);

    private static string QuoteShellArgument(string value) =>
        "'" + value.Replace("'", "'\"'\"'") + "'";

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
            if (IsAdministrator())
            {
                WindowsServiceManager.EnsureAutomaticStart();
            }

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
