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
}
