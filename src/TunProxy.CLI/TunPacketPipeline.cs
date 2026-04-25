using Serilog;
using TunProxy.Core.Metrics;
using TunProxy.Core.Tun;

namespace TunProxy.CLI;

internal sealed class TunPacketPipeline
{
    private const int TcpStateMachineWorkerCount = 1;

    private readonly AsyncBoundedWorkQueue<byte[]> _queue;
    private readonly ProxyMetrics _metrics;
    private readonly Action<DateTime> _markPacketRead;

    private TunPacketPipeline(
        int capacity,
        int workerCount,
        Func<byte[], CancellationToken, Task> processPacketAsync,
        ProxyMetrics metrics,
        Action<DateTime> markPacketRead)
    {
        _queue = new AsyncBoundedWorkQueue<byte[]>(capacity, workerCount, processPacketAsync);
        _metrics = metrics;
        _markPacketRead = markPacketRead;
        WorkerCount = workerCount;
        Capacity = capacity;
    }

    public int WorkerCount { get; }
    public int Capacity { get; }
    public int Count => _queue.Count;
    public Task Completion => _queue.Completion;

    public static TunPacketPipeline Start(
        int capacity,
        Func<byte[], CancellationToken, Task> processPacketAsync,
        ProxyMetrics metrics,
        Action<DateTime> markPacketRead,
        CancellationToken ct)
    {
        var workerCount = TcpStateMachineWorkerCount;
        var pipeline = new TunPacketPipeline(capacity, workerCount, processPacketAsync, metrics, markPacketRead);
        pipeline._queue.Start(ct);

        Log.Information(
            "Packet processing queue started with {Workers} workers and capacity {Capacity}",
            workerCount,
            capacity);

        return pipeline;
    }

    public void Complete() => _queue.Complete();

    public async Task ReadPacketsAsync(
        ITunDevice device,
        TaskCompletionSource proxyReady,
        CancellationToken ct)
    {
        proxyReady.TrySetResult();

        while (!ct.IsCancellationRequested)
        {
            var data = device.ReadPacket();
            if (data == null)
            {
                break;
            }

            _metrics.IncrementRawPacketsReceived();
            _markPacketRead(DateTime.UtcNow);
            await _queue.EnqueueAsync(data, ct);
        }
    }
}
