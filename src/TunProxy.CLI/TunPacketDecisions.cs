using System.Net;
using System.Net.Sockets;
using TunProxy.Core.Packets;

namespace TunProxy.CLI;

internal static class TunPacketDecisions
{
    public static bool IsIpv6Packet(ReadOnlySpan<byte> data) =>
        data.Length > 0 && ((data[0] >> 4) & 0x0F) == 6;

    public static bool ShouldRejectUdpPacket(RouteDecision decision)
    {
        if (!decision.ShouldProxy)
        {
            return false;
        }

        if (decision.Reason.Equals("GFW", StringComparison.OrdinalIgnoreCase) ||
            decision.Reason.Equals("Global", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return decision.Reason.StartsWith("Geo:", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldBufferInitialTlsPayload(
        int destPort,
        int payloadLength,
        int bufferedPayloadLength,
        bool hasCachedHostname,
        byte[] payload)
    {
        if (destPort != 443 ||
            payloadLength == 0 ||
            hasCachedHostname)
        {
            return false;
        }

        return bufferedPayloadLength > 0 || ProtocolInspector.LooksLikeTlsClientHello(payload);
    }

    public static bool ShouldSkipDirectBypassRoute(
        IPAddress destinationAddress,
        string? tunIpAddress,
        string? originalDefaultGateway)
    {
        if (destinationAddress.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(destinationAddress))
        {
            return true;
        }

        if (IPAddress.TryParse(tunIpAddress, out var tunIp) &&
            destinationAddress.Equals(tunIp))
        {
            return true;
        }

        var bytes = destinationAddress.GetAddressBytes();
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return true;
        }

        return IPAddress.TryParse(originalDefaultGateway, out var gatewayIp) &&
               destinationAddress.Equals(gatewayIp);
    }
}
