using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinNewsWire.Models;

public partial class FeedItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _link = string.Empty;

    [ObservableProperty]
    private string _externalLink = string.Empty;

    [ObservableProperty]
    private string _author = string.Empty;

    [ObservableProperty]
    private DateTimeOffset _publishedDate = DateTimeOffset.MinValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnreadDot))]
    [NotifyPropertyChangedFor(nameof(ShowStar))]
    private bool _isRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnreadDot))]
    [NotifyPropertyChangedFor(nameof(ShowStar))]
    private bool _isStarred;

    [ObservableProperty]
    private string _imageUrl = string.Empty;

    [ObservableProperty]
    private string _feedTitle = string.Empty;

    [ObservableProperty]
    private string _feedIconUrl = string.Empty;

    [ObservableProperty]
    private string? _faviconPath;

    public string FeedId { get; set; } = string.Empty;
    public string AccountID { get; set; } = string.Empty;

    /// <summary>Drives the unread-indicator dot in the article list. Mirrors NetNewsWire
    /// Mac's <c>TimelineTableCellView</c>: the dot is hidden when the article is read
    /// OR starred (the star takes the dot's slot in that case).</summary>
    public bool ShowUnreadDot => !IsRead && !IsStarred;

    /// <summary>Drives the star glyph in the article list — visible whenever the article
    /// is starred, regardless of read state.</summary>
    public bool ShowStar => IsStarred;

    /// <summary>Multi-line tooltip rendered on hover over an article list row — mirrors
    /// Mac NNW's <c>TimelineTableCellView</c> tooltip: title, feed, date, summary excerpt.</summary>
    public string Tooltip
    {
        get
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title)) lines.Add(StripHtml(Title));
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(FeedTitle)) meta.Add(FeedTitle);
            if (!string.IsNullOrWhiteSpace(Author)) meta.Add(Author);
            if (PublishedDate > DateTimeOffset.MinValue)
                meta.Add(PublishedDate.LocalDateTime.ToString("MMM d, yyyy h:mm tt"));
            if (meta.Count > 0) lines.Add(string.Join(" · ", meta));
            if (!string.IsNullOrWhiteSpace(Summary))
            {
                var s = StripHtml(Summary);
                if (s.Length > 280) s = s[..280] + "…";
                lines.Add("");
                lines.Add(s);
            }
            return string.Join("\n", lines);
        }
    }

    private static string StripHtml(string s)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", " ");
        t = System.Net.WebUtility.HtmlDecode(t);
        return System.Text.RegularExpressions.Regex.Replace(t, "\\s+", " ").Trim();
    }
}
