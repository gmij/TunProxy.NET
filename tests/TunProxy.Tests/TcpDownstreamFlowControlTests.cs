using TunProxy.CLI;

namespace TunProxy.Tests;

public class TcpDownstreamFlowControlTests
{
    [Fact]
    public void CanSendSegment_ReturnsTrue_WhenWithinWindow()
    {
        var canSend = TcpDownstreamFlowControl.CanSendSegment(
            nextServerSeq: 2000,
            lastClientAck: 1500,
            clientAdvertisedWindow: 1000,
            segmentLength: 400);

        Assert.True(canSend);
    }

    [Fact]
    public void CanSendSegment_ReturnsFalse_WhenExceedingWindow()
    {
        var canSend = TcpDownstreamFlowControl.CanSendSegment(
            nextServerSeq: 2600,
            lastClientAck: 2000,
            clientAdvertisedWindow: 512,
            segmentLength: 100);

        Assert.False(canSend);
    }

    [Fact]
    public void GetSendAllowance_ReturnsRemainingBytes_WhenWindowPartiallyUsed()
    {
        var allowance = TcpDownstreamFlowControl.GetSendAllowance(
            nextServerSeq: 2400,
            lastClientAck: 2000,
            clientAdvertisedWindow: 1024);

        Assert.Equal(624, allowance);
    }

    [Fact]
    public void CanSendSegment_ReturnsTrue_ForSubMssChunkWhenWindowSmall()
    {
        var canSend = TcpDownstreamFlowControl.CanSendSegment(
            nextServerSeq: 3500,
            lastClientAck: 3000,
            clientAdvertisedWindow: 700,
            segmentLength: 200);

        Assert.True(canSend);
    }
}
