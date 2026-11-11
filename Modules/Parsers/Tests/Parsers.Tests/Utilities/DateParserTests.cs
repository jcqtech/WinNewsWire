using System.Text;
using Xunit;

namespace WinNewsWire.Parsers.Tests;

public sealed class DateParserTests
{
    private static DateTime Expected(int year, int month, int day, int hour, int minute, int second, int milliseconds = 0)
        => new(year, month, day, hour, minute, second, milliseconds, DateTimeKind.Utc);

    private static void AssertParsedDate(string input, DateTime expected, double accuracy = 0)
    {
        var fromString = Assert.IsType<DateTime>(DateParser.Parse(input));
        var fromBytes = Assert.IsType<DateTime>(DateParser.Parse(Encoding.UTF8.GetBytes(input)));
        if (accuracy > 0)
        {
            Assert.True(Math.Abs((fromString - expected).TotalSeconds) < accuracy, input);
            Assert.True(Math.Abs((fromBytes - expected).TotalSeconds) < accuracy, input);
        }
        else
        {
            Assert.Equal(expected, fromString);
            Assert.Equal(expected, fromBytes);
        }
        Assert.Equal(fromString, fromBytes);
    }

    private static void AssertNilResult(string input)
    {
        Assert.Null(DateParser.Parse(input));
        Assert.Null(DateParser.Parse(Encoding.UTF8.GetBytes(input)));
    }

    [Fact] public void PubDateStandardForms()
    {
        foreach (var suffix in new[] { "GMT", "+0000", "-0000", "+00:00", "-00:00" })
            AssertParsedDate("Fri, 28 May 2010 21:03:38 " + suffix, Expected(2010, 5, 28, 21, 3, 38));
    }
    [Fact] public void PubDateWithoutWeekday()
        => AssertParsedDate("21 May 2010 21:22:53 GMT", Expected(2010, 5, 21, 21, 22, 53));
    [Fact] public void PubDateWithoutSeconds()
    {
        AssertParsedDate("Fri, 28 May 2010 21:03 GMT", Expected(2010, 5, 28, 21, 3, 0));
        AssertParsedDate("30 Apr 2010 5:00 PDT", Expected(2010, 4, 30, 12, 0, 0));
    }
    [Fact] public void W3CStandardForms()
    {
        foreach (var suffix in new[] { "Z", "+00:00", "-00:00", "+0000", "-0000" })
            AssertParsedDate("2010-05-28T21:03:38" + suffix, Expected(2010, 5, 28, 21, 3, 38));
    }
    [Fact] public void W3CMissingTSeparator()
        => AssertParsedDate("2010-11-17 08:40:07-05:00", Expected(2010, 11, 17, 13, 40, 7));
    [Fact] public void SlashDateFormat()
    {
        AssertParsedDate("2020/1/10 14:33:00", Expected(2020, 1, 10, 14, 33, 0));
        AssertParsedDate("2020/12/31 23:59:59", Expected(2020, 12, 31, 23, 59, 59));
    }
    [Fact] public void W3CWithOffset()
    {
        AssertParsedDate("2010-11-17T08:40:07-05:00", Expected(2010, 11, 17, 13, 40, 7));
        AssertParsedDate("2010-07-13T17:06:40+00:00", Expected(2010, 7, 13, 17, 6, 40));
    }
    [Fact] public void HalfHourOffsets()
    {
        AssertParsedDate("2010-05-28T21:03:38+05:30", Expected(2010, 5, 28, 15, 33, 38));
        AssertParsedDate("2010-05-28T21:03:38+09:30", Expected(2010, 5, 28, 11, 33, 38));
        AssertParsedDate("2010-05-28T21:03:38-02:30", Expected(2010, 5, 28, 23, 33, 38));
    }
    [Fact] public void FortyFiveMinuteOffsets()
    {
        AssertParsedDate("2010-05-28T21:03:38+05:45", Expected(2010, 5, 28, 15, 18, 38));
        AssertParsedDate("2010-05-28T21:03:38+12:45", Expected(2010, 5, 28, 8, 18, 38));
    }
    [Fact] public void ExtremeNumericOffsets()
    {
        AssertParsedDate("2010-05-28T21:03:38+14:00", Expected(2010, 5, 28, 7, 3, 38));
        AssertParsedDate("2010-05-28T21:03:38-12:00", Expected(2010, 5, 29, 9, 3, 38));
    }
    [Fact] public void TimeZoneAbbreviations()
    {
        AssertParsedDate("Wed, 23 Jun 2010 03:43:50 Z", Expected(2010, 6, 23, 3, 43, 50));
        AssertParsedDate("Wed, 09 Jun 2010 00:00 EST", Expected(2010, 6, 9, 5, 0, 0));
        AssertParsedDate("Wed, 09 Jun 2010 09:00:00 JST", Expected(2010, 6, 9, 0, 0, 0));
        AssertParsedDate("Wed, 09 Jun 2010 01:00:00 CET", Expected(2010, 6, 9, 0, 0, 0));
        AssertParsedDate("Wed, 09 Jun 2010 02:00:00 IST", Expected(2010, 6, 9, 0, 0, 0));
        AssertParsedDate("Wed, 09 Jun 2010 10:00:00 AEST", Expected(2010, 6, 9, 0, 0, 0));
    }
    [Fact] public void TimeZoneLowercaseZ()
        => AssertParsedDate("2010-05-28T21:03:38z", Expected(2010, 5, 28, 21, 3, 38));
    [Fact] public void UnknownTimeZoneAbbreviation()
        => AssertParsedDate("Fri, 28 May 2010 21:03:38 XYZ", Expected(2010, 5, 28, 21, 3, 38));
    [Fact] public void AllTwelveMonths()
    {
        var names = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        for (var i = 0; i < names.Length; i++)
            AssertParsedDate($"15 {names[i]} 2020 12:00:00 GMT", Expected(2020, i + 1, 15, 12, 0, 0));
    }
    [Fact] public void CaseInsensitiveMonthName()
    {
        foreach (var name in new[] { "Jan", "jan", "JAN", "jAN" })
            AssertParsedDate($"15 {name} 2020 12:00:00 GMT", Expected(2020, 1, 15, 12, 0, 0));
    }
    [Fact] public void TwoDigitYear()
    {
        AssertParsedDate("Sun, 12 Apr 26 17:24:19 +0000", Expected(2026, 4, 12, 17, 24, 19));
        AssertParsedDate("12 Apr 26 17:24:19 +0000", Expected(2026, 4, 12, 17, 24, 19));
        AssertParsedDate("Fri, 28 May 99 21:03:38 +0000", Expected(2099, 5, 28, 21, 3, 38));
        AssertParsedDate("01 Jan 00 00:00:00 +0000", Expected(2000, 1, 1, 0, 0, 0));
    }
    [Fact] public void EpochBoundary()
    {
        var epoch = Expected(1970, 1, 1, 0, 0, 0);
        Assert.Equal(0, new DateTimeOffset(epoch).ToUnixTimeSeconds());
        AssertParsedDate("1970-01-01T00:00:00Z", epoch);
    }
    [Fact] public void FarFutureYear() => AssertParsedDate("2099-12-31T23:59:59Z", Expected(2099, 12, 31, 23, 59, 59));
    [Fact] public void LeapYearFeb29() => AssertParsedDate("2020-02-29T12:00:00Z", Expected(2020, 2, 29, 12, 0, 0));
    [Fact] public void NonLeapYearFebMarchBoundary()
    {
        AssertParsedDate("2021-02-28T23:59:59Z", Expected(2021, 2, 28, 23, 59, 59));
        AssertParsedDate("2021-03-01T00:00:00Z", Expected(2021, 3, 1, 0, 0, 0));
    }
    [Fact] public void MillisecondsStandard()
        => AssertParsedDate("2019-09-27T21:01:48.000000Z", Expected(2019, 9, 27, 21, 1, 48), 0.000001);
    [Fact] public void MillisecondsTruncatedBeyondThreeDigits()
        => AssertParsedDate("2021-03-29T10:46:56.516941+00:00", Expected(2021, 3, 29, 10, 46, 56, 516), 0.000001);
    [Fact] public void EmptyInputReturnsNil() => AssertNilResult("");
    [Fact] public void TooShortInputReturnsNil()
    {
        AssertNilResult("abc");
        AssertNilResult("2010-");
    }
    [Fact] public void TooLongInputReturnsNil() => AssertNilResult(new string('x', 200));
    [Fact] public void UnknownMonthNameFallsBackToJanuary()
    {
        AssertParsedDate("Fri, 28 Xyz 2010 21:03:38 GMT", Expected(2010, 1, 28, 21, 3, 38));
        AssertParsedDate("28 Xyz 2010 21:03:38 GMT", Expected(2010, 1, 28, 21, 3, 38));
    }
    [Fact] public void MonthPrefixMatchAccepted()
        => AssertParsedDate("28 Februar 2010 21:03:38 GMT", Expected(2010, 2, 28, 21, 3, 38));
    [Fact] public void GarbageInputReturnsEpoch()
        => AssertParsedDate("the quick brown fox jumped over", DateTime.UnixEpoch);
    [Fact] public void PartialIsoDateAcceptedWithMissingFieldsZeroed()
    {
        AssertParsedDate("2020-02", Expected(2020, 2, 1, 0, 0, 0));
        AssertParsedDate("2020-02-15", Expected(2020, 2, 15, 0, 0, 0));
    }
    [Fact] public void MixedSeparatorsTolerated()
    {
        AssertParsedDate("2020-01/10 14:33:00", Expected(2020, 1, 10, 14, 33, 0));
        AssertParsedDate("2020/01-10 14:33:00", Expected(2020, 1, 10, 14, 33, 0));
    }
    [Fact] public void ExistingFeedDate()
        => AssertParsedDate("2010-06-22T03:57:49+00:00", Expected(2010, 6, 22, 3, 57, 49));
    [Fact] public void ByteSliceRespectsBounds()
        => Assert.Equal(Expected(2020, 1, 10, 14, 33, 0), DateParser.Parse("xx2020/1/10 14:33:00xx"u8[2..^2]));
}
