using TunProxy.Core.Metrics;

namespace TunProxy.Tests;

public class TunDeviceWriteMetricsTests
{
    [Fact]
    public void Incrementers_UpdateCounters()
    {
        TunDeviceWriteMetrics.ResetForTests();

        TunDeviceWriteMetrics.IncrementSendAllocationRetryAttempts();
        TunDeviceWriteMetrics.IncrementSendAllocationRetryAttempts();
        TunDeviceWriteMetrics.IncrementSendAllocationDrops();

        Assert.Equal(2, TunDeviceWriteMetrics.SendAllocationRetryAttempts);
        Assert.Equal(1, TunDeviceWriteMetrics.SendAllocationDrops);

        TunDeviceWriteMetrics.ResetForTests();
    }

    [Fact]
    public void ProxyMetricsSnapshot_IncludesTunDeviceWriteCounters()
    {
        TunDeviceWriteMetrics.ResetForTests();
        TunDeviceWriteMetrics.IncrementSendAllocationRetryAttempts();
        TunDeviceWriteMetrics.IncrementSendAllocationDrops();

        var snapshot = new ProxyMetrics().GetSnapshot();

        Assert.Equal(1, snapshot.TunSendAllocationRetryAttempts);
        Assert.Equal(1, snapshot.TunSendAllocationDrops);

        TunDeviceWriteMetrics.ResetForTests();
    }
}
