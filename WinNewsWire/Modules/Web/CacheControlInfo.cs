using System.Net.Http.Headers;

namespace WinNewsWire.Web;

/// <summary>Port of <c>CacheControlInfo</c>. Tracks Cache-Control max-age for feed skip logic.</summary>
public sealed record CacheControlInfo(DateTime DateCreated, TimeSpan MaxAge)
{
    public DateTime ResumeDate => DateCreated + MaxAge;
    public bool CanResume => DateTime.UtcNow >= ResumeDate;

    /// <summary>CanResume with a maximum cap on max-age, because many sites misconfigure it.</summary>
    public bool CanResume_Clamped(TimeSpan maxMaxAge)
    {
        var clamped = MaxAge < maxMaxAge ? MaxAge : maxMaxAge;
        return DateTime.UtcNow >= DateCreated + clamped;
    }

    public static CacheControlInfo? FromHeaders(HttpResponseHeaders headers)
    {
        var cc = headers.CacheControl;
        if (cc?.MaxAge is not { } maxAge || maxAge <= TimeSpan.Zero)
            return null;
        return new CacheControlInfo(DateTime.UtcNow, maxAge);
    }

    /// <summary>Parse from raw Cache-Control header value (e.g. "max-age=3600, public").</summary>
    public static CacheControlInfo? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase))
            {
                var ageStr = part.AsSpan(8);
                if (double.TryParse(ageStr, out var seconds) && seconds > 0)
                    return new CacheControlInfo(DateTime.UtcNow, TimeSpan.FromSeconds(seconds));
            }
        }

        return null;
    }
}
