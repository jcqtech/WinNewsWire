using WinNewsWire.Articles;
using WinNewsWire.Parsers;

namespace WinNewsWire.Account;

public sealed partial class Account
{
    internal static void UpdateFeedMetadata(Feed feed, ParsedFeed parsed)
    {
        if (!string.IsNullOrEmpty(parsed.Title)) feed.Name = parsed.Title;
        if (feed.HomePageUrl is null) feed.HomePageUrl = parsed.HomePageUrl;
        if (!string.IsNullOrEmpty(parsed.IconUrl)) feed.IconUrl = parsed.IconUrl;
        if (!string.IsNullOrEmpty(parsed.FaviconUrl)) feed.FaviconUrl = parsed.FaviconUrl;
        if (parsed.Authors is { } authors)
            feed.Authors = authors.Select(author => Author.Create(null, author.Name, author.Url, author.AvatarUrl, author.EmailAddress)).OfType<Author>().ToHashSet();
        feed.OnDisplayNameChanged();
    }
}
