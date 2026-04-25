using TunProxy.CLI;

namespace TunProxy.Tests;

public class TunTcpPayloadDecisionsTests
{
    [Fact]
    public void EvaluateIncomingPayload_AcceptsExpectedSequence()
    {
        var decision = TunTcpPayloadDecisions.EvaluateIncomingPayload(
            sequenceNumber: 101,
            payloadLength: 20,
            expectedClientSeq: 101);

        Assert.Equal(TcpPayloadSequenceAction.Accept, decision.Action);
        Assert.Equal(121u, decision.IncomingEnd);
    }

    [Fact]
    public void EvaluateIncomingPayload_RejectsAlreadyAcknowledgedPayload()
    {
        var decision = TunTcpPayloadDecisions.EvaluateIncomingPayload(
            sequenceNumber: 80,
            payloadLength: 10,
            expectedClientSeq: 101);

        Assert.Equal(TcpPayloadSequenceAction.AlreadyAcknowledged, decision.Action);
        Assert.Equal(90u, decision.IncomingEnd);
    }

    [Fact]
    public void EvaluateIncomingPayload_RejectsOutOfOrderPayload()
    {
        var decision = TunTcpPayloadDecisions.EvaluateIncomingPayload(
            sequenceNumber: 150,
            payloadLength: 10,
            expectedClientSeq: 101);

        Assert.Equal(TcpPayloadSequenceAction.OutOfOrder, decision.Action);
        Assert.Equal(160u, decision.IncomingEnd);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void CanContinueWithBufferedPayload_RequiresExistingBufferedPayload(
        int bufferedPayloadLength,
        bool expected)
    {
        Assert.Equal(expected, TunTcpPayloadDecisions.CanContinueWithBufferedPayload(bufferedPayloadLength));
    }

    [Fact]
    public void SelectInitialPayload_UsesBufferedPayloadWhenAlreadyAcked()
    {
        var selected = TunTcpPayloadDecisions.SelectInitialPayload(
            payloadAlreadyAcked: true,
            bufferedPayload: [1, 2, 3],
            fallbackPayload: [9]);

        Assert.Equal([1, 2, 3], selected);
    }

    [Fact]
    public void SelectInitialPayload_FallsBackWhenBufferedPayloadIsEmpty()
    {
        var selected = TunTcpPayloadDecisions.SelectInitialPayload(
            payloadAlreadyAcked: true,
            bufferedPayload: [],
            fallbackPayload: [9]);

        Assert.Equal([9], selected);
    }
}
