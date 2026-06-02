using System.Text.Json;
using TunProxy.CLI;
using TunProxy.Core.Configuration;

namespace TunProxy.Tests;

public class CliConfigCommandHandlerTests
{
    [Fact]
    public async Task ConfigPath_PrintsStorePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunproxy-config-{Guid.NewGuid():N}.json");
        var writer = new StringWriter();
        var handler = new CliConfigCommandHandler(
            new AppConfigStore(path),
            input: new StringReader(string.Empty),
            output: writer);

        var exitCode = await handler.TryHandleAsync(["config", "path"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(path, writer.ToString().Trim());
    }

    [Fact]
    public async Task ConfigSet_SavesUpdatedValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunproxy-config-{Guid.NewGuid():N}.json");

        try
        {
            var writer = new StringWriter();
            var handler = new CliConfigCommandHandler(
                new AppConfigStore(path),
                input: new StringReader(string.Empty),
                output: writer);

            var exitCode = await handler.TryHandleAsync(
                ["config", "set", "--proxy", "10.0.0.2:1081", "--type", "http", "--mode", "tun", "--disable-gfw"]);

            Assert.Equal(0, exitCode);

            var saved = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                AppJsonContext.Default.AppConfig);

            Assert.NotNull(saved);
            Assert.Equal("10.0.0.2", saved.Proxy.Host);
            Assert.Equal(1081, saved.Proxy.Port);
            Assert.Equal("http", saved.Proxy.Type);
            Assert.True(saved.Tun.Enabled);
            Assert.False(saved.Route.EnableGfwList);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ConfigWizard_UsesPromptAnswersAndSaves()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunproxy-config-{Guid.NewGuid():N}.json");

        try
        {
            var answers = string.Join(
                Environment.NewLine,
                [
                    "proxy.example",
                    "9000",
                    "Http",
                    "",
                    "",
                    "",    // listen host: accept default 127.0.0.1
                    "8088",
                    "1.1.1.1",
                    "y",
                    "n",
                    "y"
                ]) + Environment.NewLine;

            var output = new StringWriter();
            var handler = new CliConfigCommandHandler(
                new AppConfigStore(path),
                input: new StringReader(answers),
                output: output,
                checkProxyAsync: (_, _) => Task.FromResult(new UpstreamProxyStatus
                {
                    IsAvailable = true,
                    Targets =
                    [
                        new UpstreamProxyTargetStatus { Name = "Google", IsOk = true, StatusCode = 200, ElapsedMs = 10 },
                        new UpstreamProxyTargetStatus { Name = "GitHub", IsOk = true, StatusCode = 200, ElapsedMs = 12 },
                        new UpstreamProxyTargetStatus { Name = "YouTube", IsOk = true, StatusCode = 200, ElapsedMs = 15 }
                    ]
                }),
                prepareEnabledResourcesAsync: (_, _) => Task.FromResult<IReadOnlyList<string>>(
                [
                    "geo: ready (/tmp/geo.mmdb)",
                    "gfw: ready (/tmp/gfwlist.txt)"
                ]));

            var exitCode = await handler.TryHandleAsync(["config", "wizard"]);

            Assert.Equal(0, exitCode);

            var saved = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                AppJsonContext.Default.AppConfig);

            Assert.NotNull(saved);
            Assert.Equal("proxy.example", saved.Proxy.Host);
            Assert.Equal(9000, saved.Proxy.Port);
            Assert.Equal("Http", saved.Proxy.Type);
            Assert.True(saved.Tun.Enabled);
            Assert.Equal(8088, saved.LocalProxy.ListenPort);
            Assert.Equal("1.1.1.1", saved.Tun.DnsServer);
            Assert.True(saved.Route.EnableGfwList);
            Assert.False(saved.Route.EnableGeo);
            Assert.True(saved.Tun.FakeIpMode);
            Assert.Contains("Upstream proxy check passed.", output.ToString());
            Assert.Contains("geo: ready", output.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
