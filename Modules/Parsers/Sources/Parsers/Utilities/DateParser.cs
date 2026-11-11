using System.Diagnostics;
using System.Text;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

public static class DateParser
{
    private static readonly Dictionary<ulong, int> TimeZoneOffsets = new (string Name, int Hours, int Minutes)[]
    {
        ("PDT", -7, 0), ("PST", -8, 0), ("EST", -5, 0), ("EDT", -4, 0),
        ("MDT", -6, 0), ("MST", -7, 0), ("CST", -6, 0), ("CDT", -5, 0),
        ("ACT", -8, 0), ("AFT", 4, 30), ("AMT", 4, 0), ("ART", -3, 0),
        ("AST", 3, 0), ("AZT", 4, 0), ("BIT", -12, 0), ("BDT", 8, 0),
        ("ACST", 9, 30), ("AEST", 10, 0), ("AKST", -9, 0), ("AMST", 5, 0),
        ("AWST", 8, 0), ("AZOST", -1, 0), ("BIOT", 6, 0), ("BRT", -3, 0),
        ("BST", 6, 0), ("BTT", 6, 0), ("CAT", 2, 0), ("CCT", 6, 30),
        ("CET", 1, 0), ("CEST", 2, 0), ("CHAST", 12, 45), ("ChST", 10, 0),
        ("CIST", -8, 0), ("CKT", -10, 0), ("CLT", -4, 0), ("CLST", -3, 0),
        ("COT", -5, 0), ("COST", -4, 0), ("CVT", -1, 0), ("CXT", 7, 0),
        ("EAST", -6, 0), ("EAT", 3, 0), ("ECT", -4, 0), ("EEST", 3, 0),
        ("EET", 2, 0), ("FJT", 12, 0), ("FKST", -4, 0), ("GALT", -6, 0),
        ("GET", 4, 0), ("GFT", -3, 0), ("GILT", 7, 0), ("GIT", -9, 0),
        ("GST", -2, 0), ("GYT", -4, 0), ("HAST", -10, 0), ("HKT", 8, 0),
        ("HMT", 5, 0), ("IRKT", 8, 0), ("IRST", 3, 30), ("IST", 2, 0),
        ("JST", 9, 0), ("KRAT", 7, 0), ("KST", 9, 0), ("LHST", 10, 30),
        ("LINT", 14, 0), ("MAGT", 11, 0), ("MIT", -9, 30), ("MSK", 3, 0),
        ("MUT", 4, 0), ("NDT", -2, 30), ("NFT", 11, 30), ("NPT", 5, 45),
        ("NT", -3, 30), ("OMST", 6, 0), ("PETT", 12, 0), ("PHOT", 13, 0),
        ("PKT", 5, 0), ("RET", 4, 0), ("SAMT", 4, 0), ("SAST", 2, 0),
        ("SBT", 11, 0), ("SCT", 4, 0), ("SLT", 5, 30), ("SST", 8, 0),
        ("TAHT", -10, 0), ("THA", 7, 0), ("UYT", -3, 0), ("UYST", -2, 0),
        ("VET", -4, 30), ("VLAT", 10, 0), ("WAT", 1, 0), ("WET", 0, 0),
        ("WEST", 1, 0), ("YAKT", 9, 0), ("YEKT", 5, 0)
    }.ToDictionary(zone => PackAscii(zone.Name),
        zone => zone.Hours * 3600 + (zone.Hours < 0 ? -zone.Minutes : zone.Minutes) * 60);

    public static DateTime? Parse(string? value) => value is null ? null : Parse(Encoding.UTF8.GetBytes(value));

    public static DateTime? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 6 or > 150) return null;
        if (LooksLikeW3C(bytes)) return ParseW3C(bytes);
        if (bytes.Contains((byte)' ') || bytes.Contains((byte)',')) return ParsePubDate(bytes);
        return ParseW3C(bytes);
    }

    private static bool LooksLikeW3C(ReadOnlySpan<byte> bytes)
    {
        var start = 0;
        while (start < bytes.Length && bytes[start].IsAsciiWhitespace()) start++;
        if (bytes.Length - start < 5) return false;
        return bytes[start].IsAsciiDigit() && bytes[start + 1].IsAsciiDigit()
            && bytes[start + 2].IsAsciiDigit() && bytes[start + 3].IsAsciiDigit()
            && bytes[start + 4] is (byte)'-' or (byte)'/';
    }

    private static DateTime? ParsePubDate(ReadOnlySpan<byte> bytes)
    {
        var finalIndex = 0;
        var day = Math.Max(1, NextNumericValue(bytes, 0, 2, ref finalIndex) ?? 1);
        var month = NextMonthValue(bytes, finalIndex + 1, ref finalIndex) ?? 1;
        var year = NextNumericValue(bytes, finalIndex + 1, 4, ref finalIndex);
        if (year < 100) year += 2000;
        var hour = NextNumericValue(bytes, finalIndex + 1, 2, ref finalIndex) ?? 0;
        var minute = NextNumericValue(bytes, finalIndex + 1, 2, ref finalIndex) ?? 0;
        var currentIndex = finalIndex + 1;
        var second = currentIndex < bytes.Length && bytes[currentIndex] == ':'
            ? NextNumericValue(bytes, currentIndex, 2, ref finalIndex) ?? 0 : 0;
        currentIndex = finalIndex + 1;
        var timeZone = currentIndex < bytes.Length && bytes[currentIndex] == ' '
            ? ParsedTimeZoneOffset(bytes, currentIndex) : 0;
        return DateWithComponents(year ?? 1970, month, day, hour, minute, second, 0, timeZone);
    }

    private static DateTime? ParseW3C(ReadOnlySpan<byte> bytes)
    {
        var finalIndex = 0;
        var year = NextNumericValue(bytes, 0, 4, ref finalIndex) ?? 1970;
        var month = NextNumericValue(bytes, finalIndex + 1, 2, ref finalIndex) ?? 1;
        var day = NextNumericValue(bytes, finalIndex + 1, 2, ref finalIndex) ?? 1;
        var hour = NextNumericValue(bytes, finalIndex + 1, 2, ref finalIndex) ?? 0;
        var minute = NextNumericValue(bytes, finalIndex + 1, 2, ref finalIndex) ?? 0;
        var second = NextNumericValue(bytes, finalIndex + 1, 2, ref finalIndex) ?? 0;
        var currentIndex = finalIndex + 1;
        var milliseconds = 0;
        if (currentIndex < bytes.Length && bytes[currentIndex] == '.')
        {
            milliseconds = NextNumericValue(bytes, currentIndex, 3, ref finalIndex) ?? 0;
            currentIndex = finalIndex + 1;
            while (currentIndex < bytes.Length && bytes[currentIndex].IsAsciiDigit()) currentIndex++;
        }
        return DateWithComponents(year, month, day, hour, minute, second, milliseconds, ParsedTimeZoneOffset(bytes, currentIndex));
    }

    private static DateTime? DateWithComponents(int year, int month, int day, int hour, int minute, int second, int milliseconds, int timeZone)
    {
        // The native days-from-civil calculation deliberately normalizes out-of-range components.
        var shiftedYear = month <= 2 ? year - 1 : year;
        var era = (shiftedYear >= 0 ? shiftedYear : shiftedYear - 399) / 400;
        var yearOfEra = shiftedYear - era * 400;
        var shiftedMonth = month > 2 ? month - 3 : month + 9;
        var dayOfYear = (153 * shiftedMonth + 2) / 5 + day - 1;
        var dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
        var days = era * 146097L + dayOfEra - 719468;
        var seconds = days * 86400 + hour * 3600 + minute * 60 + second - timeZone;
        var ticks = DateTime.UnixEpoch.Ticks + seconds * TimeSpan.TicksPerSecond + milliseconds * TimeSpan.TicksPerMillisecond;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            Trace.TraceWarning("Feed date falls outside the supported .NET DateTime range.");
            return null;
        }
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static int? NextNumericValue(ReadOnlySpan<byte> bytes, int startingIndex, int maxDigits, ref int finalIndex)
    {
        var i = startingIndex;
        while (i < bytes.Length && !bytes[i].IsAsciiDigit()) finalIndex = i++;
        if (i >= bytes.Length) return null;
        var value = 0;
        for (var count = 0; i < bytes.Length && count < Math.Min(4, maxDigits) && bytes[i].IsAsciiDigit(); count++)
        {
            value = value * 10 + bytes[i] - '0';
            finalIndex = i++;
        }
        return value;
    }

    private static int? NextMonthValue(ReadOnlySpan<byte> bytes, int startingIndex, ref int finalIndex)
    {
        Span<byte> month = stackalloc byte[3];
        month.Clear();
        var count = 0;
        for (var i = startingIndex; i < bytes.Length; i++)
        {
            finalIndex = i;
            var b = bytes[i];
            if (!b.IsAsciiLetter())
            {
                if (count == 0) continue;
                break;
            }
            var lower = b.AsciiLowercased();
            if (count == 0)
            {
                var unambiguous = lower switch { (byte)'f' => 2, (byte)'s' => 9, (byte)'o' => 10, (byte)'n' => 11, (byte)'d' => 12, _ => 0 };
                if (unambiguous != 0) return unambiguous;
            }
            month[count++] = lower;
            if (count == 3) break;
        }
        if (count < 2) return null;
        return month[0] switch
        {
            (byte)'j' => month[1] == 'u' ? month[2] == 'n' ? 6 : 7 : 1,
            (byte)'m' => month[2] == 'y' ? 5 : 3,
            (byte)'a' => month[1] == 'u' ? 8 : 4,
            _ => 1
        };
    }

    private static int ParsedTimeZoneOffset(ReadOnlySpan<byte> bytes, int startingIndex)
    {
        var i = startingIndex;
        while (i < bytes.Length && bytes[i] is (byte)' ' or (byte)':') i++;
        if (i >= bytes.Length || bytes[i] is (byte)'z' or (byte)'Z') return 0;
        if (bytes[i] is (byte)'+' or (byte)'-') return ParseNumericTimeZoneOffset(bytes, i);
        if (!bytes[i].IsAsciiLetter()) return 0;
        ulong packed = 0;
        var count = 0;
        while (i < bytes.Length && count < 5)
        {
            var b = bytes[i++];
            if (b.IsAsciiLetter()) packed |= (ulong)b.AsciiLowercased() << (count++ * 8);
            else if (b is not ((byte)':' or (byte)' ')) break;
        }
        return TimeZoneOffsets.GetValueOrDefault(packed);
    }

    private static int ParseNumericTimeZoneOffset(ReadOnlySpan<byte> bytes, int start)
    {
        var positive = bytes[start] == '+';
        var i = start + 1;
        var hours = 0;
        for (var count = 0; i < bytes.Length && count < 2 && bytes[i].IsAsciiDigit(); count++)
            hours = hours * 10 + bytes[i++] - '0';
        if (i < bytes.Length && bytes[i] is (byte)':' or (byte)' ') i++;
        var minutes = 0;
        for (var count = 0; i < bytes.Length && count < 2 && bytes[i].IsAsciiDigit(); count++)
            minutes = minutes * 10 + bytes[i++] - '0';
        var seconds = hours * 3600 + minutes * 60;
        return positive ? seconds : -seconds;
    }

    private static ulong PackAscii(string name)
    {
        ulong packed = 0;
        for (var i = 0; i < Math.Min(8, name.Length); i++) packed |= (ulong)(name[i] | 0x20) << (i * 8);
        return packed;
    }
}
