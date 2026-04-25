using System.Collections.Concurrent;
using Serilog;

namespace TunProxy.CLI;

internal sealed class PendingRelayStateCleaner
{
    public int Cleanup(
        ConcurrentDictionary<string, TcpRelayState> relayStates,
        TimeSpan idleTimeout,
        DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var removedCount = 0;

        foreach (var (connKey, state) in relayStates)
        {
            if (!ShouldRemove(state.IsProxyConnected, state.LastActivityUtc, now, idleTimeout))
            {
                continue;
            }

            if (relayStates.TryRemove(connKey, out var removed))
            {
                removed.Dispose();
                removedCount++;
                Log.Debug("[CONN] removed pending relay state after {Seconds}s idle: {ConnKey}",
                    idleTimeout.TotalSeconds,
                    connKey);
            }
        }

        return removedCount;
    }

    internal static bool ShouldRemove(
        bool isProxyConnected,
        DateTime lastActivityUtc,
        DateTime nowUtc,
        TimeSpan idleTimeout) =>
        !isProxyConnected && nowUtc - lastActivityUtc > idleTimeout;
}
