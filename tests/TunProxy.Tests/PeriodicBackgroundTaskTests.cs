using TunProxy.CLI;

namespace TunProxy.Tests;

public class PeriodicBackgroundTaskTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateInterval_RejectsNonPositiveIntervals(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PeriodicBackgroundTask.ValidateInterval(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void ValidateInterval_AcceptsPositiveInterval()
    {
        PeriodicBackgroundTask.ValidateInterval(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Start_StopsQuietlyWhenCanceledBeforeFirstTick()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var ticked = false;

        await PeriodicBackgroundTask.Start(
            TimeSpan.FromMilliseconds(1),
            _ =>
            {
                ticked = true;
                return Task.CompletedTask;
            },
            cts.Token);

        Assert.False(ticked);
    }
}
