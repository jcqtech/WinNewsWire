namespace WinNewsWire.AppShared.Timeline;

public static class ArticleArray
{
    public static IEnumerable<int> OrderedRowIndexes(int count, int startIndex, bool wrappingToTop)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        int start = Math.Min(startIndex, count);
        for (int index = start; index < count; index++) yield return index;
        if (wrappingToTop)
            for (int index = 0; index < start; index++) yield return index;
    }

    public static int? RowOfNextUnreadArticle<T>(IReadOnlyList<T> articles, int selectedRow,
        bool wrappingToTop, Func<T, bool> isRead)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(isRead);
        if (selectedRow < -1) throw new ArgumentOutOfRangeException(nameof(selectedRow));
        int start = selectedRow >= articles.Count ? articles.Count : selectedRow + 1;
        foreach (var index in OrderedRowIndexes(articles.Count, start, wrappingToTop))
            if (!isRead(articles[index])) return index;
        return null;
    }
}
