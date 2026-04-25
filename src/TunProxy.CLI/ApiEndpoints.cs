using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TunProxy.Core.Configuration;
using TunProxy.Core.Localization;

namespace TunProxy.CLI;

public static class ApiEndpoints
{
    public static WebApplication MapApiEndpoints(this WebApplication app)
    {
        app.UseEmbeddedWebConsole();

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

        app.MapGet("/api/rule-resources/status", async (AppConfig cfg, RuleResourceService ruleResources) =>
            Results.Json(
                await ruleResources.GetStatusAsync(cfg),
                AppJsonContext.Default.RuleResourcesStatus));

        app.MapGet("/api/i18n", (HttpContext ctx) =>
        {
            var requestedCulture = ctx.Request.Query["culture"].FirstOrDefault();
            var catalog = LocalizedText.GetFrontendCatalog(requestedCulture);
            return Results.Json(catalog, AppJsonContext.Default.DictionaryStringString);
        });

        app.MapPost("/api/config", async (
            HttpContext ctx,
            AppConfig cfg,
            IProxyService svc,
            ConfigWorkflowService configWorkflow,
            CancellationToken ct) =>
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

                await configWorkflow.ApplyAndSaveAsync(cfg, incoming, svc, ct);

                return Results.NoContent();
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/rule-resources/prepare", async (
            HttpContext ctx,
            AppConfig cfg,
            IProxyService svc,
            RuleResourceService ruleResources,
            CancellationToken ct) =>
        {
            try
            {
                var effectiveConfig = await ReadEffectiveConfigAsync(ctx, cfg, ct);
                var result = await ruleResources.PrepareEnabledAsync(effectiveConfig, svc, ct);

                return result.AllReady
                    ? Results.Json(result.StatusByResource, AppJsonContext.Default.DictionaryStringString)
                    : Results.Problem("One or more enabled rule resources failed to download or load.");
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapPost("/api/rule-resources/download", async (
            HttpContext ctx,
            AppConfig cfg,
            IProxyService svc,
            RuleResourceService ruleResources,
            CancellationToken ct) =>
        {
            try
            {
                var effectiveConfig = await ReadEffectiveConfigAsync(ctx, cfg, ct);
                var resource = ctx.Request.Query["resource"].FirstOrDefault();
                var result = await ruleResources.DownloadAsync(effectiveConfig, resource, svc, ct);

                return result.Succeeded && result.Status != null
                    ? Results.Json(result.Status, AppJsonContext.Default.RuleResourcesStatus)
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

        app.MapPost("/api/set-pac", async (
            HttpContext ctx,
            AppConfig cfg,
            ConfigWorkflowService configWorkflow,
            CancellationToken ct) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return Results.BadRequest(LocalizedText.Get("Api.WindowsOnly"));
            }

            cfg.LocalProxy.SystemProxyMode = SystemProxyModes.Pac;
            cfg.Tun.Enabled = false;
            await configWorkflow.SaveCurrentAsync(cfg, ct);

            var pacUrl = $"http://127.0.0.1:{ctx.Connection.LocalPort}/proxy.pac";
            var manager = SystemProxyManagerFactory.Create(cfg);
            manager.SetPacUrl(pacUrl);
            return Results.Json(
                new Dictionary<string, string> { ["url"] = pacUrl },
                AppJsonContext.Default.DictionaryStringString);
        });

        app.MapPost("/api/clear-pac", async (
            AppConfig cfg,
            ConfigWorkflowService configWorkflow,
            CancellationToken ct) =>
        {
            cfg.LocalProxy.SystemProxyMode = SystemProxyModes.None;
            cfg.Tun.Enabled = false;
            await configWorkflow.SaveCurrentAsync(cfg, ct);

            if (OperatingSystem.IsWindows())
            {
                var manager = SystemProxyManagerFactory.Create(cfg);
                manager.ClearPacUrl();
            }

            return Results.Ok();
        });

        app.MapPost("/api/enable-tun", async (
            AppConfig cfg,
            ConfigWorkflowService configWorkflow,
            RestartCoordinator restart,
            CancellationToken ct) =>
        {
            await configWorkflow.EnableTunAsync(cfg, ct);

            restart.RequestRestart(cfg);

            return Results.Ok();
        });

        app.MapPost("/api/restart", (RestartCoordinator restart, AppConfig cfg) =>
        {
            restart.RequestRestart(cfg);
            return Results.Json(
                new Dictionary<string, string> { ["status"] = "restarting" },
                AppJsonContext.Default.DictionaryStringString);
        });

        app.MapPost("/api/service/restart", (RestartCoordinator restart, AppConfig cfg) =>
        {
            restart.RequestRestart(cfg);
            return Results.Json(
                new Dictionary<string, string> { ["status"] = "restarting" },
                AppJsonContext.Default.DictionaryStringString);
        });

        app.MapPost("/api/service/stop", (RestartCoordinator restart) =>
        {
            restart.RequestStop();

            return Results.Json(
                new Dictionary<string, string> { ["status"] = "stopping" },
                AppJsonContext.Default.DictionaryStringString);
        });

        return app;
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
}
