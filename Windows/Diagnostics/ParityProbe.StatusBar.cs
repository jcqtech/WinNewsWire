#if PARITY_PROBE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinNewsWire.Account;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.StatusBar;
using WinNewsWire.Core;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckStatusBarInvalidationScopeAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        bool saved = AppDefaults.Shared.ShowStatusBar;
        string? hovered = model.HoveredLink;
        var list = (ListView)content.FindName("ArticleListView");
        try
        {
            await content.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
            AppDefaults.Shared.ShowStatusBar = true;
            model.HoveredLink = "https://status-layout.test/initial";
            await SettleAppearanceAsync(window, list);
            var before = content.ProbeToolbarPerformance;
            for (int i = 0; i < 10; i++)
            {
                model.HoveredLink = "https://status-layout.test/item/" + i;
                await SettleAppearanceAsync(window, list);
            }
            var after = content.ProbeToolbarPerformance;
            Require(after.Layout == before.Layout && after.Presentation == before.Presentation,
                "Status-bar link changes repeatedly relaid out the unrelated command headers.");
            var refresh = (Button)content.FindName("ReloadButton");
            var begin = typeof(CombinedRefreshProgress).GetMethod("BeginRefresh",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The account refresh-session entry point is missing.");
            using (var session = (IDisposable)begin.Invoke(CombinedRefreshProgress.Shared,
                [AccountManager.Shared.DefaultAccount.AccountID, null])!)
                await WaitFor(() => Task.FromResult(!refresh.IsEnabled), "Active refresh still disables toolbar command");
            await WaitFor(() => Task.FromResult(refresh.IsEnabled), "Completed refresh still enables toolbar command");
            return new { LinkChanges = 10, ExtraHeaderLayouts = after.Layout - before.Layout, RefreshAvailabilityPreserved = true };
        }
        finally { model.HoveredLink = hovered; AppDefaults.Shared.ShowStatusBar = saved; }
    }

    private static async Task VerifyWindowChromeAsync(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        var content = window.ProbeContent;
        var gear = Descendants(root).OfType<Button>().SingleOrDefault(button => button.Name == "SettingsMenuButton")
            ?? throw new InvalidOperationException("The settings gear is not in the top-right menu-bar row.");
        var row = Descendants(root).OfType<Grid>().Single(grid => grid.Name == "MenuBarRow");
        var host = Descendants(root).OfType<Grid>().Single(grid => grid.Name == "MenuBarHost");
        var bar = Descendants(root).OfType<MenuBar>().Single(menu => menu.Name == "AppMenuBar");
        var pane = (Border)content.FindName("ContentContainer");
        var body = (Grid)content.FindName("MainContentGrid");
        var sidebar = (Grid)content.FindName("SidebarPanel");
        var splitter = (FrameworkElement)content.FindName("SidebarSplitter");
        global::Windows.Foundation.Rect Bounds(FrameworkElement element)
            => element.TransformToVisual(root).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        bool SidebarAligned()
        {
            double width = ((ColumnDefinition)content.FindName("SidebarColumn")).ActualWidth;
            return sidebar.Visibility == Visibility.Visible
                ? width > 0 && Math.Abs(Bounds(pane).Left - Bounds(body).Left - width - 10) < 1
                    && splitter.ActualWidth == 8 && Bounds(splitter).Left >= Bounds(sidebar).Right - 1
                : width == 0 && Math.Abs(Bounds(pane).Left - Bounds(body).Left) < 1;
        }
        await WaitFor(() => Task.FromResult(gear.Visibility == Visibility.Visible && gear.ActualWidth > 0
            && Math.Abs(Bounds(gear).Right - (Bounds(row).Right - 8)) < 1
            && Math.Abs(Bounds(gear).Top + gear.ActualHeight / 2 - (Bounds(row).Top + row.ActualHeight / 2)) < 1
            && Bounds(host).Right <= Bounds(gear).Left
            && bar.Items.Where(item => item.Visibility == Visibility.Visible).All(item => item.ActualWidth > 0
                && Bounds(item).Right <= Bounds(host).Right + 1)
            && Math.Abs(Bounds(pane).Top - Bounds(body).Top) < 1
            && Math.Abs(Bounds(pane).Right - Bounds(body).Right) < 1
            && Math.Abs(Bounds(pane).Bottom - Bounds(body).Bottom) < 1 && SidebarAligned()),
            "Flush reading pane and nonoverlapping top-right settings");
        Require(Descendants(row).Contains(gear) && !Descendants((Border)content.FindName("StatusBarSurface")).Contains(gear),
            "The settings gear still belongs to the footer.");
        Require(pane.Margin == default && pane.CornerRadius.TopLeft > 0 && pane.CornerRadius.TopRight == 0
            && pane.CornerRadius.BottomRight == 0 && pane.CornerRadius.BottomLeft == 0,
            "The combined pane must keep only its top-left rounding and no outer margins.");
        VerifyPaneShadowClip(content);
        Require(pane.BorderThickness.Bottom == 0,
            "The reading panes must not duplicate the footer's separator.");
    }

    private static void VerifyPaneShadowClip(MainContent content)
    {
        var pane = (Border)content.FindName("ContentContainer");
        var body = (Grid)content.FindName("MainContentGrid");
        var caster = content.FindName("ContentShadowCaster") as Border
            ?? throw new InvalidOperationException("The reading pane's original left-side shadow is missing.");
        var host = (Grid)content.FindName("ContentLeftShadow");
        Require(caster.Shadow is ThemeShadow && caster.Translation.Z == 16,
            "The reading pane's original left-side shadow is missing.");
        Require(pane.Shadow is null && pane.Translation.Z == 0,
            "The translucent pane fill must stay flat instead of receiving its own shadow.");
        Require(body.Clip is RectangleGeometry clip && clip.Rect.X == 0 && clip.Rect.Y == 0
            && Math.Abs(clip.Rect.Width - body.ActualWidth) < 0.01
            && Math.Abs(clip.Rect.Height - body.ActualHeight) < 0.01,
            "The reading pane's shadow is not clipped to the content row above the footer.");
        Require(!host.IsHitTestVisible && host.Clip is RectangleGeometry,
            "The left shadow must be clipped and must not intercept sidebar input.");
        var shadowBounds = host.TransformToVisual(content).TransformBounds(((RectangleGeometry)host.Clip).Rect);
        var paneBounds = pane.TransformToVisual(content).TransformBounds(new(0, 0, pane.ActualWidth, pane.ActualHeight));
        var casterBounds = caster.TransformToVisual(content).TransformBounds(new(0, 0, caster.ActualWidth, caster.ActualHeight));
        Require(Math.Abs(shadowBounds.Right - paneBounds.Left) < 0.01
            && Math.Abs(shadowBounds.Top - paneBounds.Top) < 0.01
            && Math.Abs(shadowBounds.Bottom - paneBounds.Bottom) < 0.01
            && Math.Abs(casterBounds.Left - paneBounds.Left) < 0.01
            && Math.Abs(casterBounds.Width - paneBounds.Width) < 0.01
            && Math.Abs(casterBounds.Height - paneBounds.Height) < 0.01,
            "The shadow layer must match the pane but render only to its left above the footer.");
    }

    private static async Task<object?> CheckWindowChromeAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        bool show = AppDefaults.Shared.ShowStatusBar, column = AppDefaults.Shared.UseColumnLayout;
        bool nativeInput = Environment.GetCommandLineArgs().Contains("--sidebar-gutter-input");
        try
        {
            await content.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
            AppDefaults.Shared.ShowStatusBar = true;
            AppDefaults.Shared.UseColumnLayout = false;
            model.IsUnifiedLayout = false;
            foreach (double width in new[] { 1400d, 650, 330 })
            {
                window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(original.Bounds with
                    { Width = (int)Math.Ceiling(width * content.XamlRoot.RasterizationScale) }));
                model.IsSidebarVisible = width >= 1000;
                await VerifyWindowChromeAsync(window);
                if (width >= 1000 && nativeInput)
                    await CheckSidebarGutterInputAsync(window, (ListView)content.FindName("SidebarListView"));
                await SaveWindowScreenshotAsync(window, "window-chrome-" + width);
            }
            int frame = window.AppWindow.Size.Width - window.AppWindow.ClientSize.Width;
            window.AppWindow.Resize(new(content.MinimumReaderWidthInPixels(content.XamlRoot.RasterizationScale) + frame,
                window.AppWindow.Size.Height));
            await WaitFor(() => Task.FromResult(Math.Abs(((Grid)content.FindName("ArticleDetailPanel")).ActualWidth
                - MainContent.ArticlePaneMinimumWidth) < 1),
                "The edge-flush layout preserves the minimum reader width");
            await VerifyWindowChromeAsync(window);
            AppDefaults.Shared.UseColumnLayout = true;
            await VerifyWindowChromeAsync(window);
            model.IsUnifiedLayout = true;
            await VerifyWindowChromeAsync(window);
            AppDefaults.Shared.ShowStatusBar = false;
            await WaitFor(() => Task.FromResult(((Border)content.FindName("StatusBarSurface")).Visibility == Visibility.Collapsed),
                "Footer hidden while the top-right gear remains available");
            await VerifyWindowChromeAsync(window);
            var gear = Descendants(window.Content).OfType<Button>().Single(button => button.Name == "SettingsMenuButton");
            var menu = (MenuFlyout)gear.Flyout;
            menu.ShowAt(gear);
            await WaitFor(() => Task.FromResult(menu.Items.OfType<MenuFlyoutItem>()
                .Any(item => item.Text == "New Feed\u2026" && item.IsLoaded)), "Settings commands without a footer");
            menu.Hide();
            return new { TopRightGear = true, IndependentOfFooter = true, MenuOverflowFits = true,
                FlushPaneEdges = true, OnlyTopLeftRounded = true, SidebarGutter = 10, ResizeTarget = 8,
                NativeScrollbarAndResizeInput = nativeInput, MinimumReaderWidth = MainContent.ArticlePaneMinimumWidth };
        }
        finally
        {
            AppDefaults.Shared.ShowStatusBar = show;
            AppDefaults.Shared.UseColumnLayout = column;
            await content.RestoreStateAsync(original);
            window.AppWindow.MoveAndResize(new(original.Bounds.X, original.Bounds.Y, original.Bounds.Width, original.Bounds.Height));
        }
    }

    private static async Task<object?> CheckPaneFooterSeamAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var original = window.CaptureState();
        var root = (Grid)content.Content;
        var background = root.Background;
        var theme = root.RequestedTheme;
        bool show = AppDefaults.Shared.ShowStatusBar, column = AppDefaults.Shared.UseColumnLayout;
        var pane = (Border)content.FindName("ContentContainer");
        var footer = (Border)content.FindName("StatusBarSurface");
        var list = (ListView)content.FindName("ArticleListView");
        var horizontal = (ScrollViewer)content.FindName("TimelineHorizontalScroll");
        var inner = (Grid)content.FindName("ArticleContentGrid");
        var samples = new List<object>();
        global::Windows.Foundation.Rect Bounds(FrameworkElement element)
            => element.TransformToVisual(root).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        try
        {
            await content.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
            root.Background = new SolidColorBrush(Microsoft.UI.Colors.Magenta);
            AppDefaults.Shared.ShowStatusBar = true;
            foreach (var appearance in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = appearance;
                foreach (string layout in new[] { "standard", "column", "reading" })
                {
                    AppDefaults.Shared.UseColumnLayout = layout == "column";
                    content.ViewModel.IsUnifiedLayout = layout == "reading";
                    await SettleAppearanceAsync(window, list);
                    await WaitForRenderedFramesAsync();
                    await WaitFor(() => Task.FromResult(footer.Visibility == Visibility.Visible
                        && Math.Abs(Bounds(pane).Bottom - Bounds(footer).Top) < 1),
                        "Pane and footer share one edge");
                    var listScroll = Descendants(list).OfType<ScrollViewer>().Single(scroll => scroll.Name == "ScrollViewer");
                    samples.Add(new
                    {
                        Appearance = appearance.ToString(), Layout = layout,
                        Pane = Bounds(pane), Inner = Bounds(inner), Footer = Bounds(footer),
                        Shadow = pane.Shadow?.GetType().Name,
                        Translation = new { pane.Translation.X, pane.Translation.Y, pane.Translation.Z },
                        pane.BorderThickness, pane.CornerRadius,
                        Clip = ((Grid)content.FindName("MainContentGrid")).Clip is RectangleGeometry clip ? clip.Rect : (global::Windows.Foundation.Rect?)null,
                        LeftShadowClip = (content.FindName("ContentLeftShadow") as Grid)?.Clip is RectangleGeometry leftClip
                            ? leftClip.Rect : (global::Windows.Foundation.Rect?)null,
                        ListCorners = list.CornerRadius, HorizontalCorners = horizontal.CornerRadius,
                        ListScrollCorners = listScroll.CornerRadius
                    });
                    File.WriteAllText(Path.Combine(OutputDirectory, "pane-footer-seams.json"),
                        JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
                    VerifyPaneShadowClip(content);
                    Require(pane.BorderThickness.Bottom == 0 && footer.BorderThickness.Top == 1
                        && Math.Abs(Bounds(inner).Bottom - Bounds(footer).Top) < 1,
                        "The pane and footer still draw a doubled bottom outline or leave a gap.");
                    Require(pane.CornerRadius.TopLeft > 0 && pane.CornerRadius.BottomLeft == 0
                        && pane.CornerRadius.BottomRight == 0 && list.CornerRadius.BottomLeft == 0
                        && list.CornerRadius.BottomRight == 0 && horizontal.CornerRadius.BottomLeft == 0
                        && horizontal.CornerRadius.BottomRight == 0 && listScroll.CornerRadius.BottomLeft == 0
                        && listScroll.CornerRadius.BottomRight == 0,
                        "A nested article-list background still rounds off the bottom fill.");

                    var image = new RenderTargetBitmap();
                    await image.RenderAsync(root);
                    byte[] pixels = (await image.GetPixelsAsync()).ToArray();
                    double scaleY = image.PixelHeight / root.ActualHeight;
                    var bounds = Bounds(pane);
                    int y = Math.Clamp((int)Math.Floor((bounds.Bottom - 1) * scaleY), 0, image.PixelHeight - 1);
                    int referenceY = Math.Clamp((int)Math.Floor((bounds.Bottom - 12) * scaleY), 0, image.PixelHeight - 1);
                    bool SamePixel(int x, int row, int reference)
                    {
                        int a = (row * image.PixelWidth + x) * 4, b = (reference * image.PixelWidth + x) * 4;
                        return Enumerable.Range(0, 4).All(channel => Math.Abs(pixels[a + channel] - pixels[b + channel]) <= 2);
                    }
                    double scale = content.XamlRoot.RasterizationScale;
                    int fillStart = Math.Clamp((int)Math.Ceiling((bounds.Left + 2) * scale), 0, image.PixelWidth - 1);
                    var changed = Enumerable.Range(fillStart, image.PixelWidth - fillStart)
                        .Where(x => !SamePixel(x, y, referenceY)).ToArray();
                    int shadowPixels = 0;
                    if (bounds.Left >= 10)
                    {
                        int left = (int)Math.Floor((bounds.Left - 8) * scale);
                        int right = (int)Math.Floor((bounds.Left - 2) * scale);
                        for (int row = (int)Math.Ceiling((bounds.Top + 20) * scaleY);
                            row < (bounds.Bottom - 20) * scaleY; row++)
                        {
                            for (int x = left; x < right; x++)
                            {
                                int pixel = (row * image.PixelWidth + x) * 4;
                                if (pixels[pixel] < 253 && pixels[pixel + 1] == 0
                                    && pixels[pixel + 2] < 253 && pixels[pixel + 3] == 255) shadowPixels++;
                            }
                        }
                    }
                    // Compare blank footer rows without changing the native shadow caster.
                    int footerTop = (int)Math.Floor((Bounds(footer).Top + 3) * scaleY);
                    int footerBottom = (int)Math.Floor((Bounds(footer).Bottom - 3) * scaleY);
                    int footerPixels = Enumerable.Range(0, image.PixelWidth)
                        .Count(x => !SamePixel(x, footerTop, footerBottom));
                    File.WriteAllText(Path.Combine(OutputDirectory, $"pane-shadow-{appearance}-{layout}.json"),
                        JsonSerializer.Serialize(new { LeftShadowPixels = shadowPixels, FooterProfileDifferences = footerPixels,
                            BottomFillDifferences = changed.Length, FillRows = new[] { y, referenceY },
                            FooterRows = new[] { footerTop, footerBottom }, image.PixelWidth, image.PixelHeight }));
                    int top = Math.Max(0, (int)Math.Floor((Bounds(footer).Top - 18) * scaleY));
                    int height = image.PixelHeight - top;
                    var strip = new byte[image.PixelWidth * height * 4];
                    Buffer.BlockCopy(pixels, top * image.PixelWidth * 4, strip, 0, strip.Length);
                    using var file = File.Create(Path.Combine(OutputDirectory, $"pane-footer-{appearance}-{layout}.png"));
                    using var stream = file.AsRandomAccessStream();
                    var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                        global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                    encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                        global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                        (uint)image.PixelWidth, (uint)height, 96, 96, strip);
                    await encoder.FlushAsync();
                    Require(changed.Length == 0,
                        $"A rounded or shaded fill remains in {appearance}/{layout}: "
                        + $"different pixels={string.Join(",", changed.Take(12))}, "
                        + $"rows={y}/{referenceY}, image={image.PixelWidth}x{image.PixelHeight}.");
                    Require(bounds.Left < 10 || shadowPixels > 20,
                        $"The left-side shadow was not rendered in {appearance}/{layout}.");
                    Require(footerPixels == 0, $"The left-side shadow bleeds into {footerPixels} footer pixels in {appearance}/{layout}.");
                }
            }
            return new { SingleSeparator = true, SquareBottomFill = true, LeftShadow = true, NoFooterShadow = true,
                LightAndDark = true, StandardColumnAndReading = true, RenderedPixels = true };
        }
        finally
        {
            root.Background = background;
            root.RequestedTheme = theme;
            AppDefaults.Shared.ShowStatusBar = show;
            AppDefaults.Shared.UseColumnLayout = column;
            await content.RestoreStateAsync(original);
        }
    }

    private static void VerifyCombinedFooterLayout(MainContent content, bool column)
    {
        var divider = content.FindName("CombinedStatusDivider") as Border;
        var counts = (TextBlock)content.FindName("CombinedTimelineStatus");
        var article = (TextBlock)content.FindName("ArticleDetailStatus");
        var webpage = (TextBlock)content.FindName("WebPageStatus");
        Require(article.TextAlignment == TextAlignment.Left && webpage.TextAlignment == TextAlignment.Left,
            "Article and webpage footer information must stay left-aligned.");
        if (!column)
        {
            Require(divider is null || divider.Visibility == Visibility.Collapsed,
                "The compact footer divider must appear only with the combined column-layout status.");
            return;
        }
        if (divider is null)
            throw new InvalidOperationException("The column footer has no divider between counts and article information.");
        Require(divider.Visibility == Visibility.Visible, "The column footer divider is not visible.");
        var status = webpage.Visibility == Visibility.Visible ? webpage : article;
        global::Windows.Foundation.Rect Bounds(FrameworkElement element)
            => element.TransformToVisual(content).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        var line = Bounds(divider);
        var footer = Bounds((Grid)content.FindName("DetailFooterContent"));
        Require(divider.ActualWidth is > 0 and <= 2 && divider.ActualHeight > 0 && divider.ActualHeight < footer.Height
            && Math.Abs(line.Top + line.Height / 2 - footer.Top - footer.Height / 2) < 1 && !divider.IsHitTestVisible,
            "The column footer divider must be a short, centered, non-interactive line.");
        if (!counts.IsTextTrimmed)
        {
            double right = counts.TransformToVisual(content)
                .TransformPoint(new(counts.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Right, 0)).X;
            Require(Math.Abs(line.Left - right - divider.Margin.Left) < 1,
                "The column footer leaves empty space between its counts and divider.");
        }
        if (!string.IsNullOrEmpty(status.Text))
        {
            double left = status.TransformToVisual(content)
                .TransformPoint(new(status.ContentStart.GetCharacterRect(LogicalDirection.Forward).Left, 0)).X;
            Require(Math.Abs(left - line.Right - divider.Margin.Right) < 1,
                "Article information does not begin immediately to the right of the column-footer divider.");
        }
    }

    private static async Task<object?> CheckStatusBarAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var defaults = AppDefaults.Shared;
        bool oldShow = defaults.ShowStatusBar, oldThemes = defaults.ShowStatusBarThemeMenu;
        bool oldColumns = defaults.UseColumnLayout;
        string oldTheme = AppShared.ArticleThemes.ArticleThemesManager.Shared.CurrentTheme.Name;
        var oldMode = defaults.GetStatusBarCountMode();
        var oldUnread = defaults.GetUnreadCountDisplay();
        var original = window.CaptureState();
        MainWindow? other = null;
        Folder? fixtureFolder = null;
        var surface = (Border)content.FindName("StatusBarSurface");
        var oldFooterTheme = surface.RequestedTheme;
        var count = (TextBlock)content.FindName("StatusBarCountText");
        var countButton = (Button)content.FindName("StatusBarCountButton");
        var link = (TextBlock)content.FindName("ArticleDetailStatus");
        var theme = (DropDownButton)content.FindName("StatusBarThemeButton");
        var progress = (ProgressBar)content.FindName("StatusBarProgress");
        var oldRefresh = AccountManager.Shared.DefaultAccount.Settings.LastRefreshCompletedDate;
        var layouts = new List<object>();
        T Element<T>(string name) where T : FrameworkElement => (T)content.FindName(name);
        global::Windows.Foundation.Rect Bounds(FrameworkElement element)
            => element.TransformToVisual(content).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        bool InFooter(DependencyObject element)
        {
            for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
                if (ReferenceEquals(current, surface)) return true;
            return false;
        }
        async Task VerifyFooterAsync(string layout)
        {
            var timeline = Element<Grid>("TimelinePanel");
            var detail = Element<Grid>("ArticleDetailPanel");
            var timelineFooter = Element<Border>("TimelineFooter");
            var detailFooter = Element<Border>("DetailFooter");
            var combined = Element<TextBlock>("CombinedTimelineStatus");
            var status = Element<TextBlock>("WebPageStatus").Visibility == Visibility.Visible
                ? Element<TextBlock>("WebPageStatus") : link;
            bool separate = !model.UseColumnLayout && !model.IsUnifiedLayout && timeline.Visibility == Visibility.Visible;
            bool column = model.UseColumnLayout && !model.IsUnifiedLayout && timeline.Visibility == Visibility.Visible;
            await WaitFor(() => Task.FromResult(surface.ActualHeight == 32
                && Math.Abs(Bounds(surface).Top - Bounds(Element<Grid>("MainContentGrid")).Bottom) < 1
                && (separate
                    ? timelineFooter.Visibility == Visibility.Visible
                        && Math.Abs(Bounds(timelineFooter).Right - Bounds(timeline).Right) < 1
                        && Math.Abs(Bounds(detailFooter).Left - Bounds(detail).Left) < 1
                    : timelineFooter.Visibility == Visibility.Collapsed)
                && Math.Abs(Bounds(detailFooter).Right - Bounds(detail).Right) < 1
                && combined.Visibility == (column ? Visibility.Visible : Visibility.Collapsed)),
                "One aligned footer in " + layout);
            await WaitForRenderedFramesAsync();
            Require(InFooter(Element<Border>("SidebarFooter")) && InFooter(timelineFooter) && InFooter(detailFooter),
                "Legacy pane footers remain stacked above the new footer.");
            Require(Element<TextBlock>("TimelineFooterStatus").Text == model.TimelineStatus && combined.Text == model.TimelineStatus
                && link.Text == model.DetailStatus, "The combined footer lost the original pane status text.");
            foreach (var element in new FrameworkElement[] { countButton, status, theme })
            {
                if (element.Visibility != Visibility.Visible) continue;
                var bounds = Bounds(element);
                Require(bounds.Top >= Bounds(surface).Top && bounds.Bottom <= Bounds(surface).Bottom + 1,
                    "A footer control occupies a second row in " + layout);
            }
            if (theme.Visibility == Visibility.Visible)
            {
                try
                {
                    await WaitFor(() => Task.FromResult(Bounds(status).Right + 7 <= Bounds(theme).Left
                        && Math.Abs(Bounds(theme).Right - (Bounds(detailFooter).Right - 12)) < 1),
                        "Article text and far-right theme picker in " + layout);
                }
                catch (TimeoutException error)
                {
                    throw new InvalidOperationException($"Footer {layout}: text={Bounds(status)}, theme={Bounds(theme)}, "
                        + $"footer={Bounds(detailFooter)}, columns={string.Join(",", Element<Grid>("DetailFooterContent")
                            .ColumnDefinitions.Select(item => item.ActualWidth))}.", error);
                }
            }
            VerifyCombinedFooterLayout(content, column);
            await VerifyWindowChromeAsync(window);
            layouts.Add(new
            {
                Layout = layout, Footer = Bounds(surface), Timeline = Bounds(timeline), TimelineFooter = Bounds(timelineFooter),
                Article = Bounds(detail), ArticleFooter = Bounds(detailFooter), Theme = Bounds(theme),
                SidebarVisible = model.IsSidebarVisible, TimelineVisible = timeline.Visibility.ToString()
            });
        }
        async Task CaptureFooterAsync(string name)
        {
            var image = new RenderTargetBitmap();
            await image.RenderAsync(surface);
            var pixels = await image.GetPixelsAsync();
            using var file = File.Create(Path.Combine(OutputDirectory, name + ".png"));
            using var stream = file.AsRandomAccessStream();
            var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
        }
        try
        {
            await content.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
            window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(original.Bounds with
                { Width = (int)Math.Ceiling(1400 * content.XamlRoot.RasterizationScale) }));
            model.IsUnifiedLayout = false;
            model.IsSidebarVisible = true;
            defaults.UseColumnLayout = false;
            model.BuildSidebar();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(AccountManager.Shared.DefaultAccount.AccountID,
                AccountManager.Shared.DefaultAccount.FlattenedFeeds().Single().FeedID);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(System.Threading.CancellationToken.None);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "probe-article");
            defaults.ShowStatusBar = defaults.ShowStatusBarThemeMenu = true;
            defaults.SetStatusBarCountMode(StatusBarCountMode.Unread);
            defaults.SetUnreadCountDisplay(UnreadCountDisplay.Count);
            try
            {
                await WaitFor(() => Task.FromResult(surface.Visibility == Visibility.Visible && surface.ActualHeight == 32
                    && countButton.ActualWidth > 0 && countButton.IsEnabled
                    && count.Text.EndsWith(" unread", StringComparison.Ordinal)), "Status bar presentation");
            }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException($"Status bar: {surface.Visibility}, height={surface.ActualHeight}, "
                    + $"count={count.Text}, button={countButton.Visibility}/{countButton.ActualWidth}/{countButton.IsEnabled}, "
                    + $"sidebar={model.IsSidebarVisible}, reading={model.IsUnifiedLayout}, width={content.ActualWidth}, "
                    + $"column={((ColumnDefinition)content.FindName("SidebarColumn")).ActualWidth}, "
                    + $"statusColumn={((ColumnDefinition)content.FindName("StatusBarSidebarColumn")).Width}/"
                    + $"{((ColumnDefinition)content.FindName("StatusBarSidebarColumn")).ActualWidth}, "
                    + $"parent={((FrameworkElement)Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(countButton)).ActualWidth}.", error);
            }
            Require(InFooter(Element<Border>("SidebarFooter")) && InFooter(Element<Border>("TimelineFooter"))
                && InFooter(Element<Border>("DetailFooter")), "Legacy pane footers remain stacked above the new footer.");
            await VerifyFooterAsync("standard");
            await CaptureFooterAsync("footer-standard");
            foreach (var appearance in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                surface.RequestedTheme = appearance;
                await WaitFor(() => Task.FromResult(surface.ActualTheme == appearance), "Footer appearance");
                await VerifyFooterAsync(appearance.ToString());
                await CaptureFooterAsync("footer-" + appearance.ToString().ToLowerInvariant());
            }
            surface.RequestedTheme = oldFooterTheme;
            InvokeButton(countButton);
            await WaitFor(() => Task.FromResult(count.Text.Contains("feed", StringComparison.Ordinal)
                && defaults.GetStatusBarCountMode() == StatusBarCountMode.FoldersAndFeeds), "Count mode click");
            fixtureFolder = AccountManager.Shared.DefaultAccount.AddFolder("Status fixture folder");
            model.BuildSidebar();
            await WaitFor(() => Task.FromResult(count.Text.StartsWith("1 folder,", StringComparison.Ordinal)), "Changed folder count");
            defaults.SetUnreadCountDisplay(UnreadCountDisplay.Hidden);
            await WaitFor(() => Task.FromResult(countButton.IsEnabled && count.Text.Contains("feed", StringComparison.Ordinal)),
                "Hidden unread counts");
            InvokeButton(countButton);
            await WaitFor(() => Task.FromResult(defaults.GetStatusBarCountMode() == StatusBarCountMode.LastRefresh
                && count.Text == "Not updated yet"), "Last refresh count mode");
            AccountManager.Shared.DefaultAccount.Settings.LastRefreshCompletedDate = DateTime.UtcNow;
            await Task.Run(content.InvalidateStatusBarClock);
            await WaitFor(() => Task.FromResult(count.Text.StartsWith("Updated ", StringComparison.Ordinal)),
                "Worker-thread clock notification");
            foreach (uint message in new uint[] { 0x001A, 0x001E, 0x0218 })
            {
                var previousDay = DateTime.UtcNow.AddDays(-(int)(message % 3 + 1));
                AccountManager.Shared.DefaultAccount.Settings.LastRefreshCompletedDate = previousDay;
                SendWindowMessage(WinRT.Interop.WindowNative.GetWindowHandle(window), message, IntPtr.Zero, IntPtr.Zero);
                await WaitFor(() => Task.FromResult(count.Text == "Updated "
                    + previousDay.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)), "Native clock/settings/resume notification");
            }
            InvokeButton(countButton);
            await WaitFor(() => Task.FromResult(defaults.GetStatusBarCountMode() == StatusBarCountMode.FoldersAndFeeds
                && count.Text.Contains("feed", StringComparison.Ordinal)), "Hidden-count mode cycling");
            model.SetHoveredLink("https://example.com/a?b=1");
            await WaitFor(() => Task.FromResult(link.Text == "https://example.com/a?b=1"), "Original article-footer hover presentation");
            Windows.ApplicationModel.DataTransfer.DataPackage? copied = null;
            content.ProbeClipboardWriter = package => copied = package;
            var linkMenu = (MenuFlyout)link.ContextFlyout;
            linkMenu.ShowAt(link);
            await WaitFor(() => Task.FromResult(linkMenu.Items.Count == 1 && linkMenu.Items[0].IsLoaded), "Link copy menu");
            InvokeMenuItem((MenuFlyoutItem)linkMenu.Items[0]);
            Require(copied is not null && await copied.GetView().GetTextAsync() == "https://example.com/a?b=1",
                "Status-bar Copy lost the full URL or its scheme.");
            linkMenu.Hide();
            model.SetHoveredLink(null);
            await WaitFor(() => Task.FromResult(link.Text == model.DetailStatus
                && !link.Text.Contains(model.SelectedArticle!.Link, StringComparison.Ordinal)),
                "Original article metadata after leaving a link");

            var gear = Descendants(window.Content).OfType<Button>().Single(button => button.Name == "SettingsMenuButton");
            var gearMenu = (MenuFlyout)gear.Flyout;
            gearMenu.ShowAt(gear);
            await WaitFor(() => Task.FromResult(gearMenu.Items.Count > 3 && gearMenu.Items[0].IsLoaded), "Gear menu");
            Require(gearMenu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == "New Feed\u2026")
                && gearMenu.Items.OfType<MenuFlyoutItem>().Any(item => !item.IsEnabled && item.Text == model.SelectedSidebarItem!.Title),
                "Gear menu lost creation commands or the selected-subscription heading.");
            Require(gearMenu.Items[0] is MenuFlyoutItem { Text: "Refresh", IsEnabled: true },
                "Gear menu lost the available Refresh command.");
            var gearClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void GearClosed(object? sender, object args) => gearClosed.TrySetResult();
            gearMenu.Closed += GearClosed;
            try
            {
                InvokeMenuItem(gearMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "Hide Sidebar"));
                await WaitFor(() => Task.FromResult(!model.IsSidebarVisible && gear.ActualWidth > 0),
                    "Gear menu sidebar hiding");
                gearMenu.Hide();
                await gearClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { gearMenu.Closed -= GearClosed; }
            gearMenu.ShowAt(gear);
            try
            {
                await WaitFor(() => Task.FromResult(gearMenu.Items.OfType<MenuFlyoutItem>()
                    .Any(item => item.Text == "Show Sidebar" && item.IsLoaded)), "Gear menu sidebar restoration command");
            }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException($"Sidebar={model.IsSidebarVisible}, gear={gear.IsLoaded}/{gear.ActualWidth}, "
                    + $"status={surface.Visibility}/{surface.ActualWidth}, menu={HasMenuPopup(window)}, "
                    + $"items={string.Join("; ", gearMenu.Items.OfType<MenuFlyoutItem>().Select(item => $"{item.Text}/{item.IsLoaded}"))}.", error);
            }
            InvokeMenuItem(gearMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "Show Sidebar"));
            await WaitFor(() => Task.FromResult(model.IsSidebarVisible), "Gear menu sidebar restoration");
            gearMenu.Hide();
            var sidebarColumn = (ColumnDefinition)content.FindName("SidebarColumn");
            var divider = (Border)content.FindName("StatusBarDivider");
            var readingPane = (Border)content.FindName("ContentContainer");
            foreach (double width in new[] { 200.0, 320.0, 400.0 })
            {
                await content.RestoreStateAsync(window.CaptureState() with { SidebarWidth = width });
                try
                {
                    await WaitFor(() => Task.FromResult(Math.Abs(sidebarColumn.ActualWidth - width) < 1
                        && Math.Abs(divider.TransformToVisual(content).TransformPoint(default).X
                            - readingPane.TransformToVisual(content).TransformPoint(default).X)
                        < 1 / content.XamlRoot.RasterizationScale), "Status-bar divider alignment after sidebar resize");
                }
                catch (TimeoutException error)
                {
                    throw new InvalidOperationException($"Requested sidebar={width}, actual={sidebarColumn.ActualWidth}, "
                        + $"status={((ColumnDefinition)content.FindName("StatusBarSidebarColumn")).ActualWidth}, "
                        + $"divider={divider.TransformToVisual(content).TransformPoint(default).X}, "
                        + $"reading={readingPane.TransformToVisual(content).TransformPoint(default).X}.", error);
                }
                await VerifyFooterAsync("sidebar-" + width);
            }
            foreach (double width in new[] { 280.0, 380.0, 500.0 })
            {
                await content.RestoreStateAsync(window.CaptureState() with { TimelineWidth = width });
                await VerifyFooterAsync("timeline-" + width);
            }
            model.IsSidebarVisible = false;
            await WaitFor(() => Task.FromResult(Element<Grid>("SidebarPanel").Visibility == Visibility.Collapsed),
                "Collapsed sidebar footer");
            await VerifyFooterAsync("sidebar-hidden");
            Require(countButton.Visibility == Visibility.Collapsed, "The hidden sidebar retained its full count section.");
            model.IsSidebarVisible = true;
            defaults.UseColumnLayout = true;
            await VerifyFooterAsync("column");
            await CaptureFooterAsync("footer-column");
            foreach (var appearance in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                surface.RequestedTheme = appearance;
                await WaitForRenderedFramesAsync();
                await VerifyFooterAsync("column-" + appearance);
                await CaptureFooterAsync("footer-column-" + appearance.ToString().ToLowerInvariant());
            }
            surface.RequestedTheme = oldFooterTheme;
            var combinedCounts = Element<TextBlock>("CombinedTimelineStatus");
            string originalCounts = model.TimelineStatus;
            try
            {
                window.AppWindow.Resize(new((int)Math.Ceiling(360 * content.XamlRoot.RasterizationScale),
                    window.AppWindow.Size.Height));
                model.TimelineStatus = string.Concat(Enumerable.Repeat(originalCounts + " ", 20));
                model.SetHoveredLink("https://example.com/" + new string('w', 400));
                try
                {
                    await WaitFor(() => Task.FromResult(combinedCounts.IsTextTrimmed && link.IsTextTrimmed
                        && Element<Grid>("DetailFooterContent").ActualWidth <= content.ActualWidth
                        && Math.Abs(Element<Grid>("DetailFooterContent").ColumnDefinitions.Sum(item => item.ActualWidth)
                            - Element<Grid>("DetailFooterContent").ActualWidth) < 1), "Compact column footer truncation");
                }
                catch (TimeoutException error)
                {
                    var row = Element<Grid>("DetailFooterContent");
                    throw new InvalidOperationException($"Compact footer: row={row.ActualWidth}, "
                        + $"counts={combinedCounts.Text.Length}/{combinedCounts.ActualWidth}/{combinedCounts.IsTextTrimmed}, "
                        + $"details={link.Text.Length}/{link.ActualWidth}/{link.IsTextTrimmed}, "
                        + $"columns={string.Join(",", row.ColumnDefinitions.Select(item => item.ActualWidth))}, "
                        + $"countLimit={combinedCounts.MaxWidth}.", error);
                }
                await VerifyFooterAsync("column-long-narrow");
                defaults.ShowStatusBarThemeMenu = false;
                await WaitFor(() => Task.FromResult(theme.Visibility == Visibility.Collapsed), "Column theme picker hidden");
                await VerifyFooterAsync("column-theme-hidden");
            }
            finally
            {
                model.TimelineStatus = originalCounts;
                model.SetHoveredLink(null);
                defaults.ShowStatusBarThemeMenu = true;
                window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(original.Bounds with
                    { Width = (int)Math.Ceiling(1400 * content.XamlRoot.RasterizationScale) }));
                model.IsSidebarVisible = true;
            }
            await content.OpenSelectedInWebViewAsync();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady
                && Element<TextBlock>("WebPageStatus").Visibility == Visibility.Visible), "Webpage footer");
            await VerifyFooterAsync("column-webpage");
            var pageStatus = Element<TextBlock>("WebPageStatus");
            var pageMenu = (MenuFlyout)pageStatus.ContextFlyout;
            pageMenu.ShowAt(pageStatus);
            await WaitFor(() => Task.FromResult(pageMenu.Items.Count == 1 && pageMenu.Items[0].IsLoaded), "Webpage link copy menu");
            InvokeMenuItem((MenuFlyoutItem)pageMenu.Items[0]);
            Require(copied is not null && await copied.GetView().GetTextAsync() == pageStatus.Text,
                "The webpage footer did not copy its displayed full URL.");
            pageMenu.Hide();
            await content.GoBackAsync();
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady && !content.ProbeShowingWebPage), "Article footer restored");
            model.IsUnifiedLayout = true;
            await VerifyFooterAsync("reading-only");
            model.IsUnifiedLayout = false;
            defaults.UseColumnLayout = false;
            window.AppWindow.Resize(new((int)Math.Ceiling(650 * content.XamlRoot.RasterizationScale), window.AppWindow.Size.Height));
            await WaitFor(() => Task.FromResult(Element<Grid>("TimelinePanel").Visibility == Visibility.Collapsed),
                "Responsive timeline collapse");
            await VerifyFooterAsync("narrow");
            window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(original.Bounds with
                { Width = (int)Math.Ceiling(1400 * content.XamlRoot.RasterizationScale) }));
            model.IsSidebarVisible = true;
            await WaitFor(() => Task.FromResult(Element<Grid>("TimelinePanel").Visibility == Visibility.Visible),
                "Responsive timeline restoration");
            AppShared.ArticleThemes.ArticleThemesManager.Shared.SelectByName("Verdana Revival");
            await WaitFor(() => Task.FromResult(theme.Content as string == "Verdana Revival"), "Long theme label");
            model.SetHoveredLink("https://example.com/" + new string('w', 400));
            await VerifyFooterAsync("long-link-and-theme");
            await WaitFor(() => Task.FromResult(link.IsTextTrimmed), "Long hover URL trims within the article footer");
            model.SetHoveredLink(null);
            var themes = (MenuFlyout)theme.Flyout;
            themes.ShowAt(theme);
            await WaitFor(() => Task.FromResult(themes.Items.OfType<ToggleMenuFlyoutItem>().Count() >= 3), "Status-bar theme menu");
            Require(themes.Items.OfType<ToggleMenuFlyoutItem>().Count(item => item.IsChecked) == 1,
                "Status-bar themes do not share the selected article theme.");
            themes.Hide();
            defaults.ShowStatusBarThemeMenu = false;
            try
            {
                // TextBlock's bounds can fit its glyphs even with Stretch; use its allocated grid slot.
                await WaitFor(() => Task.FromResult(theme.Visibility == Visibility.Collapsed
                    && Math.Abs(Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(link).Right
                        - Element<Grid>("DetailFooterContent").ActualWidth) < 1),
                    "Hiding the theme menu returns its space to article information");
            }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException($"Theme={theme.Visibility}/{Bounds(theme)}, text={Bounds(link)}, "
                    + $"text alignment={link.HorizontalAlignment}/{link.TextAlignment}, desired={link.DesiredSize}, "
                    + $"footer={Bounds(Element<Border>("DetailFooter"))}, "
                    + $"columns={string.Join(",", Element<Grid>("DetailFooterContent").ColumnDefinitions.Select(column => column.ActualWidth))}.",
                    error);
            }
            await VerifyFooterAsync("theme-hidden");

            var combined = CombinedRefreshProgress.Shared;
            combined.AddAccount(AccountManager.Shared.DefaultAccount);
            var reporter = combined.GetProgressReporter(AccountManager.Shared.DefaultAccount);
            await Task.Run(() => reporter.Report(new ProgressInfo(5, 3, 8)));
            await WaitFor(() => Task.FromResult(progress.Visibility == Visibility.Visible && progress.Maximum == 8
                && progress.Value == 3 && count.Text == "3 of 8"), "Worker-thread refresh progress");
            model.IsSidebarVisible = false;
            await WaitFor(() => Task.FromResult(Element<Grid>("SidebarPanel").Visibility == Visibility.Collapsed
                && Element<Border>("SidebarFooter").Visibility == Visibility.Visible), "Compact progress without the sidebar");
            await VerifyFooterAsync("sidebar-hidden-refreshing");
            foreach (var button in new[] { (Button)content.FindName("StatusBarActivityButton"), countButton })
            {
                if (ReferenceEquals(button, countButton))
                {
                    model.IsSidebarVisible = true;
                    await WaitFor(() => Task.FromResult(countButton.Visibility == Visibility.Visible), "Sidebar count action restored");
                }
                var modeBeforeActivity = defaults.GetStatusBarCountMode();
                Require(!AppWindows.CurrentActivityWindow.ShouldOpenAtStartup, "Current Activity was already open.");
                try
                {
                    InvokeButton(button);
                    await WaitFor(() => Task.FromResult(AppWindows.CurrentActivityWindow.ShouldOpenAtStartup),
                        "Status-bar Current Activity action");
                    Require(defaults.GetStatusBarCountMode() == modeBeforeActivity,
                        "Clicking refresh progress changed the saved count mode.");
                }
                finally
                {
                    if (AppWindows.CurrentActivityWindow.ShouldOpenAtStartup)
                        AppWindows.CurrentActivityWindow.Show().Close();
                    window.Activate();
                }
            }
            reporter.Report(new ProgressInfo(0, 8, 8));
            await WaitFor(() => Task.FromResult(progress.Visibility == Visibility.Collapsed
                && count.Text.Contains("feed", StringComparison.Ordinal)), "Refresh completion");

            other = App.OpenMainWindow(original);
            Require(await other.Restoration.WaitAsync(TimeSpan.FromSeconds(30)), "Second status-bar window restoration failed.");
            double contentHeight = Element<Grid>("MainContentGrid").ActualHeight;
            content.SetStatusBarVisible(false);
            await WaitFor(() => Task.FromResult(surface.Visibility == Visibility.Collapsed
                && ((Border)other.ProbeContent.FindName("StatusBarSurface")).Visibility == Visibility.Collapsed
                && Math.Abs(Element<Grid>("MainContentGrid").ActualHeight - contentHeight - 32) < 1),
                "Hide the single shared footer without legacy fallbacks");
            content.SetStatusBarVisible(true);
            await WaitFor(() => Task.FromResult(surface.Visibility == Visibility.Visible
                && ((Border)other.ProbeContent.FindName("StatusBarSurface")).Visibility == Visibility.Visible), "Restored status-bar visibility");
            return new { SingleFooter = true, OriginalPaneStatus = true, FarRightTheme = true, TopRightGear = true,
                CountCycling = true, HiddenUnreadCounts = true, LastRefresh = true, ClockNotifications = true,
                DividerAlignment = true,
                FullLinkCopy = true, GearSelection = true,
                SharedThemes = true, WorkerProgress = true, ActivityActions = true, MultipleWindows = true,
                StandardColumnUnifiedAndNarrow = true };
        }
        finally
        {
            File.WriteAllText(Path.Combine(OutputDirectory, "footer-layouts.json"),
                JsonSerializer.Serialize(layouts, new JsonSerializerOptions { WriteIndented = true }));
            content.ProbeClipboardWriter = null;
            surface.RequestedTheme = oldFooterTheme;
            model.SetHoveredLink(null);
            if (other is not null) await CloseWindowAsync(other);
            if (fixtureFolder is not null) AccountManager.Shared.DefaultAccount.RemoveFolder(fixtureFolder);
            CombinedRefreshProgress.Shared.Reset();
            AccountManager.Shared.DefaultAccount.Settings.LastRefreshCompletedDate = oldRefresh;
            defaults.ShowStatusBar = oldShow;
            defaults.ShowStatusBarThemeMenu = oldThemes;
            defaults.UseColumnLayout = oldColumns;
            AppShared.ArticleThemes.ArticleThemesManager.Shared.SelectByName(oldTheme);
            defaults.SetStatusBarCountMode(oldMode);
            defaults.SetUnreadCountDisplay(oldUnread);
            await content.RestoreStateAsync(original);
            window.AppWindow.MoveAndResize(new(original.Bounds.X, original.Bounds.Y, original.Bounds.Width, original.Bounds.Height));
        }
    }
}
#endif
