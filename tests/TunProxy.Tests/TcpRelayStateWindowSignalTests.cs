using System.Net;
using TunProxy.CLI;
using TunProxy.Core.Packets;

namespace TunProxy.Tests;

public class TcpRelayStateWindowSignalTests
{
    // Build a minimal SYN packet so TcpRelayState can be constructed.
    private static IPPacket BuildSynPacket()
    {
        var raw = PacketBuilder.BuildTcpPacketRaw(
            sourceIP: [10, 0, 0, 1],
            destIP: [10, 0, 0, 2],
            sourcePort: 12345,
            destPort: 443,
            flags: PacketBuilder.TcpFlags.SYN,
            seqNum: 1000,
            ackNum: 0);
        return IPPacket.Parse(raw)!;
    }

    [Fact]
    public async Task WaitForWindowUpdate_ReturnsImmediately_WhenSignaledBeforeTimeout()
    {
        var state = new TcpRelayState(BuildSynPacket(), serverIsn: 5000, clientIsn: 1000);

        // Signal the window from a background task before the 5 s timeout expires.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            state.UpdateClientFlowControl(new TCPHeaderInfo
            {
                Flags = 0x10, // ACK
                AckNumber = 5001,
                WindowSize = 8192
            });
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var signaled = await state.WaitForWindowUpdateAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        sw.Stop();

        Assert.True(signaled);
        // Should wake up well before the 5 s timeout (allow generous headroom for CI).
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"Expected early wakeup but elapsed {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task WaitForWindowUpdate_ReturnsFalse_OnTimeout()
    {
        var state = new TcpRelayState(BuildSynPacket(), serverIsn: 5000, clientIsn: 1000);

        var signaled = await state.WaitForWindowUpdateAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.False(signaled);
    }

    [Fact]
    public async Task WaitForWindowUpdate_ThrowsOnCancellation()
    {
        var state = new TcpRelayState(BuildSynPacket(), serverIsn: 5000, clientIsn: 1000);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => state.WaitForWindowUpdateAsync(TimeSpan.FromSeconds(5), cts.Token));
    }

    [Fact]
    public async Task UpdateClientFlowControl_SignalsWindow_OnAckAdvance()
    {
        var state = new TcpRelayState(BuildSynPacket(), serverIsn: 5000, clientIsn: 1000);

        // First update: no waiter yet, signal goes into the semaphore.
        state.UpdateClientFlowControl(new TCPHeaderInfo
        {
            Flags = 0x10, // ACK
            AckNumber = 5002,
            WindowSize = 65535
        });

        // Immediately checking should succeed (semaphore count = 1).
        var signaled = await state.WaitForWindowUpdateAsync(TimeSpan.FromMilliseconds(0), CancellationToken.None);
        Assert.True(signaled);
    }
}
