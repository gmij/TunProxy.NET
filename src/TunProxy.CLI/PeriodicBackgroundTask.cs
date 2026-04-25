namespace TunProxy.CLI;

internal static class PeriodicBackgroundTask
{
    public static Task Start(
        TimeSpan interval,
        Func<CancellationToken, Task> tick,
        CancellationToken ct)
    {
        ValidateInterval(interval);
        ArgumentNullException.ThrowIfNull(tick);
        if (ct.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() => RunAsync(interval, tick, ct), ct);
    }

    internal static void ValidateInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be greater than zero.");
        }
    }

    private static async Task RunAsync(
        TimeSpan interval,
        Func<CancellationToken, Task> tick,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await tick(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
