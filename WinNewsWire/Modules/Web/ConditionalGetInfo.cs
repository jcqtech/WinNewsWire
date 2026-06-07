using System.Globalization;

namespace WinNewsWire.Web;

/// <summary>Port of <c>HTTPConditionalGetInfo</c>.</summary>
public sealed record ConditionalGetInfo(string? LastModified, string? ETag)
{
    public static ConditionalGetInfo? FromResponse(System.Net.Http.Headers.HttpResponseHeaders headers,
                                                   System.Net.Http.Headers.HttpContentHeaders contentHeaders)
    {
        string? lastModified = contentHeaders.LastModified?.ToString("R", CultureInfo.InvariantCulture);
        string? etag = headers.ETag?.Tag;
        if (lastModified is null && etag is null) return null;
        return new ConditionalGetInfo(lastModified, etag);
    }

    public void ApplyTo(System.Net.Http.HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(ETag))
            req.Headers.TryAddWithoutValidation("If-None-Match", ETag);
        if (!string.IsNullOrEmpty(LastModified))
            req.Headers.TryAddWithoutValidation("If-Modified-Since", LastModified);
    }
}
