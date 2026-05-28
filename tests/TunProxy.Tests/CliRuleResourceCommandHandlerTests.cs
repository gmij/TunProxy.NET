using System.Text;
using System.Text.Json;
using TunProxy.CLI;
using TunProxy.Core.Configuration;

namespace TunProxy.Tests;

public class CliRuleResourceCommandHandlerTests
{
    [Fact]
    public async Task ResourceStatus_PrintsConfiguredPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tunproxy-resource-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "tunproxy.json");

        try
        {
            var config = new AppConfig
            {
                Route = new RouteConfig
                {
                    EnableGeo = false,
                    EnableGfwList = false,
                    GeoIpDbPath = "geo.mmdb",
                    GfwListPath = "gfwlist.txt"
                }
            };

            File.WriteAllText(path, JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig));

            var writer = new StringWriter();
            var handler = new CliRuleResourceCommandHandler(
                new AppConfigStore(path),
                output: writer);

            var exitCode = await handler.TryHandleAsync(["resource", "status"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("geo:", writer.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gfw:", writer.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResourcePrepare_DefaultsToEnabledResources()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tunproxy-resource-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "tunproxy.json");
        var gfwPath = Path.Combine(directory, "gfwlist.txt");

        try
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("||google.com^\n"));
            File.WriteAllText(gfwPath, payload);

            var config = new AppConfig
            {
                Route = new RouteConfig
                {
                    EnableGeo = false,
                    EnableGfwList = true,
                    GfwListUrl = "https://example.com/gfwlist.txt",
                    GfwListPath = gfwPath
                }
            };

            File.WriteAllText(path, JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig));

            var writer = new StringWriter();
            var handler = new CliRuleResourceCommandHandler(
                new AppConfigStore(path),
                output: writer);

            var exitCode = await handler.TryHandleAsync(["resource", "prepare"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("gfw: ready", writer.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResourcePrepare_WhenNothingEnabled_PrintsHint()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunproxy-config-{Guid.NewGuid():N}.json");

        try
        {
            var config = new AppConfig
            {
                Route = new RouteConfig
                {
                    EnableGeo = false,
                    EnableGfwList = false
                }
            };

            File.WriteAllText(path, JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig));

            var writer = new StringWriter();
            var handler = new CliRuleResourceCommandHandler(
                new AppConfigStore(path),
                output: writer);

            var exitCode = await handler.TryHandleAsync(["resource", "prepare"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("No enabled rule resources", writer.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
