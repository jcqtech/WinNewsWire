using WinNewsWire.Parsers;

namespace WinNewsWire.Account;

/// <summary>
/// Port of NNW's <c>OPMLNormalizer</c>.
/// Deduplicates feed URLs, promotes children of unnamed folders,
/// and flattens nested folder structures to a single level.
/// </summary>
internal static class OPMLNormalizer
{
    public static IReadOnlyList<OpmlItem> Normalize(IReadOnlyList<OpmlItem> items)
    {
        var result = new List<OpmlItem>();
        var rootFeedUrls = new HashSet<string>(StringComparer.Ordinal);
        AddItems(items, result, rootFeedUrls);
        return result;
    }

    private static void AddItems(IReadOnlyList<OpmlItem> items, List<OpmlItem> result, HashSet<string> feedUrls)
    {
        foreach (var item in items)
        {
            if (item.FeedSpecifier is { } specifier)
            {
                if (feedUrls.Add(specifier.FeedUrl)) result.Add(item);
            }
            else if (string.IsNullOrWhiteSpace(item.Title))
            {
                AddItems(item.Children, result, feedUrls);
            }
            else
            {
                var feeds = DescendantFeeds(item.Children)
                    .DistinctBy(child => child.FeedSpecifier!.FeedUrl, StringComparer.Ordinal)
                    .ToList();
                result.Add(new OpmlItem(item.Attributes, feeds));
            }
        }
    }

    private static IEnumerable<OpmlItem> DescendantFeeds(IReadOnlyList<OpmlItem> items)
    {
        foreach (var item in items)
        {
            if (item.FeedSpecifier is not null) yield return item;
            else
                foreach (var feed in DescendantFeeds(item.Children)) yield return feed;
        }
    }
}
