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
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32.SafeHandles;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.Core;
using WinNewsWire.Parsers;
using WinRT.Interop;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    private const string RefreshScrollStart = """
        (() => {
          const state = window.__refreshScroll = {start:performance.now(),sequence:0,frames:[],wheels:[],touches:[],scrolls:[],tasks:[],running:true,last:performance.now()};
          state.wheel=e=>state.wheels.push({at:performance.now()-state.start,delay:Math.max(0,performance.now()-e.timeStamp),
            delta:e.deltaY,deltaX:e.deltaX,deltaMode:e.deltaMode,ctrl:e.ctrlKey,sequence:++state.sequence});
          state.touch=e=>state.touches.push({at:performance.now()-state.start,type:e.type,count:e.touches.length,trusted:e.isTrusted});
          state.scroll=()=>state.scrolls.push({at:performance.now()-state.start,y:scrollY,sequence:++state.sequence});
          addEventListener('wheel',state.wheel,{passive:true,capture:true});
          for(const type of ['touchstart','touchmove','touchend','touchcancel']) addEventListener(type,state.touch,{passive:true,capture:true});
          addEventListener('scroll',state.scroll,{passive:true});
          state.observer=new PerformanceObserver(list=>state.tasks.push(...list.getEntries().map(e=>({at:e.startTime-state.start,duration:e.duration}))));
          state.observer.observe({type:'longtask',buffered:false});
          const frame=now=>{if(!state.running)return;state.frames.push({at:now-state.start,gap:now-state.last});state.last=now;state.raf=requestAnimationFrame(frame)};
          state.raf=requestAnimationFrame(frame);
          return {width:innerWidth,height:innerHeight,max:document.scrollingElement.scrollHeight-innerHeight,url:location.href};
        })()
        """;
    private const string RefreshScrollStop = """
        (() => {
          const state=window.__refreshScroll;
          state.running=false;cancelAnimationFrame(state.raf);
          removeEventListener('wheel',state.wheel,true);removeEventListener('scroll',state.scroll);
          for(const type of ['touchstart','touchmove','touchend','touchcancel']) removeEventListener(type,state.touch,true);
          state.tasks.push(...state.observer.takeRecords().map(e=>({at:e.startTime-state.start,duration:e.duration})));
          state.observer.disconnect();
          const percentile=(values,p)=>{values.sort((a,b)=>a-b);return values.length?values[Math.min(values.length-1,Math.ceil(values.length*p)-1)]:0};
          const frames=state.frames.map(x=>x.gap), wheels=state.wheels.map(x=>x.delay);
          const reversals=[];
          let direction=0;
          for (const event of state.wheels) {
            if (event.ctrl || !event.delta) continue;
            const next=Math.sign(event.delta);
            if (direction && next!==direction) {
              let previous=state.scrolls.filter(s=>s.sequence<event.sequence).at(-1);
              let response=null;
              if (previous) for (const scroll of state.scrolls.filter(s=>s.sequence>event.sequence)) {
                if (next*(scroll.y-previous.y)>.1) { response=scroll.at-event.at; break; }
                previous=scroll;
              }
              reversals.push({at:event.at,direction:next,responseMs:response});
            }
            direction=next;
          }
          const lastWheel=state.wheels.at(-1),lastScroll=state.scrolls.at(-1);
          return {frames:state.frames,wheels:state.wheels,touches:state.touches,scrolls:state.scrolls,longTasks:state.tasks,
            maximumTouchContacts:Math.max(0,...state.touches.filter(e=>e.trusted).map(e=>e.count)),
            reversals,scrollTailMs:lastWheel&&lastScroll?Math.max(0,lastScroll.at-lastWheel.at):null,
            frame95Ms:percentile([...frames],.95),frame99Ms:percentile([...frames],.99),frameMaximumMs:Math.max(0,...frames),
            framesOver33Ms:frames.filter(x=>x>33.4).length,framesOver50Ms:frames.filter(x=>x>50).length,
            wheel95Ms:percentile([...wheels],.95),wheel99Ms:percentile([...wheels],.99),wheelMaximumMs:Math.max(0,...wheels),
            scrollRange:Math.max(0,...state.scrolls.map(x=>x.y))-Math.min(0,...state.scrolls.map(x=>x.y)),url:location.href};
        })()
        """;

    private static async Task<object?> CheckRefreshScrollingAsync(MainWindow window, string fixtureUrl)
    {
        var args = Environment.GetCommandLineArgs();
        bool controlledPages = args.Contains("--controlled-scroll-pages");
        string inputKind = args.Contains("--two-contact-touch") ? "InjectedTwoContactTouchscreen"
            : args.Contains("--notched-wheel") ? "InjectedNotchedMouseWheel" : "InjectedHighResolutionMouseWheel";
        string opml = args[Array.IndexOf(args, "--opml-fixture") + 1];
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var account = AccountManager.Shared.DefaultAccount!;
        var expected = OpmlParser.Parse(new(opml, await File.ReadAllBytesAsync(opml))).Children
            .Select(item => item.FeedSpecifier?.FeedUrl).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Require(expected.Count == 50 && expected.All(url => account.ExistingFeedWithUrl(url) is not null),
            "This workload requires the complete supplied 50-feed profile.");
        Require(AccountManager.Shared.Accounts.Count == 1 && account.Type == AccountType.OnMyMac,
            "Do not run this workload against a profile containing remote credentials.");
        foreach (var feed in account.FlattenedFeeds().Where(feed => !expected.Contains(feed.Url)).ToArray())
        {
            Require(Uri.TryCreate(feed.Url, UriKind.Absolute, out var url) && url.IsLoopback,
                "The isolated profile contains an unexpected non-fixture subscription.");
            account.RemoveFeed(feed);
        }
        var articleCounts = await account.Database.FetchArticleCountsAsync(account.FlattenedFeeds().Select(feed => feed.FeedID));
        Require(articleCounts.TotalCount > 12000, "The launched app does not contain the expected populated article database.");
        var samples = new List<object>();
        var nativeUiSamples = new List<object>();
        int replacements = 0, sidebar = 0, downloaded = 0;
        void Changed(object? sender, PropertyChangedEventArgs change)
        {
            if (change.PropertyName == nameof(model.ArticleItems)) replacements++;
        }
        void Rebuilt() => sidebar++;
        void Downloaded(object? sender, NewArticlesEventArgs change) => Interlocked.Add(ref downloaded, change.NewArticles.Count);
        void Save() => File.WriteAllText(Path.Combine(OutputDirectory, "refresh-scrolling.json"),
            JsonSerializer.Serialize(new
            {
                ProcessID = Environment.ProcessId, Executable = Environment.ProcessPath,
#if DEBUG
                Configuration = "Debug",
#else
                Configuration = "NotDebug",
#endif
                Feeds = account.FlattenedFeeds().Count(), LoadedArticles = articleCounts.TotalCount,
                FullRuntimeStarted = AppService.Shared.ProbeStarted, IndependentNativeInput = true,
                InputKind = inputKind,
                ControlledPages = controlledPages,
                PrecisionTouchpadVerified = false, Samples = samples, NativeUiSamples = nativeUiSamples
            }, new JsonSerializerOptions { WriteIndented = true }));
        model.PropertyChanged += Changed;
        model.SidebarRebuilt += Rebuilt;
        account.NewArticlesDownloaded += Downloaded;
        window.Activate();
        try
        {
            await WaitFor(() => Task.FromResult(content.IsLoaded && content.ProbeWebViewReady), "Actual Debug reader initialization");
            var scale = content.XamlRoot.RasterizationScale;
            var bounds = window.CaptureState().Bounds;
            window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(bounds with { Width = (int)Math.Ceiling(1300 * scale) }));
            model.IsSidebarVisible = true;
            model.IsUnifiedLayout = false;
            model.IsReaderMode = false;
            model.SearchQuery = "";
            AppDefaults.Shared.UseColumnLayout = false;
            model.BuildSidebar();
            model.SelectedSidebarItem = model.SidebarItems.Single(item => ReferenceEquals(item.Tag, account));
            await model.FilterArticlesAsync();
            await model.ProbeDrainViewUpdatesAsync();
            var article = model.ArticleItems.Where(item => Uri.TryCreate(item.Link, UriKind.Absolute, out var url)
                    && url.Scheme == "https" && url.Host.Contains("substack.com", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.SourceArticle?.ContentHtml?.Length ?? 0).First();
            model.SelectedArticle = article;
            await model.PendingAutomaticReads;
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Real feed article");
            if (args.Contains("--rapid-scrolling"))
                return await CheckRapidScrollingAsync(window, fixtureUrl);
            if (controlledPages) await content.ProbeOpenWebPageAsync(fixtureUrl + "/scroll-article");
            else await content.OpenSelectedInWebViewAsync();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Actual publisher webpage");
            var view = content.ProbeWebPageView;
            var core = view.CoreWebView2;
            using var appearance = JsonDocument.Parse(await core.ExecuteScriptAsync(
                "({dark:matchMedia('(prefers-color-scheme:dark)').matches,background:getComputedStyle(document.body).backgroundColor,width:innerWidth,height:innerHeight,scale:devicePixelRatio})"));
            using (var process = Process.GetCurrentProcess())
            {
                var xaml = process.Modules.Cast<ProcessModule>().Single(module => module.ModuleName.Equals("Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase));
                File.WriteAllText(Path.Combine(OutputDirectory, "runtime.json"), JsonSerializer.Serialize(new
                {
                    ProcessID = process.Id, WinUI = xaml.FileName, WinUIVersion = xaml.FileVersionInfo.FileVersion,
                    WebViewVersion = core.Environment.BrowserVersionString, UserDataFolder = core.Environment.UserDataFolder,
                    DebuggerAttached = Debugger.IsAttached, PageAppearance = appearance.RootElement,
                    SoftwareRasterizationEnabled = WebViewEnvironment.UsesSoftwareRasterization,
                    DirectScrollingEnabled = WebViewEnvironment.UsesDirectScrolling,
                    BrowserProcessID = core.BrowserProcessId
                }));
                Require(!xaml.FileName.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
                    "Concurrent scrolling must use the installed runtime used by the normal app.");
            }
            core.IsMuted = true;
            await Task.Delay(1500);
            await core.CallDevToolsProtocolMethodAsync("Performance.enable", "{}");
            async Task<int> CreateWorld()
            {
                using var frame = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
                string frameId = frame.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
                using var world = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
                    JsonSerializer.Serialize(new { frameId, worldName = "WinNewsWire.ConcurrentScroll" })));
                return world.RootElement.GetProperty("executionContextId").GetInt32();
            }
            int contextId = await CreateWorld();
            async Task<JsonElement> Evaluate(string expression)
            {
                using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                    JsonSerializer.Serialize(new { contextId, expression, returnByValue = true, awaitPromise = true })));
                if (result.RootElement.TryGetProperty("exceptionDetails", out var error))
                    throw new InvalidOperationException("Page instrumentation failed: " + error);
                return result.RootElement.GetProperty("result").GetProperty("value").Clone();
            }
            async Task<Dictionary<string, double>> Metrics()
            {
                using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Performance.getMetrics", "{}"));
                return result.RootElement.GetProperty("metrics").EnumerateArray()
                    .ToDictionary(entry => entry.GetProperty("name").GetString()!, entry => entry.GetProperty("value").GetDouble());
            }
            await Evaluate("""
                (() => {
                  window.__refreshScroll={frames:[],touches:[],tasks:[],raf:0,wheel:()=>{},touch:()=>{},scroll:()=>{},
                    observer:{takeRecords:()=>[],disconnect:()=>{}},
                    wheels:[{at:0,delta:120,delay:0,sequence:1},{at:100,delta:-120,delay:0,sequence:3}],
                    scrolls:[{at:0,y:100,sequence:2},{at:100,y:0,sequence:4}]};
                  return true;
                })()
                """);
            var tiedEvents = await Evaluate(RefreshScrollStop);
            Require(tiedEvents.GetProperty("reversals")[0].GetProperty("responseMs").GetDouble() == 0,
                "Direction-response accounting lost a scroll event with the same timestamp as its wheel event.");
            async Task Measure(string name, Func<Task>? refresh, int seconds)
            {
                window.Activate();
                view.Focus(FocusState.Programmatic);
                var activationWait = Stopwatch.StartNew();
                while (GetForegroundWindow() != WindowNative.GetWindowHandle(window))
                {
                    if (activationWait.Elapsed > TimeSpan.FromMinutes(4))
                        throw new TimeoutException("Windows did not grant the private test window foreground input.");
                    SetForegroundWindow(WindowNative.GetWindowHandle(window));
                    await Task.Delay(500);
                }
                await Task.Delay(250);
                await Evaluate("scrollTo(0,0); true");
                await Task.Delay(300);
                var dimensions = await Evaluate(RefreshScrollStart);
                Require(dimensions.GetProperty("max").GetDouble() > 1000, "Use a real webpage with enough scrolling content.");
                var before = await Metrics();
                var toolbar = content.ProbeToolbarPerformance;
                int oldReplacements = replacements, oldSidebar = sidebar, oldDownloaded = downloaded;
                var uiGaps = new List<double>();
                var uiStalls = new List<object>();
                var gcStart = GC.GetTotalPauseDuration();
                var lastGcPause = gcStart;
                var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                var timer = window.DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(16);
                long last = Stopwatch.GetTimestamp();
                void Tick(DispatcherQueueTimer sender, object data)
                {
                    long now = Stopwatch.GetTimestamp();
                    double gap = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
                    var pause = GC.GetTotalPauseDuration();
                    uiGaps.Add(gap);
                    if (gap > 50) uiStalls.Add(new { GapMs = gap, GcPauseMs = (pause - lastGcPause).TotalMilliseconds });
                    lastGcPause = pause;
                    last = now;
                }
                timer.Tick += Tick;
                var watch = Stopwatch.StartNew();
                double? refreshStart = null, refreshEnd = null;
                using var process = Process.GetCurrentProcess();
                double cpu = process.TotalProcessorTime.TotalMilliseconds;
                long allocated = GC.GetTotalAllocatedBytes();
                timer.Start();
                Task refreshTask = Task.CompletedTask;
                Task<object>? input = null;
                try
                {
                    input = args.Contains("--two-contact-touch") ? DriveTwoContactTouchAsync(window, view, seconds)
                        : DriveIndependentWheelAsync(window, view, seconds);
                    if (refresh is not null)
                    {
                        await Task.Delay(700);
                        refreshStart = watch.Elapsed.TotalMilliseconds;
                        refreshTask = Refresh();
                        async Task Refresh()
                        {
                            try { await refresh(); }
                            finally { refreshEnd = watch.Elapsed.TotalMilliseconds; }
                        }
                    }
                    var delivery = await input;
                    double inputFinished = watch.Elapsed.TotalMilliseconds;
                    await Task.Delay(400);
                    var browser = await Evaluate(RefreshScrollStop);
                    var after = await Metrics();
                    samples.Add(new
                    {
                        Name = name, DurationMs = watch.Elapsed.TotalMilliseconds, InputFinishedMs = inputFinished,
                        RefreshStartMs = refreshStart, RefreshEndMs = refreshEnd,
                        RefreshStillActive = !refreshTask.IsCompleted, NativeInput = delivery, Browser = browser,
                        UiMaximumGapMs = uiGaps.Count == 0 ? watch.Elapsed.TotalMilliseconds : uiGaps.Max(),
                        UiGapsOver50Ms = uiGaps.Count(gap => gap > 50), UiGapsOver100Ms = uiGaps.Count(gap => gap > 100),
                        UiGaps = uiGaps, HostCpuMs = process.TotalProcessorTime.TotalMilliseconds - cpu,
                        UiStalls = uiStalls, GcPauseMs = (GC.GetTotalPauseDuration() - gcStart).TotalMilliseconds,
                        GcCollections = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - collections[index]).ToArray(),
                        AllocatedMB = (GC.GetTotalAllocatedBytes() - allocated) / 1048576d,
                        TimelineReplacements = replacements - oldReplacements, SidebarRebuilds = sidebar - oldSidebar,
                        NewArticles = downloaded - oldDownloaded, ToolbarUpdates = content.ProbeToolbarPerformance.State - toolbar.State,
                        ScriptMs = 1000 * (after["ScriptDuration"] - before["ScriptDuration"]),
                        LayoutMs = 1000 * (after["LayoutDuration"] - before["LayoutDuration"]),
                        BrowserTaskMs = 1000 * (after["TaskDuration"] - before["TaskDuration"]),
                        ViewStillVisible = content.ProbeShowingWebPage
                    });
                    Save();
                    Require(content.ProbeShowingWebPage && browser.GetProperty("scrollRange").GetDouble() > 1000,
                        "The displayed webpage did not remain visible and scroll throughout the workload.");
                    if (args.Contains("--two-contact-touch"))
                        Require(browser.GetProperty("maximumTouchContacts").GetInt32() == 2,
                            "The browser did not receive two simultaneous native touch contacts.");
                    if (refreshStart is not null)
                        Require(Math.Min(refreshEnd ?? inputFinished, inputFinished) - refreshStart.Value > 1500,
                            "The refresh did not overlap enough scrolling to exercise contention.");
                    await refreshTask.WaitAsync(TimeSpan.FromMinutes(3));
                    await model.ProbeDrainViewUpdatesAsync();
                }
                finally
                {
                    timer.Stop();
                    timer.Tick -= Tick;
                    try { if (input is not null) await input; }
                    finally { if (!refreshTask.IsCompleted) await refreshTask.WaitAsync(TimeSpan.FromMinutes(3)); }
                }
            }
            await CaptureBrowserTraceAsync(core, "initial-scroll", () => Measure("idle-before-runtime", null, 12));
            if (args.Contains("--gpu-only"))
            {
                await CaptureBrowserTraceAsync(core, "repeat-scroll", () => Measure("idle-repeated-scroll", null, 12));
                return new { Feeds = expected.Count, Articles = articleCounts.TotalCount, GpuDiagnosticOnly = true };
            }
            await CaptureBrowserTraceAsync(core, "startup-refresh-scroll", () => Measure("normal-runtime-startup-refresh", () =>
            {
                AppService.Shared.Start();
                return AppService.Shared.ProbeStartup;
            }, 24));
            await Measure("idle-with-runtime", null, 12);
            Task PrepareFullRefresh() => Task.Run(() =>
            {
                foreach (var feed in account.FlattenedFeeds())
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
                    ?? throw new InvalidOperationException("The actual Reload button did not start a refresh.");
            }
            await PrepareFullRefresh();
            await CaptureBrowserTraceAsync(core, "refresh-scroll", () => Measure("full-refresh-and-reparse", Reload, 24));
            await Measure("idle-after-full-refresh", null, 12);
            var video = model.ArticleItems.First(item => Uri.TryCreate(item.Link, UriKind.Absolute, out var url)
                && url.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) && url.AbsolutePath == "/watch");
            model.SelectedArticle = video;
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Real video feed article");
            if (controlledPages) await content.ProbeOpenWebPageAsync(fixtureUrl + "/scroll-second");
            else await content.OpenSelectedInWebViewAsync();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Actual video webpage");
            await Task.Delay(2000);
            contextId = await CreateWorld();
            await CaptureBrowserTraceAsync(core, "video-initial-scroll", () => Measure("video-idle-before-refresh", null, 12));
            await PrepareFullRefresh();
            await CaptureBrowserTraceAsync(core, "video-refresh-scroll", () => Measure("video-full-refresh", Reload, 24));
            await Measure("video-idle-after-refresh", null, 12);
            async Task MeasureNative(string name, ListView target)
            {
                var scroll = Descendants(target).OfType<ScrollViewer>().Single(item => item.Name == "ScrollViewer");
                window.Activate();
                SetForegroundWindow(WindowNative.GetWindowHandle(window));
                await WaitFor(() => Task.FromResult(GetForegroundWindow() == WindowNative.GetWindowHandle(window)
                    && scroll.ScrollableHeight > 200), "Scrollable native " + name);
                scroll.ChangeView(null, 0, null, disableAnimation: true);
                await WaitFor(() => Task.FromResult(scroll.VerticalOffset == 0), "Native scroll origin");
                var offsets = new List<double>();
                void Scrolled(object? sender, ScrollViewerViewChangedEventArgs change) => offsets.Add(scroll.VerticalOffset);
                scroll.ViewChanged += Scrolled;
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
                var watch = Stopwatch.StartNew();
                Task refresh = Task.CompletedTask;
                Task<object>? input = null;
                double? refreshEnd = null;
                try
                {
                    timer.Start();
                    input = DriveIndependentWheelAsync(window, target, 12);
                    await Task.Delay(700);
                    double refreshStart = watch.Elapsed.TotalMilliseconds;
                    refresh = Refresh();
                    async Task Refresh()
                    {
                        try { await Reload(); }
                        finally { refreshEnd = watch.Elapsed.TotalMilliseconds; }
                    }
                    var delivery = await input;
                    double inputEnd = watch.Elapsed.TotalMilliseconds;
                    await refresh.WaitAsync(TimeSpan.FromMinutes(3));
                    double range = offsets.Count == 0 ? 0 : offsets.Max() - offsets.Min();
                    nativeUiSamples.Add(new { Name = name, NativeInput = delivery,
                        InputKind = args.Contains("--notched-wheel") ? "InjectedNotchedMouseWheel" : "InjectedHighResolutionMouseWheel",
                        RefreshStartMs = refreshStart,
                        RefreshEndMs = refreshEnd, InputFinishedMs = inputEnd, ScrollRange = range,
                        UiMaximumGapMs = gaps.Count == 0 ? watch.Elapsed.TotalMilliseconds : gaps.Max() });
                    Save();
                    Require(range > 200 && Math.Min(refreshEnd ?? inputEnd, inputEnd) - refreshStart > 1500,
                        "Native scrolling did not overlap enough refresh work: " + name);
                    await model.ProbeDrainViewUpdatesAsync();
                }
                finally
                {
                    timer.Stop();
                    timer.Tick -= Tick;
                    scroll.ViewChanged -= Scrolled;
                    try { if (input is not null) await input; }
                    finally { if (!refresh.IsCompleted) await refresh.WaitAsync(TimeSpan.FromMinutes(3)); }
                }
            }
            await PrepareFullRefresh();
            await MeasureNative("sidebar-refresh", (ListView)content.FindName("SidebarListView"));
            await PrepareFullRefresh();
            await MeasureNative("timeline-refresh", (ListView)content.FindName("ArticleListView"));
            Require(AppService.Shared.ProbeStarted, "The actual application runtime was not started.");
            return new { RealDebugWindow = true, FullRuntime = true, Feeds = expected.Count, Articles = articleCounts.TotalCount,
                IndependentInputDriver = true, InputKind = inputKind, PrecisionTouchpadVerified = false,
                ActiveRefreshCompared = true, PublisherAndVideo = !controlledPages, ControlledPages = controlledPages };
        }
        finally
        {
            model.PropertyChanged -= Changed;
            model.SidebarRebuilt -= Rebuilt;
            account.NewArticlesDownloaded -= Downloaded;
            Save();
        }
    }

    private static async Task CaptureBrowserTraceAsync(CoreWebView2 core, string name, Func<Task> action)
    {
        if (!Environment.GetCommandLineArgs().Contains("--browser-trace"))
        {
            await action();
            return;
        }
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = core.GetDevToolsProtocolEventReceiver("Tracing.tracingComplete");
        void Completed(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
        {
            using var result = JsonDocument.Parse(args.ParameterObjectAsJson);
            completion.TrySetResult(result.RootElement.GetProperty("stream").GetString()!);
        }
        receiver.DevToolsProtocolEventReceived += Completed;
        try
        {
            // Full renderer traces overwhelm repeated touch captures; retain input latency unless diagnosing the GPU.
            var categories = new List<string> { "input.scrolling", "latencyInfo" };
            if (Environment.GetCommandLineArgs().Contains("--gpu-detail"))
            {
                var available = await core.CallDevToolsProtocolMethodAsync("Tracing.getCategories", "{}");
                File.WriteAllText(Path.Combine(OutputDirectory, "trace-categories.json"), available);
                categories.AddRange(["benchmark", "cc", "input", "toplevel", "devtools.timeline",
                    "disabled-by-default-devtools.timeline", "disabled-by-default-devtools.timeline.frame", "blink.user_timing", "viz",
                    "gpu", "gpu.angle", "disabled-by-default-gpu.service", "disabled-by-default-gpu.device",
                    "disabled-by-default-gpu.debug", "disabled-by-default-skia.gpu", "disabled-by-default-skia.shaders"]);
            }
            await core.CallDevToolsProtocolMethodAsync("Tracing.start", JsonSerializer.Serialize(new
            {
                transferMode = "ReturnAsStream",
                traceConfig = new
                {
                    recordMode = "recordContinuously",
                    includedCategories = categories
                }
            }));
            try { await action(); }
            finally
            {
                await core.CallDevToolsProtocolMethodAsync("Tracing.end", "{}");
                var handle = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
                try
                {
                    await using var output = File.Create(Path.Combine(OutputDirectory, name + "-browser-trace.json"));
                    while (true)
                    {
                        using var chunk = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("IO.read", JsonSerializer.Serialize(new { handle, size = 1024 * 1024 })));
                        var value = chunk.RootElement;
                        string data = value.GetProperty("data").GetString()!;
                        var bytes = value.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean()
                            ? Convert.FromBase64String(data) : System.Text.Encoding.UTF8.GetBytes(data);
                        await output.WriteAsync(bytes);
                        if (value.GetProperty("eof").GetBoolean()) break;
                    }
                }
                finally { await core.CallDevToolsProtocolMethodAsync("IO.close", JsonSerializer.Serialize(new { handle })); }
            }
        }
        finally { receiver.DevToolsProtocolEventReceived -= Completed; }
    }

    private sealed class NativeScrollPacer : WaitHandle
    {
        public NativeScrollPacer(int milliseconds)
        {
            SafeWaitHandle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
            if (SafeWaitHandle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error, "Could not create the native input timer.");
            }
            long due = -milliseconds * 10000L;
            if (!SetWaitableTimer(SafeWaitHandle, ref due, milliseconds, IntPtr.Zero, IntPtr.Zero, false))
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error, "Could not start the native input timer.");
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period,
            IntPtr callback, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    }

    private static (IntPtr Handle, CapturePoint Point, CapturePoint Original) PrepareWheelInput(
        MainWindow window, FrameworkElement view)
    {
        var handle = WindowNative.GetWindowHandle(window);
        var location = view.TransformToVisual((UIElement)window.Content).TransformPoint(new(view.ActualWidth / 2, view.ActualHeight / 2));
        var origin = new CapturePoint();
        Require(ClientToScreen(handle, ref origin), "Could not locate the owned browser.");
        var point = new CapturePoint
        {
            X = origin.X + (int)Math.Round(location.X * view.XamlRoot.RasterizationScale),
            Y = origin.Y + (int)Math.Round(location.Y * view.XamlRoot.RasterizationScale)
        };
        Require(GetCursorPos(out var original), "Could not preserve the cursor position.");
        Require(GetForegroundWindow() == handle && SetCursorPos(point.X, point.Y), "The private Debug window must own foreground input.");
        return (handle, point, original);
    }

    private static void RequireWheelTarget(IntPtr handle, CapturePoint point, double elapsedMs)
    {
        Require(GetForegroundWindow() == handle, $"The owned window lost foreground at {elapsedMs:F0} ms; input stopped.");
        Require(GetCursorPos(out var current) && current.X == point.X && current.Y == point.Y,
            $"The cursor moved at {elapsedMs:F0} ms: expected {point.X},{point.Y}, actual {current.X},{current.Y}; input stopped.");
        Require(GetAncestor(WindowFromPoint(point), 2) == handle,
            $"The input point was covered at {elapsedMs:F0} ms; input stopped.");
    }

    private static Task<object> DriveIndependentWheelAsync(MainWindow window, FrameworkElement view, int seconds)
    {
        bool notched = Environment.GetCommandLineArgs().Contains("--notched-wheel");
        var (handle, point, original) = PrepareWheelInput(window, view);
        return Task.Factory.StartNew<object>(() =>
        {
            using var pacer = new NativeScrollPacer(notched ? 80 : 8);
            var watch = Stopwatch.StartNew();
            var sent = new List<double>();
            try
            {
                while (watch.Elapsed.TotalSeconds < seconds)
                {
                    RequireWheelTarget(handle, point, watch.Elapsed.TotalMilliseconds);
                    int direction = (int)(watch.Elapsed.TotalSeconds / 4) % 2 == 0 ? -1 : 1;
                    var input = new ScrollInput { Mouse = new() { Data = unchecked((uint)(direction * (notched ? 120 : 15))), Flags = 0x0800 } };
                    Require(SendInput(1, [input], Marshal.SizeOf<ScrollInput>()) == 1, "Native wheel input failed.");
                    sent.Add(watch.Elapsed.TotalMilliseconds);
                    pacer.WaitOne();
                }
                return new { EventsSent = sent.Count, DurationMs = watch.Elapsed.TotalMilliseconds,
                    Pacing = "HighResolutionWaitableTimer", SentAtMs = sent };
            }
            finally
            {
                if (GetCursorPos(out var current) && current.X == point.X && current.Y == point.Y)
                    SetCursorPos(original.X, original.Y);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
#endif
