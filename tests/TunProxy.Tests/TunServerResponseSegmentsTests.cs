using TunProxy.CLI;

namespace TunProxy.Tests;

public class TunServerResponseSegmentsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_ReturnsEmptyForNonPositiveByteCounts(int bytesRead)
    {
        Assert.Empty(TunServerResponseSegments.Create(bytesRead));
    }

    [Fact]
    public void Create_ReturnsSingleSegmentWhenPayloadFitsMss()
    {
        var segments = TunServerResponseSegments.Create(100);

        Assert.Equal([new TunServerResponseSegment(0, 100)], segments);
    }

    [Fact]
    public void Create_SplitsPayloadByMaxSegmentSize()
    {
        var segments = TunServerResponseSegments.Create(3000, maxSegmentSize: 1460);

        Assert.Equal(
            [
                new TunServerResponseSegment(0, 1460),
                new TunServerResponseSegment(1460, 1460),
                new TunServerResponseSegment(2920, 80)
            ],
            segments);
    }

    [Fact]
    public void Create_RejectsNonPositiveSegmentSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TunServerResponseSegments.Create(100, maxSegmentSize: 0));
    }
}
