using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using WinNewsWire.Core;

namespace WinNewsWire.Models;

public partial class FeedItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    private string? _title;
    public string Title
    {
        get => _title ??= SourceArticle is { } article
            ? AppShared.Extensions.ArticleStringFormatter.Shared.TruncatedTitle(article) : "";
        set
        {
            if (SetProperty(ref _title, value)) OnPropertyChanged(nameof(ColumnTitle));
        }
    }

    private string? _formattedTitle;
    public string FormattedTitle
    {
        get => _formattedTitle ??= SourceArticle is { } article
            ? AppShared.Extensions.ArticleStringFormatter.Shared.TruncatedTitle(article, forHtml: true) : "";
        set => SetProperty(ref _formattedTitle, value);
    }

    private AppShared.Extensions.AttributedText? _attributedTitle;
    public AppShared.Extensions.AttributedText AttributedTitle
    {
        get => _attributedTitle ??= SourceArticle is { } article
            ? AppShared.Extensions.ArticleStringFormatter.Shared.AttributedTruncatedTitle(article)
            : AppShared.Extensions.AttributedText.Empty;
        set => SetProperty(ref _attributedTitle, value);
    }

    private string? _summary;
    public string Summary
    {
        get => _summary ??= SourceArticle is { } article
            ? AppShared.Extensions.ArticleStringFormatter.Shared.TruncatedSummary(article) : "";
        set
        {
            if (SetProperty(ref _summary, value)) OnPropertyChanged(nameof(ColumnTitle));
        }
    }

#if PARITY_PROBE
    internal bool ProbeHasFormattedText => _title is not null || _formattedTitle is not null
        || _attributedTitle is not null || _summary is not null;
#endif

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _link = string.Empty;

    [ObservableProperty]
    private string _externalLink = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineByline))]
    private string _author = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColumnDate))]
    private DateTimeOffset _publishedDate = DateTimeOffset.MinValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnreadDot))]
    [NotifyPropertyChangedFor(nameof(ShowStar))]
    [NotifyPropertyChangedFor(nameof(IsUnread))]
    [NotifyPropertyChangedFor(nameof(ColumnFontWeight))]
    private bool _isRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnreadDot))]
    [NotifyPropertyChangedFor(nameof(ShowStar))]
    private bool _isStarred;

    [ObservableProperty]
    private string _imageUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineByline))]
    private string _feedTitle = string.Empty;

    [ObservableProperty]
    private string _feedIconUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FaviconSource))]
    private string? _faviconPath;

    private long _faviconRevision;
    public FeedImageReference FaviconSource => new(FaviconPath, _faviconRevision);

    internal void RefreshFavicon()
    {
        _faviconRevision++;
        OnPropertyChanged(nameof(FaviconSource));
    }

    internal void RefreshFaviconAppearance() => OnPropertyChanged(nameof(FaviconSource));

    public string FeedId { get; set; } = string.Empty;
    public string AccountID { get; set; } = string.Empty;
    private WinNewsWire.Articles.Article? _sourceArticle;
    public WinNewsWire.Articles.Article? SourceArticle { get => _sourceArticle; init => _sourceArticle = value; }
    public string? FeedUrl { get; set; }
    public string? FeedHomePageUrl { get; set; }
    public string? FeedFaviconUrl { get; set; }

    internal void UpdateFrom(FeedItem item)
    {
        if (Id != item.Id || AccountID != item.AccountID)
            throw new ArgumentException("A timeline row cannot change article identity.", nameof(item));
        if (!ReferenceEquals(_sourceArticle, item.SourceArticle))
        {
            _sourceArticle = item.SourceArticle;
            OnPropertyChanged(nameof(SourceArticle));
        }
        if (_title is not null) Title = item.Title;
        if (_formattedTitle is not null) FormattedTitle = item.FormattedTitle;
        if (_attributedTitle is not null) AttributedTitle = item.AttributedTitle;
        if (_summary is not null) Summary = item.Summary;
        Content = item.Content;
        Link = item.Link;
        ExternalLink = item.ExternalLink;
        Author = item.Author;
        PublishedDate = item.PublishedDate;
        IsRead = item.IsRead;
        IsStarred = item.IsStarred;
        ImageUrl = item.ImageUrl;
        FeedTitle = item.FeedTitle;
        FeedIconUrl = item.FeedIconUrl;
        FaviconPath = item.FaviconPath;
        FeedId = item.FeedId;
        FeedUrl = item.FeedUrl;
        FeedHomePageUrl = item.FeedHomePageUrl;
        FeedFaviconUrl = item.FeedFaviconUrl;
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>Drives the unread-indicator dot in the article list. Mirrors NetNewsWire
    /// Mac's <c>TimelineTableCellView</c>: the dot is hidden when the article is read
    /// OR starred (the star takes the dot's slot in that case).</summary>
    public bool ShowUnreadDot => !IsRead && !IsStarred;

    /// <summary>Drives the star glyph in the article list — visible whenever the article
    /// is starred, regardless of read state.</summary>
    public bool ShowStar => IsStarred;
    public bool IsUnread => !IsRead;
    public string ColumnTitle => Title.Length > 0 ? Title : Summary.CollapsingWhitespace();
    public string ColumnDate => PublishedDate == DateTimeOffset.MinValue ? ""
        : AppShared.Extensions.ArticleStringFormatter.Shared.DateString(PublishedDate.UtcDateTime);
    public global::Windows.UI.Text.FontWeight ColumnFontWeight => IsRead
        ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.Bold;

    /// <summary>Multi-line tooltip rendered on hover over an article list row — mirrors
    /// Mac NNW's <c>TimelineTableCellView</c> tooltip: title, feed, date, summary excerpt.</summary>
    public string Tooltip
    {
        get
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title)) lines.Add(Title);
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(FeedTitle)) meta.Add(FeedTitle);
            if (!string.IsNullOrWhiteSpace(Author)) meta.Add(Author);
            if (PublishedDate > DateTimeOffset.MinValue)
                meta.Add(PublishedDate.LocalDateTime.ToString("MMM d, yyyy h:mm tt"));
            if (meta.Count > 0) lines.Add(string.Join(" · ", meta));
            if (!string.IsNullOrWhiteSpace(Summary))
            {
                var s = Summary;
                if (s.Length > 280) s = s[..280] + "…";
                lines.Add("");
                lines.Add(s);
            }
            return string.Join("\n", lines);
        }
    }

}

public readonly record struct FeedImageReference(string? Path, long Revision);
