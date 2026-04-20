using System.Diagnostics;
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

        app.MapGet("/api/dns-records", async (IProxyService svc, CancellationToken ct) =>
        {
            List<DnsRouteRecord> records = svc is TunProxyService tunService
                ? new List<DnsRouteRecord>(await tunService.GetDnsRouteRecordsAsync(ct))
                : [];

            return Results.Json(records, AppJsonContext.Default.ListDnsRouteRecord);
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

            Log.Warning("Manual TUN mode repair triggered. Configuration updated; restarting service...");
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                lifetime.StopApplication();
            });

            return Results.Ok();
        });

        app.MapPost("/api/restart", (IHostApplicationLifetime lifetime) =>
        {
            Task.Run(async () =>
            {
                await Task.Delay(300);
                if (!Environment.UserInteractive && OperatingSystem.IsWindows())
                {
                    lifetime.StopApplication();
                    return;
                }

                var exe = Environment.ProcessPath;
                if (exe != null)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = true
                    });
                }

                lifetime.StopApplication();
            });

            return Results.Ok();
        });

        return app;
    }
}
