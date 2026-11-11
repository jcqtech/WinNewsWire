#if PARITY_PROBE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinNewsWire.Account;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.Articles;
using WinNewsWire.Core;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    private static async Task<object?> CheckLongSessionAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var originalSearch = model.SearchQuery;
        bool originalColumn = AppDefaults.Shared.UseColumnLayout;
        bool originalScripts = AppDefaults.Shared.ArticleContentJavascriptEnabled;
        var account = AccountManager.Shared.DefaultAccount!;
        var feeds = Enumerable.Range(0, 4).Select(index => account.AddFeed(serverUrl + "/soak-feed-" + index, "Soak feed " + index)).ToArray();
        var now = DateTime.UtcNow;
        await account.Database.UpdateArticlesAsync(feeds.SelectMany((feed, group) => Enumerable.Range(0, 40).Select(index =>
        {
            string id = $"soak-{group}-{index}";
            string body = $"<div id='soak-body'><h2>Article {id}</h2>"
                + string.Concat(Enumerable.Repeat("<p>Long-session article content with ordinary text, emphasis, and a stable reader layout.</p>", 180))
                + "</div>";
            return new Article(account.AccountID, id, feed.FeedID, id, "Soak article " + id, body,
                null, null, serverUrl + "/navigation-first", null, null, null, now.AddMinutes(-index), null, null, null,
                new ArticleStatus(id, true, false, now));
        })));
        var samples = new List<object>();
        var scrollSamples = new List<object>();
        var weakWindows = new List<WeakReference>();
        var weakModels = new List<WeakReference>();
        var secondaryWindows = new List<WeakReference>();
        var navigationTimes = new List<double>();
        var scriptHeap = new List<double>();
        var nativeHistories = new List<int>();
        int CountCache(string field) => ((ICollection)(typeof(ArticleStringFormatter).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(ArticleStringFormatter.Shared) ?? throw new InvalidOperationException("Formatter cache is unavailable."))).Count;

        async Task Sample(string stage, int visits)
        {
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Settled long-session article");
            await content.ProbeWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("HeapProfiler.collectGarbage", "{}");
            for (int collection = 0; collection < 3; collection++)
            {
                await Task.Run(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                });
                await Task.Delay(150);
            }
            using var process = Process.GetCurrentProcess();
            using var heap = JsonDocument.Parse(await content.ProbeWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.getHeapUsage", "{}"));
            using var dom = JsonDocument.Parse(await content.ProbeWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Memory.getDOMCounters", "{}"));
            using var history = JsonDocument.Parse(await content.ProbeWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}"));
            int nativeHistory = history.RootElement.GetProperty("entries").GetArrayLength();
            nativeHistories.Add(nativeHistory);
            double jsHeap = heap.RootElement.GetProperty("usedSize").GetDouble();
            scriptHeap.Add(jsHeap);
            var browserProcesses = new List<object>();
            foreach (var item in content.ProbeWebView.CoreWebView2.Environment.GetProcessInfos())
            {
                try
                {
                    using var browser = Process.GetProcessById((int)item.ProcessId);
                    browserProcesses.Add(new { item.ProcessId, Kind = item.Kind.ToString(),
                        PrivateMB = browser.PrivateMemorySize64 / 1048576d, WorkingMB = browser.WorkingSet64 / 1048576d });
                }
                catch (ArgumentException) { }
            }
            samples.Add(new
            {
                Stage = stage, ProcessID = Environment.ProcessId, Visits = visits, ManagedMB = GC.GetTotalMemory(false) / 1048576d,
                HostPrivateMB = process.PrivateMemorySize64 / 1048576d, HostWorkingMB = process.WorkingSet64 / 1048576d,
                process.HandleCount, Threads = process.Threads.Count, AppHistory = content.ProbeBackHistoryCount,
                UserObjects = GetGuiResources(process.Handle, 1), GdiObjects = GetGuiResources(process.Handle, 0),
                NativeReaderHistory = nativeHistory, PendingBrowserTasks = content.ProbePendingBrowserTasks,
                JavascriptMB = jsHeap / 1048576d, Dom = dom.RootElement.Clone(), BrowserProcesses = browserProcesses,
                TitleCache = CountCache("_titleCache"), SummaryCache = CountCache("_summaryCache"),
                AttributedTitleCache = CountCache("_attributedTitleCache"),
                RecentNavigationMedianMs = navigationTimes.Count == 0 ? 0 : navigationTimes.TakeLast(25).Order().ElementAt(Math.Min(24, navigationTimes.Count - 1) / 2),
                RetainedClosedWindows = weakWindows.Count(reference => reference.IsAlive),
                RetainedClosedModels = weakModels.Count(reference => reference.IsAlive),
                RetainedSecondaryWindows = secondaryWindows.Count(reference => reference.IsAlive)
            });
            File.WriteAllText(Path.Combine(OutputDirectory, "long-session.json"),
                JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
            Require(content.ProbePendingBrowserTasks == 0, "Completed browser tasks accumulated during the long session.");
        }

        async Task SelectFeed(Feed feed)
        {
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
        }

        async Task ScrollSample(string stage)
        {
            var core = content.ProbeWebView.CoreWebView2;
            await core.ExecuteScriptAsync("window.scrollTo(0,0)");
            await Task.Delay(150);
            using var point = JsonDocument.Parse(await core.ExecuteScriptAsync("({x:innerWidth/2,y:innerHeight/2})"));
            await core.ExecuteScriptAsync(ScrollPerformanceStart);
            await core.CallDevToolsProtocolMethodAsync("Input.synthesizeScrollGesture",
                JsonSerializer.Serialize(new { x = point.RootElement.GetProperty("x").GetDouble(),
                    y = point.RootElement.GetProperty("y").GetDouble(), yDistance = -1800, speed = 1200,
                    gestureSourceType = "mouse", preventFling = true }));
            await Task.Delay(200);
            using var result = JsonDocument.Parse(await core.ExecuteScriptAsync(ScrollPerformanceStop));
            Require(result.RootElement.GetProperty("scrollY").GetDouble() >= 1700, "The long-session article did not actually scroll.");
            scrollSamples.Add(new { Stage = stage, Measurements = result.RootElement.Clone() });
            File.WriteAllText(Path.Combine(OutputDirectory, "long-session-scrolling.json"),
                JsonSerializer.Serialize(scrollSamples, new JsonSerializerOptions { WriteIndented = true }));
        }

        async Task CloseCompanion()
        {
            var other = App.OpenMainWindow();
            await WaitFor(() => Task.FromResult(other.ProbeContent.IsLoaded && other.ProbeContent.ViewModel.SidebarItems.Count > 0),
                "Soak companion window");
            weakWindows.Add(new(other));
            weakModels.Add(new(other.ProbeContent.ViewModel));
            await CloseWindowAsync(other);
        }

        async Task CloseSecondary(int kind)
        {
            Microsoft.UI.Xaml.Window other = kind switch
            {
                0 => new AppWindows.PreferencesWindow(),
                1 => new AppWindows.KeyboardShortcutsWindow(),
                2 => AppWindows.CurrentActivityWindow.Show(),
                3 => new AppWindows.ActivityLogWindow(),
                4 => AppWindows.AccountStatsWindow.Show(),
                _ => AppWindows.DinosaursWindow.Show()
            };
            secondaryWindows.Add(new(other));
            other.Activate();
            await WaitFor(() => Task.FromResult(other.Content is Microsoft.UI.Xaml.FrameworkElement { IsLoaded: true }), "Soak secondary window");
            foreach (var resize in Descendants(other.Content).OfType<Helpers.ColumnResizeThumb>())
            {
                var header = (Microsoft.UI.Xaml.Controls.Grid)Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(resize);
                Require(ReferenceEquals(resize.Column, header.ColumnDefinitions[Microsoft.UI.Xaml.Controls.Grid.GetColumn(resize)]),
                    "A secondary-window resize handle lost its named column binding.");
            }
            if (other is AppWindows.IAsyncWindowLifetime lifetime) await lifetime.PrepareToCloseAsync();
            other.Close();
        }

        try
        {
            model.IsReaderMode = false;
            AppDefaults.Shared.ArticleContentJavascriptEnabled = true;
            model.SearchQuery = "";
            model.BuildSidebar();
            await model.ProbeDrainViewUpdatesAsync();
            foreach (var feed in feeds)
            {
                await SelectFeed(feed);
                model.SelectedArticle = model.ArticleItems[0];
                await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Warm long-session article");
            }
            await Sample("warm", 0);
            await ScrollSample("warm");
            for (int batch = 0; batch < 4; batch++)
            {
                for (int visit = 0; visit < 75; visit++)
                {
                    if (visit % 15 == 0) await SelectFeed(feeds[(batch + visit / 15) % feeds.Length]);
                    var elapsed = Stopwatch.StartNew();
                    model.SelectedArticle = model.ArticleItems[(visit + batch) % model.ArticleItems.Count];
                    await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Soak article navigation");
                    navigationTimes.Add(elapsed.Elapsed.TotalMilliseconds);
                }
                await Sample("articles-" + (batch + 1), (batch + 1) * 75);
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    await content.OpenSelectedInWebViewAsync();
                    await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Soak webpage");
                    await content.GoBackAsync();
                    await WaitFor(() => Task.FromResult(content.ProbeArticleReady && !content.ProbeShowingWebPage), "Soak webpage Back");
                }
                await Sample("webpages-" + (batch + 1), (batch + 1) * 75);
                await CloseCompanion();
                await CloseCompanion();
                await CloseSecondary(batch * 2 % 6);
                await CloseSecondary((batch * 2 + 1) % 6);
                window.Activate();
                await Sample("batch-" + (batch + 1), (batch + 1) * 75);
                await ScrollSample("batch-" + (batch + 1));
            }
            AppRuntime.AppService.Shared.ReleaseMemoryCaches();
            await Sample("after-cache-release", 300);
            Require(weakModels.Count(reference => reference.IsAlive) == 0, "Closed main windows retained their view models.");
            Require(weakWindows.All(reference => !reference.IsAlive) && secondaryWindows.All(reference => !reference.IsAlive),
                "Closed windows still retain their native event owners.");
            Require(scriptHeap[^2] <= Math.Max(scriptHeap[1] * 2, scriptHeap[1] + 8 * 1048576),
                "Reader JavaScript retention continued growing across repeated visits.");
            return new { ArticleVisits = 300, WebPageRoundTrips = 12, ClosedWindows = weakWindows.Count,
                ClosedSecondaryWindows = secondaryWindows.Count, PendingTasksDrained = true,
                ClosedViewModelsReleased = true, NativeReaderHistoryMaximum = nativeHistories.Max() };
        }
        finally
        {
            foreach (var other in App.MainWindows.Where(other => !ReferenceEquals(other, window)).ToArray()) await App.CloseMainWindowAsync(other);
            AppDefaults.Shared.UseColumnLayout = originalColumn;
            AppDefaults.Shared.ArticleContentJavascriptEnabled = originalScripts;
            foreach (var feed in feeds) account.RemoveFeed(feed);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            model.SearchQuery = originalSearch;
            if (model.SelectedArticle is not null) await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Restored reader after soak");
        }
    }
}
#endif
