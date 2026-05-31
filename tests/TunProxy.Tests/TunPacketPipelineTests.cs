using TunProxy.CLI;
using TunProxy.Core.Metrics;

namespace TunProxy.Tests;

public class TunPacketPipelineTests
{
    [Fact]
    public void Start_UsesPartitionedQueuesForConcurrency()
    {
        using var cts = new CancellationTokenSource();
        var pipeline = TunPacketPipeline.Start(
            capacity: 16,
            processPacketAsync: (_, _) => Task.CompletedTask,
            metrics: new ProxyMetrics(),
            markPacketRead: _ => { },
            cts.Token);

        pipeline.Complete();

        // Each TCP connection is hashed to a fixed partition (which has 1 internal worker)
        // so per-connection packet order is preserved while connections run in parallel.
        var expected = Math.Clamp(Environment.ProcessorCount, 2, 8);
        Assert.Equal(expected, pipeline.WorkerCount);
    }
}
