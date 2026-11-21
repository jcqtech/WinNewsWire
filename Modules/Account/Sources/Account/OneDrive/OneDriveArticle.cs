using WinNewsWire.Articles;

namespace WinNewsWire.Account.OneDrive;

internal sealed record OneDriveArticle(string ID, string FeedUrl, string UniqueID, string? Title,
    string? Html, string? Text, string? Markdown, string? Url, string? ExternalUrl, string? Summary,
    string? Image, string? Thumbnail, DateTime? Published, DateTime? Modified,
    string? Authors, string? Attachments)
{
    internal static OneDriveArticle From(Article article, string feedUrl) => new(article.ArticleID, feedUrl,
        article.UniqueID, article.Title, article.ContentHtml, article.ContentText, article.Markdown,
        article.RawLink, article.RawExternalLink, article.Summary, article.RawImageLink, article.RawThumbnailLink,
        article.DatePublished, article.DateModified,
        article.Authors is null ? null : Author.ToJson(article.Authors.OrderBy(a => a.AuthorID, StringComparer.Ordinal)),
        Attachment.ToJson(article.Attachments));

    internal Article ToArticle(Account account, Feed feed) => new(account.AccountID, ID, feed.FeedID, UniqueID,
        Title, Html, Text, Markdown, Url, ExternalUrl, Summary, Image, Published, Modified,
        Authors is null ? null : Author.FromJson(Authors), Attachment.FromJson(Attachments),
        new ArticleStatus(ID, read: true, starred: false, dateArrived: DateTime.UtcNow), Thumbnail);
}
