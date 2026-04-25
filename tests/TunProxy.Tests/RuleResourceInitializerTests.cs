using TunProxy.CLI;
using TunProxy.Core.Configuration;

namespace TunProxy.Tests;

public class RuleResourceInitializerTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void ShouldInitializeResource_RespectsEnabledAndReadyState(
        bool enabled,
        bool initialized,
        bool includeAlreadyInitialized,
        bool expected)
    {
        Assert.Equal(
            expected,
            RuleResourceInitializer.ShouldInitializeResource(
                enabled,
                initialized,
                includeAlreadyInitialized));
    }

    [Fact]
    public void BuildInitializationPlan_ReturnsOnlyEnabledUnreadyResources()
    {
        var config = new AppConfig();
        config.Route.EnableGeo = true;
        config.Route.EnableGfwList = true;

        var plan = RuleResourceInitializer.BuildInitializationPlan(
            config,
            geoReady: true,
            gfwReady: false,
            includeAlreadyInitialized: false);

        Assert.Equal([RuleResourceKind.Gfw], plan);
    }

    [Fact]
    public void BuildInitializationPlan_CanIncludeAlreadyReadyEnabledResources()
    {
        var config = new AppConfig();
        config.Route.EnableGeo = true;
        config.Route.EnableGfwList = true;

        var plan = RuleResourceInitializer.BuildInitializationPlan(
            config,
            geoReady: true,
            gfwReady: false,
            includeAlreadyInitialized: true);

        Assert.Equal([RuleResourceKind.Geo, RuleResourceKind.Gfw], plan);
    }

    [Fact]
    public void ShouldWaitForProxyReady_WaitsOnlyWhenNoExplicitProxyCanBeBuilt()
    {
        Assert.True(RuleResourceInitializer.ShouldWaitForProxyReady(null, waitForProxyReadyWhenNeeded: true));
        Assert.False(RuleResourceInitializer.ShouldWaitForProxyReady(null, waitForProxyReadyWhenNeeded: false));
        Assert.False(RuleResourceInitializer.ShouldWaitForProxyReady(
            new ProxyConfig { Host = "127.0.0.1", Port = 7890, Type = "Socks5" },
            waitForProxyReadyWhenNeeded: true));
    }
}
