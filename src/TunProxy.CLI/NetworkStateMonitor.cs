using System.Net.NetworkInformation;
using Serilog;

namespace TunProxy.CLI;

internal sealed class NetworkStateMonitor : IAsyncDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cts;
    private readonly bool _subscribe;
    private readonly Task _worker;

    public NetworkStateMonitor(Func<CancellationToken, Task> refresh, CancellationToken ct,
        bool subscribe = true, TimeSpan? interval = null)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _subscribe = subscribe;
        if (subscribe)
        {
            NetworkChange.NetworkAddressChanged += OnAddressChanged;
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        }
        _worker = Task.Run(() => RunAsync(refresh, interval ?? TimeSpan.FromSeconds(2)));
    }

    public void Signal()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private void OnAddressChanged(object? sender, EventArgs args) => Signal();
    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => Signal();

    private async Task RunAsync(Func<CancellationToken, Task> refresh, TimeSpan interval)
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (await _signal.WaitAsync(interval, ct))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                    while (_signal.Wait(0)) { }
                }
                try { await refresh(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { Log.Warning(ex, "[ROUTE] Network refresh failed; will retry."); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscribe)
        {
            NetworkChange.NetworkAddressChanged -= OnAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        }
        _cts.Cancel();
        await _worker;
        _cts.Dispose();
        _signal.Dispose();
    }
}
