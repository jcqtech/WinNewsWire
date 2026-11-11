namespace WinNewsWire.Core;

/// <summary>Simple MIME type constants/helpers (port of RSWeb.MimeType).</summary>
public static class MimeType
{
    public const string Rss = "application/rss+xml";
    public const string Atom = "application/atom+xml";
    public const string Xml = "application/xml";
    public const string TextXml = "text/xml";
    public const string JsonFeed = "application/feed+json";
    public const string Json = "application/json";
    public const string Html = "text/html";
    public const string Opml = "text/x-opml";

    public static bool IsFeedMimeType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        var s = contentType.ToLowerInvariant();
        return s.Contains("rss+xml") || s.Contains("atom+xml") || s.Contains("feed+json") ||
               s.Contains("xml") || s.StartsWith("application/json");
    }

    public static bool IsHtmlMimeType(string? contentType)
        => contentType is not null && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
}
