#if PARITY_PROBE
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using WinNewsWire.Account;
using WinNewsWire.AppShared.Exporters;
using WinNewsWire.Core;
using WinNewsWire.Parsers;
using WinRT.Interop;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckOpmlPerformanceAsync(MainWindow window)
    {
        var args = Environment.GetCommandLineArgs();
        var path = args[Array.IndexOf(args, "--opml-fixture") + 1];
        var document = OpmlParser.Parse(new ParserData(path, await File.ReadAllBytesAsync(path)));
        static IEnumerable<OpmlFeedSpecifier> Feeds(IEnumerable<OpmlItem> items)
        {
            foreach (var item in items)
            {
                if (item.FeedSpecifier is { } feed) yield return feed;
                foreach (var child in Feeds(item.Children)) yield return child;
            }
        }
        var expected = Feeds(document.Children).DistinctBy(feed => feed.FeedUrl).ToArray();
        Require(expected.Length > 0, "The supplied OPML contains no feeds.");
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var account = AccountManager.Shared.DefaultAccount!;
        var original = window.CaptureState();
        var originalSearch = model.SearchQuery;
        bool originalColumns = AppDefaults.Shared.UseColumnLayout;
        bool originalScripts = AppDefaults.Shared.ArticleContentJavascriptEnabled;
        var phases = new List<object>();
        var browserAttempts = new List<object>();
        int sidebarRebuilds = 0, timelineReplacements = 0, structureChanges = 0;
        void SidebarChanged() => sidebarRebuilds++;
        void ModelChanged(object? sender, PropertyChangedEventArgs change)
        {
            if (change.PropertyName == nameof(model.ArticleItems)) timelineReplacements++;
        }
        void StructureChanged(object? sender, EventArgs change) => Interlocked.Increment(ref structureChanges);
        void Save() => File.WriteAllText(Path.Combine(OutputDirectory, "opml-performance.json"),
            JsonSerializer.Serialize(new { FeedCount = expected.Length, Phases = phases, BrowserAttempts = browserAttempts },
                new JsonSerializerOptions { WriteIndented = true }));

        async Task Measure(string name, Func<Task> action)
        {
            var gaps = new List<double>();
            var pulse = window.DispatcherQueue.CreateTimer();
            pulse.Interval = TimeSpan.FromMilliseconds(16);
            long last = Stopwatch.GetTimestamp();
            void Tick(DispatcherQueueTimer sender, object data)
            {
                long now = Stopwatch.GetTimestamp();
                gaps.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
                last = now;
            }
            pulse.Tick += Tick;
            int side = sidebarRebuilds, timeline = timelineReplacements;
            var toolbar = content.ProbeToolbarPerformance;
            long allocated = GC.GetTotalAllocatedBytes();
            using var process = Process.GetCurrentProcess();
            double cpu = process.TotalProcessorTime.TotalMilliseconds;
            var elapsed = Stopwatch.StartNew();
            pulse.Start();
            string? failure = null;
            try { await action(); await Task.Delay(80); }
            catch (Exception error) { failure = error.ToString(); throw; }
            finally
            {
                pulse.Stop();
                pulse.Tick -= Tick;
                var after = content.ProbeToolbarPerformance;
                phases.Add(new
                {
                    Name = name, Milliseconds = elapsed.Elapsed.TotalMilliseconds,
                    CpuMs = process.TotalProcessorTime.TotalMilliseconds - cpu,
                    AllocatedMB = (GC.GetTotalAllocatedBytes() - allocated) / 1048576d,
                    MaximumUiGapMs = gaps.Count == 0 ? elapsed.Elapsed.TotalMilliseconds : gaps.Max(),
                    UiGapsOver50Ms = gaps.Count(gap => gap > 50), UiGapsOver100Ms = gaps.Count(gap => gap > 100),
                    SidebarRebuilds = sidebarRebuilds - side, TimelineReplacements = timelineReplacements - timeline,
                    ToolbarState = after.State - toolbar.State, ToolbarLayout = after.Layout - toolbar.Layout,
                    ToolbarColumns = after.Columns - toolbar.Columns, Articles = model.ArticleItems.Count, Failure = failure
                });
                Save();
            }
        }

        async Task ScrollPage(string name, WebView2 view, bool native)
        {
            var core = view.CoreWebView2;
            await core.CallDevToolsProtocolMethodAsync("Performance.enable", "{}");
            async Task<Dictionary<string, double>> Metrics()
            {
                using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Performance.getMetrics", "{}"));
                return result.RootElement.GetProperty("metrics").EnumerateArray()
                    .ToDictionary(item => item.GetProperty("name").GetString()!, item => item.GetProperty("value").GetDouble());
            }
            using var tree = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
            string frameId = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
            using var world = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
                JsonSerializer.Serialize(new { frameId, worldName = "WinNewsWire.OpmlPerformance" })));
            int contextId = world.RootElement.GetProperty("executionContextId").GetInt32();
            async Task<JsonElement> Evaluate(string expression)
            {
                using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                    JsonSerializer.Serialize(new { contextId, expression, returnByValue = true, awaitPromise = true })));
                if (result.RootElement.TryGetProperty("exceptionDetails", out var error))
                    throw new InvalidOperationException("Page measurement failed: " + error);
                return result.RootElement.GetProperty("result").GetProperty("value").Clone();
            }
            var dimensions = await Evaluate("({width:innerWidth,height:innerHeight,max:Math.max(0,document.scrollingElement.scrollHeight-innerHeight),url:location.href})");
            Require(dimensions.GetProperty("max").GetDouble() > 200, "The selected webpage does not have enough scrollable content.");
            await Evaluate("window.scrollTo(0,0); true");
            await Task.Delay(250);
            await Evaluate(ScrollPerformanceStart);
            var beforeMetrics = await Metrics();
            try
            {
                await Measure(name, async () =>
                {
                    if (native)
                        await SendOwnedArticleWheelAsync(window, view,
                            dimensions.GetProperty("width").GetDouble() / 2, dimensions.GetProperty("height").GetDouble() / 2);
                    else
                        await core.CallDevToolsProtocolMethodAsync("Input.synthesizeScrollGesture", JsonSerializer.Serialize(new
                        {
                            x = dimensions.GetProperty("width").GetDouble() / 2,
                            y = dimensions.GetProperty("height").GetDouble() / 2,
                            yDistance = -1800, speed = 900, gestureSourceType = "mouse", preventFling = true
                        }));
                    await Task.Delay(150);
                });
            }
            finally
            {
                var result = await Evaluate(ScrollPerformanceStop);
                var afterMetrics = await Metrics();
                phases.Add(new { Name = name + "-frames", Page = dimensions.GetProperty("url").GetString(), Measurements = result,
                    ScriptMs = 1000 * (afterMetrics["ScriptDuration"] - beforeMetrics["ScriptDuration"]),
                    LayoutMs = 1000 * (afterMetrics["LayoutDuration"] - beforeMetrics["LayoutDuration"]),
                    StyleMs = 1000 * (afterMetrics["RecalcStyleDuration"] - beforeMetrics["RecalcStyleDuration"]),
                    BrowserTaskMs = 1000 * (afterMetrics["TaskDuration"] - beforeMetrics["TaskDuration"]) });
                Save();
                Require(result.GetProperty("scrollY").GetDouble() > 150, "Input did not scroll the displayed page.");
            }
        }

        async Task ScrollList(string name, ListView list)
        {
            var viewer = Descendants(list).OfType<ScrollViewer>().First();
            viewer.ChangeView(null, 0, null, true);
            await Task.Delay(100);
            double before = viewer.VerticalOffset;
            await Measure(name, () => SendOwnedArticleWheelAsync(window, list, list.ActualWidth / 2, list.ActualHeight / 2));
            phases.Add(new { Name = name + "-offset", Before = before, After = viewer.VerticalOffset,
                Extent = viewer.ScrollableHeight, RealizedRows = Descendants(list).OfType<ListViewItem>().Count() });
            Save();
            Require(viewer.VerticalOffset > before, "Native input did not scroll " + name);
        }

        ContentDialog? OpenDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(content.XamlRoot)
            .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault();

        model.SidebarRebuilt += SidebarChanged;
        model.PropertyChanged += ModelChanged;
        account.AccountStructureChanged += StructureChanged;
        try
        {
            window.Activate();
            double scale = content.XamlRoot.RasterizationScale;
            window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(original.Bounds with { Width = (int)Math.Ceiling(1400 * scale) }));
            AppDefaults.Shared.UseColumnLayout = false;
            AppDefaults.Shared.ArticleContentJavascriptEnabled = true;
            model.IsUnifiedLayout = false;
            model.IsSidebarVisible = true;
            model.IsReaderMode = false;
            model.SearchQuery = "";
            model.SelectedSidebarItem = model.SidebarItems.Single(item => ReferenceEquals(item.Tag, account));
            await model.FilterArticlesAsync();
            if (model.IsReadFiltered) await model.ToggleReadFilterCommand.ExecuteAsync(null);
            var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            bool progressVisible = false;
            await Measure("opml-import-ui-and-live-refresh", async () =>
            {
                var import = Dialogs.OpmlCommands.ImportFileAsync(file, content.XamlRoot);
                var watch = Stopwatch.StartNew();
                while (!import.IsCompleted)
                {
                    var dialog = OpenDialog();
                    if (dialog?.Title as string == "Importing Subscriptions" && dialog.IsLoaded) progressVisible = true;
                    if (dialog is not null && (dialog.Title as string)?.EndsWith("could not finish", StringComparison.Ordinal) == true)
                    {
                        string error = dialog.Content?.ToString() ?? "Import failed.";
                        dialog.Hide();
                        await import;
                        throw new InvalidOperationException(error);
                    }
                    if (watch.Elapsed > TimeSpan.FromMinutes(5))
                    {
                        if (dialog is not null) InvokeDialogButton(dialog, "CloseButton");
                        throw new TimeoutException("The OPML import exceeded five minutes.");
                    }
                    await Task.Delay(100);
                }
                await import;
                await model.ProbeDrainViewUpdatesAsync();
                await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            });
            var imported = expected.Select(item => account.ExistingFeedWithUrl(item.FeedUrl)
                ?? throw new InvalidOperationException("An imported subscription is missing.")).ToArray();
            Require(imported.All(feed => account.TopLevelFeeds.Contains(feed)), "A root subscription changed membership.");
            Require(imported.Zip(expected).All(pair => pair.Second.Title is null || pair.First.EditedName == pair.Second.Title),
                "Import lost an OPML custom title.");
            var exported = OpmlParser.Parse(new ParserData("export", System.Text.Encoding.UTF8.GetBytes(OpmlExporter.OPMLString(account, "Subscriptions"))));
            var exportedUrls = Feeds(exported.Children).Select(feed => feed.FeedUrl).ToHashSet(StringComparer.Ordinal);
            Require(expected.All(feed => exportedUrls.Contains(feed.FeedUrl)), "OPML export lost an imported subscription.");
            var savedOpml = account.OpmlFile ?? throw new InvalidOperationException("The local account has no OPML mirror.");
            var savedDocument = OpmlParser.Parse(new ParserData(savedOpml.FilePath, await File.ReadAllBytesAsync(savedOpml.FilePath)));
            Require(Feeds(savedDocument.Children).Select(feed => feed.FeedUrl).ToHashSet(StringComparer.Ordinal).SetEquals(exportedUrls),
                "The persisted subscription mirror does not match the account export.");
            int beforeReimport = account.FlattenedFeeds().Count();
            await Measure("idempotent-opml-reimport", async () =>
            {
                await Dialogs.OpmlCommands.ImportFileAsync(file, content.XamlRoot).WaitAsync(TimeSpan.FromMinutes(2));
                await model.ProbeDrainViewUpdatesAsync();
            });
            Require(account.FlattenedFeeds().Count() == beforeReimport
                && imported.All(feed => ReferenceEquals(feed, account.ExistingFeedWithUrl(feed.Url))), "Reimport duplicated or replaced feeds.");
            var articles = await account.Database.FetchArticlesAsync(imported.Select(feed => feed.FeedID));
            phases.Add(new { Name = "import-result", ProgressVisible = progressVisible, FeedCount = imported.Length,
                ArticleCount = articles.Count, StructureChanges = structureChanges,
                Responses = imported.GroupBy(feed => feed.LastResponseCode).Select(group => new { Status = group.Key, Count = group.Count() }).ToArray() });
            Save();
            Require(articles.Count > 0, "The imported feeds did not produce any articles; live UX could not be measured.");
            await SaveWindowScreenshotAsync(window, "opml-imported-profile");
            await ScrollList("imported-sidebar-native-wheel", (ListView)content.FindName("SidebarListView"));
            await ScrollList("imported-timeline-native-wheel", (ListView)content.FindName("ArticleListView"));
            var largest = articles.GroupBy(article => article.FeedID).MaxBy(group => group.Count())!;
            await Measure("largest-feed-with-read-articles", async () =>
            {
                model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, largest.Key);
                await model.FilterArticlesAsync();
                if (model.IsReadFiltered) await model.ToggleReadFilterCommand.ExecuteAsync(null);
                await model.WaitForCurrentTimelineAsync(CancellationToken.None);
                Require(model.ArticleItems.Count == largest.Count(), "The largest feed's timeline omitted stored articles.");
            });
            await ScrollList("largest-feed-timeline-native-wheel", (ListView)content.FindName("ArticleListView"));
            AppDefaults.Shared.UseColumnLayout = true;
            await Task.Delay(200);
            await ScrollList("largest-feed-column-native-wheel", (ListView)content.FindName("ArticleListView"));
            AppDefaults.Shared.UseColumnLayout = false;
            model.SelectedSidebarItem = model.SidebarItems.Single(item => ReferenceEquals(item.Tag, account));
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            var importedIds = articles.Select(article => article.ArticleID).ToHashSet(StringComparer.Ordinal);
            var candidates = model.ArticleItems.Where(item => item.AccountID == account.AccountID && importedIds.Contains(item.Id))
                .OrderByDescending(item => item.SourceArticle?.ContentHtml?.Length ?? 0).ToArray();
            Require(candidates.Length > 0, "No imported timeline articles were displayed.");
            await Measure("imported-article-selection", async () =>
            {
                foreach (var item in candidates.Take(6))
                {
                    model.SelectedArticle = item;
                    await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Imported article document");
                }
                model.SelectedArticle = candidates[0];
                await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Longest imported article");
            });
            await ScrollPage("imported-article-native-wheel", content.ProbeWebView, native: true);
            int successfulPages = 0;
            foreach (var item in candidates.Where(item => Uri.TryCreate(item.Link, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
                .GroupBy(item => new Uri(item.Link).Host)
                .Select(group => group.OrderByDescending(item => new Uri(item.Link).AbsolutePath == "/watch").First())
                .OrderByDescending(item => new Uri(item.Link).Host.Contains("youtube", StringComparison.OrdinalIgnoreCase)).Take(5))
            {
                model.SelectedArticle = item;
                await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Publisher page prerequisite");
                try
                {
                    await Measure("embedded-page-open", async () =>
                    {
                        await content.OpenSelectedInWebViewAsync();
                        await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Live publisher page");
                    });
                    content.ProbeWebPageView.CoreWebView2.IsMuted = true;
                    await Task.Delay(1000);
                    await ScrollPage("embedded-page-native-wheel", content.ProbeWebPageView, native: true);
                    await ScrollPage("embedded-page-warm-scroll", content.ProbeWebPageView, native: false);
                    browserAttempts.Add(new { Url = item.Link, Passed = true });
                    successfulPages++;
                }
                catch (Exception error) when (error is TimeoutException or COMException or InvalidOperationException)
                {
                    browserAttempts.Add(new { Url = item.Link, Passed = false, Error = error.ToString() });
                }
                finally
                {
                    Save();
                    await content.GoBackAsync();
                    await WaitFor(() => Task.FromResult(!content.ProbeShowingWebPage && content.ProbeArticleReady), "Return from publisher page");
                }
                if (successfulPages >= 3) break;
            }
            Require(successfulPages > 0, "No live embedded webpage could be scrolled; see the recorded attempts.");
            return new { ImportedFeeds = imported.Length, Articles = articles.Count, ProgressVisible = progressVisible,
                ExportPreserved = true, MirrorPersisted = true, IdempotentReimport = true,
                LivePagesScrolled = successfulPages, UserProfileUntouched = true };
        }
        finally
        {
            OpenDialog()?.Hide();
            model.SidebarRebuilt -= SidebarChanged;
            model.PropertyChanged -= ModelChanged;
            account.AccountStructureChanged -= StructureChanged;
            AppDefaults.Shared.UseColumnLayout = originalColumns;
            AppDefaults.Shared.ArticleContentJavascriptEnabled = originalScripts;
            await content.RestoreStateAsync(original);
            model.SearchQuery = originalSearch;
            window.AppWindow.MoveAndResize(new(original.Bounds.X, original.Bounds.Y, original.Bounds.Width, original.Bounds.Height));
            Save();
        }
    }
}
#endif
