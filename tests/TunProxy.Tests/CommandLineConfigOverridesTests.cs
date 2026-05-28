using TunProxy.CLI;
using TunProxy.Core.Configuration;

namespace TunProxy.Tests;

public class CommandLineConfigOverridesTests
{
    [Fact]
    public void Apply_UpdatesConfigFromSupportedOptions()
    {
        var config = new AppConfig();

        CommandLineConfigOverrides.Apply(
            config,
            ["--proxy", "127.0.0.2:9001", "--type", "http", "--mode", "tun", "--listen-port", "8088", "--disable-geo", "--no-fake-ip"],
            strict: true);

        Assert.Equal("127.0.0.2", config.Proxy.Host);
        Assert.Equal(9001, config.Proxy.Port);
        Assert.Equal("http", config.Proxy.Type);
        Assert.True(config.Tun.Enabled);
        Assert.Equal(8088, config.LocalProxy.ListenPort);
        Assert.False(config.Route.EnableGeo);
        Assert.False(config.Tun.FakeIpMode);
    }

    [Fact]
    public void ParseProxyEndpoint_SupportsBracketedIpv6()
    {
        var endpoint = CommandLineConfigOverrides.ParseProxyEndpoint("[2001:db8::1]:7890");

        Assert.Equal("2001:db8::1", endpoint.Host);
        Assert.Equal(7890, endpoint.Port);
    }

    [Fact]
    public void Apply_StrictModeRejectsUnknownOptions()
    {
        var config = new AppConfig();

        var error = Assert.Throws<InvalidOperationException>(() =>
            CommandLineConfigOverrides.Apply(config, ["--does-not-exist"], strict: true));

        Assert.Contains("--does-not-exist", error.Message);
    }
}
