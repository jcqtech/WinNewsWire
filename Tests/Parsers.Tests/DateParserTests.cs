using System;
using Xunit;
using WinNewsWire.Parsers.Utilities;

namespace WinNewsWire.Parsers.Tests;

public class DateParserTests
{
    private static DateTime Expected(int y, int mo, int d, int h, int mi, int s, int ms = 0)
        => new DateTime(y, mo, d, h, mi, s, ms, DateTimeKind.Utc);

    [Theory]
    [InlineData("Fri, 28 May 2010 21:03:38 +0000", 2010, 5, 28, 21, 3, 38)]
    [InlineData("Fri, 28 May 2010 21:03:38 +00:00", 2010, 5, 28, 21, 3, 38)]
    [InlineData("Fri, 28 May 2010 21:03:38 -00:00", 2010, 5, 28, 21, 3, 38)]
    [InlineData("Fri, 28 May 2010 21:03:38 -0000", 2010, 5, 28, 21, 3, 38)]
    [InlineData("Fri, 28 May 2010 21:03:38 GMT", 2010, 5, 28, 21, 3, 38)]
    [InlineData("2010-05-28T21:03:38+00:00", 2010, 5, 28, 21, 3, 38)]
    [InlineData("2010-05-28T21:03:38+0000", 2010, 5, 28, 21, 3, 38)]
    [InlineData("2010-05-28T21:03:38-0000", 2010, 5, 28, 21, 3, 38)]
    [InlineData("2010-05-28T21:03:38-00:00", 2010, 5, 28, 21, 3, 38)]
    [InlineData("2010-05-28T21:03:38Z", 2010, 5, 28, 21, 3, 38)]
    [InlineData("2010-07-13T17:06:40+00:00", 2010, 7, 13, 17, 6, 40)]
    [InlineData("30 Apr 2010 5:00 PDT", 2010, 4, 30, 12, 0, 0)]
    [InlineData("21 May 2010 21:22:53 GMT", 2010, 5, 21, 21, 22, 53)]
    [InlineData("Wed, 09 Jun 2010 00:00 EST", 2010, 6, 9, 5, 0, 0)]
    [InlineData("Wed, 23 Jun 2010 03:43:50 Z", 2010, 6, 23, 3, 43, 50)]
    [InlineData("2010-06-22T03:57:49+00:00", 2010, 6, 22, 3, 57, 49)]
    [InlineData("2010-11-17T08:40:07-05:00", 2010, 11, 17, 13, 40, 7)]
    public void ParsesFeedDates(string input, int y, int mo, int d, int h, int mi, int s)
        => Assert.Equal(Expected(y, mo, d, h, mi, s), DateParser.Parse(input));

    [Fact]
    public void AtomDateWithMissingTCharacter()
        => Assert.Equal(Expected(2010, 11, 17, 13, 40, 7), DateParser.Parse("2010-11-17 08:40:07-05:00"));

    [Fact]
    public void FeedbinDate()
        => Assert.Equal(Expected(2019, 9, 27, 21, 1, 48), DateParser.Parse("2019-09-27T21:01:48.000000Z"));

    [Theory]
    [InlineData("Sun, 12 Apr 26 17:24:19 +0000", 2026, 4, 12, 17, 24, 19)]
    [InlineData("12 Apr 26 17:24:19 +0000", 2026, 4, 12, 17, 24, 19)]
    [InlineData("Fri, 28 May 99 21:03:38 +0000", 2099, 5, 28, 21, 3, 38)]
    [InlineData("01 Jan 00 00:00:00 +0000", 2000, 1, 1, 0, 0, 0)]
    public void TwoDigitYear(string input, int y, int mo, int d, int h, int mi, int s)
        => Assert.Equal(Expected(y, mo, d, h, mi, s), DateParser.Parse(input));

    [Fact]
    public void HighMillisecondDate()
    {
        var actual = DateParser.Parse("2021-03-29T10:46:56.516941+00:00")!.Value;
        var expected = Expected(2021, 3, 29, 10, 46, 56, 516);
        Assert.True(Math.Abs((actual - expected).TotalMilliseconds) < 1.0);
    }
}
