using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Serilog;
using TunProxy.Core;
using TunProxy.Core.Configuration;
using TunProxy.Core.Localization;

namespace TunProxy.CLI;

public static class ApiEndpoints
{
    public static WebApplication MapApiEndpoints(this WebApplication app)
    {
        app.UseStaticFiles();

        app.MapGet("/", () => Results.Redirect("/index.html"));

        app.MapGet("/api/status", (IProxyService svc) =>
            Results.Json(svc.GetStatus(), AppJsonContext.Default.ServiceStatus));

        app.MapGet("/api/config", (AppConfig cfg) =>
            Results.Json(cfg, AppJsonContext.Default.AppConfig));

        app.MapPost("/api/upstream-proxy/check", async (HttpContext ctx, AppConfig cfg, CancellationToken ct) =>
        {
            try
            {
                var proxy = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body,
                    AppJsonContext.Default.ProxyConfig,
                    ct);

                proxy ??= cfg.Proxy;
                return Results.Json(
                    await UpstreamProxyHealthChecker.CheckAsync(proxy, ct),
                    AppJsonContext.Default.UpstreamProxyStatus);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapGet("/api/rule-resources/status", async (AppConfig cfg) =>
            Results.Json(
                await BuildRuleResourcesStatusAsync(cfg),
                AppJsonContext.Default.RuleResourcesStatus));

        app.MapGet("/api/i18n", (HttpContext ctx) =>
        {
            var requestedCulture = ctx.Request.Query["culture"].FirstOrDefault();
            var catalog = LocalizedText.GetFrontendCatalog(requestedCulture);
            return Results.Json(catalog, AppJsonContext.Default.DictionaryStringString);
        });

        app.MapPost("/api/config", async (HttpContext ctx, AppConfig cfg, IProxyService svc, CancellationToken ct) =>
        {
            try
            {
                var incoming = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body,
                    AppJsonContext.Default.AppConfig,
                    ct);

                if (incoming == null)
                {
                    return Results.BadRequest(LocalizedText.Get("Api.InvalidConfigJson"));
                }

                var activeSystemProxyBackup = cfg.LocalProxy.SystemProxyBackup.Clone();
                cfg.ApplyFrom(incoming);
                if (activeSystemProxyBackup.Captured && !incoming.LocalProxy.SystemProxyBackup.Captured)
                {
                    cfg.LocalProxy.SystemProxyBackup.ApplyFrom(activeSystemProxyBackup);
                }
                if (cfg.Tun.Enabled && OperatingSystem.IsWindows())
                {
                    SystemProxyManagerFactory.Create(cfg).DisableForTun();
                }

                await SaveConfigAsync(cfg, ct);

                await svc.RefreshRuleResourcesAsync(ct);

                return Results.NoContent();
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/rule-resources/prepare", async (HttpContext ctx, AppConfig cfg, IProxyService svc, CancellationToken ct) =>
        {
            try
            {
                var effectiveConfig = await ReadEffectiveConfigAsync(ctx, cfg, ct);
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var allReady = true;

                if (effectiveConfig.Route.EnableGeo)
                {
                    using var geo = new GeoIpService(effectiveConfig.Route.GeoIpDbPath);
                    var ready = await geo.InitializeAsync(ct, effectiveConfig.Proxy);
                    result["geo"] = ready ? "ready" : "failed";
                    allReady &= ready;
                }
                else
                {
                    result["geo"] = "disabled";
                }

                if (effectiveConfig.Route.EnableGfwList)
                {
                    var gfw = new GfwListService(effectiveConfig.Route.GfwListUrl, effectiveConfig.Route.GfwListPath);
                    var ready = await gfw.InitializeAsync(ct, effectiveConfig.Proxy);
                    result["gfw"] = ready ? "ready" : "failed";
                    allReady &= ready;
                }
                else
                {
                    result["gfw"] = "disabled";
                }

                if (allReady)
                {
                    await svc.RefreshRuleResourcesAsync(ct);
                }

                return allReady
                    ? Results.Json(result, AppJsonContext.Default.DictionaryStringString)
                    : Results.Problem("One or more enabled rule resources failed to download or load.");
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapPost("/api/rule-resources/download", async (HttpContext ctx, AppConfig cfg, IProxyService svc, CancellationToken ct) =>
        {
            try
            {
                var effectiveConfig = await ReadEffectiveConfigAsync(ctx, cfg, ct);
                var resource = ctx.Request.Query["resource"].FirstOrDefault();
                bool ok;
                switch (resource?.ToLowerInvariant())
                {
                    case "geo":
                        ok = await PrepareGeoResourceAsync(effectiveConfig, ct);
                        break;
                    case "gfw":
                        ok = await PrepareGfwResourceAsync(effectiveConfig, ct);
                        break;
                    case "all":
                    case null:
                    case "":
                        var geoReady = await PrepareGeoResourceAsync(effectiveConfig, ct);
                        var gfwReady = await PrepareGfwResourceAsync(effectiveConfig, ct);
                        ok = geoReady && gfwReady;
                        break;
                    default:
                        ok = false;
                        break;
                }

                if (ok)
                {
                    await svc.RefreshRuleResourcesAsync(ct);
                }

                return ok
                    ? Results.Json(await BuildRuleResourcesStatusAsync(effectiveConfig), AppJsonContext.Default.RuleResourcesStatus)
                    : Results.Problem("Rule resource download or validation failed.");
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapGet("/api/dns-records", async (IProxyService svc, CancellationToken ct) =>
        {
            List<DnsRouteRecord> records = svc is TunProxyService tunService
                ? new List<DnsRouteRecord>(await tunService.GetDnsRouteRecordsAsync(ct))
                : [];

            return Results.Json(records, AppJsonContext.Default.ListDnsRouteRecord);
        });

        app.MapDelete("/api/dns-cache", (IProxyService svc, HttpContext ctx) =>
        {
            if (svc is not TunProxyService tunService)
            {
                return Results.BadRequest("DNS cache is only available in TUN mode.");
            }

            var ip = ctx.Request.Query["ip"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(ip))
            {
                return Results.BadRequest("Missing ip.");
            }

            var domain = ctx.Request.Query["domain"].FirstOrDefault();
            return tunService.ClearDnsCacheEntry(domain, ip)
                ? Results.NoContent()
                : Results.NotFound();
        });

        app.MapGet("/api/diagnostics/tun", (IProxyService svc) =>
        {
            if (svc is not TunProxyService tunService)
            {
                return Results.BadRequest("TUN diagnostics are only available in TUN mode.");
            }

            return Results.Json(
                tunService.GetDiagnostics(),
                AppJsonContext.Default.TunDiagnosticsSnapshot);
        });

        app.MapGet("/api/logs", (HttpContext ctx) =>
        {
            long.TryParse(ctx.Request.Query["after"].FirstOrDefault(), out var after);
            return Results.Json(
                MemoryLogSink.Instance.GetEntriesAfter(after),
                AppJsonContext.Default.LogEntryArray);
        });

        app.MapGet("/proxy.pac", async (AppConfig cfg) =>
            Results.Content(
                await PacGenerator.GenerateAsync(cfg),
                "application/x-ns-proxy-autoconfig"));

        app.MapPost("/api/set-pac", async (HttpContext ctx, AppConfig cfg, CancellationToken ct) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return Results.BadRequest(LocalizedText.Get("Api.WindowsOnly"));
            }

            cfg.LocalProxy.SystemProxyMode = SystemProxyModes.Pac;
            cfg.Tun.Enabled = false;
            await SaveConfigAsync(cfg, ct);

            var pacUrl = $"http://127.0.0.1:{ctx.Connection.LocalPort}/proxy.pac";
            var manager = SystemProxyManagerFactory.Create(cfg);
            manager.SetPacUrl(pacUrl);
            return Results.Json(
                new Dictionary<string, string> { ["url"] = pacUrl },
                AppJsonContext.Default.DictionaryStringString);
        });

        app.MapPost("/api/clear-pac", async (AppConfig cfg, CancellationToken ct) =>
        {
            cfg.LocalProxy.SystemProxyMode = SystemProxyModes.None;
            cfg.Tun.Enabled = false;
            await SaveConfigAsync(cfg, ct);

            if (OperatingSystem.IsWindows())
            {
                var manager = SystemProxyManagerFactory.Create(cfg);
                manager.ClearPacUrl();
            }

            return Results.Ok();
        });

        app.MapPost("/api/enable-tun", async (AppConfig cfg, IHostApplicationLifetime lifetime, CancellationToken ct) =>
        {
            cfg.Tun.Enabled = true;
            cfg.LocalProxy.SystemProxyMode = SystemProxyModes.Tun;
            if (OperatingSystem.IsWindows())
            {
                SystemProxyManagerFactory.Create(cfg).DisableForTun();
            }
            await SaveConfigAsync(cfg, ct);

            Log.Warning("Manual TUN mode repair triggered. Configuration updated; requesting service-mode restart...");
            RequestServiceRestart(lifetime, cfg);

            return Results.Ok();
        });

        app.MapPost("/api/restart", (IHostApplicationLifetime lifetime, AppConfig cfg) =>
        {
            RequestServiceRestart(lifetime, cfg);
            return Results.Json(
                new Dictionary<string, string> { ["status"] = "restarting" },
                AppJsonContext.Default.DictionaryStringString);
        });

        app.MapPost("/api/service/restart", (IHostApplicationLifetime lifetime, AppConfig cfg) =>
        {
            RequestServiceRestart(lifetime, cfg);
            return Results.Json(
                new Dictionary<string, string> { ["status"] = "restarting" },
                AppJsonContext.Default.DictionaryStringString);
        });

        app.MapPost("/api/service/stop", (IHostApplicationLifetime lifetime) =>
        {
            Task.Run(async () =>
            {
                await Task.Delay(300);
                lifetime.StopApplication();
            });

            return Results.Json(
                new Dictionary<string, string> { ["status"] = "stopping" },
                AppJsonContext.Default.DictionaryStringString);
        });

        return app;
    }

    private static void RequestServiceRestart(IHostApplicationLifetime lifetime, AppConfig cfg)
    {
        RequestTrayRestart();
        var helperStarted = TryStartRestartHelper(cfg);
        Log.Information("[RESTART] Web restart requested. HelperStarted={HelperStarted}", helperStarted);

        if (helperStarted)
        {
            Task.Run(async () =>
            {
                await Task.Delay(300);
                lifetime.StopApplication();
            });
        }
        else
        {
            Log.Warning("[RESTART] Restart helper was not started; keeping the current process alive.");
        }
    }

    private static async Task SaveConfigAsync(AppConfig cfg, CancellationToken ct)
    {
        await new AppConfigStore().SaveAsync(cfg, ct);
    }

    private static void RequestTrayRestart()
    {
        try
        {
            File.WriteAllText(AppPaths.RestartRequestPath, DateTimeOffset.UtcNow.ToString("O"));
            Log.Information("[RESTART] Restart marker written for tray: {Path}", AppPaths.RestartRequestPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RESTART] Failed to write restart marker: {Path}", AppPaths.RestartRequestPath);
        }
    }

    private static bool TryStartRestartHelper(AppConfig cfg)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!WindowsServiceManager.IsInstalled() && cfg.Tun.Enabled)
                {
                    return TryStartElevatedServiceInstallHelper();
                }

                var command = WindowsServiceManager.IsInstalled()
                    ? $"timeout /t 2 /nobreak > nul & sc stop {TunProxyProduct.ServiceName} & timeout /t 2 /nobreak > nul & sc start {TunProxyProduct.ServiceName}"
                    : $"timeout /t 2 /nobreak > nul & start \"\" \"{Environment.ProcessPath}\"";
                return TryStartDetachedProcess("cmd.exe", "/c " + command);
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            return TryStartDetachedProcess("/bin/sh", $"-c \"sleep 2; \\\"{exePath}\\\"\"");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RESTART] Failed to start restart helper.");
            return false;
        }
    }

    private static bool TryStartElevatedServiceInstallHelper()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--install",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            Log.Information("[RESTART] Started elevated service installer for TUN mode.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RESTART] Failed to start elevated service installer.");
            return false;
        }
    }

    private static bool TryStartDetachedProcess(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RESTART] Failed to start helper process: {FileName} {Arguments}", fileName, arguments);
            return false;
        }
    }

    private static async Task<RuleResourcesStatus> BuildRuleResourcesStatusAsync(AppConfig cfg)
    {
        using var geo = new GeoIpService(cfg.Route.GeoIpDbPath);
        var gfw = new GfwListService(cfg.Route.GfwListUrl, cfg.Route.GfwListPath);
        var geoExists = File.Exists(geo.DatabasePath);
        var gfwExists = File.Exists(gfw.ListPath);
        return new RuleResourcesStatus(
            new RuleResourceStatus(
                "geo",
                cfg.Route.EnableGeo,
                geo.DatabasePath,
                geoExists,
                geoExists && geo.HasValidDatabase()),
            new RuleResourceStatus(
                "gfw",
                cfg.Route.EnableGfwList,
                gfw.ListPath,
                gfwExists,
                gfwExists && await gfw.HasValidListAsync()));
    }

    private static async Task<AppConfig> ReadEffectiveConfigAsync(HttpContext ctx, AppConfig cfg, CancellationToken ct)
    {
        if (ctx.Request.ContentLength.GetValueOrDefault() <= 0)
        {
            return cfg;
        }

        return await JsonSerializer.DeserializeAsync(
            ctx.Request.Body,
            AppJsonContext.Default.AppConfig,
            ct) ?? cfg;
    }

    private static async Task<bool> PrepareGeoResourceAsync(AppConfig cfg, CancellationToken ct)
    {
        using var geo = new GeoIpService(cfg.Route.GeoIpDbPath);
        return await geo.InitializeAsync(ct, cfg.Proxy);
    }

    private static async Task<bool> PrepareGfwResourceAsync(AppConfig cfg, CancellationToken ct)
    {
        var gfw = new GfwListService(cfg.Route.GfwListUrl, cfg.Route.GfwListPath);
        return await gfw.InitializeAsync(ct, cfg.Proxy);
    }
}
