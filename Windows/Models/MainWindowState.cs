using System.Collections.Generic;
using System.Text.Json.Serialization;
using WinNewsWire.AppShared.Timeline;

namespace WinNewsWire.Models;

public sealed record WindowBounds(int X, int Y, int Width, int Height);

public sealed record MainWindowState
{
    public int Version { get; init; } = 1;
    public WindowBounds Bounds { get; init; } = new(0, 0, 1400, 900);
    public bool IsMaximized { get; init; }
    public bool IsFullScreen { get; init; }
    public double SidebarWidth { get; init; } = 260;
    public double TimelineWidth { get; init; } = 380;
    public double ColumnLayoutTimelineHeight { get; init; }
    public bool IsSidebarHidden { get; init; }
    public bool IsUnifiedLayout { get; init; }
    public SidebarWindowState Sidebar { get; init; } = new();
    public TimelineWindowState Timeline { get; init; } = new();
    public DetailWindowState Detail { get; init; } = new();
}

public sealed record SidebarWindowState
{
    public bool IsReadFiltered { get; init; }
    public SidebarItemKey[] SelectedItems { get; init; } = [];
    public SidebarItemKey[]? ExpandedContainers { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SidebarItemKey[]? ExpandedFolders { get; init; }
}

public sealed record TimelineWindowState
{
    public Dictionary<string, bool> ReadFilters { get; init; } = new();
    public ArticleIdentityKey? SelectedArticle { get; init; }
    public ArticleSortParameters? SortParameters { get; init; }
}

public sealed record DetailWindowState(bool IsReaderMode = false, double ScrollY = 0);

public sealed record TimelineColumnsState
{
    public ArticleSortKey[] Order { get; init; } = [];
    public Dictionary<ArticleSortKey, double> Widths { get; init; } = new();
}
