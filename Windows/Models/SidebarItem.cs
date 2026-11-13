using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.SmartFeeds;
using WinNewsWire.Core;

namespace WinNewsWire.Models;

public partial class SidebarItem : ObservableObject
{
    private UnreadCountDisplay _unreadDisplay = AppDefaults.Shared.GetUnreadCountDisplay();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpansionLabel))]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _icon = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadCountText))]
    [NotifyPropertyChangedFor(nameof(ShowUnreadCount))]
    [NotifyPropertyChangedFor(nameof(ShowUnreadCountDot))]
    [NotifyPropertyChangedFor(nameof(ShowUnreadIndicator))]
    [NotifyPropertyChangedFor(nameof(AccessibilityName))]
    private int _unreadCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpansionGlyph))]
    [NotifyPropertyChangedFor(nameof(ExpansionLabel))]
    private bool _isExpanded = true;

    [ObservableProperty]
    private SidebarItemType _itemType = SidebarItemType.Feed;

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

    /// <summary>True while the user is editing this item's name inline. The sidebar
    /// row template watches this to swap between the read-only label and a TextBox
    /// with accept/cancel buttons. Mirrors NetNewsWire Mac's inline rename mode.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>Two-way bound to the inline rename TextBox while
    /// <see cref="IsRenaming"/> is true. Distinct from <see cref="Title"/> so the
    /// user can type freely and only commit on Enter/Accept.</summary>
    [ObservableProperty]
    private string _editableTitle = string.Empty;

    /// <summary>When the sidebar item is backed by an <c>Account.Feed</c>, <c>Account.Folder</c>,
    /// or <c>Account.Account</c>, the reference is stashed here so commands (refresh, rename,
    /// delete, inspect) can act on the real domain object.</summary>
    public object? Tag { get; set; }
    internal (string? HomePage, string? Favicon, string? Icon) FeedImageIdentity { get; init; }
    public Account.Folder? ParentFolder { get; init; }
    public bool IsFolder => Tag is Account.Folder;
    public bool IsSelectable => ItemType != SidebarItemType.SectionHeader && Identity is not null;
    public bool HasSymbolIcon => IsFolder || ItemType == SidebarItemType.SmartFeed;
    public bool HasFeedImage => Tag is Account.Feed;
    public bool IsExpandable => IsFolder;
    public string UnreadCountText => _unreadDisplay.TextFor(UnreadCount) ?? "";
    public bool ShowUnreadCount => _unreadDisplay == UnreadCountDisplay.Count && UnreadCount > 0;
    public bool ShowUnreadCountDot => _unreadDisplay.ShowsDot(UnreadCount);
    public bool ShowUnreadIndicator => ShowUnreadCount || ShowUnreadCountDot;
    public string AccessibilityName => _unreadDisplay.AccessibilityTextFor(UnreadCount) is { } unread ? Title + " " + unread : Title;
    public int OutlineLevel => ItemType == SidebarItemType.SectionHeader ? 1 : ParentFolder is null ? 2 : 3;
    public string ExpansionGlyph => IsExpanded ? "\uE70D" : "\uE76C";
    public string ExpansionLabel => (IsExpanded ? "Collapse " : "Expand ") + Title;
    public Thickness OutlineMargin => new(ParentFolder is null ? 0 : 20, 0, 0, 0);
    public SidebarItemKey? Identity => Tag switch
    {
        Account.Feed feed => new(SidebarItemType.Feed, feed.AccountID, feed.Url, ParentFolder?.FolderID),
        Account.Folder folder => new(SidebarItemType.Folder, folder.AccountID, folder.FolderID.ToString(System.Globalization.CultureInfo.InvariantCulture), null),
        Account.Account account => new(SidebarItemType.Account, account.AccountID, account.AccountID, null),
        IPseudoFeed smart => new(SidebarItemType.SmartFeed, "", smart.SidebarItemID.StorageKey, null),
        _ => null
    };

    public ObservableCollection<SidebarItem> Children { get; } = new();

    public void UpdateUnreadCountDisplay(UnreadCountDisplay display)
    {
        if (_unreadDisplay == display) return;
        _unreadDisplay = display;
        OnPropertyChanged(nameof(UnreadCountText));
        OnPropertyChanged(nameof(ShowUnreadCount));
        OnPropertyChanged(nameof(ShowUnreadCountDot));
        OnPropertyChanged(nameof(ShowUnreadIndicator));
        OnPropertyChanged(nameof(AccessibilityName));
    }

    public override string ToString() => Title;
}

public readonly record struct SidebarItemKey(SidebarItemType Kind, string AccountID, string ItemID, int? ParentFolderID);

public enum SidebarItemType
{
    SmartFeed,
    Folder,
    Feed,
    SectionHeader,
    Account
}
