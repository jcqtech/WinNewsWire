#if PARITY_PROBE
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.Toolbar;
using WinNewsWire.Core;
using Windows.Foundation;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckPaneScrollbarLayoutAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        await content.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
        var original = window.CaptureState();
        var items = model.ArticleItems;
        var selection = model.SelectedArticle;
        var configuration = content.CurrentToolbarConfiguration;
        bool column = AppDefaults.Shared.UseColumnLayout, status = AppDefaults.Shared.ShowStatusBar;
        var thumbnails = AppDefaults.Shared.GetTimelineThumbnailsDisplay();
        var list = (ListView)content.FindName("ArticleListView");
        var sidebar = (ListView)content.FindName("SidebarListView");
        double sidebarHeight = sidebar.Height;
        var sidebarAlignment = sidebar.VerticalAlignment;
        var sidebarVisibility = ScrollViewer.GetVerticalScrollBarVisibility(sidebar);
        var samples = new List<object>();
        var failures = new List<string>();
        var root = (UIElement)window.Content;
        Rect Bounds(FrameworkElement element) => element.TransformToVisual(root)
            .TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        bool OwnsPoint(FrameworkElement target, Point point)
        {
            var hit = VisualTreeHelper.FindElementsInHostCoordinates(point, root).FirstOrDefault();
            return ReferenceEquals(hit, target) || hit is not null && Descendants(target).Contains(hit);
        }
        ScrollBar ShowBar(ListView owner)
        {
            var scroll = Descendants(owner).OfType<ScrollViewer>().First(view => view.Name == "ScrollViewer");
            var bar = Descendants(scroll).OfType<ScrollBar>().Single(value => value.Name == "VerticalScrollBar"
                && value.Visibility == Visibility.Visible && value.ActualWidth > 0);
            Require(VisualStateManager.GoToState(scroll, "MouseIndicatorFull", false)
                && VisualStateManager.GoToState(bar, "ExpandedWithoutAnimation", false),
                "The native expanded scrollbar presentation is unavailable.");
            return bar;
        }
        try
        {
            model.IsUnifiedLayout = false;
            model.IsSidebarVisible = true;
            AppDefaults.Shared.ShowStatusBar = true;
            AppDefaults.Shared.SetTimelineThumbnailsDisplay(TimelineThumbnailsDisplay.None);
            model.SelectedArticle = null;
            model.ArticleItems = new ObservableCollection<Models.FeedItem>(
                Enumerable.Range(0, 100).Select(index => AppearanceRow("pane-scroll-" + index, $"Article {index:D3}")));
            sidebar.Height = 96;
            sidebar.VerticalAlignment = VerticalAlignment.Top;
            ScrollViewer.SetVerticalScrollBarVisibility(sidebar, ScrollBarVisibility.Visible);
            foreach (bool columns in new[] { false, true })
            {
                AppDefaults.Shared.UseColumnLayout = columns;
                foreach (var display in new[] { ToolbarDisplayMode.IconsOnly, ToolbarDisplayMode.IconsAndText })
                {
                    foreach (bool visible in new[] { true, false })
                    {
                        string name = $"{(columns ? "column" : "standard")}-{display}-{visible}";
                        Require(content.SaveToolbarConfiguration(configuration.WithDisplayMode(display).WithVisibility(visible)) is null,
                            "Could not set the header presentation.");
                        await SettleAppearanceAsync(window, list);
                        await WaitFor(() => Task.FromResult(Math.Abs(Bounds(sidebar).Left
                            - Bounds((Grid)content.FindName("MainContentGrid")).Left) < 0.1),
                            "Sidebar reveal animation has settled");
                        var bar = ShowBar(list);
                        var sidebarBar = ShowBar(sidebar);
                        await WaitForRenderedFramesAsync();
                        var timeline = (Grid)content.FindName("TimelinePanel");
                        var header = (Grid)content.FindName("TimelineToolbar");
                        var headings = (Grid)content.FindName("TimelineColumnsHeader");
                        var detail = (Grid)content.FindName("ArticleDetailPanel");
                        var line = (FrameworkElement)content.FindName("ArticleDividerLine");
                        var splitter = (FrameworkElement)content.FindName("ArticleSplitter");
                        var sidebarSplitter = (FrameworkElement)content.FindName("SidebarSplitter");
                        var pane = (Border)content.FindName("ContentContainer");
                        var footerSplitter = (FrameworkElement)content.FindName("StatusBarArticleSplitter");
                        var footerLine = Descendants(footerSplitter).OfType<Border>().Single();
                        double visibleTop = columns ? Bounds(headings).Bottom : visible ? Bounds(header).Bottom : Bounds(list).Top;
                        var arrow = Descendants(bar).OfType<RepeatButton>().Single(button => button.Name == "VerticalSmallDecrease");
                        var arrowBounds = Bounds(arrow);
                        var arrowPoint = new Point(arrowBounds.Left + arrowBounds.Width / 2, arrowBounds.Top + arrowBounds.Height / 2);
                        var sideBounds = Bounds(sidebarSplitter);
                        var splitBounds = Bounds(splitter);
                        bool arrowHit = OwnsPoint(bar, arrowPoint);
                        bool sidebarHit = OwnsPoint(sidebarSplitter, new(sideBounds.Right - 1, sideBounds.Top + sideBounds.Height / 2));
                        bool splitterHit = OwnsPoint(splitter, new(splitBounds.Right - 1, splitBounds.Top + splitBounds.Height / 2));
                        double sidebarGap = Bounds(pane).Left - Bounds(sidebarBar).Right;
                        samples.Add(new
                        {
                            Name = name, VisibleTop = visibleTop, Scrollbar = Bounds(bar), bar.Margin,
                            Timeline = Bounds(timeline), Divider = Bounds(line), Detail = Bounds(detail),
                            Splitter = splitBounds, FooterDivider = Bounds(footerLine),
                            SidebarScrollbar = Bounds(sidebarBar), SidebarSplitter = sideBounds, SidebarGap = sidebarGap,
                            SidebarColumnWidth = ((ColumnDefinition)content.FindName("SidebarColumn")).ActualWidth,
                            SidebarHeader = Bounds((Grid)content.FindName("SidebarHeader")),
                            SidebarList = Bounds(sidebar), ArrowHit = arrowHit, SidebarResizeHit = sidebarHit, ArticleResizeHit = splitterHit
                        });
                        if (Bounds(bar).Top < visibleTop || Bounds(bar).Top > visibleTop + 2)
                            failures.Add(name + ": article scrollbar does not start below the visible header.");
                        if (!arrowHit) failures.Add(name + ": the header or divider covers the native scrollbar arrow.");
                        if (!columns && (Math.Abs(Bounds(timeline).Right - Bounds(line).Left) > 0.1
                            || Math.Abs(Bounds(detail).Left - Bounds(line).Right) > 0.1))
                            failures.Add(name + ": empty space remains on either side of the article divider.");
                        if (!columns && Math.Abs(Bounds(footerLine).Left - Bounds(line).Left) > 0.75)
                            failures.Add(name + ": the footer divider does not align with the pane divider.");
                        if (sidebarGap > 6.1)
                            failures.Add(name + ": the sidebar scrollbar is still too far from the pane border.");
                        if (!sidebarHit || sidebarSplitter.ActualWidth != 8 || Bounds(sidebarBar).Right > sideBounds.Left + 0.1)
                            failures.Add(name + ": the sidebar scrollbar and full resize target overlap or are covered.");
                        if (!columns && (!splitterHit || splitter.ActualWidth != 8))
                            failures.Add(name + ": the article divider lost its full resize target.");
                    }
                }
            }
            File.WriteAllText(Path.Combine(OutputDirectory, "pane-scrollbar-layout.json"),
                JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
            Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
            return new { HeadersShownAndHidden = true, BothHeaderSizes = true, NativeScrollbarHitTesting = true,
                NoDividerGaps = true, SidebarRailShifted = true, ResizeTargetsPreserved = true, StandardAndColumn = true };
        }
        finally
        {
            sidebar.Height = sidebarHeight;
            sidebar.VerticalAlignment = sidebarAlignment;
            ScrollViewer.SetVerticalScrollBarVisibility(sidebar, sidebarVisibility);
            content.SaveToolbarConfiguration(configuration);
            AppDefaults.Shared.UseColumnLayout = column;
            AppDefaults.Shared.ShowStatusBar = status;
            AppDefaults.Shared.SetTimelineThumbnailsDisplay(thumbnails);
            model.ArticleItems = items;
            model.SelectedArticle = selection;
            await content.RestoreStateAsync(original);
        }
    }
}
#endif
