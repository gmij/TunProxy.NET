using TunProxy.Core.Packets;

namespace TunProxy.CLI;

internal static class TunTcpPayloadDecisions
{
    public static TcpPayloadSequenceDecision EvaluateIncomingPayload(
        uint sequenceNumber,
        int payloadLength,
        uint expectedClientSeq)
    {
        var incomingEnd = CalculateIncomingEnd(sequenceNumber, payloadLength);
        if (ProtocolInspector.IsSeqBeforeOrEqual(incomingEnd, expectedClientSeq))
        {
            return new TcpPayloadSequenceDecision(
                TcpPayloadSequenceAction.AlreadyAcknowledged,
                incomingEnd);
        }

        if (sequenceNumber != expectedClientSeq)
        {
            return new TcpPayloadSequenceDecision(
                TcpPayloadSequenceAction.OutOfOrder,
                incomingEnd);
        }

        return new TcpPayloadSequenceDecision(
            TcpPayloadSequenceAction.Accept,
            incomingEnd);
    }

    public static uint CalculateIncomingEnd(uint sequenceNumber, int payloadLength) =>
        sequenceNumber + (uint)payloadLength;

    public static bool CanContinueWithBufferedPayload(int bufferedPayloadLength) =>
        bufferedPayloadLength > 0;

    public static byte[] SelectInitialPayload(
        bool payloadAlreadyAcked,
        byte[] bufferedPayload,
        byte[] fallbackPayload)
    {
        if (payloadAlreadyAcked && bufferedPayload.Length > 0)
        {
            return bufferedPayload;
        }

        return fallbackPayload;
    }
}

internal enum TcpPayloadSequenceAction
{
    Accept,
    AlreadyAcknowledged,
    OutOfOrder
}

internal readonly record struct TcpPayloadSequenceDecision(
    TcpPayloadSequenceAction Action,
    uint IncomingEnd);
