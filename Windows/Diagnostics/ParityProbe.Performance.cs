#if PARITY_PROBE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinNewsWire.Account;
using WinNewsWire.AppShared.Toolbar;
using WinNewsWire.Articles;
using WinNewsWire.Core;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static Task<object?> CheckTimelineMeasurementRetentionAsync()
    {
        Helpers.MultilineTextFieldSizer.EmptyCache();
        var title = new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
        var summary = new TextBlock { Text = "A short preview.", FontSize = 14, TextWrapping = TextWrapping.Wrap };
        var panel = new Helpers.TimelineTextPanel();
        panel.Children.Add(title);
        panel.Children.Add(summary);
        try
        {
            var timer = Stopwatch.StartNew();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 500; index++)
            {
                Helpers.HtmlInlines.SetAttributedText(title, AppShared.Extensions.SimpleHtml.Parse(
                    $"<b>Article {index}</b> has an independently wrapped title in the scrolling timeline."));
                panel.InvalidateMeasure();
                panel.Measure(new global::Windows.Foundation.Size(240, double.PositiveInfinity));
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var fields = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var sizers = (System.Collections.IDictionary)(typeof(Helpers.MultilineTextFieldSizer)
                .GetField("Sizers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .GetValue(null)!);
            int retained = sizers.Values.Cast<object>().Sum(sizer =>
                ((System.Collections.IDictionary)sizer.GetType().GetField("_cache", fields)!.GetValue(sizer)!).Count
                + ((System.Collections.IDictionary)sizer.GetType().GetField("_attributedCache", fields)!.GetValue(sizer)!).Count);
            var report = new { Rows = 500, RetainedMeasurementTexts = retained, AllocatedBytes = allocated, ElapsedMs = timer.Elapsed.TotalMilliseconds };
            File.WriteAllText(Path.Combine(OutputDirectory, "measurement-retention.json"), JsonSerializer.Serialize(report));
            Require(retained == 0, "Timeline layout retained visited titles in a redundant global measurement cache.");
            return Task.FromResult<object?>(report);
        }
        finally { Helpers.MultilineTextFieldSizer.EmptyCache(); }
    }

    private static async Task<object?> CheckIconContrastAsync(MainWindow window)
    {
        var list = (ListView)window.ProbeContent.FindName("ArticleListView");
        var originalHeader = list.Header;
        var directory = Path.Combine(OutputDirectory, "icon-contrast");
        Directory.CreateDirectory(directory);
        async Task WriteIcon(string path, byte gray, byte alpha)
        {
            var pixels = new byte[16 * 16 * 4];
            for (int y = 2; y < 14; y++)
                for (int x = 2; x < 14; x++)
                {
                    int offset = (y * 16 + x) * 4;
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = gray;
                    pixels[offset + 3] = alpha;
                }
            using var file = File.Create(path);
            using var stream = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(file);
            var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                global::Windows.Graphics.Imaging.BitmapAlphaMode.Straight, 16, 16, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        var white = Path.Combine(directory, "white.png");
        var black = Path.Combine(directory, "black.png");
        var transparent = Path.Combine(directory, "transparent.png");
        await WriteIcon(white, 255, 255);
        await WriteIcon(black, 0, 255);
        await WriteIcon(transparent, 0, 0);
        var icons = new[] { white, black, transparent, white }.Select(path => new Helpers.IconView
        {
            Width = 16, Height = 16, RequestedTheme = ElementTheme.Light, ImageSource = new BitmapImage(new Uri(path))
        }).ToArray();
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Height = 24 };
        foreach (var icon in icons) panel.Children.Add(icon);
        try
        {
            int before = Helpers.IconView.ProbePixelDecodes;
            int readbacks = Helpers.IconView.ProbeRenderReadbacks;
            list.Header = panel;
            list.UpdateLayout();
            Descendants(list).OfType<ScrollViewer>().First().ChangeView(null, 0, null, true);
            await WaitFor(() => Task.FromResult(icons.All(icon => icon.ProbeLuminanceReady)), "Raster icon luminance");
            Require(icons[0].ProbeLuminance == ImageLuminanceType.Bright && icons[1].ProbeLuminance == ImageLuminanceType.Dark
                && icons[2].ProbeLuminance == ImageLuminanceType.Regular, "Native raster sampling changed icon luminance classifications.");
            Require(icons[0].ProbeBackground is SolidColorBrush { Color.R: 242 } && icons[1].ProbeBackground is null
                && icons[2].ProbeBackground is null, "Light-theme icon contrast changed.");
            int decodes = Helpers.IconView.ProbePixelDecodes;
            Require(decodes - before == 3, "The duplicate icon was decoded more than once.");
            foreach (var icon in icons) icon.RequestedTheme = ElementTheme.Dark;
            await WaitFor(() => Task.FromResult(icons[1].ProbeBackground is SolidColorBrush { Color.R: 56 }), "Dark icon background");
            Require(icons[0].ProbeBackground is null && Helpers.IconView.ProbePixelDecodes == decodes,
                "A theme change re-decoded icons or lost dark-theme contrast.");
            await WriteIcon(white, 0, 255);
            File.SetLastWriteTimeUtc(white, DateTime.UtcNow.AddSeconds(2));
            icons[0].ImageSource = new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache, UriSource = new Uri(white) };
            await WaitFor(() => Task.FromResult(icons[0].ProbeLuminanceReady && icons[0].ProbeLuminance == ImageLuminanceType.Dark),
                "Changed icon file invalidated luminance");
            Require(Helpers.IconView.ProbePixelDecodes == decodes + 1 && Helpers.IconView.ProbeRenderReadbacks == readbacks,
                "Changed raster content was not decoded once or still used compositor readbacks.");
            AppRuntime.AppService.Shared.ReleaseMemoryCaches();
            Require(Helpers.IconView.ProbeLuminanceCacheCount == 0, "Icon measurements were not released by cache cleanup.");
            return new { NativePixels = true, TransparentPixels = true, LightAndDarkContrast = true,
                CoalescedDuplicate = true, ChangedFileInvalidation = true, NoCompositorReadbacks = true, CacheCleanup = true };
        }
        finally { list.Header = originalHeader; }
    }

    private sealed record UiPerformanceSample(string Scenario, double ElapsedMs, double CpuMs, double AllocatedMB,
        double MaximumUiGapMs, int ToolbarStates, int ToolbarMeasurements, int ToolbarLayouts,
        int HeaderColumnRebuilds, int TitleBarChecks, int IconRenderReadbacks, int IconPixelDecodes);

    private static async Task<object?> CheckPerformanceAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var originalSearch = model.SearchQuery;
        var originalToolbar = content.CurrentToolbarConfiguration;
        bool originalColumn = AppDefaults.Shared.UseColumnLayout;
        var account = AccountManager.Shared.DefaultAccount!;
        var feed = account.AddFeed(serverUrl + "/performance-feed", "Performance fixture");
        feed.FaviconUrl = serverUrl + "/icon.png";
        feed.HomePageUrl = serverUrl + "/";
        var samples = new List<UiPerformanceSample>();
        var measurements = new List<int>();
        var rebuilds = new List<int>();
        var selectionLatency = new List<double>();
        int maximumRealizedRows = 0;
        const int articleCount = 2000;
        var now = DateTime.UtcNow;
        await account.Database.UpdateArticlesAsync(Enumerable.Range(0, articleCount).Select(index =>
        {
            string id = "performance-" + index;
            return new Article(account.AccountID, id, feed.FeedID, id, $"Performance article {index:D4}",
                "<p>Performance fixture with a representative paragraph of readable article content.</p>",
                null, null, serverUrl + "/navigation-first", null, null, null, now.AddMinutes(-index), null, null, null,
                new ArticleStatus(id, true, false, now));
        }));

        async Task<UiPerformanceSample> Measure(string scenario, Func<Task> action)
        {
            var before = content.ProbeToolbarPerformance;
            int titleChecks = window.ProbeTitleBarRegionChecks;
            int iconReadbacks = Helpers.IconView.ProbeRenderReadbacks;
            int iconDecodes = Helpers.IconView.ProbePixelDecodes;
            long allocated = GC.GetTotalAllocatedBytes();
            using var process = Process.GetCurrentProcess();
            double cpu = process.TotalProcessorTime.TotalMilliseconds;
            double maximumGap = 0;
            long lastTick = Stopwatch.GetTimestamp();
            var pulse = window.DispatcherQueue.CreateTimer();
            pulse.Interval = TimeSpan.FromMilliseconds(16);
            pulse.IsRepeating = true;
            void Tick(DispatcherQueueTimer sender, object args)
            {
                long current = Stopwatch.GetTimestamp();
                maximumGap = Math.Max(maximumGap, Stopwatch.GetElapsedTime(lastTick, current).TotalMilliseconds);
                lastTick = current;
            }
            pulse.Tick += Tick;
            var elapsed = Stopwatch.StartNew();
            pulse.Start();
            try
            {
                await action();
                await Task.Delay(80);
            }
            finally
            {
                pulse.Stop();
                pulse.Tick -= Tick;
            }
            process.Refresh();
            var after = content.ProbeToolbarPerformance;
            var result = new UiPerformanceSample(scenario, elapsed.Elapsed.TotalMilliseconds,
                process.TotalProcessorTime.TotalMilliseconds - cpu, (GC.GetTotalAllocatedBytes() - allocated) / 1048576d,
                maximumGap, after.State - before.State, after.Presentation - before.Presentation,
                after.Layout - before.Layout, after.Columns - before.Columns, window.ProbeTitleBarRegionChecks - titleChecks,
                Helpers.IconView.ProbeRenderReadbacks - iconReadbacks, Helpers.IconView.ProbePixelDecodes - iconDecodes);
            samples.Add(result);
            return result;
        }

        try
        {
            window.Activate();
            double scale = content.XamlRoot.RasterizationScale;
            window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(original.Bounds with { Width = (int)Math.Ceiling(1280 * scale) }));
            await WaitFor(() => Task.FromResult(((Grid)content.FindName("MainContentGrid")).ActualWidth > 1000), "Performance window size");
            model.IsUnifiedLayout = false;
            model.IsSidebarVisible = true;
            model.IsReaderMode = false;
            model.SearchQuery = "";
            content.SaveToolbarConfiguration(ToolbarConfiguration.Default);
            model.BuildSidebar();
            await model.ProbeDrainViewUpdatesAsync();
            await Measure("Load 2000-article feed", async () =>
            {
                model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
                await model.FilterArticlesAsync();
                await model.WaitForCurrentTimelineAsync(CancellationToken.None);
                Require(model.ArticleItems.Count == articleCount, "The performance fixture lost articles.");
            });
            var list = (ListView)content.FindName("ArticleListView");
            foreach (bool column in new[] { false, true })
            {
                AppDefaults.Shared.UseColumnLayout = column;
                model.SelectedArticle = model.ArticleItems[0];
                await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Performance article document");
                await Task.Delay(350);
                string mode = column ? "Column" : "Regular";
                await Measure(mode + " idle", () => Task.Delay(800));
                var switching = await Measure(mode + " 16 article selections", async () =>
                {
                    for (int index = 1; index <= 16; index++)
                    {
                        var elapsed = Stopwatch.StartNew();
                        model.SelectedArticle = model.ArticleItems[index];
                        await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Performance article selection");
                        selectionLatency.Add(elapsed.Elapsed.TotalMilliseconds);
                    }
                });
                measurements.Add(switching.ToolbarMeasurements);
                rebuilds.Add(switching.HeaderColumnRebuilds);
                await Measure(mode + " scrolling", async () =>
                {
                    var scroll = Descendants(list).OfType<ScrollViewer>().First();
                    for (int step = 0; step < 20; step++)
                    {
                        scroll.ChangeView(null, Math.Min(scroll.ScrollableHeight, step * scroll.ViewportHeight), null, true);
                        await Task.Delay(20);
                        maximumRealizedRows = Math.Max(maximumRealizedRows, Descendants(list).OfType<ListViewItem>().Count());
                    }
                    scroll.ChangeView(null, 0, null, true);
                });
                await Measure(mode + " resizing", async () =>
                {
                    for (int step = 0; step < 8; step++)
                    {
                        int width = (int)Math.Ceiling((step % 2 == 0 ? 1100 : 1280) * scale);
                        window.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(width, window.AppWindow.Size.Height));
                        await Task.Delay(40);
                    }
                });
            }
            int firstChanceBefore = App.ProbeFirstChanceLogWrites;
            await Measure("50 handled cancellations", () =>
            {
                for (int index = 0; index < 50; index++)
                {
                    try { throw new TaskCanceledException("Expected performance probe cancellation."); }
                    catch (TaskCanceledException) { }
                }
                return Task.CompletedTask;
            });
            int cancellationWrites = App.ProbeFirstChanceLogWrites - firstChanceBefore;
            int explicitBefore = App.ProbeExplicitLogWrites;
            App.LogException("Performance diagnostic", new InvalidOperationException("Expected explicit diagnostic verification."));
            Require(App.ProbeExplicitLogWrites > explicitBefore, "Explicit diagnostic errors stopped being recorded.");

            await content.OpenSelectedInWebViewAsync();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Performance publisher page");
            var web = content.ProbeWebPageView.CoreWebView2;
            await web.ExecuteScriptAsync("document.getElementById('page-input').value='performance state';");
            model.SelectedArticle = model.ArticleItems[30];
            await WaitFor(() => Task.FromResult(!content.ProbeShowingWebPage && content.ProbeArticleReady), "Hidden publisher page");
            await Task.Delay(500);
            bool hiddenWebSuspended = web.IsSuspended;
            await content.GoBackAsync();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Resumed publisher page");
            bool resumedState = !web.IsSuspended && JsonSerializer.Deserialize<string>(
                await web.ExecuteScriptAsync("document.getElementById('page-input').value")) == "performance state";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                model.SelectedArticle = model.ArticleItems[30];
                await content.GoBackAsync();
                await Task.Delay(300);
                Require(content.ProbeShowingWebPage && !web.IsSuspended,
                    "A stale suspension request froze the newly visible webpage.");
            }
            await content.GoBackAsync();

            var report = new
            {
                ArticleCount = articleCount, Samples = samples, MaximumRealizedRows = maximumRealizedRows,
                SelectionMedianMs = selectionLatency.Order().ElementAt(selectionLatency.Count / 2),
                SelectionMaximumMs = selectionLatency.Max(), CancellationLogWrites = cancellationWrites,
                HiddenWebPageSuspended = hiddenWebSuspended, ResumedWebStatePreserved = resumedState
            };
            File.WriteAllText(Path.Combine(OutputDirectory, "performance.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Require(maximumRealizedRows is > 0 and < 200, "Scrolling realized the whole feed instead of virtualizing rows.");
            Require(measurements.All(count => count <= 2), "Article selection repeatedly remeasured every toolbar command.");
            Require(rebuilds.All(count => count == 0), "Article selection rebuilt unchanged header column collections.");
            Require(samples.Where(sample => sample.Scenario.EndsWith("scrolling", StringComparison.Ordinal))
                .All(sample => sample.IconRenderReadbacks == 0), "Scrolling still snapshots each raster icon through the compositor.");
            Require(cancellationWrites == 0, "Handled cancellations still synchronously write first-chance stack traces.");
            Require(hiddenWebSuspended && resumedState, "An inactive publisher page keeps running or loses its state when resumed.");
            return report;
        }
        finally
        {
            content.SaveToolbarConfiguration(originalToolbar);
            AppDefaults.Shared.UseColumnLayout = originalColumn;
            account.RemoveFeed(feed);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            model.SearchQuery = originalSearch;
            window.AppWindow.MoveAndResize(new(original.Bounds.X, original.Bounds.Y, original.Bounds.Width, original.Bounds.Height));
        }
    }
}
#endif
