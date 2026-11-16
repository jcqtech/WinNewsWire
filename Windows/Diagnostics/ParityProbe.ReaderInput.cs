#if PARITY_PROBE
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using WinNewsWire.Account;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinRT.Interop;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private sealed record BrowserWheelDelivery(string Type, double RequestedAt, double FirstAt, double LastAt,
        int Events, double MaximumDispatchDelayMs);

    private static async Task<object?> CheckNativeWheelPolicyAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        await content.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
        var sidebar = Descendants((ListView)content.FindName("SidebarListView")).OfType<ScrollViewer>()
            .Single(scroll => scroll.Name == "ScrollViewer");
        var timeline = Descendants((ListView)content.FindName("ArticleListView")).OfType<ScrollViewer>()
            .Single(scroll => scroll.Name == "ScrollViewer");
        var columns = (ScrollViewer)content.FindName("TimelineHorizontalScroll");
        Require(!sidebar.IsScrollInertiaEnabled && !timeline.IsScrollInertiaEnabled && !columns.IsScrollInertiaEnabled,
            "The actual native scroll viewers still enable wheel inertia by default.");
        return new { Sidebar = true, Timeline = true, HorizontalColumns = true, PhysicalInputMeasured = false };
    }

    private static async Task<object?> CheckReaderInputLatencyAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        bool scripts = AppDefaults.Shared.ArticleContentJavascriptEnabled;
        bool requireResponsiveness = Environment.GetCommandLineArgs().Contains("--reader-input-latency");
        var account = AccountManager.Shared.DefaultAccount;
        var feed = account.FlattenedFeeds().Single();
        const string id = "reader-input-latency";
        string body = string.Concat(Enumerable.Range(0, 180).Select(index =>
            $"<p>Paragraph {index}. Direct scrolling must not wait for an animation or a busy article script. "
            + "Touchscreen gestures keep the browser's native momentum.</p>"));
        await account.Database.UpdateArticlesAsync([new Article(account.AccountID, id, feed.FeedID, id, "Reader input",
            body, null, null, serverUrl + "/article", null, null, null, DateTime.UtcNow, null, null, null,
            new ArticleStatus(id, true, false, DateTime.UtcNow))]);
        var core = content.ProbeWebView.CoreWebView2;
        try
        {
            AppDefaults.Shared.ArticleContentJavascriptEnabled = false;
            model.IsUnifiedLayout = true;
            model.IsReaderMode = false;
            model.BuildSidebar();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == id);
            await model.ProbeDrainViewUpdatesAsync();
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Direct-input reader fixture");
            content.ProbeWebView.Focus(FocusState.Programmatic);
            await core.CallDevToolsProtocolMethodAsync("Page.bringToFront", "{}");
            using var tree = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
            string frameId = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
            const string worldName = "WinNewsWire.InputLatency";
            using var world = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
                JsonSerializer.Serialize(new { frameId, worldName })));
            int context = world.RootElement.GetProperty("executionContextId").GetInt32();
            async Task<JsonElement> Evaluate(string expression)
            {
                using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                    JsonSerializer.Serialize(new { contextId = context, expression, returnByValue = true, awaitPromise = true })));
                if (result.RootElement.TryGetProperty("exceptionDetails", out var error))
                    throw new InvalidOperationException("Reader input instrumentation failed: " + error);
                return result.RootElement.GetProperty("result").GetProperty("value").Clone();
            }
            await Evaluate("window.scrollTo(0,1000); true");
            await Task.Delay(250);
            await Evaluate("globalThis.__inputProbeWheels=[]; addEventListener('wheel',e=>"
                + "__inputProbeWheels.push({at:Date.now(),delta:e.deltaY,trusted:e.isTrusted}),{passive:true}); true");
            var viewport = await Evaluate("({width:innerWidth,height:innerHeight,y:scrollY})");
            double x = viewport.GetProperty("width").GetDouble() / 2;
            double before = viewport.GetProperty("y").GetDouble();
            BrowserWheelDelivery? delivered = null;
            var frames = await CaptureReaderFramesAsync(core, async wheel =>
            {
                delivered = await wheel(0.5, 0.5, 120);
                await Task.Delay(350);
            });
            var input = delivered ?? throw new InvalidDataException("The private wheel driver did not return input timing.");
            double sentAt = input.FirstAt;
            var browser = await Evaluate("({scroll:scrollY,focus:document.hasFocus(),visibility:document.visibilityState,"
                + "height:document.scrollingElement.scrollHeight,wheels:__inputProbeWheels})");
            var response = frames.Frames.FirstOrDefault(frame => frame.TimestampMs >= sentAt - 1 && frame.Offset > before + 2);
            double? responseMs = response is null ? null : response.TimestampMs - sentAt;
            double previousOffset = before;
            double? settledMs = null;
            foreach (var frame in frames.Frames.Where(frame => frame.TimestampMs >= sentAt - 1))
            {
                if (Math.Abs(frame.Offset - previousOffset) > 0.1) settledMs = frame.TimestampMs - sentAt;
                previousOffset = frame.Offset;
            }
            var report = new
            {
                InputKind = "IndependentTrustedBrowserWheel", PhysicalInputMeasured = false,
                ForegroundOwned = GetForegroundWindow() == WindowNative.GetWindowHandle(window),
                InputSentAtBrowserMs = sentAt,
                FirstPresentedScrollMs = responseMs, LastPresentedScrollMs = settledMs, PresentationBudgetMs = 100,
                AnimationDurationMs = settledMs - responseMs - (input.LastAt - input.FirstAt),
                RequestToInputMs = input.FirstAt - input.RequestedAt, Input = input,
                ResponsivenessPassed = responseMs is >= -1 and < 100 && settledMs + input.FirstAt - input.LastAt is >= -1 and < 100,
                Browser = browser, frames.ProcessId, frames.Frames
            };
            File.WriteAllText(Path.Combine(OutputDirectory, "reader-input-latency.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            double? animation = settledMs - responseMs - (input.LastAt - input.FirstAt);
            Require(responseMs is not null && settledMs is not null && animation <= 34,
                $"Wheel input retained an animation tail: {animation:F1} ms, or no visible input was delivered.");
            if (requireResponsiveness)
            {
                Require(responseMs is >= -1 and < 100 && settledMs + input.FirstAt - input.LastAt is >= -1 and < 100,
                    $"Wheel scrolling exceeded the presentation budget: {settledMs?.ToString("F1") ?? "no frame"} ms.");
            }

            await Evaluate("window.scrollTo(0,1500); true");
            await Task.Delay(150);
            await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled",
                JsonSerializer.Serialize(new { enabled = true, maxTouchPoints = 1 }));
            double startY = viewport.GetProperty("height").GetDouble() * 0.8;
            double distance = viewport.GetProperty("height").GetDouble() * 0.5;
            double touchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
            async Task Touch(string type, double point, double timestamp)
            {
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchTouchEvent",
                    JsonSerializer.Serialize(new { type, timestamp,
                        touchPoints = new[] { new { x, y = point, id = 0, radiusX = 2, radiusY = 2, force = 1 } } }));
            }
            await Touch("touchStart", startY, touchTime);
            for (int step = 1; step <= 8; step++)
            {
                await Task.Delay(16);
                await Touch("touchMove", startY - distance * step / 8, touchTime + step * 0.016);
            }
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchTouchEvent",
                JsonSerializer.Serialize(new { type = "touchEnd", timestamp = touchTime + 0.144, touchPoints = Array.Empty<object>() }));
            double atRelease = (await Evaluate("scrollY")).GetDouble();
            await Task.Delay(200);
            double coasted = (await Evaluate("scrollY")).GetDouble();
            File.WriteAllText(Path.Combine(OutputDirectory, "reader-touchscreen-momentum.json"),
                JsonSerializer.Serialize(new { AtRelease = atRelease, After200Ms = coasted, MomentumPixels = coasted - atRelease }));
            Require(atRelease > 1500 && coasted > atRelease + 10,
                "Disabling wheel animation removed the browser's touchscreen momentum.");
            await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":false}");
            return new { BrowserInput = report, LastWheelMotionMs = settledMs,
                BrowserTouchscreenMomentumPixels = coasted - atRelease, NativeTouchscreenInputMeasured = false };
        }
        finally
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":false}");
            AppDefaults.Shared.ArticleContentJavascriptEnabled = scripts;
            await content.RestoreStateAsync(original);
        }
    }

    private static async Task<object?> CheckPublishedWheelPolicyAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var original = window.CaptureState();
        try
        {
            await content.ProbeOpenWebPageAsync(serverUrl + "/scroll-article");
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Embedded direct-input fixture");
            var core = content.ProbeWebPageView.CoreWebView2;
            content.ProbeWebPageView.Focus(FocusState.Programmatic);
            await core.CallDevToolsProtocolMethodAsync("Page.bringToFront", "{}");
            await core.ExecuteScriptAsync("window.scrollTo(0,1000)");
            await Task.Delay(200);
            await core.ExecuteScriptAsync("window.__inputProbeWheels=[]; addEventListener('wheel',e=>"
                + "__inputProbeWheels.push({at:Date.now(),delta:e.deltaY}),{passive:true})");
            double offset = JsonSerializer.Deserialize<double>(await core.ExecuteScriptAsync("scrollY"));
            BrowserWheelDelivery? delivered = null;
            var frames = await CaptureReaderFramesAsync(core, async wheel =>
            {
                delivered = await wheel(0.5, 0.5, 120);
                await Task.Delay(350);
            }, "webpage-input");
            var input = delivered ?? throw new InvalidDataException("The private webpage driver did not return input timing.");
            double sent = input.FirstAt;
            double? lastChange = null;
            double? firstChange = null;
            foreach (var frame in frames.Frames.Where(frame => frame.TimestampMs >= sent - 1))
            {
                if (Math.Abs(frame.Offset - offset) > 0.1)
                {
                    lastChange = frame.TimestampMs - sent;
                    firstChange ??= lastChange;
                }
                offset = frame.Offset;
            }
            using var events = JsonDocument.Parse(await core.ExecuteScriptAsync("({wheels:__inputProbeWheels,scroll:scrollY})"));
            var result = new { PhysicalInputMeasured = false, InputSentAtBrowserMs = sent,
                FirstPresentedScrollMs = firstChange, LastPresentedScrollMs = lastChange,
                AnimationDurationMs = lastChange - firstChange - (input.LastAt - input.FirstAt),
                RequestToInputMs = input.FirstAt - input.RequestedAt, Input = input,
                ResponsivenessPassed = firstChange is >= -1 and < 100 && lastChange + input.FirstAt - input.LastAt is >= -1 and < 100,
                Browser = events.RootElement.Clone(), frames.Frames };
            File.WriteAllText(Path.Combine(OutputDirectory, "webpage-input-latency.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            double? animation = lastChange - firstChange - (input.LastAt - input.FirstAt);
            Require(firstChange is not null && lastChange is not null && animation <= 34,
                $"The embedded webpage retained a wheel animation: {animation:F1} ms, or no visible input was delivered.");
            if (Environment.GetCommandLineArgs().Contains("--reader-input-latency"))
                Require(firstChange is >= -1 and < 100 && lastChange + input.FirstAt - input.LastAt is >= -1 and < 100,
                    $"The embedded webpage exceeded the presentation budget: {lastChange?.ToString("F1") ?? "no frame"} ms.");
            return result;
        }
        finally { await content.RestoreStateAsync(original); }
    }

    private static async Task<CapturedScrollFrames> CaptureReaderFramesAsync(CoreWebView2 core,
        Func<Func<double, double, double, Task<BrowserWheelDelivery>>, Task> action,
        string name = "reader-input")
    {
        var arguments = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(arguments, "--frame-driver-host");
        if (index < 0 || index + 1 >= arguments.Length || !Path.IsPathFullyQualified(arguments[index + 1])
            || !File.Exists(arguments[index + 1]))
            throw new ArgumentException("Reader input latency needs an absolute independent frame-driver host.");
        string portPath = Path.Combine(core.Environment.UserDataFolder, "EBWebView", "DevToolsActivePort");
        if (!File.Exists(portPath)) portPath = Path.Combine(core.Environment.UserDataFolder, "DevToolsActivePort");
        var lines = await File.ReadAllLinesAsync(portPath);
        Require(lines.Length > 0 && int.TryParse(lines[0], out _), "The private reader debug port is unavailable.");
        int port = int.Parse(lines[0], System.Globalization.CultureInfo.InvariantCulture);
        Require(port is > 0 and <= 65535, "The private reader debug port is invalid.");
        using var target = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.getTargetInfo", "{}"));
        string targetId = target.RootElement.GetProperty("targetInfo").GetProperty("targetId").GetString()!;
        using var process = new Process { StartInfo = new()
        {
            FileName = arguments[index + 1], UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CaptureScrollFrames.mjs"));
        process.StartInfo.ArgumentList.Add($"ws://127.0.0.1:{port}/devtools/page/{targetId}");
        Require(process.Start(), "The independent reader frame capture did not start.");
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            Require(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ready",
                "The independent reader frame capture did not initialize.");
            await Task.Delay(100);
            async Task<BrowserWheelDelivery> Wheel(double x, double y, double deltaY)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "wheel", x, y, deltaY }));
                var delivered = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
                    ?? throw new InvalidDataException("The private browser input process returned no delivery time.");
                var input = JsonSerializer.Deserialize<BrowserWheelDelivery>(delivered)
                    ?? throw new InvalidDataException("The private input acknowledgement was empty.");
                Require(input.Type == "wheel" && input.Events > 0, "Unexpected private input acknowledgement.");
                return input;
            }
            await CaptureBrowserTraceAsync(core, name, () => action(Wheel));
            await process.StandardInput.WriteLineAsync("stop");
            var json = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
                ?? throw new InvalidDataException("The frame collector returned no results.");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(process.ExitCode == 0, "The frame collector failed: " + await errors);
            var frames = JsonSerializer.Deserialize<CapturedScrollFrames>(json)
                ?? throw new InvalidDataException("The captured reader frames were empty.");
            File.WriteAllText(Path.Combine(OutputDirectory, name + "-frames.json"), json);
            Require(frames.ProcessId == process.Id, "The reader frames came from an unexpected process.");
            return frames;
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
            await errors;
        }
    }
}
#endif
