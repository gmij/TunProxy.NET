namespace TunProxy.Core.Metrics;

public static class TunDeviceWriteMetrics
{
    private static long _sendAllocationRetryAttempts;
    private static long _sendAllocationDrops;

    public static long SendAllocationRetryAttempts => Interlocked.Read(ref _sendAllocationRetryAttempts);
    public static long SendAllocationDrops => Interlocked.Read(ref _sendAllocationDrops);

    public static void IncrementSendAllocationRetryAttempts() =>
        Interlocked.Increment(ref _sendAllocationRetryAttempts);

    public static void IncrementSendAllocationDrops() =>
        Interlocked.Increment(ref _sendAllocationDrops);

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _sendAllocationRetryAttempts, 0);
        Interlocked.Exchange(ref _sendAllocationDrops, 0);
    }
}
