using OpenVideoSmartShare.Upnp;

namespace OpenVideoSmartShare.Tests;

public class ByteRangeTests
{
    [Theory]
    [InlineData("bytes=0-", 1000, 0, 999)]
    [InlineData("bytes=0-99", 1000, 0, 99)]
    [InlineData("bytes=500-2000", 1000, 500, 999)]
    [InlineData("bytes=-100", 1000, 900, 999)]
    [InlineData("bytes=-5000", 1000, 0, 999)]
    [InlineData("BYTES=10-20, 30-40", 1000, 10, 20)]
    public void ParsesSatisfiableRanges(string header, long size, long start, long end)
    {
        Assert.Equal(RangeParse.Range, ByteRange.TryParse(header, size, out var range));
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
        Assert.Equal(end - start + 1, range.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-1")]
    [InlineData("bytes=abc")]
    [InlineData("bytes=20-10")]
    public void IgnoresMissingOrMalformedHeaders(string? header)
    {
        Assert.Equal(RangeParse.None, ByteRange.TryParse(header, 1000, out _));
    }

    [Fact]
    public void RejectsStartBeyondEnd()
    {
        Assert.Equal(RangeParse.Unsatisfiable, ByteRange.TryParse("bytes=1000-", 1000, out _));
    }
}
