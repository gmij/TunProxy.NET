using TunProxy.Core.Packets;
using TunProxy.Core.Tun;

namespace TunProxy.Core.Wintun;

/// <summary>
/// Writes synthesized packets back to the TUN device.
/// </summary>
public static class TunWriter
{
    public static void WritePacket(ITunDevice device, byte[] packetData)
    {
        if (packetData.Length == 0)
        {
            return;
        }

        device.WritePacket(packetData);
    }

    public static void WriteSynAck(ITunDevice device, IPPacket requestPacket, out uint serverIsn)
    {
        WritePacket(device, PacketBuilder.BuildSynAck(requestPacket, out serverIsn));
    }

    public static void WriteSynAck(ITunDevice device, IPPacket requestPacket, uint serverIsn)
    {
        WritePacket(device, PacketBuilder.BuildSynAck(requestPacket, serverIsn));
    }

    public static void WriteRst(ITunDevice device, IPPacket requestPacket)
    {
        WritePacket(device, PacketBuilder.BuildRst(requestPacket));
    }

    public static void WriteAck(ITunDevice device, IPPacket requestPacket, uint seqNum, uint ackNum)
    {
        WritePacket(device, PacketBuilder.BuildAck(requestPacket, seqNum, ackNum));
    }

    public static void WriteFinAck(ITunDevice device, IPPacket requestPacket, uint seqNum, uint ackNum)
    {
        WritePacket(device, PacketBuilder.BuildFinAck(requestPacket, seqNum, ackNum));
    }

    public static void WriteDataResponse(
        ITunDevice device,
        IPPacket synPacket,
        ReadOnlySpan<byte> data,
        uint serverSeq,
        uint clientAck)
    {
        WritePacket(device, PacketBuilder.BuildDataResponse(synPacket, data, serverSeq, clientAck));
    }

    public static void WriteUdpResponse(ITunDevice device, IPPacket requestPacket, ReadOnlySpan<byte> responseData)
    {
        WritePacket(device, PacketBuilder.BuildUdpResponse(requestPacket, responseData));
    }

    public static void WriteIcmpPortUnreachable(ITunDevice device, IPPacket udpPacket)
    {
        WritePacket(device, PacketBuilder.BuildIcmpPortUnreachable(udpPacket));
    }
}
