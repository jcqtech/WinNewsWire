using System;

namespace WinNewsWire.Data;

/// <summary>
/// Handles date epoch conversions for NetNewsWire database compatibility.
/// Articles DB uses Unix epoch (1970-01-01). FeedSettings and Errors DBs use
/// Apple's reference date epoch (2001-01-01).
/// </summary>
public static class DateConversion
{
    /// <summary>
    /// Seconds between Unix epoch (1970-01-01) and Apple reference date (2001-01-01).
    /// </summary>
    public const double AppleEpochOffset = 978307200.0;

    public static double ToUnixTimestamp(DateTimeOffset date)
    {
        return date.ToUnixTimeMilliseconds() / 1000.0;
    }

    public static DateTimeOffset FromUnixTimestamp(double timestamp)
    {
        long ms = (long)(timestamp * 1000.0);
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    public static double ToAppleReferenceDate(DateTimeOffset date)
    {
        return ToUnixTimestamp(date) - AppleEpochOffset;
    }

    public static DateTimeOffset FromAppleReferenceDate(double appleTimestamp)
    {
        return FromUnixTimestamp(appleTimestamp + AppleEpochOffset);
    }

    public static double? ToUnixTimestampOrNull(DateTimeOffset? date)
    {
        return date.HasValue ? ToUnixTimestamp(date.Value) : null;
    }

    public static DateTimeOffset? FromUnixTimestampOrNull(double? timestamp)
    {
        return timestamp.HasValue ? FromUnixTimestamp(timestamp.Value) : null;
    }

    public static double? ToAppleReferenceDateOrNull(DateTimeOffset? date)
    {
        return date.HasValue ? ToAppleReferenceDate(date.Value) : null;
    }

    public static DateTimeOffset? FromAppleReferenceDateOrNull(double? appleTimestamp)
    {
        return appleTimestamp.HasValue ? FromAppleReferenceDate(appleTimestamp.Value) : null;
    }
}
