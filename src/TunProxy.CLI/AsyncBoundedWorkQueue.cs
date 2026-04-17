using System.Threading.Channels;

namespace TunProxy.CLI;

internal sealed class AsyncBoundedWorkQueue<T>
{
    private readonly Channel<T> _channel;
    private readonly Func<T, CancellationToken, Task> _handler;
    private readonly Task[] _workers;
    private int _started;

    public AsyncBoundedWorkQueue(int capacity, int workerCount, Func<T, CancellationToken, Task> handler)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        ArgumentNullException.ThrowIfNull(handler);

        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        });
        _handler = handler;
        _workers = new Task[workerCount];
    }

    public int WorkerCount => _workers.Length;

    public void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i] = Task.Run(() => WorkerLoopAsync(ct), CancellationToken.None);
        }
    }

    public ValueTask EnqueueAsync(T item, CancellationToken ct) =>
        _channel.Writer.WriteAsync(item, ct);

    public void Complete(Exception? error = null)
    {
        _channel.Writer.TryComplete(error);
    }

    public Task Completion => Task.WhenAll(_workers.Where(static task => task != null));

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    await _handler(item, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
