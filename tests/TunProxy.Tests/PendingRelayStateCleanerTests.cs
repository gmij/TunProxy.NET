using TunProxy.CLI;

namespace TunProxy.Tests;

public class PendingRelayStateCleanerTests
{
    [Fact]
    public void ShouldRemove_ReturnsFalseForConnectedRelay()
    {
        var now = DateTime.UtcNow;

        Assert.False(PendingRelayStateCleaner.ShouldRemove(
            isProxyConnected: true,
            lastActivityUtc: now.AddMinutes(-10),
            now,
            idleTimeout: TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ShouldRemove_ReturnsFalseWithinIdleTimeout()
    {
        var now = DateTime.UtcNow;

        Assert.False(PendingRelayStateCleaner.ShouldRemove(
            isProxyConnected: false,
            lastActivityUtc: now.AddSeconds(-30),
            now,
            idleTimeout: TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ShouldRemove_ReturnsTrueForDisconnectedIdleRelay()
    {
        var now = DateTime.UtcNow;

        Assert.True(PendingRelayStateCleaner.ShouldRemove(
            isProxyConnected: false,
            lastActivityUtc: now.AddSeconds(-31),
            now,
            idleTimeout: TimeSpan.FromSeconds(30)));
    }
}
