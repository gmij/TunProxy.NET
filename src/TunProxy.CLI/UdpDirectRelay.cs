using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Serilog;
using TunProxy.Core.Packets;
using TunProxy.Core.Tun;
using TunProxy.Core.Wintun;

namespace TunProxy.CLI;

/// <summary>
/// Application-layer UDP relay.  For each unique (srcIP:srcPort → dstIP:dstPort) 4-tuple a
/// <see cref="UdpRelaySession"/> is created that owns a <see cref="UdpClient"/> bound to the
/// physical outbound interface.  Datagrams received from the remote end are written back into
/// the TUN device so the originating application receives them transparently.
///
/// This replaces the previous route-table (AddBypassRoute + first-packet-drop) approach for
/// direct-routed UDP traffic, which lost the first datagram and required kernel route changes.
/// </summary>
internal sealed class UdpDirectRelay : IDisposable
{
    private readonly ConcurrentDictionary<string, UdpRelaySession> _sessions = new();
    private bool _disposed;

    /// <summary>
    /// Forwards <paramref name="packet"/>'s UDP payload to its destination and starts (or
    /// reuses) a relay session that pipes responses back into <paramref name="device"/>.
    /// </summary>
    public Task ForwardAsync(
        ITunDevice device,
        IPPacket packet,
        IPAddress? bindAddress,
        CancellationToken ct)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        var srcIp = packet.Header.SourceAddress;
        var dstIp = packet.Header.DestinationAddress;
        var srcPort = packet.SourcePort!.Value;
        var dstPort = packet.DestinationPort!.Value;
        var key = MakeKey(srcIp, srcPort, dstIp, dstPort);

        var session = _sessions.GetOrAdd(
            key,
            _ => CreateSession(key, device, srcIp, srcPort, dstIp, dstPort, bindAddress, ct));

        session.Touch();
        return session.SendAsync(packet.Payload, ct);
    }

    /// <summary>Number of active relay sessions.</summary>
    public int Count => _sessions.Count;

    /// <summary>
    /// Disposes sessions that have been idle for longer than <paramref name="idleTimeout"/>.
    /// </summary>
    public void CleanupExpired(TimeSpan idleTimeout, DateTime? nowUtc = null)
    {
        var cutoff = (nowUtc ?? DateTime.UtcNow) - idleTimeout;
        foreach (var (key, session) in _sessions)
        {
            if (session.LastActivityUtc < cutoff)
            {
                if (_sessions.TryRemove(key, out _))
                {
                    session.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    // ── private ──────────────────────────────────────────────────────────────

    private UdpRelaySession CreateSession(
        string key,
        ITunDevice device,
        IPAddress srcIp,
        ushort srcPort,
        IPAddress dstIp,
        ushort dstPort,
        IPAddress? bindAddress,
        CancellationToken ct)
    {
        var localEndpoint = bindAddress != null
            ? new IPEndPoint(bindAddress, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        var udpClient = new UdpClient(localEndpoint);
        var session = new UdpRelaySession(
            udpClient,
            srcIp.GetAddressBytes(),
            srcPort,
            dstIp.GetAddressBytes(),
            dstPort);

        _ = ReceiveLoopAsync(session, device, key, ct);
        return session;
    }

    private async Task ReceiveLoopAsync(
        UdpRelaySession session,
        ITunDevice device,
        string key,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMinutes(1));
                    result = await session.Socket.ReceiveAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Either global CT or idle timeout – exit the loop.
                    break;
                }
                catch
                {
                    break;
                }

                session.Touch();

                // Build a UDP packet from the remote peer back to the original client.
                // remote → client: src = dstIp:dstPort, dst = srcIp:srcPort
                var responsePacket = PacketBuilder.BuildUdpPacket(
                    session.DestIp,
                    session.ClientSrcIp,
                    session.DestPort,
                    session.ClientSrcPort,
                    result.Buffer);

                TunWriter.WritePacket(device, responsePacket);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "[UDP ] Relay receive loop error for session {Key}", key);
        }
        finally
        {
            _sessions.TryRemove(key, out _);
            session.Dispose();
        }
    }

    private static string MakeKey(IPAddress srcIp, ushort srcPort, IPAddress dstIp, ushort dstPort) =>
        $"{srcIp}:{srcPort}->{dstIp}:{dstPort}";
}

/// <summary>
/// Holds the state for a single UDP relay session.
/// </summary>
internal sealed class UdpRelaySession : IDisposable
{
    private long _lastActivityTicks;
    private bool _disposed;

    public UdpClient Socket { get; }
    public byte[] ClientSrcIp { get; }
    public ushort ClientSrcPort { get; }
    public byte[] DestIp { get; }
    public ushort DestPort { get; }

    public DateTime LastActivityUtc =>
        new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    public void Touch() =>
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    public UdpRelaySession(
        UdpClient socket,
        byte[] clientSrcIp,
        ushort clientSrcPort,
        byte[] destIp,
        ushort destPort)
    {
        Socket = socket;
        ClientSrcIp = clientSrcIp;
        ClientSrcPort = clientSrcPort;
        DestIp = destIp;
        DestPort = destPort;
        _lastActivityTicks = DateTime.UtcNow.Ticks;
    }

    public async Task SendAsync(byte[] payload, CancellationToken ct)
    {
        if (_disposed)
        {
            return;
        }

        var destEndpoint = new IPEndPoint(new IPAddress(DestIp), DestPort);
        await Socket.SendAsync(payload, destEndpoint, ct);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Socket.Dispose();
    }
}
