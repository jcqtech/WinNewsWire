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
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.AppShared;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinRT.Interop;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckTimelineSeparatorsAsync(MainWindow window)
    {
        const string key = "timelineShowSeparators";
        var model = window.ProbeContent.ViewModel;
        var list = (ListView)window.ProbeContent.FindName("ArticleListView");
        var previousItems = model.ArticleItems;
        var previousSelection = model.SelectedArticle;
        bool previousColumns = AppDefaults.Shared.UseColumnLayout;
        bool previousUnified = model.IsUnifiedLayout;
        bool previousSeparators = AppDefaults.Shared.GetValue(key, false);
        var preferences = new AppWindows.PreferencesWindow();
        var previousDisplay = AppDefaults.Shared.GetTimelineThumbnailsDisplay();
        Models.FeedItem Row(string id)
        {
            var article = new Article("account", id, "feed", id, "Separator " + id,
                "<p>Separator fixture.</p>", null, null, "https://separator.test/" + id,
                null, null, null, null, null, null, null, new ArticleStatus(id, true, false, DateTime.UnixEpoch));
            return new Models.FeedItem { Id = id, AccountID = article.AccountID, SourceArticle = article, IsRead = true };
        }
        var rows = new System.Collections.ObjectModel.ObservableCollection<Models.FeedItem>
            { Row("first"), Row("second") };
        Border? Separator(int index) => list.ContainerFromIndex(index) is DependencyObject container
            ? Descendants(container).OfType<Border>().SingleOrDefault(border => border.Name == "TimelineRowSeparator") : null;
        async Task SettleAsync()
        {
            list.UpdateLayout();
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Require(window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => settled.TrySetResult()),
                "The separator layout dispatcher was unavailable.");
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        try
        {
            AppDefaults.Shared.UseColumnLayout = false;
            AppDefaults.Shared.SetTimelineThumbnailsDisplay(TimelineThumbnailsDisplay.None);
            model.IsUnifiedLayout = false;
            model.SelectedArticle = null;
            model.ArticleItems = rows;
            await WaitFor(() => Task.FromResult(list.ContainerFromIndex(1) is not null), "Separator fixture rows");
            preferences.Activate();
            await WaitFor(() => Task.FromResult(((FrameworkElement)preferences.Content).IsLoaded), "Separator preferences");
            var toggle = Descendants(preferences.Content).OfType<ToggleSwitch>()
                .SingleOrDefault(control => control.Name == "TimelineShowSeparatorsToggle");
            Require(toggle is not null, "Preferences have no timeline separator control.");
            toggle!.IsOn = true;
            await SettleAsync();
            Require(new AppDefaults().GetValue(key, false), "The separator preference did not persist.");
            Require(Separator(0)?.Visibility == Visibility.Visible, "Enabled standard rows have no native separator.");
            Require(Separator(1)?.Visibility == Visibility.Collapsed, "The final row retained a separator below it.");
            rows.Add(Row("third"));
            await SettleAsync();
            Require(Separator(1)?.Visibility == Visibility.Visible && Separator(2)?.Visibility == Visibility.Collapsed,
                "Appending an article left the former final row's separator stale.");
            list.SelectedItem = rows[0];
            await SettleAsync();
            Require(Separator(0)?.Visibility == Visibility.Collapsed, "A selected row retained its separator.");
            list.SelectedItems.Clear();
            rows.RemoveAt(2);
            await SettleAsync();
            Require(Separator(0)?.Visibility == Visibility.Visible && Separator(1)?.Visibility == Visibility.Collapsed,
                "Removing the final article did not update the new final separator.");
            AppDefaults.Shared.UseColumnLayout = true;
            await SettleAsync();
            Require(!Descendants(list).OfType<Border>().Any(border => border.Name == "TimelineRowSeparator"
                && border.Visibility == Visibility.Visible), "Column layout displayed standard-row separators.");
            Require(!Descendants(list).OfType<Grid>().Any(grid => grid.Name == "TimelineColumnRow" && grid.BorderThickness.Bottom > 0),
                "Column layout retained a drawn row border.");
            AppDefaults.Shared.UseColumnLayout = false;
            await Task.Run(() => AppDefaults.Shared.SetValue(key, false));
            await SettleAsync();
            Require(!toggle.IsOn, "The separator preference control ignored a worker notification.");
            Require(!Descendants(list).OfType<Border>().Any(border => border.Name == "TimelineRowSeparator"
                && border.Visibility == Visibility.Visible), "Disabled separators remained visible.");
            return new { FinalRow = true, SelectedRow = true, Append = true, Remove = true, Columns = true, Preference = true };
        }
        finally
        {
            preferences.Close();
            AppDefaults.Shared.SetValue(key, previousSeparators);
            model.ArticleItems = previousItems;
            model.SelectedArticle = previousSelection;
            model.IsUnifiedLayout = previousUnified;
            AppDefaults.Shared.SetTimelineThumbnailsDisplay(previousDisplay);
            AppDefaults.Shared.UseColumnLayout = previousColumns;
            window.Activate();
        }
    }

    private static async Task<object?> CheckReaderResizeAnchorAsync(MainWindow window, bool fractional = false, bool gap = false)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var previousItems = model.ArticleItems;
        var previousSelection = model.SelectedArticle;
        bool previousUnified = model.IsUnifiedLayout;
        bool previousScripts = AppDefaults.Shared.ArticleContentJavascriptEnabled;
        double previousWidth = content.ProbeWebView.MaxWidth;
        var previousDisplay = AppDefaults.Shared.GetTimelineThumbnailsDisplay();
        string body = string.Concat(Enumerable.Range(0, 100).Select(index =>
            $"<p id='resize-paragraph-{index}'>Paragraph {index}. "
            + string.Concat(Enumerable.Repeat("Readable article text wraps differently when the reading pane changes width. ", 12))
            + "</p>"));
        var article = new Article("account", "resize-anchor", "feed", "resize-anchor", "Resize fixture",
            body, null, null, "https://resize-anchor.test/article", null, null, null,
            null, null, null, null, new ArticleStatus("resize-anchor", true, false, DateTime.UnixEpoch));
        var row = new Models.FeedItem
            { Id = article.ArticleID, AccountID = article.AccountID, SourceArticle = article, IsRead = true };
        try
        {
            model.SelectedArticle = null;
            model.IsUnifiedLayout = true;
            AppDefaults.Shared.ArticleContentJavascriptEnabled = false;
            AppDefaults.Shared.SetTimelineThumbnailsDisplay(TimelineThumbnailsDisplay.None);
            content.ProbeWebView.MaxWidth = 740;
            model.ArticleItems = new System.Collections.ObjectModel.ObservableCollection<Models.FeedItem> { row };
            model.SelectedArticle = row;
            await WaitFor(async () => content.ProbeArticleReady && content.ProbeReaderInteractionReady
                && await ScriptBool(content, "window.innerWidth > 700 && document.getElementById('resize-paragraph-40') !== null"),
                "Wide reader resize fixture");
            string distance = gap ? "-12" : fractional ? "rect.height / 2" : "5";
            string targetRect = "const rect = document.getElementById('resize-paragraph-40').getBoundingClientRect();";
            string atTarget = $"(() => {{{targetRect} return Math.abs(rect.top + {distance}) < 2;}})()";
            await content.ProbeWebView.ExecuteScriptAsync(
                $"(() => {{{targetRect} window.scrollTo(0, window.scrollY + rect.top + {distance});}})()");
            await WaitFor(() => ScriptBool(content, atTarget),
                "Reader starts within the target paragraph");
            if (gap)
                Require(await ScriptBool(content, """
                    (() => {
                        const hit = document.elementFromPoint(window.innerWidth / 2, 1);
                        return hit && hit.children.length > 0 && !Array.from(hit.childNodes).some(
                            node => node.nodeType === Node.TEXT_NODE && node.textContent.trim() !== "");
                    })()
                    """), "The gap fixture did not hit a whitespace-only container.");
            else
            {
                await content.ProbeWebView.ExecuteScriptAsync(
                    $"(() => {{{targetRect} window.__resizeFixtureFraction = -rect.top / rect.height;}})()");
                atTarget = $"(() => {{{targetRect} return Math.abs(rect.top + rect.height * window.__resizeFixtureFraction) < 2;}})()";
            }
            await Task.Delay(250);
            var anchors = new List<JsonElement> { await content.ProbeResizeAnchorStateAsync() };
            if (gap)
                Require(anchors[0].GetProperty("id").GetString() == "resize-paragraph-40",
                    "The reader recorded a whitespace-only container instead of the next paragraph.");
            foreach (int width in new[] { 420, 740 })
            {
                content.ProbeWebView.MaxWidth = width;
                await WaitFor(() => ScriptBool(content, $"Math.abs(window.innerWidth - {width}) < 3"),
                    "Reader host width changes");
                await WaitFor(async () => !(await content.ProbeResizeAnchorStateAsync()).GetProperty("changingSize").GetBoolean(),
                    "Reader resize restoration settles");
                anchors.Add(await content.ProbeResizeAnchorStateAsync());
                Require(await ScriptBool(content, atTarget),
                    "Reader resize lost the visible paragraph or its top offset: " + JsonSerializer.Serialize(anchors));
                await WaitFor(() => ScriptBool(content,
                    $"Math.abs(window.scrollY - {window.CaptureState().Detail.ScrollY.ToString(System.Globalization.CultureInfo.InvariantCulture)}) < 2"),
                    "Reader resize updates the saved scroll position");
            }
            return new { NarrowAndWide = true, ParagraphPreserved = true, OffsetPreserved = true, Fractional = fractional, Gap = gap, SavedPositionCurrent = true,
                PublisherScriptsDisabled = true, Anchors = anchors };
        }
        finally
        {
            content.ProbeWebView.MaxWidth = previousWidth;
            model.ArticleItems = previousItems;
            model.SelectedArticle = previousSelection;
            model.IsUnifiedLayout = previousUnified;
            AppDefaults.Shared.ArticleContentJavascriptEnabled = previousScripts;
            AppDefaults.Shared.SetTimelineThumbnailsDisplay(previousDisplay);
        }
    }

    private sealed record ScrollArticleFixture(string? Title, string Content, string Url, bool ReaderMode, int TextSize = 1);
    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollMouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInput { public uint Type; public ScrollMouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeKeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct ProbeInputData
    {
        [FieldOffset(0)] public ScrollMouseInput Mouse;
        [FieldOffset(0)] public ProbeKeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeNativeInput { public uint Type; public ProbeInputData Data; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, ScrollInput[] input, int size);
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendKeyboardInput(uint count, ProbeNativeInput[] input, int size);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(CapturePoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CapturePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    private static void SendOwnedWindowKey(MainWindow window, global::Windows.System.VirtualKey key)
    {
        Require(GetForegroundWindow() == WindowNative.GetWindowHandle(window),
            "The private probe lost foreground ownership; no keyboard input was sent.");
        var down = new ProbeNativeInput { Type = 1, Data = new() { Keyboard = new() { VirtualKey = (ushort)key } } };
        var up = new ProbeNativeInput { Type = 1, Data = new() { Keyboard = new() { VirtualKey = (ushort)key, Flags = 2 } } };
        Require(SendKeyboardInput(2, [down, up], Marshal.SizeOf<ProbeNativeInput>()) == 2, "The owned keyboard input was not delivered.");
    }

    private static async Task SendOwnedArticleWheelAsync(MainWindow window, FrameworkElement view, double x, double y)
    {
        var position = view.TransformToVisual((UIElement)window.Content).TransformPoint(new(x, y));
        var origin = new CapturePoint();
        var handle = WindowNative.GetWindowHandle(window);
        Require(ClientToScreen(handle, ref origin), "Could not locate the owned scrolling window.");
        var screen = new CapturePoint
        {
            X = origin.X + (int)Math.Round(position.X * view.XamlRoot.RasterizationScale),
            Y = origin.Y + (int)Math.Round(position.Y * view.XamlRoot.RasterizationScale)
        };
        Require(GetCursorPos(out var original), "Could not preserve the cursor position.");
        Require(GetForegroundWindow() == handle, "The private probe is not the foreground window; native input was not sent.");
        Require(SetCursorPos(screen.X, screen.Y), "Could not position the cursor in the owned article control.");
        try
        {
            for (int wheel = 0; wheel < 30; wheel++)
            {
                Require(GetForegroundWindow() == handle && GetCursorPos(out var current)
                    && current.X == screen.X && current.Y == screen.Y
                    && GetAncestor(WindowFromPoint(screen), 2) == handle,
                    "Focus or pointer ownership changed; the native scroll test stopped without further input.");
                var input = new ScrollInput { Mouse = new() { Data = unchecked((uint)-120), Flags = 0x0800 } };
                Require(SendInput(1, [input], Marshal.SizeOf<ScrollInput>()) == 1, "The owned wheel input was not delivered.");
                await Task.Delay(100);
            }
        }
        finally
        {
            if (GetCursorPos(out var current) && current.X == screen.X && current.Y == screen.Y)
                SetCursorPos(original.X, original.Y);
        }
    }

    private static async Task<object?> CheckArticleScrollingAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var originalSearch = model.SearchQuery;
        bool originalColumn = AppDefaults.Shared.UseColumnLayout;
        bool originalScripts = AppDefaults.Shared.ArticleContentJavascriptEnabled;
        int originalTextSize = AppDefaults.Shared.ArticleTextSizeRaw;
        var container = (Border)content.FindName("ContentContainer");
        var originalShadow = container.Shadow;
        var originalBackground = content.ProbeWebView.DefaultBackgroundColor;
        var arguments = Environment.GetCommandLineArgs();
        int fixtureArgument = Array.IndexOf(arguments, "--article-fixture");
        if (fixtureArgument == arguments.Length - 1)
            throw new InvalidDataException("The article fixture path is missing.");
        var fixture = fixtureArgument >= 0
            ? JsonSerializer.Deserialize<ScrollArticleFixture>(File.ReadAllText(arguments[fixtureArgument + 1]))
                ?? throw new InvalidDataException("The article fixture is empty.")
            : null;
        if (fixture is not null && (!Uri.TryCreate(fixture.Url, UriKind.Absolute, out var fixtureUrl)
            || fixtureUrl.Scheme is not ("http" or "https")))
            throw new InvalidDataException("The article fixture must identify an HTTP(S) page.");
        var account = Account.AccountManager.Shared.DefaultAccount!;
        var feed = account.AddFeed(serverUrl + "/article-scroll-feed", "Article scroll fixture");
        feed.FaviconUrl = serverUrl + "/icon.png";
        feed.HomePageUrl = serverUrl + "/";
        var now = DateTime.UtcNow;
        var body = "<div id='scroll-fixture'>" + string.Concat(Enumerable.Range(0, 180).Select(index =>
            $"<p><a href='{serverUrl}/section-{index}'>Section {index}: This is a representative article paragraph with enough readable text "
            + "to wrap naturally across the reading column. <em>Formatted text and links</em> exercise the same layout and hover behavior "
            + "as a feed article while scrolling, without a live website changing during the comparison.</a></p>"
            + (index % 12 == 0 ? $"<img src='{serverUrl}/icon.png' width='600' height='180' alt='Fixture image'>" : "")))
            + "</div>";
        if (fixture is not null) body = fixture.Content;
        await account.Database.UpdateArticlesAsync(Enumerable.Range(0, 2000).Select(index =>
        {
            string id = "article-scroll-" + index;
            return new Article(account.AccountID, id, feed.FeedID, id, index == 0 && fixture is not null ? fixture.Title : "Article scroll " + index,
                index == 0 ? body : "<p>Other timeline article</p>", null, null,
                index == 0 && fixture is not null ? fixture.Url : serverUrl + "/article", null, null, null,
                now.AddMinutes(-index), null, null, null, new ArticleStatus(id, true, false, now));
        }));
        var samples = new List<object>();
        try
        {
            AppDefaults.Shared.ArticleContentJavascriptEnabled = true;
            AppDefaults.Shared.ArticleTextSizeRaw = fixture?.TextSize ?? 1;
            model.IsReaderMode = false;
            model.IsUnifiedLayout = false;
            model.SearchQuery = "";
            double scale = content.XamlRoot.RasterizationScale;
            window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(original.Bounds with { Width = (int)Math.Ceiling(1280 * scale) }));
            model.BuildSidebar();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.ProbeDrainViewUpdatesAsync();
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "article-scroll-0");
            if (fixture?.ReaderMode == true)
            {
                model.IsReaderMode = true;
                await WaitFor(() => Task.FromResult(model.HasExtractedArticle), "Matching Reader View extraction");
            }
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Long article document");
            var core = content.ProbeWebView.CoreWebView2;
            await WaitFor(async () => await ScriptBool(content, "Array.from(document.images).every(image => image.complete)"), "Article images decoded");
            await core.CallDevToolsProtocolMethodAsync("Performance.enable", "{}");
            using var tree = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
            var frameId = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
            using var world = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
                JsonSerializer.Serialize(new { frameId, worldName = "WinNewsWire.ScrollPerformance" })));
            int context = world.RootElement.GetProperty("executionContextId").GetInt32();
            async Task<JsonElement> Evaluate(string expression)
            {
                using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                    JsonSerializer.Serialize(new { contextId = context, expression, returnByValue = true, awaitPromise = true })));
                if (result.RootElement.TryGetProperty("exceptionDetails", out var error))
                    throw new InvalidOperationException("Scrolling instrumentation failed: " + error);
                return result.RootElement.GetProperty("result").GetProperty("value").Clone();
            }
            async Task<Dictionary<string, double>> Metrics()
            {
                using var data = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Performance.getMetrics", "{}"));
                return data.RootElement.GetProperty("metrics").EnumerateArray()
                    .ToDictionary(entry => entry.GetProperty("name").GetString()!, entry => entry.GetProperty("value").GetDouble());
            }
            foreach (var variant in new (string Name, bool Column, bool OldWidth, bool Messages, bool Opaque, bool Shadow, bool NativeInput)[]
            {
                ("regular-native-cold", false, false, true, false, true, true),
                ("regular-current", false, false, true, false, true, false),
                ("regular-old-width", false, true, true, false, true, false),
                ("regular-no-host-messages", false, false, false, false, true, false),
                ("regular-opaque", false, false, true, true, true, false),
                ("regular-no-shadow", false, false, true, false, false, false),
                ("column-current", true, false, true, false, true, false),
                ("column-old-width", true, true, true, false, true, false),
                ("regular-current-warm", false, false, true, false, true, false),
                ("regular-native-wheel", false, false, true, false, true, true)
            })
            {
                AppDefaults.Shared.UseColumnLayout = variant.Column;
                container.Shadow = variant.Shadow ? originalShadow : null;
                content.ProbeWebView.DefaultBackgroundColor = variant.Opaque ? Microsoft.UI.Colors.White : originalBackground;
                content.ProbeEnableReaderMessages(variant.Messages);
                await Evaluate("document.body.style.maxWidth=" + JsonSerializer.Serialize(variant.OldWidth ? "44em" : "48em")
                    + ";document.body.style.paddingLeft=document.body.style.paddingRight="
                    + JsonSerializer.Serialize(variant.OldWidth ? "48px" : "40px") + ";window.scrollTo(0,0);true;");
                await Task.Delay(250);
                var point = await Evaluate("({x:Math.max(20,Math.min(innerWidth-20,document.getElementById('bodyContainer').getBoundingClientRect().left+80)),y:innerHeight/2,distance:Math.min(3600,document.documentElement.scrollHeight-innerHeight-10)})");
                double x = point.GetProperty("x").GetDouble(), y = point.GetProperty("y").GetDouble();
                double distance = point.GetProperty("distance").GetDouble();
                Require(distance >= 500, "The article fixture is too short for a meaningful scrolling measurement.");
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                    JsonSerializer.Serialize(new { type = "mouseMoved", x, y }));
                await Evaluate(ScrollPerformanceStart);
                var before = await Metrics();
                int scrollMessages = content.ProbeReaderScrollMessages, hoverMessages = content.ProbeReaderHoverMessages;
                long messageTicks = content.ProbeReaderMessageTicks;
                int statusUpdates = model.ProbeSectionStatusUpdates;
                var header = content.ProbeToolbarPerformance;
                using var process = Process.GetCurrentProcess();
                double cpu = process.TotalProcessorTime.TotalMilliseconds;
                long lastTick = Stopwatch.GetTimestamp();
                double maximumUiGap = 0;
                var pulse = window.DispatcherQueue.CreateTimer();
                pulse.Interval = TimeSpan.FromMilliseconds(16);
                pulse.IsRepeating = true;
                void Tick(DispatcherQueueTimer sender, object args)
                {
                    long tick = Stopwatch.GetTimestamp();
                    maximumUiGap = Math.Max(maximumUiGap, Stopwatch.GetElapsedTime(lastTick, tick).TotalMilliseconds);
                    lastTick = tick;
                }
                pulse.Tick += Tick;
                pulse.Start();
                var elapsed = Stopwatch.StartNew();
                try
                {
                    if (variant.NativeInput)
                    {
                        content.ProbeWebView.Focus(FocusState.Programmatic);
                        await SendOwnedArticleWheelAsync(window, content.ProbeWebView, x, y);
                    }
                    else await core.CallDevToolsProtocolMethodAsync("Input.synthesizeScrollGesture",
                            JsonSerializer.Serialize(new { x, y, yDistance = -distance, speed = 1200, gestureSourceType = "mouse", preventFling = true }));
                    await Task.Delay(300);
                }
                finally { pulse.Stop(); pulse.Tick -= Tick; }
                var browser = await Evaluate(ScrollPerformanceStop);
                var after = await Metrics();
                process.Refresh();
                Require(browser.GetProperty("scrollY").GetDouble() >= (variant.NativeInput ? 500 : distance * 0.85),
                    "The actual article did not scroll through the requested input.");
                if (variant.Messages)
                {
                    Require(content.ProbeReaderScrollMessages - scrollMessages <= Math.Ceiling(elapsed.Elapsed.TotalMilliseconds / 100) + 3,
                        "Article scrolling still sends a host notification for every frame.");
                    Require(model.ProbeSectionStatusUpdates == statusUpdates,
                        "Link hovering during scrolling retallied the entire timeline.");
                    Require(Math.Abs(window.CaptureState().Detail.ScrollY - browser.GetProperty("scrollY").GetDouble()) < 2,
                        "Scroll coalescing lost the final reading position.");
                }
                samples.Add(new
                {
                    variant.Name, ReaderView = model.HasExtractedArticle, RequestedDistance = distance, WallMs = elapsed.Elapsed.TotalMilliseconds,
                    HostCpuMs = process.TotalProcessorTime.TotalMilliseconds - cpu, MaximumUiGapMs = maximumUiGap,
                    ScrollMessages = content.ProbeReaderScrollMessages - scrollMessages,
                    HoverMessages = content.ProbeReaderHoverMessages - hoverMessages,
                    HostMessageMs = (content.ProbeReaderMessageTicks - messageTicks) * 1000d / Stopwatch.Frequency,
                    StatusRetallies = model.ProbeSectionStatusUpdates - statusUpdates,
                    HeaderLayouts = content.ProbeToolbarPerformance.Layout - header.Layout,
                    BrowserTaskMs = (after["TaskDuration"] - before["TaskDuration"]) * 1000,
                    BrowserScriptMs = (after["ScriptDuration"] - before["ScriptDuration"]) * 1000,
                    BrowserLayoutMs = (after["LayoutDuration"] - before["LayoutDuration"]) * 1000,
                    BrowserStyleMs = (after["RecalcStyleDuration"] - before["RecalcStyleDuration"]) * 1000,
                    Browser = browser
                });
                File.WriteAllText(Path.Combine(OutputDirectory, "article-scrolling.json"),
                    JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
            }
            content.ProbeEnableReaderMessages(true);
            await Evaluate("window.scrollTo(0,700);true;");
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "article-scroll-1");
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Article selected during pending scroll telemetry");
            await Task.Delay(150);
            Require(Math.Abs(window.CaptureState().Detail.ScrollY) < 2, "A late scroll update from the old article changed the new article's position.");
            await content.GoBackAsync();
            await WaitFor(async () => content.ProbeArticleReady && await ScriptBool(content, "Math.abs(window.scrollY-700)<2"),
                "Back after immediately leaving a scrolled article");
            await core.CallDevToolsProtocolMethodAsync("Performance.disable", "{}");
            return new { Scenarios = samples.Count, TrustedWheelInput = true, CoalescedTelemetry = true,
                NoHoverRetallies = true, FinalPositionPreserved = true, NoStaleScrollUpdate = true, ImmediateBackRestoresPosition = true };
        }
        finally
        {
            content.ProbeEnableReaderMessages(true);
            content.ProbeWebView.DefaultBackgroundColor = originalBackground;
            container.Shadow = originalShadow;
            AppDefaults.Shared.UseColumnLayout = originalColumn;
            AppDefaults.Shared.ArticleContentJavascriptEnabled = originalScripts;
            AppDefaults.Shared.ArticleTextSizeRaw = originalTextSize;
            account.RemoveFeed(feed);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            model.SearchQuery = originalSearch;
            window.AppWindow.MoveAndResize(new(original.Bounds.X, original.Bounds.Y, original.Bounds.Width, original.Bounds.Height));
            if (model.SelectedArticle is not null)
                await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Restored reader after scrolling measurements");
        }
    }

    private const string ScrollPerformanceStart = """
        (() => {
            globalThis.__scrollPerformance?.stop?.();
            const state = globalThis.__scrollPerformance = { frames: [], offsets: [], longTasks: [], wheelDelays: [], active: true };
            const wheel = event => state.wheelDelays.push(performance.now() - event.timeStamp);
            addEventListener("wheel", wheel, { passive: true });
            const observer = new PerformanceObserver(list => state.longTasks.push(...list.getEntries().map(entry => entry.duration)));
            observer.observe({ entryTypes: ["longtask"] });
            const tick = time => {
                state.frames.push(time);
                state.offsets.push(scrollY);
                if (state.active) state.frame = requestAnimationFrame(tick);
            };
            state.frame = requestAnimationFrame(tick);
            state.stop = () => {
                state.active = false;
                cancelAnimationFrame(state.frame);
                removeEventListener("wheel", wheel);
                observer.disconnect();
            };
            return true;
        })()
        """;

    private const string ScrollPerformanceStop = """
        (() => {
            const state = globalThis.__scrollPerformance;
            state.stop();
            const gaps = state.frames.slice(1).map((time, index) => time - state.frames[index]);
            const ordered = [...gaps].sort((a,b) => a-b);
            return {
                scrollY, viewportWidth: innerWidth, viewportHeight: innerHeight, frames: state.frames.length,
                frameMedianMs: ordered[Math.floor(ordered.length / 2)] ?? 0,
                frame95Ms: ordered[Math.floor(ordered.length * .95)] ?? 0,
                frameMaximumMs: Math.max(0,...gaps),
                framesOver33Ms: gaps.filter(gap => gap > 33.4).length,
                maximumWheelDelayMs: Math.max(0,...state.wheelDelays),
                longTasks: state.longTasks, frameGaps: gaps
            };
        })()
        """;
}
#endif
