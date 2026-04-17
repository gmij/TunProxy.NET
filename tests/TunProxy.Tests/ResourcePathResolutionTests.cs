using TunProxy.CLI;

namespace TunProxy.Tests;

public class ResourcePathResolutionTests
{
    [Fact]
    public void GeoIpService_ResolvesRelativePathAgainstAppDirectory()
    {
        var service = new GeoIpService("GeoLite2-Country.mmdb");

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "GeoLite2-Country.mmdb"),
            service.DatabasePath);
    }

    [Fact]
    public void GfwListService_ResolvesRelativePathAgainstAppDirectory()
    {
        var service = new GfwListService("https://example.com/gfwlist.txt", "gfwlist.txt");

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "gfwlist.txt"),
            service.ListPath);
    }

    [Fact]
    public void ResolveAppFilePath_PreservesAbsolutePath()
    {
        var absolutePath = Path.Combine(Path.GetTempPath(), "geo.mmdb");

        var resolvedPath = AppPathResolver.ResolveAppFilePath(absolutePath);

        Assert.Equal(absolutePath, resolvedPath);
    }
}
