using Serilog;
using TunProxy.Core.Metrics;
using TunProxy.Core.Packets;
using TunProxy.Core.Tun;

namespace TunProxy.CLI;

internal sealed class TunPacketPipeline
{
    private const int MinWorkerCount = 2;
    private const int MaxWorkerCount = 8;

    private readonly AsyncBoundedWorkQueue<byte[]>[] _queues;
    private readonly ProxyMetrics _metrics;
    private readonly Action<DateTime> _markPacketRead;
    private int _nextPartition;

    private TunPacketPipeline(
        int capacity,
        int workerCount,
        Func<byte[], CancellationToken, Task> processPacketAsync,
        ProxyMetrics metrics,
        Action<DateTime> markPacketRead)
    {
        _queues = Enumerable
            .Range(0, workerCount)
            .Select(_ => new AsyncBoundedWorkQueue<byte[]>(capacity, workerCount: 1, processPacketAsync))
            .ToArray();
        _metrics = metrics;
        _markPacketRead = markPacketRead;
        WorkerCount = workerCount;
        Capacity = capacity;
    }

    public int WorkerCount { get; }
    public int Capacity { get; }
    public int Count => _queues.Sum(static queue => queue.Count);
    public Task Completion => Task.WhenAll(_queues.Select(static queue => queue.Completion));

    public static TunPacketPipeline Start(
        int capacity,
        Func<byte[], CancellationToken, Task> processPacketAsync,
        ProxyMetrics metrics,
        Action<DateTime> markPacketRead,
        CancellationToken ct)
    {
        var workerCount = Math.Clamp(Environment.ProcessorCount, MinWorkerCount, MaxWorkerCount);
        var pipeline = new TunPacketPipeline(capacity, workerCount, processPacketAsync, metrics, markPacketRead);
        foreach (var queue in pipeline._queues)
        {
            queue.Start(ct);
        }

        Log.Information(
            "Packet processing queue started with {Workers} workers, {Partitions} partitions, and capacity {Capacity} per partition",
            workerCount,
            workerCount,
            capacity);

        return pipeline;
    }

    public void Complete()
    {
        foreach (var queue in _queues)
        {
            queue.Complete();
        }
    }

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
            await _queues[GetPartitionIndex(data)].EnqueueAsync(data, ct);
        }
    }

    private int GetPartitionIndex(byte[] data)
    {
        var packet = IPPacket.Parse(data);
        if (packet == null)
        {
            return GetNextRoundRobinPartition();
        }

        var sourcePort = packet.SourcePort ?? 0;
        var destinationPort = packet.DestinationPort ?? 0;
        var hash = HashCode.Combine(
            packet.Header.SourceAddress,
            packet.Header.DestinationAddress,
            sourcePort,
            destinationPort,
            packet.Header.ProtocolType);

        return Math.Abs(hash % _queues.Length);
    }

    private int GetNextRoundRobinPartition() =>
        Math.Abs(Interlocked.Increment(ref _nextPartition) % _queues.Length);
}
