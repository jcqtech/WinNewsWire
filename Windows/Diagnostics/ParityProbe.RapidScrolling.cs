#if PARITY_PROBE
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.Articles;
using WinRT.Interop;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private sealed record WheelWorkload(string Name, int IntervalMs, int Delta, int ReverseMs, int DurationMs,
        bool FocusTarget = true, int BurstMs = 0);
    private sealed record WheelObservation(double AtMs, int Delta);
    private sealed record ScrollObservation(double AtMs, double Offset);
    private sealed record VisibleRowObservation(double AtMs, int Index, double Top, double Height);
    private sealed record RapidWheelDelivery(List<WheelObservation> Events, double InputStoppedMs, double QuietEndedMs,
        int DriverProcessId, int[] GcCollections, int[] QuietGcCollections);
    private sealed record CapturedScrollFrame(double TimestampMs, double Offset);
    private sealed record CapturedScrollFrames(int ProcessId, List<CapturedScrollFrame> Frames);

    private static async Task<object> CheckRapidScrollingAsync(MainWindow window, string fixtureUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        bool controlledReader = Environment.GetCommandLineArgs().Contains("--controlled-scroll-pages");
        if (controlledReader)
        {
            var account = AccountManager.Shared.DefaultAccount!;
            var feed = account.FlattenedFeeds().First(item => !item.ReaderViewAlwaysEnabled);
            const string id = "winnewswire-inertial-reader-fixture";
            var status = new ArticleStatus(id, false, false, DateTime.UtcNow);
            string body = string.Concat(Enumerable.Range(0, 160).Select(index =>
                $"<p>Paragraph {index}: a stable rendered feed article for wheel inertia, coasting, and immediate direction changes. " +
                "The production reader renders this text while the populated RSS account refreshes normally.</p>"));
            var article = new Article(account.AccountID, id, feed.FeedID, id, "Interruptible wheel scrolling",
                body, null, null, fixtureUrl + "/scroll-article", null, null, null, DateTime.UtcNow, null, null, null, status);
            await account.Database.UpdateArticlesAsync([article]);
            await account.Database.MarkAsync([id], ArticleStatus.Key.Read, false);
            await model.FilterArticlesAsync();
            await model.ProbeDrainViewUpdatesAsync();
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == id);
            model.IsReaderMode = false;
            await model.PendingAutomaticReads;
        }
        var scale = content.XamlRoot.RasterizationScale;
        window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(window.CaptureState().Bounds with
        {
            Width = (int)Math.Ceiling(1351 * scale), Height = (int)Math.Ceiling(966 * scale)
        }));
        await model.ProbeDrainViewUpdatesAsync();
        await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Warm article before rapid scrolling");
        int initiallyFormattedRows = model.ArticleItems.Count(item => item.ProbeHasFormattedText);
        Require(initiallyFormattedRows < model.ArticleItems.Count / 4,
            "The virtualized timeline formatted most off-screen rows before scrolling.");
        using (var process = Process.GetCurrentProcess())
        {
            var xaml = process.Modules.Cast<ProcessModule>().Single(module =>
                module.ModuleName.Equals("Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase));
            var core = content.ProbeWebView.CoreWebView2;
            File.WriteAllText(Path.Combine(OutputDirectory, "runtime.json"), JsonSerializer.Serialize(new
            {
                ProcessID = process.Id, WinUI = xaml.FileName, WinUIVersion = xaml.FileVersionInfo.FileVersion,
                WebViewVersion = core.Environment.BrowserVersionString, core.Environment.UserDataFolder,
                SoftwareRasterizationEnabled = WebViewEnvironment.UsesSoftwareRasterization,
                DirectScrollingEnabled = WebViewEnvironment.UsesDirectScrolling,
                BrowserProcessID = core.BrowserProcessId
            }));
            Require(!xaml.FileName.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
                "Rapid scrolling must use the installed runtime used by the normal app.");
        }
        var samples = new List<object>();
        var arguments = Environment.GetCommandLineArgs();
        bool inertialScrolling = arguments.Contains("--inertial-scrolling");
        string frameHost = "";
        if (inertialScrolling)
        {
            int frameHostArgument = Array.IndexOf(arguments, "--frame-driver-host");
            if (frameHostArgument < 0 || frameHostArgument + 1 >= arguments.Length)
                throw new ArgumentException("Inertial scrolling requires the independent frame-driver host.");
            frameHost = arguments[frameHostArgument + 1];
            if (!Path.IsPathFullyQualified(frameHost) || !File.Exists(frameHost))
                throw new ArgumentException("The frame-driver host must be an existing absolute executable path.");
        }
        string host = arguments[Array.IndexOf(arguments, "--wheel-driver-host") + 1];
        using var driver = new Process
        {
            StartInfo = new()
            {
                FileName = host, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        bool driverStarted = false;
        Task<string> driverErrors = Task.FromResult("");
        var workloads = new List<WheelWorkload>
        {
            new WheelWorkload("rapid-notched", 8, 120, 120, 6120),
            new WheelWorkload("very-rapid-notched", 2, 120, 120, 6120),
            new WheelWorkload("rapid-high-resolution", 4, 30, 120, 6120),
            new WheelWorkload("rapid-unfocused", 8, 120, 120, 6120, FocusTarget: false),
            new WheelWorkload("slow-notched", 80, 120, 1000, 2040)
        };
        if (inertialScrolling)
            workloads.InsertRange(0, [
                new("coast-single-notch", 160, 120, 160, 2560),
                new("coast-reverse-notched", 16, 120, 200, 3200, BurstMs: 96),
                new("coast-reverse-high-resolution", 4, 30, 200, 3200, BurstMs: 96)
            ]);
        object? touchTransition = null;
        object? browserRouting = null;
        Task SaveAsync() => Task.Run(async () =>
        {
            await using var stream = new FileStream(Path.Combine(OutputDirectory, "rapid-scrolling.json"),
                FileMode.Create, FileAccess.Write, FileShare.Read, 65536, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, new
            {
                ProcessId = Environment.ProcessId,
                ControlledReaderDocument = controlledReader,
                InitiallyFormattedRows = initiallyFormattedRows,
                InertialScrolling = inertialScrolling,
                FocusedInertia = arguments.Contains("--inertia-focus"),
                FocusedRefresh = arguments.Contains("--refresh-focus"),
                MemoryPressureInjected = arguments.Contains("--refresh-memory-pressure"),
                GcLatency = GCSettings.LatencyMode.ToString(), ServerGc = GCSettings.IsServerGC,
                FullRuntimeStarted = AppService.Shared.ProbeStarted,
                PrecisionTouchpadVerified = false,
                TouchTransition = touchTransition,
                BrowserRouting = browserRouting,
                Samples = samples
            }, new JsonSerializerOptions { WriteIndented = true });
        });

        async Task Measure(string pane, FrameworkElement target, WheelWorkload workload, Func<Task>? refresh = null,
            FrameworkElement? inputTarget = null)
        {
            window.Activate();
            SetForegroundWindow(WindowNative.GetWindowHandle(window));
            await WaitFor(() => Task.FromResult(GetForegroundWindow() == WindowNative.GetWindowHandle(window)),
                "Rapid-scroll window foreground");
            (workload.FocusTarget ? target : (FrameworkElement)content.FindName("SidebarListView")).Focus(FocusState.Programmatic);
            var list = target as ListView;
            var scroll = list is not null
                ? Descendants(list).OfType<ScrollViewer>().Single(item => item.Name == "ScrollViewer") : null;
            var core = (target as WebView2)?.CoreWebView2;
            double scrollableHeight;
            int context = 0;
            async Task<JsonElement> Evaluate(string expression)
            {
                using var result = JsonDocument.Parse(await core!.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                    JsonSerializer.Serialize(new { contextId = context, expression, returnByValue = true, awaitPromise = true })));
                if (result.RootElement.TryGetProperty("exceptionDetails", out var error))
                    throw new InvalidOperationException("Rapid-scroll instrumentation failed: " + error);
                return result.RootElement.GetProperty("result").GetProperty("value").Clone();
            }
            if (core is not null)
            {
                using var tree = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
                string frameId = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
                using var world = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
                    JsonSerializer.Serialize(new { frameId, worldName = "WinNewsWire.RapidScroll" })));
                context = world.RootElement.GetProperty("executionContextId").GetInt32();
                var height = await Evaluate("document.scrollingElement.scrollHeight-innerHeight");
                scrollableHeight = height.GetDouble();
                Require(scrollableHeight > 4000, "The rapid-scroll browser fixture is too short.");
                await Evaluate("scrollTo({top:Math.min(12000,(document.scrollingElement.scrollHeight-innerHeight)/2),behavior:'instant'}); true");
            }
            else
            {
                if (scroll is null || scroll.ScrollableHeight <= 24000)
                    throw new InvalidOperationException("The native article list is not populated.");
                scrollableHeight = scroll.ScrollableHeight;
                scroll.ChangeView(null, 12000, null, disableAnimation: true);
            }
            await Task.Delay(500);
            JsonElement? browserHitTarget = core is null ? null : await Evaluate("""
                (() => {
                    const target=document.elementFromPoint(innerWidth/2,innerHeight/2);
                    return {tag:target?.tagName,source:target?.getAttribute("src"),width:innerWidth,height:innerHeight,
                        scriptEnabled:typeof window.__wnwArticleProcessed,frameCount:frames.length};
                })()
                """);
            var startedUtc = DateTime.UtcNow;
            long origin = Stopwatch.GetTimestamp();
            double Now() => Stopwatch.GetElapsedTime(origin).TotalMilliseconds;
            double browserOrigin = 0, clockError = 0, traceClockOffset = 0, traceClockError = 0;
            if (core is not null)
            {
                await core.CallDevToolsProtocolMethodAsync("Performance.enable", "{}");
                await Evaluate(RefreshScrollStart);
                await Evaluate("window.__refreshScroll.scrolls.push({at:0,y:scrollY,sequence:0}); true");
                double bestRoundTrip = double.PositiveInfinity;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    double before = Now();
                    var time = await Evaluate("performance.now()-window.__refreshScroll.start");
                    double after = Now();
                    if (after - before >= bestRoundTrip) continue;
                    bestRoundTrip = after - before;
                    browserOrigin = (before + after) / 2 - time.GetDouble();
                    clockError = bestRoundTrip / 2;
                }
                Require(clockError < 10, "The browser/host scroll clocks could not be aligned accurately.");
                bestRoundTrip = double.PositiveInfinity;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    double before = Now();
                    using var metrics = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Performance.getMetrics", "{}"));
                    double after = Now();
                    if (after - before >= bestRoundTrip) continue;
                    double timestamp = metrics.RootElement.GetProperty("metrics").EnumerateArray()
                        .Single(value => value.GetProperty("name").GetString() == "Timestamp").GetProperty("value").GetDouble() * 1000;
                    bestRoundTrip = after - before;
                    traceClockOffset = timestamp - (before + after) / 2;
                    traceClockError = bestRoundTrip / 2;
                }
                Require(traceClockError < 10, "The browser presentation clock could not be aligned accurately.");
            }
            var received = new List<WheelObservation>(1024);
            int readerScrollMessages = content.ProbeReaderScrollMessages;
            int memoryCleanups = AppService.Shared.ProbeMemoryCacheCleanups;
            var timeline = model.ArticleItems;
            int rowsAdded = 0, rowsRemoved = 0, rowsReplaced = 0, rowsMoved = 0, timelineResets = 0;
            double? firstTimelineChangeMs = null;
            void TimelineChanged(object? sender, NotifyCollectionChangedEventArgs args)
            {
                firstTimelineChangeMs ??= Now();
                switch (args.Action)
                {
                    case NotifyCollectionChangedAction.Add: rowsAdded += args.NewItems?.Count ?? 0; break;
                    case NotifyCollectionChangedAction.Remove: rowsRemoved += args.OldItems?.Count ?? 0; break;
                    case NotifyCollectionChangedAction.Replace: rowsReplaced += args.NewItems?.Count ?? 0; break;
                    case NotifyCollectionChangedAction.Move: rowsMoved += args.NewItems?.Count ?? 0; break;
                    case NotifyCollectionChangedAction.Reset: timelineResets++; break;
                }
            }
            var positions = new List<ScrollObservation>(1024);
            var compositedFrames = new List<ScrollObservation>(1024);
            Process? frameProcess = null;
            bool frameStarted = false;
            Task<string> frameErrors = Task.FromResult("");
            double browserEpoch = 0;
            var visibleRows = new List<VisibleRowObservation>(1024);
            var changes = new List<object>();
            var documentChanges = new List<object>();
            var navigations = new List<object>();
            string priorHtml = model.ArticleHtml;
            void ModelChanged(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName != nameof(model.ArticleHtml)) return;
                string after = model.ArticleHtml;
                int difference = 0;
                while (difference < Math.Min(priorHtml.Length, after.Length) && priorHtml[difference] == after[difference]) difference++;
                int start = Math.Max(0, difference - 100);
                documentChanges.Add(new
                {
                    AtMs = Now(), SelectedID = model.SelectedArticle?.Id, model.IsReaderMode,
                    Difference = difference, BeforeLength = priorHtml.Length, AfterLength = after.Length,
                    Before = priorHtml.Substring(start, Math.Min(400, priorHtml.Length - start)),
                    After = after.Substring(start, Math.Min(400, after.Length - start))
                });
                priorHtml = after;
            }
            void Navigating(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) =>
                navigations.Add(new { AtMs = Now(), args.NavigationId, args.IsUserInitiated,
                    Uri = args.Uri.StartsWith("data:", StringComparison.Ordinal) ? "<generated article>" : args.Uri });
            var gaps = new List<double>();
            var stalls = new List<object>();
            var lastPause = GC.GetTotalPauseDuration();
            var firstPause = lastPause;
            long firstAllocation = GC.GetTotalAllocatedBytes();
            var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var timer = window.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(16);
            double lastTick = Now();
            void Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
            {
                double now = Now();
                gaps.Add(now - lastTick);
                var pause = GC.GetTotalPauseDuration();
                if (now - lastTick > 45)
                {
                    var memory = GC.GetGCMemoryInfo();
                    stalls.Add(new { AtMs = now, GapMs = now - lastTick, GcPauseMs = (pause - lastPause).TotalMilliseconds,
                        memory.Generation, memory.Concurrent, memory.Compacted, HeapMB = memory.HeapSizeBytes / 1048576d });
                }
                lastPause = pause;
                lastTick = now;
            }
            PointerEventHandler wheel = (_, args) =>
                received.Add(new(Now(), args.GetCurrentPoint(target).Properties.MouseWheelDelta));
            void CaptureVisibleRow(object? sender, object args)
            {
                if (list?.ItemsPanelRoot is not ItemsStackPanel panel || panel.FirstVisibleIndex < 0
                    || list.ContainerFromIndex(panel.FirstVisibleIndex) is not FrameworkElement container
                    || container.ActualHeight <= 0) return;
                double top = container.TransformToVisual(list).TransformPoint(new()).Y;
                if (visibleRows.LastOrDefault() is { } previous && previous.Index == panel.FirstVisibleIndex
                    && Math.Abs(previous.Top - top) < .1 && previous.Height == container.ActualHeight) return;
                visibleRows.Add(new(Now(), panel.FirstVisibleIndex, top, container.ActualHeight));
            }
            void Changed(object? sender, ScrollViewerViewChangedEventArgs args)
            {
                positions.Add(new(Now(), scroll!.VerticalOffset));
                CaptureVisibleRow(sender, args);
            }
            void Changing(object? sender, ScrollViewerViewChangingEventArgs args) =>
                changes.Add(new { AtMs = Now(), Next = args.NextView.VerticalOffset, Final = args.FinalView.VerticalOffset, args.IsInertial });
            if (scroll is not null) target.AddHandler(UIElement.PointerWheelChangedEvent, wheel, true);
            model.PropertyChanged += ModelChanged;
            timeline.CollectionChanged += TimelineChanged;
            if (core is not null) core.NavigationStarting += Navigating;
            if (scroll is not null)
            {
                positions.Add(new(Now(), scroll.VerticalOffset));
                CaptureVisibleRow(null, EventArgs.Empty);
                scroll.ViewChanged += Changed;
                scroll.ViewChanging += Changing;
                CompositionTarget.Rendering += CaptureVisibleRow;
            }
            timer.Tick += Tick;
            if (scroll is not null) timer.Start();
            bool observing = true;
            void StopObserving()
            {
                if (!observing) return;
                observing = false;
                timer.Stop();
                timer.Tick -= Tick;
                if (scroll is not null)
                {
                    target.RemoveHandler(UIElement.PointerWheelChangedEvent, wheel);
                    scroll.ViewChanged -= Changed;
                    scroll.ViewChanging -= Changing;
                    CompositionTarget.Rendering -= CaptureVisibleRow;
                }
                model.PropertyChanged -= ModelChanged;
                timeline.CollectionChanged -= TimelineChanged;
                if (core is not null) core.NavigationStarting -= Navigating;
            }
            Task<RapidWheelDelivery>? input = null;
            Task refreshTask = Task.CompletedTask;
            double? refreshStart = null, refreshEnd = null;
            try
            {
                if (core is not null && inertialScrolling)
                {
                    browserEpoch = (await Evaluate("performance.timeOrigin+window.__refreshScroll.start")).GetDouble();
                    string portPath = Path.Combine(core.Environment.UserDataFolder, "EBWebView", "DevToolsActivePort");
                    if (!File.Exists(portPath)) portPath = Path.Combine(core.Environment.UserDataFolder, "DevToolsActivePort");
                    var portLines = await File.ReadAllLinesAsync(portPath);
                    if (portLines.Length == 0 || !int.TryParse(portLines[0], out int port) || port is < 1 or > 65535)
                        throw new InvalidDataException("The private browser debugging port is invalid.");
                    using var targetInfo = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.getTargetInfo", "{}"));
                    string targetId = targetInfo.RootElement.GetProperty("targetInfo").GetProperty("targetId").GetString()!;
                    frameProcess = new Process { StartInfo = new()
                    {
                        FileName = frameHost, UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                    } };
                    frameProcess.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CaptureScrollFrames.mjs"));
                    frameProcess.StartInfo.ArgumentList.Add($"ws://127.0.0.1:{port}/devtools/page/{targetId}");
                    frameStarted = frameProcess.Start();
                    Require(frameStarted, "The independent frame capture process did not start.");
                    frameErrors = frameProcess.StandardError.ReadToEndAsync();
                    string? frameReady = await frameProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Require(frameReady == "ready", "Independent frame capture did not initialize: " +
                        (frameProcess.HasExited ? await frameErrors : frameReady));
                }
                var inputStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                input = DriveRapidWheelAsync(driver, window, inputTarget ?? target, workload, origin, inputStarted);
                await inputStarted.Task;
                if (refresh is not null)
                {
                    await Task.Delay(400);
                    refreshStart = Now();
                    refreshTask = Refresh();
                    async Task Refresh()
                    {
                        try { await refresh(); }
                        finally { refreshEnd = Now(); }
                    }
                }
                var delivery = await input;
                if (frameProcess is not null && frameStarted)
                {
                    await frameProcess.StandardInput.WriteLineAsync("stop");
                    string frameJson = await frameProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))
                        ?? throw new InvalidOperationException("The independent frame process exited without results: " + await frameErrors);
                    var captured = JsonSerializer.Deserialize<CapturedScrollFrames>(frameJson)
                        ?? throw new InvalidDataException("The independent frame process returned no frames.");
                    Require(captured.ProcessId == frameProcess.Id && captured.ProcessId != Environment.ProcessId,
                        "Composited frames must be captured outside the app's managed runtime.");
                    compositedFrames.AddRange(captured.Frames.Select(value =>
                        new ScrollObservation(value.TimestampMs - browserEpoch + browserOrigin, value.Offset)));
                }
                JsonElement? browser = null;
                JsonElement? wheelBehavior = null;
                if (core is not null)
                {
                    Require(navigations.Count == 0, "The browser document was replaced while scrolling; see rapid-document-changes.json.");
                    browser = await Evaluate(RefreshScrollStop);
                    positions.AddRange(browser.Value.GetProperty("scrolls").EnumerateArray().Select(value =>
                        new ScrollObservation(value.GetProperty("at").GetDouble() + browserOrigin, value.GetProperty("y").GetDouble())));
                    if (inertialScrolling) wheelBehavior = await WebViewScrolling.ReadProbeStateAsync(core,
                        ReferenceEquals(target, content.ProbeWebView));
                }
                await refreshTask.WaitAsync(TimeSpan.FromMinutes(3));
                StopObserving();
                // Estimated virtual extents can change VerticalOffset without moving visible rows.
                var motion = core is null
                    ? visibleRows.Select(row => new ScrollObservation(row.AtMs, row.Index - row.Top / row.Height)).ToList()
                    : inertialScrolling ? compositedFrames : positions;
                double minimumMotion = core is null ? .005 : .5;
                var reversals = new List<object>();
                for (int index = 1; index < delivery.Events.Count; index++)
                {
                    var current = delivery.Events[index];
                    if (Math.Sign(current.Delta) == Math.Sign(delivery.Events[index - 1].Delta)) continue;
                    double priorInput = delivery.Events[index - 1].AtMs;
                    int oldDirection = Math.Sign(current.Delta);
                    var coasting = motion.Zip(motion.Skip(1))
                        .Where(pair => pair.Second.AtMs > priorInput + 32 && pair.Second.AtMs < current.AtMs
                            && oldDirection * (pair.Second.Offset - pair.First.Offset) > minimumMotion)
                        .Select(pair => pair.Second).ToArray();
                    double until = delivery.Events.Skip(index + 1).FirstOrDefault(item =>
                        Math.Sign(item.Delta) != Math.Sign(current.Delta))?.AtMs ?? delivery.InputStoppedMs + 100;
                    double? Response(IReadOnlyList<ScrollObservation> observed, double threshold)
                    {
                        var previous = observed.LastOrDefault(item => item.AtMs < current.AtMs);
                        foreach (var position in observed.Where(item => item.AtMs >= current.AtMs && item.AtMs < until))
                        {
                            if (previous is not null && -Math.Sign(current.Delta) * (position.Offset - previous.Offset) > threshold)
                                return position.AtMs - current.AtMs;
                            previous = position;
                        }
                        return null;
                    }
                    reversals.Add(new { AtMs = current.AtMs, ResponseMs = Response(motion, minimumMotion),
                        DocumentResponseMs = core is null ? null : Response(positions, .5),
                        InputGapMs = current.AtMs - priorInput,
                        CoastActiveBefore = coasting.Length >= 2 && current.AtMs - coasting[^1].AtMs < 75 });
                }
                var moving = motion.Zip(motion.Skip(1))
                    .Where(pair => Math.Abs(pair.Second.Offset - pair.First.Offset) > minimumMotion).Select(pair => pair.Second).ToArray();
                double lastMotion = moving.LastOrDefault()?.AtMs ?? 0;
                double tail = Math.Max(0, lastMotion - delivery.InputStoppedMs);
                double range = positions.Count == 0 ? 0 : positions.Max(item => item.Offset) - positions.Min(item => item.Offset);
                double? dispatchMaximum = received.Count == delivery.Events.Count
                    ? received.Zip(delivery.Events).Max(pair => pair.First.AtMs - pair.Second.AtMs) : null;
                samples.Add(new
                {
                    Pane = pane, Workload = workload, StartedUtc = startedUtc, OriginTicks = origin,
                    ClockFrequency = Stopwatch.Frequency,
                    delivery.Events, delivery.InputStoppedMs, delivery.QuietEndedMs,
                    ColumnLayout = Core.AppDefaults.Shared.UseColumnLayout,
                    delivery.DriverProcessId, DriverGcCollections = delivery.GcCollections,
                    DriverQuietGcCollections = delivery.QuietGcCollections,
                    TargetWidth = target.ActualWidth, TargetHeight = target.ActualHeight, ScrollableHeight = scrollableHeight,
                    NativeInertiaEnabled = scroll?.IsScrollInertiaEnabled,
                    RefreshStartMs = refreshStart, RefreshEndMs = refreshEnd, RefreshStillActive = !refreshTask.IsCompleted,
                    RefreshWorkerProcessID = AccountManager.Shared.DefaultAccount!.RefreshProcessId,
                    RefreshWorkerStatistics = refresh is null ? null : AccountManager.Shared.DefaultAccount!.LastRefreshStatistics,
                    NativeWheelReceived = received, NativeDispatchMaximumMs = dispatchMaximum,
                    ReaderScrollMessages = content.ProbeReaderScrollMessages - readerScrollMessages,
                    MemoryCacheCleanups = AppService.Shared.ProbeMemoryCacheCleanups - memoryCleanups,
                    TimelineChanges = new { Added = rowsAdded, Removed = rowsRemoved, Replaced = rowsReplaced,
                        Moved = rowsMoved, Resets = timelineResets, FirstAtMs = firstTimelineChangeMs,
                        CollectionReplaced = !ReferenceEquals(timeline, model.ArticleItems) },
                    Positions = positions, VisibleRows = visibleRows, NativeViewChanges = changes, Reversals = reversals,
                    CompositedFrames = compositedFrames,
                    FrameCaptureProcessId = frameProcess?.Id,
                    Browser = browser, WheelBehavior = wheelBehavior, ClockErrorMs = clockError, ScrollRange = range,
                    BrowserHitTarget = browserHitTarget,
                    InputStoppedTraceMs = core is null ? (double?)null : delivery.InputStoppedMs + traceClockOffset,
                    TraceClockErrorMs = traceClockError,
                    ScrollTailMs = tail, MotionAfter100Ms = moving.Count(item => item.AtMs > delivery.InputStoppedMs + 100),
                    UiStalls = stalls, GcPauseMs = (GC.GetTotalPauseDuration() - firstPause).TotalMilliseconds,
                    AllocatedMB = (GC.GetTotalAllocatedBytes() - firstAllocation) / 1048576d,
                    HeapMB = GC.GetGCMemoryInfo().HeapSizeBytes / 1048576d,
                    GcCollections = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - collections[index]).ToArray(),
                    UiMaximumGapMs = gaps.Count == 0 ? (double?)null : gaps.Max()
                });
                await SaveAsync();
                await model.ProbeDrainViewUpdatesAsync();
            }
            finally
            {
                StopObserving();
                if (frameProcess is not null && frameStarted)
                {
                    try
                    {
                        var remainingOutput = frameProcess.StandardOutput.ReadToEndAsync();
                        frameProcess.StandardInput.Close();
                        try { await frameProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                        catch (TimeoutException)
                        {
                            frameProcess.Kill(entireProcessTree: true);
                            await frameProcess.WaitForExitAsync();
                            throw new TimeoutException("The owned frame capture process did not stop.");
                        }
                        await remainingOutput;
                        if (frameProcess.ExitCode != 0)
                            throw new InvalidOperationException("Independent frame capture failed: " + await frameErrors);
                    }
                    finally { frameProcess.Dispose(); }
                }
                else frameProcess?.Dispose();
                if (navigations.Count > 0)
                    File.WriteAllText(Path.Combine(OutputDirectory, "rapid-document-changes.json"),
                        JsonSerializer.Serialize(new { Pane = pane, Workload = workload.Name, Navigations = navigations, Changes = documentChanges },
                            new JsonSerializerOptions { WriteIndented = true }));
                try { if (input is not null) await input; }
                finally { if (!refreshTask.IsCompleted) await refreshTask.WaitAsync(TimeSpan.FromMinutes(3)); }
            }
        }

        Task PrepareRefresh() => Task.Run(() =>
        {
            foreach (var feed in AccountManager.Shared.DefaultAccount!.FlattenedFeeds())
            {
                feed.LastCheckDate = DateTime.UtcNow.AddDays(-3);
                feed.CacheControlInfo = null;
                feed.ContentHash = null;
                feed.Settings.ConditionalGetInfo = null;
            }
        });
        Task Reload()
        {
            InvokeButton((Button)content.FindName("ReloadButton"));
            return model.ReloadFeedsCommand.ExecutionTask
                ?? throw new InvalidOperationException("The real refresh button did not start a refresh.");
        }
        var refreshWorkload = new WheelWorkload("rapid-refresh", 8, 120, 120, 12000);
        try
        {
            driverStarted = driver.Start();
            Require(driverStarted, "The independent wheel process did not start.");
            driverErrors = driver.StandardError.ReadToEndAsync();
            string? ready = await driver.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Require(ready == "ready", "The independent wheel process did not initialize: " + (driver.HasExited ? await driverErrors : ready));
            if (arguments.Contains("--inertia-focus"))
            {
                AppService.Shared.Start();
                await AppService.Shared.ProbeStartup.WaitAsync(TimeSpan.FromMinutes(3));
                await model.ProbeDrainViewUpdatesAsync();
                await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Focused inertia fixture");
                var focused = workloads.Where(item => item.Name.StartsWith("coast-", StringComparison.Ordinal)).ToArray();
                var focusedList = (ListView)content.FindName("ArticleListView");
                foreach (var workload in focused)
                    await Measure("article-list", focusedList, workload);
                var focusedScroll = Descendants(focusedList).OfType<ScrollViewer>().Single(item => item.Name == "ScrollViewer");
                var focusedBar = Descendants(focusedScroll).OfType<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>()
                    .Single(item => item.Orientation == Orientation.Vertical);
                VisualStateManager.GoToState(focusedScroll, "MouseIndicatorFull", false);
                VisualStateManager.GoToState(focusedBar, "ExpandedWithoutAnimation", false);
                await Task.Delay(200);
                await Measure("article-list", focusedList, new("coast-scrollbar", 16, 120, 200, 3200, BurstMs: 96), inputTarget: focusedBar);
                foreach (var workload in focused)
                    await Measure("article-reader", content.ProbeWebView, workload);
                Core.AppDefaults.Shared.UseColumnLayout = true;
                await WaitFor(() => Task.FromResult(((FrameworkElement)content.FindName("TimelineColumnsHeader")).Visibility == Visibility.Visible),
                    "Focused inertia column layout");
                var focusedColumn = new WheelWorkload("coast-column", 16, 120, 200, 3200, BurstMs: 96);
                await Measure("article-list", focusedList, focusedColumn);
                await Measure("article-reader", content.ProbeWebView, focusedColumn);
                Core.AppDefaults.Shared.UseColumnLayout = false;
                await WaitFor(() => Task.FromResult(((FrameworkElement)content.FindName("TimelineColumnsHeader")).Visibility == Visibility.Collapsed),
                    "Focused inertia standard layout");
                await content.ProbeOpenWebPageAsync(fixtureUrl + "/scroll-article");
                await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Focused browser inertia fixture");
                foreach (var workload in focused)
                    await Measure("embedded-webpage", content.ProbeWebPageView, workload);
                browserRouting = await CheckBrowserWheelRoutingAsync(driver, window, content.ProbeWebPageView);
                return new { FocusedInertiaDiagnosis = true };
            }
            await PrepareRefresh();
            var startupWorkload = refreshWorkload with { Name = "rapid-startup-refresh" };
            await CaptureBrowserTraceAsync(content.ProbeWebView.CoreWebView2, "rapid-reader-" + startupWorkload.Name,
                () => Measure("article-reader", content.ProbeWebView, startupWorkload, async () =>
                {
                    AppService.Shared.Start();
                    if (arguments.Contains("--refresh-memory-pressure"))
                    {
                        await Task.Delay(900);
                        AppService.Shared.ReleaseMemoryCaches();
                    }
                    await AppService.Shared.ProbeStartup;
                }));
            if (arguments.Contains("--refresh-focus"))
            {
                await PrepareRefresh();
                await Measure("article-list", (ListView)content.FindName("ArticleListView"), refreshWorkload, Reload);
                await PrepareRefresh();
                await Measure("article-reader", content.ProbeWebView, refreshWorkload, Reload);
                await content.ProbeOpenWebPageAsync(fixtureUrl + "/scroll-article");
                await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Refresh-scroll embedded webpage");
                await PrepareRefresh();
                await Measure("embedded-webpage", content.ProbeWebPageView, refreshWorkload, Reload);
                browserRouting = await CheckBrowserWheelRoutingAsync(driver, window, content.ProbeWebPageView);
                return new { FocusedRefreshDiagnosis = true };
            }
            foreach (var workload in workloads)
                await Measure("article-list", (ListView)content.FindName("ArticleListView"), workload);
            var list = (ListView)content.FindName("ArticleListView");
            var nativeScroll = Descendants(list).OfType<ScrollViewer>().Single(item => item.Name == "ScrollViewer");
            if (inertialScrolling)
            {
                var bar = Descendants(nativeScroll).OfType<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>()
                    .Single(item => item.Orientation == Orientation.Vertical);
                VisualStateManager.GoToState(nativeScroll, "MouseIndicatorFull", false);
                VisualStateManager.GoToState(bar, "ExpandedWithoutAnimation", false);
                await Task.Delay(200);
                await Measure("article-list", list, new("coast-scrollbar", 16, 120, 200, 3200, BurstMs: 96), inputTarget: bar);
            }
            var (_, pointer, originalPointer) = PrepareWheelInput(window, list);
            var touchOffsets = new List<double>();
            bool touchInertia = false;
            void TouchChanged(object? sender, ScrollViewerViewChangedEventArgs args) => touchOffsets.Add(nativeScroll.VerticalOffset);
            void TouchChanging(object? sender, ScrollViewerViewChangingEventArgs args) =>
                touchInertia |= args.IsInertial && nativeScroll.IsScrollInertiaEnabled;
            nativeScroll.ViewChanged += TouchChanged;
            nativeScroll.ViewChanging += TouchChanging;
            try
            {
                var selection = model.SelectedArticle;
                var touch = await DriveTwoContactTouchAsync(window, list, 2);
                await Task.Delay(350);
                bool enabledAfterTouch = nativeScroll.IsScrollInertiaEnabled;
                bool observedTouchInertia = touchInertia;
                double touchRange = touchOffsets.Count == 0 ? 0 : touchOffsets.Max() - touchOffsets.Min();
                await Measure("article-list", list, new("rapid-after-touch", 8, 120, 120, 6120));
                touchTransition = new
                {
                    NativeInput = touch, InertiaObserved = observedTouchInertia, EnabledAfterTouch = enabledAfterTouch,
                    EnabledAfterWheel = nativeScroll.IsScrollInertiaEnabled,
                    ScrollRange = touchRange
                };
                model.SelectedArticle = selection;
                await model.PendingAutomaticReads;
                await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Article after native touch scrolling");
                await SaveAsync();
            }
            finally
            {
                nativeScroll.ViewChanged -= TouchChanged;
                nativeScroll.ViewChanging -= TouchChanging;
                if (GetCursorPos(out var current) && current.X == pointer.X && current.Y == pointer.Y)
                    SetCursorPos(originalPointer.X, originalPointer.Y);
            }
            await PrepareRefresh();
            await Measure("article-list", (ListView)content.FindName("ArticleListView"), refreshWorkload, Reload);
            foreach (var workload in workloads)
                await CaptureBrowserTraceAsync(content.ProbeWebView.CoreWebView2, "rapid-reader-" + workload.Name,
                    () => Measure("article-reader", content.ProbeWebView, workload));
            await PrepareRefresh();
            await CaptureBrowserTraceAsync(content.ProbeWebView.CoreWebView2, "rapid-reader-" + refreshWorkload.Name,
                () => Measure("article-reader", content.ProbeWebView, refreshWorkload, Reload));
            Core.AppDefaults.Shared.UseColumnLayout = true;
            await WaitFor(() => Task.FromResult(((FrameworkElement)content.FindName("TimelineColumnsHeader")).Visibility == Visibility.Visible),
                "Column layout before rapid scrolling");
            var columnWorkload = new WheelWorkload("rapid-column", 8, 120, 120, 6120);
            await Measure("article-list", list, columnWorkload);
            await CaptureBrowserTraceAsync(content.ProbeWebView.CoreWebView2, "rapid-reader-" + columnWorkload.Name,
                () => Measure("article-reader", content.ProbeWebView, columnWorkload));
            Core.AppDefaults.Shared.UseColumnLayout = false;
            await WaitFor(() => Task.FromResult(((FrameworkElement)content.FindName("TimelineColumnsHeader")).Visibility == Visibility.Collapsed),
                "Restored standard layout");
            await content.ProbeOpenWebPageAsync(fixtureUrl + "/scroll-article");
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Rapid-scroll embedded webpage");
            foreach (var workload in workloads)
                await CaptureBrowserTraceAsync(content.ProbeWebPageView.CoreWebView2, "rapid-webpage-" + workload.Name,
                    () => Measure("embedded-webpage", content.ProbeWebPageView, workload));
            await PrepareRefresh();
            await CaptureBrowserTraceAsync(content.ProbeWebPageView.CoreWebView2, "rapid-webpage-" + refreshWorkload.Name,
                () => Measure("embedded-webpage", content.ProbeWebPageView, refreshWorkload, Reload));
            if (inertialScrolling)
                browserRouting = await CheckBrowserWheelRoutingAsync(driver, window, content.ProbeWebPageView);
            return new { FullRuntime = true, RapidDirectionChanges = true, IndependentNativeWheel = true, Samples = samples.Count };
        }
        finally
        {
            await SaveAsync();
            if (driverStarted)
            {
                driver.StandardInput.Close();
                try { await driver.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException)
                {
                    driver.Kill(entireProcessTree: true);
                    await driver.WaitForExitAsync();
                    throw new TimeoutException("The task-owned wheel process did not stop after its input pipe closed.");
                }
                await driverErrors;
            }
        }
    }

    private static async Task<RapidWheelDelivery> DriveRapidWheelAsync(Process driver, MainWindow window, FrameworkElement target,
        WheelWorkload workload, long origin, TaskCompletionSource started)
    {
        CapturePoint point = default;
        CapturePoint? original = null;
        try
        {
            var prepared = PrepareWheelInput(window, target);
            point = prepared.Point;
            original = prepared.Original;
            await driver.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                Handle = prepared.Handle.ToInt64(), ProcessId = Environment.ProcessId, point.X, point.Y,
                Origin = origin, Frequency = Stopwatch.Frequency,
                workload.IntervalMs, workload.Delta, workload.ReverseMs, workload.DurationMs, workload.BurstMs
            }));
            Require(await driver.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "started",
                "The independent wheel driver did not acknowledge the workload.");
            started.TrySetResult();
            string json = await driver.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(workload.DurationMs + 20000))
                ?? throw new InvalidOperationException("The independent wheel driver exited without results.");
            using var report = JsonDocument.Parse(json);
            if (report.RootElement.TryGetProperty("Error", out var error))
                throw new InvalidOperationException("Independent wheel input failed: " + error.GetString());
            var result = JsonSerializer.Deserialize<RapidWheelDelivery>(json)
                ?? throw new InvalidDataException("The independent wheel driver returned no delivery report.");
            Require(result.DriverProcessId == driver.Id && result.DriverProcessId != Environment.ProcessId,
                "Wheel input must originate outside the app's managed runtime.");
            return result;
        }
        catch (Exception error)
        {
            started.TrySetException(error);
            throw;
        }
        finally
        {
            if (original is { } saved && GetCursorPos(out var current) && current.X == point.X && current.Y == point.Y)
                SetCursorPos(saved.X, saved.Y);
        }
    }
}
#endif
