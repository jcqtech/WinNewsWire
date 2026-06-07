using System.Text;

namespace WinNewsWire.Articles;

/// <summary>Port of <c>Article</c>.</summary>
public sealed class Article : IEquatable<Article>
{
    public string ArticleID { get; }
    public string AccountID { get; }
    public string FeedID { get; }
    public string UniqueID { get; }
    public string? Title { get; }
    public string? ContentHtml { get; }
    public string? ContentText { get; }
    public string? Markdown { get; }
    public string? RawLink { get; }
    public string? RawExternalLink { get; }
    public string? Summary { get; }
    public string? RawImageLink { get; }
    public DateTime? DatePublished { get; }
    public DateTime? DateModified { get; }
    public IReadOnlySet<Author>? Authors { get; }
    public IReadOnlySet<Attachment>? Attachments { get; }
    public ArticleStatus Status { get; }

    public Article(
        string accountID, string? articleID, string feedID, string uniqueID,
        string? title, string? contentHtml, string? contentText, string? markdown,
        string? url, string? externalURL, string? summary, string? imageURL,
        DateTime? datePublished, DateTime? dateModified,
        IReadOnlySet<Author>? authors, IReadOnlySet<Attachment>? attachments, ArticleStatus status)
    {
        AccountID = accountID;
        FeedID = feedID;
        UniqueID = uniqueID;
        Title = title;
        ContentHtml = contentHtml;
        ContentText = contentText;
        Markdown = markdown;
        RawLink = url;
        RawExternalLink = externalURL;
        Summary = summary;
        RawImageLink = imageURL;
        DatePublished = datePublished;
        DateModified = dateModified;
        Authors = authors;
        Attachments = attachments;
        Status = status;
        ArticleID = articleID ?? CalculatedArticleID(feedID, uniqueID);
    }

    public static string CalculatedArticleID(string feedID, string uniqueID)
        => DatabaseID.For($"{feedID} {uniqueID}");

    public override int GetHashCode() => ArticleID.GetHashCode();
    public override bool Equals(object? obj) => obj is Article a && Equals(a);
    public bool Equals(Article? o)
        => o is not null && o.ArticleID == ArticleID && o.AccountID == AccountID
           && o.FeedID == FeedID && o.UniqueID == UniqueID && o.Title == Title
           && o.ContentHtml == ContentHtml && o.ContentText == ContentText
           && o.RawLink == RawLink && o.RawExternalLink == RawExternalLink
           && o.Summary == Summary && o.RawImageLink == RawImageLink
           && o.DatePublished == DatePublished && o.DateModified == DateModified;

    private static readonly HashSet<string> AllowedInlineTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "b","bdi","bdo","cite","code","del","dfn","em","i","ins","kbd","mark","q","s","samp",
        "small","strong","sub","sup","time","u","var"
    };

    public string? SanitizedTitle(bool forHtml = true)
    {
        if (Title is null) return null;
        var sb = new StringBuilder(Title.Length);
        int i = 0;
        while (i < Title.Length)
        {
            int lt = Title.IndexOf('<', i);
            if (lt < 0) { sb.Append(Title, i, Title.Length - i); break; }
            sb.Append(Title, i, lt - i);
            int gt = Title.IndexOf('>', lt + 1);
            if (gt < 0) { sb.Append(Title, lt, Title.Length - lt); break; }
            var tag = Title.Substring(lt + 1, gt - lt - 1);
            var tagName = tag.Replace("/", "");
            if (AllowedInlineTags.Contains(tagName)) sb.Append(forHtml ? $"<{tag}>" : "");
            else sb.Append(forHtml ? $"&lt;{tag}&gt;" : $"<{tag}>");
            i = gt + 1;
        }
        return sb.ToString();
    }
}

public static class ArticleSetExtensions
{
    public static HashSet<string> ArticleIDs(this IEnumerable<Article> articles)
        => articles.Select(a => a.ArticleID).ToHashSet();

    public static HashSet<Article> UnreadArticles(this IEnumerable<Article> articles)
        => articles.Where(a => !a.Status.Read).ToHashSet();
}
