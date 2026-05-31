using TunProxy.CLI;

namespace TunProxy.Tests;

public class DomainFailureTrackerTests
{
    [Fact]
    public void RecordDirectFailure_ActivatesFallbackAfterThreshold()
    {
        var now = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new DomainFailureTracker(
            directFailureThreshold: 3,
            failureWindow: TimeSpan.FromMinutes(5),
            proxyFallbackTtl: TimeSpan.FromMinutes(15),
            getUtcNow: () => now);

        Assert.False(tracker.RecordDirectFailure("api.example.com", "connect failed").IsProxyFallbackActive);
        Assert.False(tracker.RecordDirectFailure("example.com", "connect failed").IsProxyFallbackActive);
        var result = tracker.RecordDirectFailure("api.example.com", "connect failed");

        Assert.False(result.IsProxyFallbackActive);

        result = tracker.RecordDirectFailure("api.example.com", "connect failed");

        Assert.True(result.ActivatedProxyFallback);
        Assert.True(tracker.IsProxyFallbackActive("api.example.com"));
        Assert.False(tracker.IsProxyFallbackActive("example.com"));
        Assert.Equal("api.example.com", result.Domain);
    }

    [Fact]
    public void RecordDirectFailure_ResetsCountOutsideFailureWindow()
    {
        var now = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new DomainFailureTracker(
            directFailureThreshold: 2,
            failureWindow: TimeSpan.FromMinutes(5),
            proxyFallbackTtl: TimeSpan.FromMinutes(15),
            getUtcNow: () => now);

        tracker.RecordDirectFailure("example.com", "connect failed");
        now = now.AddMinutes(6);
        var result = tracker.RecordDirectFailure("example.com", "connect failed");

        Assert.False(result.IsProxyFallbackActive);
        Assert.Equal(1, result.DirectFailureCount);
    }

    [Fact]
    public void RecordDirectSuccess_ClearsFailuresAndFallback()
    {
        var now = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new DomainFailureTracker(
            directFailureThreshold: 2,
            getUtcNow: () => now);

        tracker.RecordDirectFailure("example.com", "connect failed");
        tracker.RecordDirectFailure("example.com", "connect failed");
        Assert.True(tracker.IsProxyFallbackActive("example.com"));

        tracker.RecordDirectSuccess("example.com");

        Assert.False(tracker.IsProxyFallbackActive("example.com"));
    }

    [Fact]
    public void IsProxyFallbackActive_ExpiresAfterTtl()
    {
        var now = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new DomainFailureTracker(
            directFailureThreshold: 1,
            proxyFallbackTtl: TimeSpan.FromMinutes(15),
            getUtcNow: () => now);

        tracker.RecordDirectFailure("example.com", "connect failed");
        Assert.True(tracker.IsProxyFallbackActive("example.com"));

        now = now.AddMinutes(16);

        Assert.False(tracker.IsProxyFallbackActive("example.com"));
    }

    [Fact]
    public void RecordDirectFailure_IgnoresIpOnlyTargets()
    {
        var tracker = new DomainFailureTracker(directFailureThreshold: 1);

        var result = tracker.RecordDirectFailure("203.0.113.1", "connect failed");

        Assert.Null(result.Domain);
        Assert.False(result.IsProxyFallbackActive);
    }
}
