using System.Runtime.CompilerServices;

namespace WinNewsWire.Parsers;

/// <summary>
/// Port of the Objective-C <c>RSDateParser</c> (RFC 822 pubDate and W3C/ISO 8601).
/// Tolerant of malformed input — GIGO, same policy as the Mac version.
/// Returns UTC <see cref="DateTime"/> (<see cref="DateTimeKind.Utc"/>).
/// </summary>
public static class DateParser
{
    // Ported verbatim from timeZoneTable in RSDateParser.m.
    // See http://en.wikipedia.org/wiki/List_of_time_zone_abbreviations.
    private static readonly (string Abbrev, int Hours, int Minutes)[] s_timeZones =
    {
        ("GMT", 0, 0),
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
        ("WEST", 1, 0), ("YAKT", 9, 0), ("YEKT", 5, 0),
    };

    private const int NotFound = -1;

    public static DateTime? Parse(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        return Parse(bytes);
    }

    public static DateTime? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6) return null;
        // Heuristic from the Swift wrapper RSDateWithString: if it contains a 'T' or '-'
        // in typical ISO-8601 positions, use W3C parsing. Else RFC822 pubDate.
        if (LooksLikeW3C(bytes)) return ParseW3C(bytes);
        return ParsePubDate(bytes);
    }

    private static bool LooksLikeW3C(ReadOnlySpan<byte> bytes)
    {
        // ISO 8601 always starts with a 4-digit year followed by '-'.
        // Ex: "2010-05-28T21:03:38Z", "2021-03-29T10:46:56.516941+00:00".
        if (bytes.Length < 10) return false;
        for (int i = 0; i < 4; i++)
        {
            if (bytes[i] < (byte)'0' || bytes[i] > (byte)'9') return false;
        }
        return bytes[4] == (byte)'-';
    }

    // ---- RFC 822 pubDate ------------------------------------------------

    private static DateTime? ParsePubDate(ReadOnlySpan<byte> bytes)
    {
        int finalIndex = 0;
        int day = NextNumericValue(bytes, 0, 2, out finalIndex);
        if (day < 1 || day == NotFound) day = 1;

        int month = NextMonthValue(bytes, finalIndex + 1, out finalIndex);
        if (month == NotFound) month = 1;

        int year = NextNumericValue(bytes, finalIndex + 1, 4, out finalIndex);
        if (year == NotFound) year = 1970;
        if (year < 100) year += 2000; // Two-digit year → 21st century.

        int hour = NextNumericValue(bytes, finalIndex + 1, 2, out finalIndex);
        if (hour == NotFound) hour = 0;

        int minute = NextNumericValue(bytes, finalIndex + 1, 2, out finalIndex);
        if (minute == NotFound) minute = 0;

        int currentIndex = finalIndex + 1;
        int second = 0;
        bool hasSeconds = currentIndex < bytes.Length && bytes[currentIndex] == (byte)':';
        if (hasSeconds)
        {
            second = NextNumericValue(bytes, currentIndex, 2, out finalIndex);
            if (second == NotFound) second = 0;
            currentIndex = finalIndex + 1;
        }

        int timeZoneOffset = ParsedTimeZoneOffset(bytes, currentIndex);

        return BuildUtc(year, month, day, hour, minute, second, 0, timeZoneOffset);
    }

    // ---- W3C / ISO 8601 -------------------------------------------------

    private static DateTime? ParseW3C(ReadOnlySpan<byte> bytes)
    {
        // Expected: yyyy-MM-dd(T| )HH:mm:ss[.fff...][Z|+HH:MM|-HH:MM|+HHMM|-HHMM]
        int finalIndex;
        int year = NextNumericValue(bytes, 0, 4, out finalIndex);
        if (year == NotFound) return null;

        int month = NextNumericValue(bytes, finalIndex + 1, 2, out finalIndex);
        if (month == NotFound) month = 1;

        int day = NextNumericValue(bytes, finalIndex + 1, 2, out finalIndex);
        if (day == NotFound) day = 1;

        int hour = NextNumericValue(bytes, finalIndex + 1, 2, out finalIndex);
        if (hour == NotFound) hour = 0;

        int minute = NextNumericValue(bytes, finalIndex + 1, 2, out finalIndex);
        if (minute == NotFound) minute = 0;

        int second = NextNumericValue(bytes, finalIndex + 1, 2, out finalIndex);
        if (second == NotFound) second = 0;

        int millis = 0;
        int currentIndex = finalIndex + 1;
        // Optional fractional seconds: ".xxxxxx"
        if (currentIndex < bytes.Length && bytes[currentIndex] == (byte)'.')
        {
            // We only keep millisecond precision, but skip over any more digits.
            int fracStart = currentIndex + 1;
            int fracEnd = fracStart;
            while (fracEnd < bytes.Length && bytes[fracEnd] >= (byte)'0' && bytes[fracEnd] <= (byte)'9')
                fracEnd++;
            int digits = fracEnd - fracStart;
            if (digits > 0)
            {
                // Take up to 3 digits for milliseconds, pad right if fewer.
                int use = Math.Min(3, digits);
                int value = 0;
                for (int i = 0; i < use; i++) value = value * 10 + (bytes[fracStart + i] - (byte)'0');
                for (int i = use; i < 3; i++) value *= 10;
                millis = value;
            }
            currentIndex = fracEnd;
        }

        int tzOffset = ParsedTimeZoneOffset(bytes, currentIndex);
        return BuildUtc(year, month, day, hour, minute, second, millis, tzOffset);
    }

    // ---- Helpers --------------------------------------------------------

    private static int NextMonthValue(ReadOnlySpan<byte> bytes, int startingIndex, out int finalIndex)
    {
        int i;
        int numberOfAlpha = 0;
        Span<byte> m = stackalloc byte[3];
        finalIndex = startingIndex;
        for (i = startingIndex; i < bytes.Length; i++)
        {
            finalIndex = i;
            byte ch = bytes[i];
            bool isAlpha = IsAlpha(ch);
            if (!isAlpha && numberOfAlpha < 1) continue;
            if (!isAlpha && numberOfAlpha > 0) break;
            numberOfAlpha++;
            if (numberOfAlpha == 1)
            {
                byte c = ToLower(ch);
                if (c == 'f') return 2;  // Feb
                if (c == 's') return 9;  // Sep
                if (c == 'o') return 10; // Oct
                if (c == 'n') return 11; // Nov
                if (c == 'd') return 12; // Dec
            }
            m[numberOfAlpha - 1] = ch;
            if (numberOfAlpha >= 3) break;
        }
        if (numberOfAlpha < 2) return NotFound;
        byte m0 = ToLower(m[0]);
        byte m1 = ToLower(m[1]);
        byte m2 = ToLower(m[2]);
        if (m0 == 'j') { // Jan, Jun, Jul
            if (m1 == 'a') return 1;
            if (m1 == 'u')
            {
                if (m2 == 'n') return 6;
                return 7;
            }
            return 1;
        }
        if (m0 == 'm') { // March, May
            if (m2 == 'y') return 5;
            return 3;
        }
        if (m0 == 'a') { // April, August
            if (m1 == 'u') return 8;
            return 4;
        }
        return 1;
    }

    private static int NextNumericValue(ReadOnlySpan<byte> bytes, int startingIndex, int maxDigits, out int finalIndex)
    {
        if (maxDigits > 4) maxDigits = 4;
        int digitsFound = 0;
        int value = 0;
        int i;
        finalIndex = startingIndex;
        for (i = startingIndex; i < bytes.Length; i++)
        {
            finalIndex = i;
            bool isDigit = bytes[i] >= (byte)'0' && bytes[i] <= (byte)'9';
            if (!isDigit && digitsFound < 1) continue;
            if (!isDigit && digitsFound > 0) break;
            value = value * 10 + (bytes[i] - (byte)'0');
            digitsFound++;
            if (digitsFound >= maxDigits) break;
        }
        if (digitsFound < 1) return NotFound;
        return value;
    }

    private static int ParsedTimeZoneOffset(ReadOnlySpan<byte> bytes, int startingIndex)
    {
        Span<byte> tz = stackalloc byte[6];
        int n = 0;
        for (int i = startingIndex; i < bytes.Length; i++)
        {
            byte ch = bytes[i];
            if (ch == (byte)':' || ch == (byte)' ') continue;
            if ((ch >= (byte)'0' && ch <= (byte)'9') || IsAlpha(ch) || ch == (byte)'+' || ch == (byte)'-')
            {
                tz[n++] = ch;
                if (n >= 5) break;
            }
        }
        if (n < 1) return 0;
        byte first = tz[0];
        if (first == (byte)'Z' || first == (byte)'z') return 0;

        // GMT / UTC anywhere in the captured characters → 0.
        if (ContainsCaseInsensitive(tz[..n], "GMT") || ContainsCaseInsensitive(tz[..n], "UTC"))
            return 0;

        bool anyAlpha = false;
        for (int i = 0; i < n; i++) { if (IsAlpha(tz[i])) { anyAlpha = true; break; } }
        if (anyAlpha) return OffsetForAbbrev(tz[..n]);
        return OffsetForNumeric(tz[..n]);
    }

    private static int OffsetForAbbrev(ReadOnlySpan<byte> abbrev)
    {
        foreach (var z in s_timeZones)
        {
            if (EqualsAsciiCaseInsensitive(abbrev, z.Abbrev))
            {
                int seconds = z.Hours < 0
                    ? (z.Hours * 3600) - (z.Minutes * 60)
                    : (z.Hours * 3600) + (z.Minutes * 60);
                return seconds;
            }
        }
        return 0;
    }

    private static int OffsetForNumeric(ReadOnlySpan<byte> tz)
    {
        bool isPlus = tz[0] == (byte)'+';
        int finalIndex;
        int hours = NextNumericValue(tz, 0, 2, out finalIndex);
        int minutes = NextNumericValue(tz, finalIndex + 1, 2, out finalIndex);
        if (hours == NotFound) hours = 0;
        if (minutes == NotFound) minutes = 0;
        int seconds = hours * 3600 + minutes * 60;
        if (!isPlus) seconds = -seconds;
        return seconds;
    }

    private static DateTime? BuildUtc(int year, int month, int day, int hour, int minute, int second, int millis, int tzOffsetSeconds)
    {
        if (year < 1 || year > 9999) return null;
        if (month < 1 || month > 12) return null;
        int maxDay = DateTime.DaysInMonth(year, month);
        if (day < 1 || day > maxDay) return null;
        if (hour < 0 || hour > 23) hour = 0;
        if (minute < 0 || minute > 59) minute = 0;
        if (second < 0 || second > 59) second = 0;
        if (millis < 0 || millis > 999) millis = 0;

        var local = new DateTime(year, month, day, hour, minute, second, millis, DateTimeKind.Utc);
        // Subtract the zone offset to normalize to UTC.
        return local.AddSeconds(-tzOffsetSeconds);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAlpha(byte b) => (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToLower(byte b) => (b >= (byte)'A' && b <= (byte)'Z') ? (byte)(b + 32) : b;

    private static bool ContainsCaseInsensitive(ReadOnlySpan<byte> haystack, string needle)
    {
        if (haystack.Length < needle.Length) return false;
        int max = haystack.Length - needle.Length;
        for (int i = 0; i <= max; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (ToLower(haystack[i + j]) != (byte)char.ToLowerInvariant(needle[j])) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }

    private static bool EqualsAsciiCaseInsensitive(ReadOnlySpan<byte> a, string b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (ToLower(a[i]) != (byte)char.ToLowerInvariant(b[i])) return false;
        }
        return true;
    }
}
