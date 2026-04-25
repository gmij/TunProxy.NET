namespace TunProxy.CLI;

internal static class TunServerResponseSegments
{
    public const int DefaultMss = 1460;

    public static IReadOnlyList<TunServerResponseSegment> Create(int bytesRead, int maxSegmentSize = DefaultMss)
    {
        if (bytesRead <= 0)
        {
            return [];
        }

        if (maxSegmentSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSegmentSize), "Segment size must be greater than zero.");
        }

        var segments = new List<TunServerResponseSegment>((bytesRead + maxSegmentSize - 1) / maxSegmentSize);
        var offset = 0;
        while (offset < bytesRead)
        {
            var length = Math.Min(maxSegmentSize, bytesRead - offset);
            segments.Add(new TunServerResponseSegment(offset, length));
            offset += length;
        }

        return segments;
    }
}

internal readonly record struct TunServerResponseSegment(int Offset, int Length);
