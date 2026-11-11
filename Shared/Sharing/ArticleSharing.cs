using System.Net;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.Articles;

namespace WinNewsWire.AppShared.Sharing;

public static class ArticleSharing
{
    public static string Subject(IEnumerable<Article> articles)
        => string.Join(", ", articles.Select(article => ArticleStringFormatter.Shared.TruncatedTitle(article)));

    public static string PlainText(IEnumerable<Article> articles)
        => string.Join(Environment.NewLine + Environment.NewLine, articles.Select(article =>
        {
            var title = ArticleStringFormatter.Shared.TruncatedTitle(article);
            return article.PreferredLink() is { } link ? title + Environment.NewLine + link : title;
        }));

    public static string Html(IEnumerable<Article> articles)
        => string.Join("\n", articles.Select(article =>
        {
            var title = WebUtility.HtmlEncode(ArticleStringFormatter.Shared.TruncatedTitle(article));
            var link = WebUtility.HtmlEncode(article.PreferredLink() ?? "");
            return $"<p><a href=\"{link}\">{title}</a></p>";
        }));
}
