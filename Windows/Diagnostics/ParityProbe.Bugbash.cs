#if PARITY_PROBE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.Models;
using WinRT.Interop;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static object[] BugbashSubscriptions(Account.Account account)
        => account.FlattenedFeeds().OrderBy(feed => feed.Url).Select(feed => (object)new
        {
            feed.Url, Name = feed.NameForDisplay,
            Folders = account.Folders.Where(folder => folder.Feeds.Contains(feed)).Select(folder => folder.NameForDisplay).Order().ToArray()
        }).ToArray();

    private sealed record BugbashStatus(string ArticleID, bool Read, bool Starred);

    private static async Task<object?> CheckBugbashRestartAsync(MainWindow window)
    {
        var account = AccountManager.Shared.DefaultAccount;
        var expected = File.ReadAllText(Path.Combine(OutputDirectory, "bugbash-expected-subscriptions.json"));
        Require(expected == JsonSerializer.Serialize(BugbashSubscriptions(account)), "Restart lost a subscription, title, or folder membership.");
        var statuses = JsonSerializer.Deserialize<BugbashStatus[]>(File.ReadAllText(Path.Combine(OutputDirectory, "bugbash-expected-statuses.json")))!;
        var articles = (await account.Database.FetchArticlesByIDsAsync(statuses.Select(item => item.ArticleID))).ToDictionary(item => item.ArticleID);
        Require(statuses.Length > 0 && statuses.All(status => articles.TryGetValue(status.ArticleID, out var article)
            && article.Status.Read == status.Read && article.Status.Starred == status.Starred), "Restart lost saved reading status.");
        var state = (AppDefaults.Shared.GetValue<MainWindowState[]>(App.MainWindowStatesKey)
            ?? throw new InvalidDataException("The private profile has no saved window state.")).Single();
        window.Activate();
        await window.ContentInitialization;
        await window.ProbeContent.RestoreStateAsync(state);
        await WaitFor(() => Task.FromResult(window.ProbeContent.ProbeArticleReady), "Restarted article");
        var selected = window.ProbeContent.ViewModel.SelectedArticle;
        Require(selected is not null && selected.Id == state.Timeline.SelectedArticle?.ArticleID
            && statuses.Any(status => status.ArticleID == selected.Id && status.Read == selected.IsRead && status.Starred == selected.IsStarred),
            "Restoring the selected article changed its saved read/star state.");
        return new { Subscriptions = account.FlattenedFeeds().Count(), Statuses = statuses.Length, SelectedArticleRestored = true };
    }

    private static async Task<object?> CheckWindowResourcesAsync(MainWindow window)
    {
        window.Activate();
        await WaitFor(() => Task.FromResult(window.ProbeContent.IsLoaded && window.ProbeContent.ProbeWebViewReady), "Resource control window");
        var samples = new List<object>();
        var closed = new List<WeakReference>();
        int cycles = 0;
        async Task Sample(string stage)
        {
            window.Activate();
            await Task.Delay(1000);
            await Task.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            using var process = Process.GetCurrentProcess();
            samples.Add(new { Stage = stage, GdiObjects = GetGuiResources(process.Handle, 0), UserObjects = GetGuiResources(process.Handle, 1),
                process.HandleCount, PrivateMB = process.PrivateMemorySize64 / 1048576d, RetainedWindows = closed.Count(item => item.IsAlive) });
            File.WriteAllText(Path.Combine(OutputDirectory, "window-resource-controls.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        }
        async Task Cycle(string kind)
        {
            Window other = kind is "reader" or "reader-reset" ? App.OpenMainWindow()
                : new Window { Title = "Bugbash native resource control", Content = new Grid() };
            if (kind is "titlebar" or "titlebar-reset")
            {
                other.ExtendsContentIntoTitleBar = true;
                other.SetTitleBar((UIElement)other.Content);
                var input = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(other.AppWindow.Id);
                for (int index = 0; index < 64; index++)
                    input.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough,
                        [new global::Windows.Graphics.RectInt32(index, 0, 100, 40)]);
            }
            closed.Add(new(other));
            other.Activate();
            await WaitFor(() => Task.FromResult(other.Content is FrameworkElement { IsLoaded: true }), "Resource control presentation");
            void ResetTitleBar()
            {
                Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(other.AppWindow.Id)
                    .SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, []);
                other.SetTitleBar(null);
                other.ExtendsContentIntoTitleBar = false;
                other.AppWindow.TitleBar.ResetToDefault();
            }
            if (other is MainWindow main)
            {
                await WaitFor(() => Task.FromResult(main.ProbeContent.ProbeWebViewReady), "Resource control browser");
                closed.Add(new(main.ProbeContent));
                closed.Add(new(main.ProbeContent.ProbeWebView));
                if (kind == "reader-reset")
                {
                    await main.ProbeContent.ShutdownAsync();
                    ResetTitleBar();
                }
                await CloseWindowAsync(main);
            }
            else
            {
                if (kind == "titlebar-reset") ResetTitleBar();
                other.Close();
            }
            cycles++;
        }
        await Sample("start");
        foreach (string kind in new[] { "plain", "titlebar", "titlebar-reset", "reader", "reader-reset", "plain" })
        {
            for (int index = 0; index < 8; index++) await Cycle(kind);
            await Sample(kind);
        }
        await Sample("settled");
        Require(closed.All(item => !item.IsAlive), "The resource controls retained managed windows.");
        return new { WindowCycles = cycles, ClosedManagedWindowsReleased = true, Samples = samples };
    }

    private static ContentDialog? BugbashDialog(MainWindow window)
        => VisualTreeHelper.GetOpenPopupsForXamlRoot(window.ProbeContent.XamlRoot)
            .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault();

    private static async Task SelectBugbashRowAsync(ListView list, object item)
    {
        Require(list.Items.Contains(item), "The requested row is not in the visible list.");
        list.ScrollIntoView(item);
        await Task.Delay(200);
        await WaitFor(() => Task.FromResult(list.ContainerFromItem(item) is ListViewItem { IsLoaded: true, ActualHeight: > 0 }),
            "Realized selectable row");
        if (item is FeedItem)
        {
            list.SelectedItem = item;
            return;
        }
        var row = (ListViewItem)list.ContainerFromItem(item);
        await ClickElementAsync(row, () => Equals(list.SelectedItem, item), "Clicked list selection", 0.6);
    }

    private static async Task ClickElementAsync(FrameworkElement element, Func<bool> completed, string description,
        double horizontalFraction = 0.5)
    {
        var window = App.WindowForXamlRoot(element.XamlRoot) ?? throw new InvalidOperationException("The element's window is unavailable.");
        var handle = WindowNative.GetWindowHandle(window);
        window.Activate();
        SetForegroundWindow(handle);
        await WaitFor(() => Task.FromResult(GetForegroundWindow() == handle), "Owned pointer input");
        var position = element.TransformToVisual((UIElement)window.Content)
            .TransformPoint(new(element.ActualWidth * horizontalFraction, element.ActualHeight / 2));
        var origin = new CapturePoint();
        Require(ClientToScreen(handle, ref origin) && GetCursorPos(out _), "Could not locate the private control.");
        Require(GetCursorPos(out var original), "Could not save the pointer position.");
        var point = new CapturePoint { X = origin.X + (int)Math.Round(position.X * element.XamlRoot.RasterizationScale),
            Y = origin.Y + (int)Math.Round(position.Y * element.XamlRoot.RasterizationScale) };
        Require(SetCursorPos(point.X, point.Y), "Could not position the pointer on the private control.");
        await Task.Delay(100);
        try
        {
            Require(GetForegroundWindow() == handle && GetAncestor(WindowFromPoint(point), 2) == handle
                && GetCursorPos(out var current) && current.X == point.X && current.Y == point.Y,
                $"The private control lost input ownership: point={point.X},{point.Y}, expected={handle}, actual={GetAncestor(WindowFromPoint(point), 2)}.");
            var down = new ScrollInput { Mouse = new() { Flags = 0x0002 } };
            var up = new ScrollInput { Mouse = new() { Flags = 0x0004 } };
            uint sent = SendInput(2, [down, up], Marshal.SizeOf<ScrollInput>());
            if (sent == 1) Require(SendInput(1, [up], Marshal.SizeOf<ScrollInput>()) == 1, "The pointer release failed.");
            Require(sent == 2, "The private control click was not delivered.");
            await WaitFor(() => Task.FromResult(completed()), description);
        }
        finally
        {
            if (GetCursorPos(out var current) && current.X == point.X && current.Y == point.Y)
                SetCursorPos(original.X, original.Y);
        }
    }

    private static async Task RunBugbashAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var account = AccountManager.Shared.DefaultAccount;
        Require(AccountManager.Shared.Accounts.Count == 1 && account.Type == AccountType.OnMyMac,
            "The bugbash requires an isolated local-only profile, without remote credentials.");
        var args = Environment.GetCommandLineArgs();
        bool smoke = args.Contains("--bugbash-smoke");
        string fixture = args[Array.IndexOf(args, "--opml-fixture") + 1];
        var urls = XDocument.Load(fixture).Descendants("outline").Select(node => (string?)node.Attribute("xmlUrl"))
            .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        Require(urls.Length == 50 && urls.All(url => account.ExistingFeedWithUrl(url) is not null),
            "The bugbash requires all 50 supplied subscriptions.");
        foreach (var feed in account.FlattenedFeeds().Where(feed => !urls.Contains(feed.Url, StringComparer.Ordinal)).ToArray())
        {
            Require(Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri) && uri.IsLoopback,
                "The copied profile contains an unexpected subscription.");
            account.RemoveFeed(feed);
        }
        var counts = await account.Database.FetchArticleCountsAsync(account.FlattenedFeeds().Select(feed => feed.FeedID));
        Require(counts.TotalCount >= 12000, "The bugbash profile is not populated.");
        AppDefaults.Shared.NotificationsEnabled = false;
        window.Activate();
        await WaitFor(() => Task.FromResult(content.IsLoaded && content.ProbeWebViewReady), "Bugbash reader startup");
        double scale = content.XamlRoot.RasterizationScale;
        window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(window.CaptureState().Bounds with { Width = (int)Math.Ceiling(1350 * scale) }));
        AppService.Shared.Start();
        await AppService.Shared.ProbeStartup.WaitAsync(TimeSpan.FromMinutes(3));
        model.IsSidebarVisible = true;
        model.IsUnifiedLayout = false;
        model.IsReaderMode = false;
        model.IsSidebarReadFiltered = false;
        model.SearchQuery = "";
        model.BuildSidebar();
        await model.ProbeDrainViewUpdatesAsync();
        await Task.Delay(500);
        using (var process = Process.GetCurrentProcess())
        {
            var xaml = process.Modules.Cast<ProcessModule>().Single(module => module.ModuleName.Equals("Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase));
            if (args.Contains("--installed-runtime")) Require(!xaml.FileName.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
                "The bugbash must use the installed runtime used by the normal reader.");
            var core = content.ProbeWebView.CoreWebView2;
            File.WriteAllText(Path.Combine(OutputDirectory, "runtime.json"), JsonSerializer.Serialize(new
            {
                ProcessID = process.Id, WinUI = xaml.FileName, WinUIVersion = xaml.FileVersionInfo.FileVersion,
                BrowserVersion = core.Environment.BrowserVersionString, core.Environment.UserDataFolder,
                SoftwareRasterizationEnabled = WebViewEnvironment.UsesSoftwareRasterization,
                DirectScrollingEnabled = WebViewEnvironment.UsesDirectScrolling
            }));
        }
        var sidebar = SidebarList(content);
        var timeline = (ListView)content.FindName("ArticleListView");
        var phases = new List<object>();
        var pages = new List<object>();
        var resources = new List<object>();
        var readTimes = new List<double>();
        var retained = new List<WeakReference>();
        int visits = 0, feedVisits = 0, browserVisits = 0, mutations = 0;
        void Save() => File.WriteAllText(Path.Combine(OutputDirectory, "bugbash.json"), JsonSerializer.Serialize(new
        {
            ProcessID = Environment.ProcessId, Executable = Environment.ProcessPath,
            FullRuntimeStarted = AppService.Shared.ProbeStarted, InitialFeeds = urls.Length, InitialArticles = counts.TotalCount,
            ArticleVisits = visits, FeedVisits = feedVisits, BrowserVisits = browserVisits, StatusActions = mutations,
            NativeFilePickers = true, InputKind = "NativeSidebarClicksAndMouseWheelWithControlActions", PrecisionTouchpadVerified = false,
            ArticleReadyMs = readTimes, Phases = phases, Pages = pages, Resources = resources
        }, new JsonSerializerOptions { WriteIndented = true }));

        async Task Measure(string name, Func<Task<object?>> action)
        {
            await CheckAsync(name, async () =>
            {
                var watch = Stopwatch.StartNew();
                var gaps = new List<double>();
                var timer = window.DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(16);
                long last = Stopwatch.GetTimestamp();
                void Tick(DispatcherQueueTimer sender, object data)
                {
                    long now = Stopwatch.GetTimestamp();
                    gaps.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
                    last = now;
                }
                timer.Tick += Tick;
                timer.Start();
                try { return await action(); }
                catch
                {
                    await SaveWindowScreenshotAsync(window, "bugbash-failure-" + phases.Count);
                    throw;
                }
                finally
                {
                    timer.Stop();
                    timer.Tick -= Tick;
                    phases.Add(new { Name = name, DurationMs = watch.Elapsed.TotalMilliseconds,
                        UiMaximumGapMs = gaps.DefaultIfEmpty(0).Max(), UiGapsOver100Ms = gaps.Count(gap => gap > 100) });
                    Save();
                    BugbashDialog(window)?.Hide();
                    content.HideArticleFind();
                }
            });
        }

        async Task SelectFeed(Feed feed)
        {
            await model.ProbeDrainViewUpdatesAsync();
            var row = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID)
                ?? throw new InvalidOperationException("A subscribed feed is missing from the sidebar: " + feed.NameForDisplay);
            await SelectBugbashRowAsync(sidebar, row);
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            if (model.IsReadFiltered) await model.ToggleReadFilterCommand.ExecuteAsync(null);
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
        }

        async Task Read(FeedItem item)
        {
            var watch = Stopwatch.StartNew();
            await SelectBugbashRowAsync(timeline, item);
            await model.PendingAutomaticReads;
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady && !content.ProbeShowingWebPage
                && ReferenceEquals(model.SelectedArticle, item)), "Selected article document");
            visits++;
            readTimes.Add(watch.Elapsed.TotalMilliseconds);
        }

        async Task SampleResources(string stage)
        {
            await Task.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            await Task.Delay(150);
            using var process = Process.GetCurrentProcess();
            resources.Add(new { Stage = stage, ManagedMB = GC.GetTotalMemory(false) / 1048576d,
                PrivateMB = process.PrivateMemorySize64 / 1048576d, process.HandleCount,
                GdiObjects = GetGuiResources(process.Handle, 0), UserObjects = GetGuiResources(process.Handle, 1),
                RetainedClosedWindows = retained.Count(reference => reference.IsAlive),
                RetainedWindowTypes = retained.Select((reference, index) => new { Index = index, Type = reference.Target?.GetType().Name })
                    .Where(item => item.Type is not null).ToArray(),
                OpenMainWindows = App.MainWindows.Count, OpenUtilities = AppWindows.WindowThemeHelper.OpenSecondaryWindows.Count,
                content.ProbeBackHistoryCount });
            Save();
        }

        async Task BrowserRoundTrip(FeedItem item)
        {
            var open = (Button)content.FindName("OpenInWebViewButton");
            if (!content.CanOpenSelectedInWebView)
            {
                Require(!open.IsEnabled, "An article without a web link enabled Web View.");
                pages.Add(new { Url = item.Link, Ready = false, Skipped = "Article has no HTTP(S) link" });
                return;
            }
            await WaitFor(() => Task.FromResult(open.IsEnabled), "Enabled Web View action");
            string sentinel = Guid.NewGuid().ToString("N");
            await content.ProbeWebView.ExecuteScriptAsync("window.__bugbashReader=" + JsonSerializer.Serialize(sentinel));
            InvokeButton(open);
            var watch = Stopwatch.StartNew();
            var error = (InfoBar)content.FindName("WebPageError");
            while (!content.ProbeWebPageReady && !error.IsOpen && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100);
            bool ready = content.ProbeWebPageReady;
            JsonElement? scrolling = null;
            if (ready)
            {
                browserVisits++;
                var view = content.ProbeWebPageView;
                view.CoreWebView2.IsMuted = true;
                var layoutWait = Stopwatch.StartNew();
                while (layoutWait.Elapsed < TimeSpan.FromSeconds(5))
                {
                    if (JsonSerializer.Deserialize<double>(await view.ExecuteScriptAsync(
                        "(document.scrollingElement?.scrollHeight ?? 0) - innerHeight")) > 300) break;
                    await Task.Delay(100);
                }
                using var dimensions = JsonDocument.Parse(await view.ExecuteScriptAsync(RefreshScrollStart));
                if (dimensions.RootElement.GetProperty("max").GetDouble() > 300)
                {
                    window.Activate();
                    SetForegroundWindow(WindowNative.GetWindowHandle(window));
                    await WaitFor(() => Task.FromResult(GetForegroundWindow() == WindowNative.GetWindowHandle(window)), "Owned browser input");
                    await DriveIndependentWheelAsync(window, view, 3);
                    await Task.Delay(400);
                }
                using var result = JsonDocument.Parse(await view.ExecuteScriptAsync(RefreshScrollStop));
                scrolling = result.RootElement.Clone();
            }
            pages.Add(new { Url = item.Link, Ready = ready, Error = error.IsOpen ? error.Message : ready ? null : "Navigation exceeded 30 seconds",
                LoadMs = watch.Elapsed.TotalMilliseconds,
                Scrolled = scrolling?.GetProperty("scrollRange").GetDouble() > 0, Scrolling = scrolling });
            InvokeButton((Button)((FrameworkElement)window.Content).FindName("NavigateBackButton"));
            await WaitFor(() => Task.FromResult(!content.ProbeShowingWebPage && content.ProbeArticleReady), "Back from publisher");
            Require(ReferenceEquals(item, model.SelectedArticle)
                && await ScriptString(content, "window.__bugbashReader") == sentinel, "Publisher Back replaced the selected article or reader document.");
        }

        await SampleResources("startup");
        for (int round = 0; round < (smoke ? 1 : 6); round++)
        {
            int batch = round;
            await Measure("Bugbash reading and browsing round " + (batch + 1), async () =>
            {
                AppDefaults.Shared.UseColumnLayout = batch % 2 != 0;
                foreach (string url in urls.Skip(batch * 9).Take(smoke ? 3 : 9))
                {
                    var feed = account.ExistingFeedWithUrl(url)!;
                    await SelectFeed(feed);
                    feedVisits++;
                    var articles = model.ArticleItems.ToArray();
                    for (int index = 0; index < Math.Min(5, articles.Length); index++)
                    {
                        var item = articles[index * Math.Max(1, articles.Length / 6)];
                        await Read(item);
                        if (index == 0)
                        {
                            foreach (string buttonName in new[] { "ToolbarMarkReadButton", "StarButton" })
                                for (int toggle = 0; toggle < 2; toggle++)
                                {
                                    bool previous = buttonName == "StarButton" ? item.IsStarred : item.IsRead;
                                    InvokeButton((Button)content.FindName(buttonName));
                                    await WaitFor(() => Task.FromResult((buttonName == "StarButton" ? item.IsStarred : item.IsRead) != previous),
                                        "Reading-status toolbar action");
                                    mutations++;
                                }
                        }
                        if (index == 2 && feedVisits % 3 == 0) await BrowserRoundTrip(item);
                    }
                }
                Save();
                return new { ArticleVisits = visits, FeedVisits = feedVisits, BrowserVisits = browserVisits };
            });
            await SampleResources("reading-" + (batch + 1));
        }

        await Measure("Bugbash feed dialogs, rename, delete, undo and redo", async () =>
        {
            AppDefaults.Shared.UseColumnLayout = false;
            for (int index = 0; index < 3; index++)
            {
                string folderName = $"Bugbash folder {index} & friends";
                var createFolder = content.NewFolderAsync();
                await WaitFor(() => Task.FromResult(BugbashDialog(window)?.IsLoaded == true), "New Folder");
                var dialog = BugbashDialog(window)!;
                Descendants(dialog).OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "Folder name").Text = folderName;
                await WaitFor(() => Task.FromResult(dialog.IsPrimaryButtonEnabled
                    && Descendants(dialog).OfType<Button>().Single(button => button.Name == "PrimaryButton").IsEnabled), "Enabled New Folder action");
                InvokeDialogButton(dialog, "PrimaryButton");
                await createFolder;
                await model.ProbeDrainViewUpdatesAsync();
                var folder = account.Folders.Single(folder => folder.NameForDisplay == folderName);
                var folderRow = sidebar.Items.OfType<SidebarItem>().Single(item => ReferenceEquals(item.Tag, folder));
                await SelectBugbashRowAsync(sidebar, folderRow);
                string url = serverUrl + "/bugbash-feed-" + index;
                var add = content.NewFeedAsync(url, $"Bugbash feed {index} <edited>");
                await WaitFor(() => Task.FromResult(BugbashDialog(window)?.IsLoaded == true), "New Feed");
                dialog = BugbashDialog(window)!;
                Require(dialog.IsPrimaryButtonEnabled, "A valid feed URL disabled Add.");
                InvokeDialogButton(dialog, "PrimaryButton");
                await add;
                var feed = account.ExistingFeedWithUrl(url) ?? throw new InvalidOperationException("Add Feed did not create its subscription: " + model.StatusMessage);
                Require(folder.Feeds.Contains(feed), "Add Feed ignored the selected folder.");
                await SelectFeed(feed);
                Require(model.ArticleItems.Count == 25, "New Feed did not refresh its articles.");
                await Read(model.ArticleItems[0]);
                await content.RenameSelectedAsync();
                await WaitFor(() => Task.FromResult(Descendants(sidebar).OfType<TextBox>().Any(box => box.IsLoaded && box.Visibility == Visibility.Visible)),
                    "Inline feed rename");
                var row = (ListViewItem)sidebar.ContainerFromItem(model.SelectedSidebarItem);
                var name = Descendants(row).OfType<TextBox>().Single();
                name.Text = $"Renamed {index} & <feed>";
                InvokeButton(Descendants(row).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Confirm rename"));
                await WaitFor(() => Task.FromResult(feed.EditedName == $"Renamed {index} & <feed>"), "Committed feed rename");
                for (int confirmation = 0; confirmation < 2; confirmation++)
                {
                    var deleting = content.DeleteSelectedAsync();
                    await WaitFor(() => Task.FromResult(BugbashDialog(window)?.IsLoaded == true), "Feed deletion confirmation");
                    InvokeDialogButton(BugbashDialog(window)!, confirmation == 0 ? "CloseButton" : "PrimaryButton");
                    await deleting;
                    Require((account.ExistingFeedWithUrl(url) is not null) == (confirmation == 0), "Delete/Cancel changed the wrong subscription state.");
                }
                await model.UndoAsync();
                Require(folder.Feeds.Contains(feed), "Undo did not restore the folder membership.");
                await model.RedoAsync();
                Require(account.ExistingFeedWithUrl(url) is null, "Redo did not remove the subscription.");
                await model.UndoAsync();
                await SelectFeed(feed);
                var duplicate = content.NewFeedAsync(url);
                await WaitFor(() => Task.FromResult(BugbashDialog(window)?.IsLoaded == true), "Duplicate feed dialog");
                InvokeDialogButton(BugbashDialog(window)!, "PrimaryButton");
                await duplicate;
                Require(account.FlattenedFeeds().Count(item => item.Url == url) == 1
                    && model.StatusMessage.StartsWith("Could not add feed:", StringComparison.Ordinal), "Duplicate subscription was not explicitly rejected.");
                var invalid = content.NewFeedAsync("file:///C:/not-a-feed");
                await WaitFor(() => Task.FromResult(BugbashDialog(window)?.IsLoaded == true), "Invalid feed dialog");
                Require(!BugbashDialog(window)!.IsPrimaryButtonEnabled, "A non-web feed URL enabled Add.");
                InvokeDialogButton(BugbashDialog(window)!, "CloseButton");
                await invalid;
            }
            return new { AddedAndRenamed = 3, DeleteCancelUndoRedo = 3, DuplicateErrors = 3, InvalidUrlCancellations = 3 };
        });

        await Measure("Bugbash native OPML pickers and repeated round trips", async () =>
        {
            for (int index = 0; index < 3; index++)
            {
                string path = Path.Combine(OutputDirectory, $"subscriptions-{index}.opml");
                await BugbashPickerAsync(window, path, exporting: true);
                var exported = XDocument.Load(path).Descendants("outline").Where(node => node.Attribute("xmlUrl") is not null).ToArray();
                var current = account.FlattenedFeeds().ToArray();
                Require(exported.Length == current.Length && current.All(feed => exported.Any(node =>
                    (string?)node.Attribute("xmlUrl") == feed.Url && ((string?)node.Attribute("title") ?? (string?)node.Attribute("text")) == feed.NameForDisplay)),
                    "The saved OPML lost a subscription or edited title.");
                int folderCount = account.Folders.Count;
                await BugbashPickerAsync(window, path, exporting: false);
                Require(account.FlattenedFeeds().Count() == current.Length && account.Folders.Count == folderCount,
                    "Reimport duplicated subscriptions or folders.");
            }
            string malformed = Path.Combine(OutputDirectory, "malformed.opml");
            await File.WriteAllTextAsync(malformed, "<opml><body><outline");
            int before = account.FlattenedFeeds().Count();
            string? error = await BugbashPickerAsync(window, malformed, exporting: false, expectError: true);
            Require(!string.IsNullOrWhiteSpace(error) && account.FlattenedFeeds().Count() == before,
                "Malformed OPML did not report an error without changing subscriptions.");
            string cancelled = Path.Combine(OutputDirectory, "cancelled.opml");
            await BugbashPickerAsync(window, cancelled, exporting: true, cancel: true);
            Require(!File.Exists(cancelled), "Cancelling Export created a file.");
            await BugbashPickerAsync(window, fixture, exporting: false, cancel: true);
            Require(account.FlattenedFeeds().Count() == before, "Cancelling Import changed subscriptions.");
            return new { ActualExports = 3, ActualReimports = 3, MalformedError = error, PickerCancellations = 2 };
        });

        await Measure("Bugbash rapid navigation and repeated window lifetime", async () =>
        {
            using var process = Process.GetCurrentProcess();
            uint initialGdi = GetGuiResources(process.Handle, 0);
            var feed = account.FlattenedFeeds().First(feed => feed.Url.StartsWith(serverUrl + "/bugbash-feed-", StringComparison.Ordinal));
            await SelectFeed(feed);
            async Task CloseCompanion(int round)
            {
                var other = App.OpenMainWindow();
                retained.Add(new(other));
                await WaitFor(() => Task.FromResult(other.ProbeContent.ProbeWebViewReady && other.ProbeContent.IsLoaded), "New reader window");
                var heldFind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    if (round == 0)
                    {
                        var otherContent = other.ProbeContent;
                        var otherModel = otherContent.ViewModel;
                        otherModel.SelectedSidebarItem = otherModel.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
                        await otherModel.FilterArticlesAsync();
                        otherModel.SelectedArticle = otherModel.ArticleItems[0];
                        await WaitFor(() => Task.FromResult(otherContent.ProbeArticleReady), "Pending-search companion article");
                        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        otherContent.ProbeFindStart = (_, _) => { started.TrySetResult(); return heldFind.Task; };
                        await otherContent.ShowArticleFindAsync();
                        ((TextBox)otherContent.FindName("ArticleFindText")).Text = "held search";
                        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    await CloseWindowAsync(other).WaitAsync(TimeSpan.FromSeconds(5));
                    Require(!heldFind.Task.IsCompleted, "Window close depended on the held native search completing.");
                }
                finally
                {
                    heldFind.TrySetResult();
                    other.ProbeContent.ProbeFindStart = null;
                }
            }
            async Task CloseUtility(int round)
            {
                Window utility = (round % 3) switch
                {
                    0 => new AppWindows.PreferencesWindow(),
                    1 => new AppWindows.KeyboardShortcutsWindow(),
                    _ => AppWindows.CurrentActivityWindow.Show()
                };
                retained.Add(new(utility));
                utility.Activate();
                await WaitFor(() => Task.FromResult(utility.Content is FrameworkElement { IsLoaded: true }), "Utility window");
                if (utility is AppWindows.IAsyncWindowLifetime lifetime) await lifetime.PrepareToCloseAsync();
                utility.Close();
            }
            for (int round = 0; round < 10; round++)
            {
                for (int index = 0; index < 20; index++) timeline.SelectedIndex = index;
                await WaitFor(() => Task.FromResult(content.ProbeArticleReady && model.SelectedArticle == model.ArticleItems[19]),
                    "Latest rapid-navigation article");
                await CloseCompanion(round);
                await CloseUtility(round);
                window.Activate();
            }
            await SampleResources("closed-20-windows");
            await Task.Delay(500);
            await SampleResources("settled-closed-windows");
            Require(retained.All(reference => !reference.IsAlive), "The repeated windows retained managed owners after closing.");
            uint finalGdi = GetGuiResources(process.Handle, 0);
            Require(finalGdi <= initialGdi + 32, $"Repeated reader windows leaked native graphics objects: {initialGdi} -> {finalGdi}.");
            await Read(model.ArticleItems[0]);
            await SaveWindowScreenshotAsync(window, "bugbash-final-reader");
            return new { RapidSelections = 200, ClosedMainWindows = 10, ClosedUtilityWindows = 10, InitialGdi = initialGdi,
                FinalGdi = finalGdi, CloseCancelsPendingFind = true };
        });
        await SampleResources("complete");
        if (model.SelectedArticle is { } selected)
        {
            if (selected.IsRead) InvokeButton((Button)content.FindName("ToolbarMarkReadButton"));
            if (!selected.IsStarred) InvokeButton((Button)content.FindName("StarButton"));
            await WaitFor(() => Task.FromResult(!selected.IsRead && selected.IsStarred), "Saved unread/starred selection");
        }
        var statuses = await account.Database.FetchArticlesByIDsAsync(model.ArticleItems.Take(20).Select(item => item.Id));
        File.WriteAllText(Path.Combine(OutputDirectory, "bugbash-expected-statuses.json"),
            JsonSerializer.Serialize(statuses.Select(article => new BugbashStatus(article.ArticleID, article.Status.Read, article.Status.Starred))));
        File.WriteAllText(Path.Combine(OutputDirectory, "bugbash-expected-subscriptions.json"), JsonSerializer.Serialize(BugbashSubscriptions(account)));
        Save();
    }

    private static async Task<string?> BugbashPickerAsync(MainWindow window, string file, bool exporting,
        bool expectError = false, bool cancel = false)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-NoProfile", "-STA", "-File", Path.GetFullPath(@"Windows\Diagnostics\DriveFilePicker.ps1"),
            "-OwnerWindow", WindowNative.GetWindowHandle(window).ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), "-FilePath", file })
            start.ArgumentList.Add(argument);
        if (cancel) start.ArgumentList.Add("-Cancel");
        using var driver = Process.Start(start) ?? throw new InvalidOperationException("Could not start the owned file-picker driver.");
        var output = driver.StandardOutput.ReadToEndAsync();
        var errors = driver.StandardError.ReadToEndAsync();
        var operation = exporting
            ? Dialogs.OpmlCommands.ExportAsync(WindowNative.GetWindowHandle(window), window.ProbeContent.XamlRoot)
            : Dialogs.OpmlCommands.ImportAsync(WindowNative.GetWindowHandle(window), window.ProbeContent.XamlRoot);
        string? reportedError = null;
        var pickerExceptions = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void FirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is COMException or FileNotFoundException)
                pickerExceptions.Enqueue(args.Exception.ToString());
        }
        AppDomain.CurrentDomain.FirstChanceException += FirstChance;
        var watch = Stopwatch.StartNew();
        try
        {
            while (!operation.IsCompleted || !driver.HasExited)
            {
                if (BugbashDialog(window) is { IsLoaded: true } dialog
                    && (dialog.Title as string)?.EndsWith("could not finish", StringComparison.Ordinal) == true)
                {
                    reportedError = dialog.Content?.ToString();
                    InvokeDialogButton(dialog, "CloseButton");
                }
                if (driver.HasExited && driver.ExitCode != 0)
                    throw new InvalidOperationException("Native picker automation failed: " + await errors);
                if (watch.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("The OPML user action did not finish.");
                await Task.Delay(100);
            }
            await operation;
            Require(driver.ExitCode == 0, "Native picker automation failed: " + await errors);
            Require(expectError == (reportedError is not null), "Unexpected OPML error outcome: " + reportedError);
            File.AppendAllText(Path.Combine(OutputDirectory, "file-pickers.log"),
                $"{(exporting ? "Export" : "Import")} {Path.GetFileName(file)}: {await output}");
            return reportedError;
        }
        catch
        {
            if (!operation.IsCompleted)
            {
                if (!driver.HasExited) { driver.Kill(); await driver.WaitForExitAsync(); }
                if (!cancel) start.ArgumentList.Add("-Cancel");
                using var cleanup = Process.Start(start) ?? throw new InvalidOperationException("Could not dismiss the owned file picker.");
                await cleanup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
                Require(cleanup.ExitCode == 0, "Could not dismiss the owned file picker after a diagnostic failure.");
                await operation.WaitAsync(TimeSpan.FromSeconds(30));
            }
            throw;
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= FirstChance;
            File.WriteAllText(Path.Combine(OutputDirectory, Path.GetFileName(file) + "-picker-errors.json"), JsonSerializer.Serialize(pickerExceptions.ToArray()));
            if (!driver.HasExited) { driver.Kill(); await driver.WaitForExitAsync(); }
        }
    }
}
#endif
