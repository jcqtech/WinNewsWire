namespace WinNewsWire.Parsers;

/// <summary>Port of <c>RSOPMLFeedSpecifier</c>.</summary>
public sealed record OpmlFeedSpecifier(string? Title, string? FeedDescription, string FeedUrl, string? HomePageUrl);

/// <summary>Port of <c>RSOPMLItem</c>.</summary>
public class OpmlItem
{
    public IReadOnlyDictionary<string, string> Attributes { get; }
    public IReadOnlyList<OpmlItem> Children { get; }
    public OpmlFeedSpecifier? FeedSpecifier { get; }

    public string? Title => Get("title") ?? Get("text");
    public string? Text => Get("text");
    public string? Type => Get("type");

    public bool IsFolder => Children.Count > 0 || (Type is null && FeedSpecifier is null);

    public OpmlItem(IReadOnlyDictionary<string, string> attributes, IReadOnlyList<OpmlItem> children)
    {
        Attributes = attributes;
        Children = children;
        FeedSpecifier = BuildFeedSpecifier(attributes);
    }

    private string? Get(string key)
    {
        foreach (var kv in Attributes)
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private static OpmlFeedSpecifier? BuildFeedSpecifier(IReadOnlyDictionary<string, string> attrs)
    {
        string? xmlUrl = null, title = null, desc = null, htmlUrl = null;
        foreach (var kv in attrs)
        {
            switch (kv.Key.ToLowerInvariant())
            {
                case "xmlurl": xmlUrl = kv.Value; break;
                case "title": title = kv.Value; break;
                case "text": title ??= kv.Value; break;
                case "description": desc = kv.Value; break;
                case "htmlurl": htmlUrl = kv.Value; break;
            }
        }
        if (string.IsNullOrEmpty(xmlUrl)) return null;
        return new OpmlFeedSpecifier(title, desc, xmlUrl!, htmlUrl);
    }
}

/// <summary>Port of <c>RSOPMLDocument</c>.</summary>
public sealed class OpmlDocument : OpmlItem
{
    public string? Title { get; }
    public string? Url { get; }
    public OpmlDocument(string? title, string? url, IReadOnlyDictionary<string, string> attributes, IReadOnlyList<OpmlItem> children)
        : base(attributes, children)
    {
        Title = title;
        Url = url;
    }
}
