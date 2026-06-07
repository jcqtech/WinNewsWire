using System.Linq;
using WinNewsWire.Articles;

namespace WinNewsWire.AppShared.Extensions;

/// <summary>
/// Port of NetNewsWire's <c>ArticleUtilities.swift</c>. Computed-property
/// extensions for <see cref="Article"/> used by sorting, rendering, and the
/// timeline byline.
/// </summary>
public static class ArticleUtilities
{
    /// <summary>
    /// Returns the article's preferred reading link: the article URL if set,
    /// otherwise the external URL (e.g. a podcast-show-page link). Mirrors
    /// Swift's <c>preferredLink</c>.
    /// </summary>
    public static string? PreferredLink(this Article article)
    {
        if (!string.IsNullOrEmpty(article.RawLink)) return article.RawLink;
        if (!string.IsNullOrEmpty(article.RawExternalLink)) return article.RawExternalLink;
        return null;
    }

    /// <summary>
    /// Preferred HTML body: <c>contentHtml</c>, then <c>contentText</c>, then
    /// <c>summary</c>. Matches Swift's <c>body</c>.
    /// </summary>
    public static string? Body(this Article article)
        => article.ContentHtml ?? article.ContentText ?? article.Summary;

    /// <summary>
    /// Effective date used for sorting. Falls back from
    /// <c>datePublished</c> → <c>dateModified</c> → <c>status.dateArrived</c>.
    /// Mirrors Swift's <c>logicalDatePublished</c>.
    /// </summary>
    public static System.DateTime LogicalDatePublished(this Article article)
        => article.DatePublished ?? article.DateModified ?? article.Status.DateArrived;

    /// <summary>
    /// Renders the article's byline (comma-separated author names) suitable
    /// for the timeline header. Returns empty string when there are no
    /// authors or when the single author's name matches the feed name (the
    /// Swift heuristic for avoiding redundant "Daring Fireball by John Gruber").
    /// </summary>
    public static string Byline(this Article article, string? feedName = null)
    {
        var authors = article.Authors;
        if (authors is null || authors.Count == 0) return string.Empty;

        if (authors.Count == 1)
        {
            var only = authors.First();
            if (!string.IsNullOrEmpty(only.Name)
                && !string.IsNullOrEmpty(feedName)
                && string.Equals(only.Name, feedName, System.StringComparison.OrdinalIgnoreCase))
                return string.Empty;
        }

        var parts = new System.Collections.Generic.List<string>(authors.Count);
        foreach (var author in authors)
        {
            // Drop noreply/no-reply addresses — they're never useful in a byline.
            string? email = author.EmailAddress;
            if (!string.IsNullOrEmpty(email) &&
                (email.Contains("noreply@", System.StringComparison.OrdinalIgnoreCase) ||
                 email.Contains("no-reply@", System.StringComparison.OrdinalIgnoreCase)))
                email = null;

            if (!string.IsNullOrEmpty(email) && email!.Contains(' '))
                parts.Add(email);                                   // "Jane Doe jane@x.com"
            else if (!string.IsNullOrEmpty(author.Name) && !string.IsNullOrEmpty(email))
                parts.Add($"{author.Name} <{email}>");
            else if (!string.IsNullOrEmpty(author.Name))
                parts.Add(author.Name!);
            else if (!string.IsNullOrEmpty(email))
                parts.Add($"<{email}>");
            else if (!string.IsNullOrEmpty(author.Url))
                parts.Add(author.Url!);
        }
        return string.Join(", ", parts);
    }
}
