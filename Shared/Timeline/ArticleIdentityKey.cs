namespace WinNewsWire.AppShared.Timeline;

public readonly record struct ArticleIdentityKey(string AccountID, string ArticleID);

public static class ArticleSelection
{
    public static List<T> InTimelineOrder<T>(IEnumerable<T> timeline, IEnumerable<ArticleIdentityKey> selected,
        Func<T, ArticleIdentityKey> identity)
    {
        var ids = selected.ToHashSet();
        return timeline.Where(item => ids.Contains(identity(item))).ToList();
    }

    public static List<T> ForContextMenu<T>(IReadOnlyList<T> timeline, IReadOnlyList<T> selected, T clicked,
        Func<T, ArticleIdentityKey> identity)
        => selected.Any(item => identity(item) == identity(clicked))
            ? InTimelineOrder(timeline, selected.Select(identity), identity) : [clicked];
}
