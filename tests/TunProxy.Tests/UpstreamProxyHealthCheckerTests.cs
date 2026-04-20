using System.Net;
using TunProxy.CLI;

namespace TunProxy.Tests;

public class UpstreamProxyHealthCheckerTests
{
    [Fact]
    public async Task CheckAsync_AllTargetsReturn200_MarksProxyAvailable()
    {
        var status = await UpstreamProxyHealthChecker.CheckAsync(
            (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request
            }),
            CancellationToken.None);

        Assert.True(status.IsAvailable);
        Assert.Equal(3, status.Targets.Count);
        Assert.All(status.Targets, target => Assert.True(target.IsOk));
    }

    [Fact]
    public async Task CheckAsync_AnyTargetNot200_MarksProxyUnavailable()
    {
        var status = await UpstreamProxyHealthChecker.CheckAsync(
            (request, _) => Task.FromResult(new HttpResponseMessage(
                request.RequestUri?.Host.Contains("github", StringComparison.OrdinalIgnoreCase) == true
                    ? HttpStatusCode.Forbidden
                    : HttpStatusCode.OK)),
            CancellationToken.None);

        Assert.False(status.IsAvailable);
        Assert.Contains(status.Targets, target => target.Name == "GitHub" && target.StatusCode == 403);
    }
}
