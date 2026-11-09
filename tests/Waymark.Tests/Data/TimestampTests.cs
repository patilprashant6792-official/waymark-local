using Waymark.Data.Sqlite;

namespace Waymark.Tests.Data;

public sealed class TimestampTests
{
    private static readonly DateTimeOffset Sample = new(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);

    [Fact]
    public void FormatsAsFixedWidthUtcText() =>
        Assert.Equal("2026-01-02T03:04:05.678Z", Timestamp.Format(Sample));

    [Fact]
    public void ConvertsOtherOffsetsToUtcBeforeFormatting()
    {
        var local = new DateTimeOffset(2026, 1, 2, 8, 34, 5, 678, TimeSpan.FromMinutes(330));

        Assert.Equal("2026-01-02T03:04:05.678Z", Timestamp.Format(local));
    }

    [Fact]
    public void DropsPrecisionBelowAMillisecond()
    {
        var precise = Sample.AddTicks(1234);

        Assert.Equal("2026-01-02T03:04:05.678Z", Timestamp.Format(precise));
    }

    [Fact]
    public void RoundTripsAtMillisecondPrecision()
    {
        var parsed = Timestamp.Parse(Timestamp.Format(Sample));

        Assert.Equal(Sample, parsed);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Fact]
    public void NullStaysNull()
    {
        Assert.Null(Timestamp.FormatNullable(null));
        Assert.Null(Timestamp.ParseNullable(null));
    }

    [Fact]
    public void NullableValuesRoundTrip()
    {
        Assert.Equal("2026-01-02T03:04:05.678Z", Timestamp.FormatNullable(Sample));
        Assert.Equal(Sample, Timestamp.ParseNullable("2026-01-02T03:04:05.678Z"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("2026-01-02 03:04:05")]
    [InlineData("2026-01-02T03:04:05Z")]
    public void ParsingAnythingElseFails(string text) =>
        Assert.Throws<FormatException>(() => Timestamp.Parse(text));

    [Fact]
    public void TextOrderIsTimeOrder()
    {
        var earlier = Timestamp.Format(Sample);
        var later = Timestamp.Format(Sample.AddMilliseconds(1));
        var muchLater = Timestamp.Format(Sample.AddYears(1));

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
        Assert.True(string.CompareOrdinal(later, muchLater) < 0);
    }
}
