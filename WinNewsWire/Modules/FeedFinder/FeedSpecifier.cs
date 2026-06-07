namespace WinNewsWire.FeedFinder;

public enum FeedSpecifierSource { UserEntered = 0, HtmlHead = 1, HtmlLink = 2 }

/// <summary>Port of <c>FeedSpecifier</c>.</summary>
public sealed record FeedSpecifier(string? Title, string UrlString, FeedSpecifierSource Source, int OrderFound)
{
    public int Score => Calculate();

    public FeedSpecifier MergeWith(FeedSpecifier other)
    {
        var title = Title ?? other.Title;
        var source = (int)Source <= (int)other.Source ? Source : other.Source;
        var order = OrderFound < other.OrderFound ? OrderFound : other.OrderFound;
        return new FeedSpecifier(title, UrlString, source, order);
    }

    public static FeedSpecifier? BestFeed(IEnumerable<FeedSpecifier> set)
    {
        FeedSpecifier? best = null; int bestScore = int.MinValue;
        foreach (var s in set)
        {
            if (s.Score > bestScore) { bestScore = s.Score; best = s; }
        }
        return best;
    }

    private int Calculate()
    {
        if (Source == FeedSpecifierSource.UserEntered) return 1000;
        int score = 0;
        if (Source == FeedSpecifierSource.HtmlHead) score += 50;
        score -= (OrderFound - 1) * 5;
        var u = UrlString;
        if (u.Contains("comments", StringComparison.OrdinalIgnoreCase)) score -= 10;
        if (u.Contains("podcast", StringComparison.OrdinalIgnoreCase)) score -= 10;
        if (u.Contains("rss", StringComparison.OrdinalIgnoreCase)) score += 5;
        if (u.EndsWith("/index.xml")) score += 5;
        if (u.EndsWith("/feed/")) score += 5;
        if (u.EndsWith("/feed")) score += 4;
        if (u.Contains("json", StringComparison.OrdinalIgnoreCase)) score += 3;
        if (Title is not null && Title.Contains("comments", StringComparison.OrdinalIgnoreCase)) score -= 10;
        return score;
    }
}
