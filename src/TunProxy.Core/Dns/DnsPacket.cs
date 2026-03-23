using System.Text;
using TunProxy.Core.Packets;

namespace TunProxy.Core.Dns;

/// <summary>
/// DNS 数据包解析器
/// </summary>
public class DnsPacket
{
    public ushort TransactionId { get; set; }
    public DnsFlags Flags { get; set; } = new DnsFlags(0);
    public List<DnsQuestion> Questions { get; set; } = new();
    public List<DnsAnswer> Answers { get; set; } = new();

    /// <summary>
    /// 解析 DNS 查询包
    /// </summary>
    public static DnsPacket? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
            return null;

        var packet = new DnsPacket
        {
            TransactionId = NetworkHelper.ReadUInt16BigEndian(data.Slice(0, 2)),
            Flags = new DnsFlags(NetworkHelper.ReadUInt16BigEndian(data.Slice(2, 2)))
        };

        var questionCount = NetworkHelper.ReadUInt16BigEndian(data.Slice(4, 2));
        var answerCount = NetworkHelper.ReadUInt16BigEndian(data.Slice(6, 2));

        var offset = 12;

        // 解析问题部分
        for (int i = 0; i < questionCount && offset < data.Length; i++)
        {
            var question = ParseQuestion(data, ref offset);
            if (question != null)
                packet.Questions.Add(question);
        }

        // 解析答案部分
        for (int i = 0; i < answerCount && offset < data.Length; i++)
        {
            var answer = ParseAnswer(data, ref offset);
            if (answer != null)
                packet.Answers.Add(answer);
        }

        return packet;
    }

    /// <summary>
    /// 构建 DNS 查询包
    /// </summary>
    public byte[] Build()
    {
        using var ms = new MemoryStream();

        // DNS 头部
        var header = new byte[12];
        NetworkHelper.WriteUInt16BigEndian(header.AsSpan(0, 2), TransactionId);
        NetworkHelper.WriteUInt16BigEndian(header.AsSpan(2, 2), Flags.Value);
        NetworkHelper.WriteUInt16BigEndian(header.AsSpan(4, 2), (ushort)Questions.Count);
        NetworkHelper.WriteUInt16BigEndian(header.AsSpan(6, 2), (ushort)Answers.Count);
        ms.Write(header);

        // 问题部分
        foreach (var question in Questions)
        {
            var nameBytes = EncodeDomainName(question.Name);
            ms.Write(nameBytes);

            var typeClass = new byte[4];
            NetworkHelper.WriteUInt16BigEndian(typeClass.AsSpan(0, 2), question.Type);
            NetworkHelper.WriteUInt16BigEndian(typeClass.AsSpan(2, 2), question.Class);
            ms.Write(typeClass);
        }

        // 答案部分
        foreach (var answer in Answers)
        {
            var nameBytes = EncodeDomainName(answer.Name);
            ms.Write(nameBytes);

            var answerHeader = new byte[10];
            NetworkHelper.WriteUInt16BigEndian(answerHeader.AsSpan(0, 2), answer.Type);
            NetworkHelper.WriteUInt16BigEndian(answerHeader.AsSpan(2, 2), answer.Class);
            NetworkHelper.WriteUInt32BigEndian(answerHeader.AsSpan(4, 4), answer.TTL);
            NetworkHelper.WriteUInt16BigEndian(answerHeader.AsSpan(8, 2), (ushort)answer.Data.Length);
            ms.Write(answerHeader);
            ms.Write(answer.Data);
        }

        return ms.ToArray();
    }

    private static DnsQuestion? ParseQuestion(ReadOnlySpan<byte> data, ref int offset)
    {
        var name = ParseDomainName(data, ref offset);
        if (name == null || offset + 4 > data.Length)
            return null;

        var type = NetworkHelper.ReadUInt16BigEndian(data.Slice(offset, 2));
        var @class = NetworkHelper.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
        offset += 4;

        return new DnsQuestion
        {
            Name = name,
            Type = type,
            Class = @class
        };
    }

    private static DnsAnswer? ParseAnswer(ReadOnlySpan<byte> data, ref int offset)
    {
        var name = ParseDomainName(data, ref offset);
        if (name == null || offset + 10 > data.Length)
            return null;

        var type = NetworkHelper.ReadUInt16BigEndian(data.Slice(offset, 2));
        var @class = NetworkHelper.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
        var ttl = NetworkHelper.ReadUInt32BigEndian(data.Slice(offset + 4, 4));
        var dataLength = NetworkHelper.ReadUInt16BigEndian(data.Slice(offset + 8, 2));
        offset += 10;

        if (offset + dataLength > data.Length)
            return null;

        var answerData = data.Slice(offset, dataLength).ToArray();
        offset += dataLength;

        return new DnsAnswer
        {
            Name = name,
            Type = type,
            Class = @class,
            TTL = ttl,
            Data = answerData
        };
    }

    private static string? ParseDomainName(ReadOnlySpan<byte> data, ref int offset)
    {
        var parts = new List<string>();
        var jumped = false;
        var maxJumps = 5;
        var jumps = 0;
        var originalOffset = offset;

        while (offset < data.Length)
        {
            var length = data[offset];

            // 结束标记
            if (length == 0)
            {
                if (!jumped)
                    offset++;
                break;
            }

            // 压缩指针
            if ((length & 0xC0) == 0xC0)
            {
                if (offset + 1 >= data.Length)
                    return null;

                var pointer = ((length & 0x3F) << 8) | data[offset + 1];
                if (!jumped)
                {
                    offset += 2;
                    jumped = true;
                }

                if (++jumps > maxJumps)
                    return null;

                offset = pointer;
                continue;
            }

            offset++;
            if (offset + length > data.Length)
                return null;

            var part = Encoding.ASCII.GetString(data.Slice(offset, length));
            parts.Add(part);
            offset += length;
        }

        if (!jumped)
            offset = originalOffset + 1;

        return string.Join(".", parts);
    }

    private static byte[] EncodeDomainName(string domain)
    {
        using var ms = new MemoryStream();
        var labels = domain.Split('.');

        foreach (var label in labels)
        {
            if (label.Length > 63)
                throw new ArgumentException("Label too long");

            ms.WriteByte((byte)label.Length);
            ms.Write(Encoding.ASCII.GetBytes(label));
        }

        ms.WriteByte(0); // 结束标记
        return ms.ToArray();
    }
}

public class DnsFlags
{
    public ushort Value { get; set; }

    public DnsFlags(ushort value)
    {
        Value = value;
    }

    public bool IsResponse => (Value & 0x8000) != 0;
    public bool IsQuery => !IsResponse;
}

public class DnsQuestion
{
    public string Name { get; set; } = "";
    public ushort Type { get; set; }
    public ushort Class { get; set; }
}

public class DnsAnswer
{
    public string Name { get; set; } = "";
    public ushort Type { get; set; }
    public ushort Class { get; set; }
    public uint TTL { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}
