using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinNewsWire.Models;

public partial class SidebarItem : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _icon = string.Empty;

    [ObservableProperty]
    private int _unreadCount;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private SidebarItemType _itemType = SidebarItemType.Feed;

    [ObservableProperty]
    private string? _faviconPath;

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

    public Feed? Feed { get; set; }
    public FeedFolder? Folder { get; set; }

    /// <summary>When the sidebar item is backed by an <c>Account.Feed</c>, <c>Account.Folder</c>,
    /// or <c>Account.Account</c>, the reference is stashed here so commands (refresh, rename,
    /// delete, inspect) can act on the real domain object.</summary>
    public object? Tag { get; set; }

    public ObservableCollection<SidebarItem> Children { get; } = new();

    public override string ToString() => Title;
}

public enum SidebarItemType
{
    SmartFeed,
    Folder,
    Feed,
    SectionHeader,
    Account
}
