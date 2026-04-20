using TunProxy.CLI;
using TunProxy.Core.Configuration;

namespace TunProxy.Tests;

public class ProxyHttpClientFactoryTests
{
    [Fact]
    public void BuildProxyUri_ReturnsSocks5Uri()
    {
        var config = new ProxyConfig
        {
            Host = "127.0.0.1",
            Port = 7890,
            Type = "Socks5"
        };

        var uri = ProxyHttpClientFactory.BuildProxyUri(config);

        Assert.Equal(new Uri("socks5://127.0.0.1:7890"), uri);
    }

    [Fact]
    public void CreateProxy_PopulatesCredentials()
    {
        var config = new ProxyConfig
        {
            Host = "127.0.0.1",
            Port = 8080,
            Type = "Http",
            Username = "user",
            Password = "pass"
        };

        var proxy = ProxyHttpClientFactory.CreateProxy(config);

        Assert.NotNull(proxy);
        var credentials = proxy!.Credentials!.GetCredential(new Uri("http://127.0.0.1:8080"), "Basic");
        Assert.NotNull(credentials);
        Assert.Equal("user", credentials!.UserName);
        Assert.Equal("pass", credentials.Password);
    }
}
