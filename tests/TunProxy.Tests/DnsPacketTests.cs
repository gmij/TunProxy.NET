using TunProxy.Core.Dns;

namespace TunProxy.Tests;

public class DnsPacketTests
{
    [Fact]
    public void Parse_Query_RetainsQuestionTypeAndClass()
    {
        var query = new DnsPacket
        {
            TransactionId = 0x1234,
            Flags = new DnsFlags(0x0100),
            Questions =
            [
                new DnsQuestion
                {
                    Name = "www.example.com",
                    Type = 28,
                    Class = 1
                }
            ]
        }.Build();

        var packet = DnsPacket.Parse(query);

        Assert.NotNull(packet);
        Assert.Single(packet!.Questions);
        Assert.Equal("www.example.com", packet.Questions[0].Name);
        Assert.Equal((ushort)28, packet.Questions[0].Type);
        Assert.Equal((ushort)1, packet.Questions[0].Class);
    }

    [Fact]
    public void Parse_ResponseWithCompressedAnswerName_ParsesAnswer()
    {
        var response = new byte[]
        {
            0x12, 0x34, 0x81, 0x80, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x03, (byte)'w', (byte)'w', (byte)'w',
            0x07, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            0x03, (byte)'c', (byte)'o', (byte)'m',
            0x00,
            0x00, 0x01, 0x00, 0x01,
            0xC0, 0x0C,
            0x00, 0x01, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x3C,
            0x00, 0x04,
            0x01, 0x02, 0x03, 0x04
        };

        var packet = DnsPacket.Parse(response);

        Assert.NotNull(packet);
        Assert.Single(packet!.Answers);
        Assert.Equal("www.example.com", packet.Answers[0].Name);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, packet.Answers[0].Data);
    }
}
