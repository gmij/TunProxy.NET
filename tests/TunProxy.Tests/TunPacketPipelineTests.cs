using TunProxy.CLI;
using TunProxy.Core.Metrics;

namespace TunProxy.Tests;

public class TunPacketPipelineTests
{
    [Fact]
    public void Start_UsesSingleWorkerForTcpStateOrdering()
    {
        using var cts = new CancellationTokenSource();
        var pipeline = TunPacketPipeline.Start(
            capacity: 16,
            processPacketAsync: (_, _) => Task.CompletedTask,
            metrics: new ProxyMetrics(),
            markPacketRead: _ => { },
            cts.Token);

        pipeline.Complete();

        Assert.Equal(1, pipeline.WorkerCount);
    }
}
