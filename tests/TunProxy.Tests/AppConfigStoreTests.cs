using System.Text.Json;
using TunProxy.CLI;
using TunProxy.Core.Configuration;

namespace TunProxy.Tests;

public class AppConfigStoreTests
{
    [Fact]
    public void LoadOrCreate_CreatesConfigAndAppliesDefaultsWhenMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunproxy-config-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AppConfigStore(path);
            var config = store.LoadOrCreate(cfg =>
            {
                cfg.Proxy.Host = "10.0.0.2";
                cfg.Proxy.Port = 1080;
            });

            Assert.Equal("10.0.0.2", config.Proxy.Host);
            Assert.Equal(1080, config.Proxy.Port);
            Assert.True(File.Exists(path));

            var saved = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                AppJsonContext.Default.AppConfig);
            Assert.NotNull(saved);
            Assert.Equal("10.0.0.2", saved.Proxy.Host);
            Assert.Equal(1080, saved.Proxy.Port);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrCreate_DoesNotReapplyDefaultsWhenConfigExists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunproxy-config-{Guid.NewGuid():N}.json");
        try
        {
            var existing = new AppConfig
            {
                Proxy = new ProxyConfig { Host = "existing.local", Port = 7890 }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(existing, AppJsonContext.Default.AppConfig));

            var store = new AppConfigStore(path);
            var config = store.LoadOrCreate(cfg => cfg.Proxy.Host = "new.local");

            Assert.Equal("existing.local", config.Proxy.Host);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrClone_ReturnsIndependentFallbackCloneWhenMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunproxy-config-{Guid.NewGuid():N}.json");
        var fallback = new AppConfig
        {
            Route = new RouteConfig { ProxyDomains = ["proxy.example"] }
        };

        var store = new AppConfigStore(path);
        var clone = store.LoadOrClone(fallback);

        fallback.Route.ProxyDomains.Add("later.example");

        Assert.Equal(["proxy.example"], clone.Route.ProxyDomains);
    }
}
