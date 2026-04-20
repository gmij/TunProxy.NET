using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Serilog;
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

        app.MapPost("/api/config", async (HttpContext ctx, AppConfig cfg) =>
        {
            try
            {
                var incoming = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body,
                    AppJsonContext.Default.AppConfig);

                if (incoming == null)
                {
                    return Results.BadRequest(LocalizedText.Get("Api.InvalidConfigJson"));
                }

                cfg.ApplyFrom(incoming);

                var configPath = Path.Combine(AppContext.BaseDirectory, "tunproxy.json");
                await File.WriteAllTextAsync(
                    configPath,
                    JsonSerializer.Serialize(cfg, AppJsonContext.Default.AppConfig));

                return Results.NoContent();
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/rule-resources/prepare", async (AppConfig cfg, CancellationToken ct) =>
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var proxyUrl = ProxyHttpClientFactory.BuildProxyUri(cfg.Proxy)?.ToString();
            var allReady = true;

            if (cfg.Route.EnableGeo)
            {
                using var geo = new GeoIpService(cfg.Route.GeoIpDbPath);
                var ready = await geo.InitializeAsync(ct, proxyUrl);
                result["geo"] = ready ? "ready" : "failed";
                allReady &= ready;
            }
            else
            {
                result["geo"] = "disabled";
            }

            if (cfg.Route.EnableGfwList)
            {
                var gfw = new GfwListService(cfg.Route.GfwListUrl, cfg.Route.GfwListPath);
                var ready = await gfw.InitializeAsync(ct, proxyUrl);
                result["gfw"] = ready ? "ready" : "failed";
                allReady &= ready;
            }
            else
            {
                result["gfw"] = "disabled";
            }

            return allReady
                ? Results.Json(result, AppJsonContext.Default.DictionaryStringString)
                : Results.Problem("One or more enabled rule resources failed to download or load.");
        });

        app.MapPost("/api/rule-resources/download", async (HttpContext ctx, AppConfig cfg, CancellationToken ct) =>
        {
            var resource = ctx.Request.Query["resource"].FirstOrDefault();
            var proxyUrl = ProxyHttpClientFactory.BuildProxyUri(cfg.Proxy)?.ToString();
            bool ok;
            switch (resource?.ToLowerInvariant())
            {
                case "geo":
                    ok = await PrepareGeoResourceAsync(cfg, proxyUrl, ct);
                    break;
                case "gfw":
                    ok = await PrepareGfwResourceAsync(cfg, proxyUrl, ct);
                    break;
                case "all":
                case null:
                case "":
                    var geoReady = await PrepareGeoResourceAsync(cfg, proxyUrl, ct);
                    var gfwReady = await PrepareGfwResourceAsync(cfg, proxyUrl, ct);
                    ok = geoReady && gfwReady;
                    break;
                default:
                    ok = false;
                    break;
            }

            return ok
                ? Results.Json(await BuildRuleResourcesStatusAsync(cfg), AppJsonContext.Default.RuleResourcesStatus)
                : Results.Problem("Rule resource download or validation failed.");
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

        app.MapGet("/api/direct-ips", (IProxyService svc) =>
            Results.Json(
                new List<string>(svc.GetDirectIps()),
                AppJsonContext.Default.ListString));

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

        app.MapPost("/api/set-pac", (HttpContext ctx) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return Results.BadRequest(LocalizedText.Get("Api.WindowsOnly"));
            }

            var pacUrl = $"http://127.0.0.1:{ctx.Connection.LocalPort}/proxy.pac";
            SystemProxyManager.SetPacUrl(pacUrl);
            return Results.Json(
                new Dictionary<string, string> { ["url"] = pacUrl },
                AppJsonContext.Default.DictionaryStringString);
        });

        app.MapPost("/api/clear-pac", () =>
        {
            if (OperatingSystem.IsWindows())
            {
                SystemProxyManager.ClearPacUrl();
            }

            return Results.Ok();
        });

        app.MapPost("/api/enable-tun", async (AppConfig cfg, IHostApplicationLifetime lifetime) =>
        {
            cfg.Tun.Enabled = true;
            var configPath = Path.Combine(AppContext.BaseDirectory, "tunproxy.json");

            await File.WriteAllTextAsync(
                    configPath,
                    JsonSerializer.Serialize(cfg, AppJsonContext.Default.AppConfig));

            RequestTrayRestart();
            Log.Warning("Manual TUN mode repair triggered. Configuration updated; requesting tray-managed restart...");
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                lifetime.StopApplication();
            });

            return Results.Ok();
        });

        app.MapPost("/api/restart", (IHostApplicationLifetime lifetime) =>
        {
            RequestTrayRestart();
            Task.Run(async () =>
            {
                await Task.Delay(300);
                lifetime.StopApplication();
            });

            return Results.Ok();
        });

        return app;
    }

    private static void RequestTrayRestart()
    {
        var markerPath = Path.Combine(AppContext.BaseDirectory, "tunproxy.restart");
        try
        {
            File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
            Log.Information("[RESTART] Restart marker written for tray: {Path}", markerPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RESTART] Failed to write restart marker: {Path}", markerPath);
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

    private static async Task<bool> PrepareGeoResourceAsync(AppConfig cfg, string? proxyUrl, CancellationToken ct)
    {
        using var geo = new GeoIpService(cfg.Route.GeoIpDbPath);
        return await geo.InitializeAsync(ct, proxyUrl);
    }

    private static async Task<bool> PrepareGfwResourceAsync(AppConfig cfg, string? proxyUrl, CancellationToken ct)
    {
        var gfw = new GfwListService(cfg.Route.GfwListUrl, cfg.Route.GfwListPath);
        return await gfw.InitializeAsync(ct, proxyUrl);
    }
}
