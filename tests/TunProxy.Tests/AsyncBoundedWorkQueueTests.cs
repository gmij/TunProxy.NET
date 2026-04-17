using System.Collections.Concurrent;
using TunProxy.CLI;

namespace TunProxy.Tests;

public class AsyncBoundedWorkQueueTests
{
    [Fact]
    public async Task Queue_ProcessesAllItems_WithBoundedConcurrency()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var processed = new ConcurrentBag<int>();
        var inFlight = 0;
        var maxInFlight = 0;

        var queue = new AsyncBoundedWorkQueue<int>(
            capacity: 4,
            workerCount: 2,
            handler: async (item, token) =>
            {
                var current = Interlocked.Increment(ref inFlight);
                UpdateMax(ref maxInFlight, current);
                try
                {
                    await Task.Delay(50, token);
                    processed.Add(item);
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
            });

        queue.Start(cts.Token);

        for (var i = 0; i < 10; i++)
        {
            await queue.EnqueueAsync(i, cts.Token);
        }

        queue.Complete();
        await queue.Completion;

        Assert.Equal(10, processed.Count);
        Assert.True(maxInFlight <= 2, $"Expected at most 2 concurrent handlers, got {maxInFlight}.");
    }

    private static void UpdateMax(ref int target, int candidate)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref target);
            if (candidate <= snapshot)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, candidate, snapshot) == snapshot)
            {
                return;
            }
        }
    }
}
