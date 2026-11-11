using System.Globalization;
using WinNewsWire.Parsers.Utilities;

namespace WinNewsWire.Parsers;

// These are the previous Windows ID formulas, not alternative parsing behavior.
internal static class LegacyArticleIdentity
{
    public static string Rss(RssItem item, bool guidFromRdf)
    {
        if (!guidFromRdf && item.Guid is not null) return XmlText(item.Guid)!;
        var permalink = guidFromRdf ? null : XmlText(item.Permalink);
        var value = permalink ?? XmlText(item.Link) ?? XmlText(item.Title);
        if (item.DatePublished is { } date)
            value = (value ?? "") + ((long)(date.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds).ToString(CultureInfo.InvariantCulture);
        else value ??= XmlText(item.Body) ?? "";
        return Md5Hash.Hex(value);
    }

    public static string Atom(RssItem item, string? permalink, DateTime? published)
        => item.Guid is not null ? XmlText(item.Guid)!
            : permalink ?? Md5Hash.Hex((XmlText(item.Title) ?? "") + (published?.Ticks.ToString(CultureInfo.InvariantCulture) ?? ""));

    public static string RssInJson(DateTime? published, string fields, string? body)
    {
        var text = published is { } date
            ? new DateTimeOffset(date.ToUniversalTime()).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + fields
            : fields;
        return Md5Hash.Hex(text.Length == 0 ? body ?? "" : text);
    }

    private static string? XmlText(string? text) => text?.Replace("\r\n", "\n").Replace('\r', '\n');
}
