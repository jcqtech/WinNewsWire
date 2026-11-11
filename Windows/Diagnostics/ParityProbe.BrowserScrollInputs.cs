#if PARITY_PROBE
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static extern int BrowserInputSystemMetric(int index);

    private static async Task<object?> CheckBrowserScrollInputsAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        Require(content.ViewModel.SelectedArticle?.Link.StartsWith(serverUrl, StringComparison.Ordinal) == true,
            "Input checks must use the private loopback article.");
        await content.OpenSelectedInWebViewAsync();
        try
        {
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Scroll-input webpage");
            var view = content.ProbeWebPageView;
            var core = view.CoreWebView2;
            Require(core.Source.StartsWith(serverUrl, StringComparison.Ordinal), "Do not rewrite a publisher page for input checks.");
            await core.ExecuteScriptAsync("""
                document.open();document.write(`<!doctype html><html><head><style>
                html {overflow-y:scroll;scroll-behavior:auto;height:auto} body {margin:0;height:auto;overflow:visible}
                ::-webkit-scrollbar {width:16px} ::-webkit-scrollbar-button {display:none}
                ::-webkit-scrollbar-thumb {background:#888;min-height:40px}
                </style></head><body tabindex="0"><div style="height:6000px">Native scrolling fixture</div></body></html>`);
                document.close();
                document.body.tabIndex=0;document.body.focus();scrollTo(0,0);
                """);
            window.Activate();
            SetForegroundWindow(WindowNative.GetWindowHandle(window));
            view.Focus(FocusState.Programmatic);
            await WaitFor(() => Task.FromResult(GetForegroundWindow() == WindowNative.GetWindowHandle(window)),
                "Owned browser keyboard focus");
            await WaitFor(async () => JsonSerializer.Deserialize<bool>(await core.ExecuteScriptAsync("document.hasFocus()")),
                "Native browser document focus");
            async Task<double> Position()
            {
                using var value = JsonDocument.Parse(await core.ExecuteScriptAsync("scrollY"));
                return value.RootElement.GetDouble();
            }
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.Down);
            await WaitFor(async () => await Position() > 0, "Native Down key scrolling");
            double down = await Position();
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.Up);
            await WaitFor(async () => await Position() < down, "Native Up key scrolling");
            await core.ExecuteScriptAsync("scrollTo(0,0)");
            await Task.Delay(300);
            using var before = JsonDocument.Parse(await core.ExecuteScriptAsync(
                "({width:innerWidth,height:innerHeight,scrollHeight:document.scrollingElement.scrollHeight,mode:document.compatMode,y:scrollY})"));
            Require(before.RootElement.GetProperty("scrollHeight").GetDouble() > 5000,
                "The scrollbar fixture is not long enough.");

            var handle = WindowNative.GetWindowHandle(window);
            var origin = new CapturePoint();
            Require(ClientToScreen(handle, ref origin), "Could not locate the browser scrollbar.");
            var position = view.TransformToVisual((UIElement)window.Content)
                .TransformPoint(new(view.ActualWidth - 8, 20));
            double scale = view.XamlRoot.RasterizationScale;
            var start = new CapturePoint
            {
                X = origin.X + (int)Math.Round(position.X * scale),
                Y = origin.Y + (int)Math.Round(position.Y * scale)
            };
            int distance = (int)Math.Round(Math.Min(80, view.ActualHeight / 4) * scale);
            Require(GetCursorPos(out var original), "Could not preserve the cursor for the scrollbar check.");
            bool pressed = false;
            var last = start;
            try
            {
                Require(GetForegroundWindow() == handle && GetAncestor(WindowFromPoint(start), 2) == handle
                    && SetCursorPos(start.X, start.Y), "The owned scrollbar is unavailable.");
                await Task.Delay(100);
                Require(GetForegroundWindow() == handle && GetCursorPos(out var ready)
                    && ready.X == start.X && ready.Y == start.Y && GetAncestor(WindowFromPoint(start), 2) == handle,
                    "Scrollbar input ownership changed before pressing.");
                var press = new ScrollInput { Mouse = new() { Flags = 0x0002 } };
                Require(SendInput(1, [press], Marshal.SizeOf<ScrollInput>()) == 1, "Scrollbar press failed.");
                pressed = true;
                await Task.Delay(100);
                int left = BrowserInputSystemMetric(76), top = BrowserInputSystemMetric(77);
                int width = BrowserInputSystemMetric(78), height = BrowserInputSystemMetric(79);
                Require(width > 1 && height > 1, "Could not measure the native input desktop.");
                await Task.Factory.StartNew(() =>
                {
                    for (int step = 1; step <= 8; step++)
                    {
                        Require(GetForegroundWindow() == handle && GetCursorPos(out var current)
                            && Math.Abs(current.X - last.X) <= 1 && Math.Abs(current.Y - last.Y) <= 1
                            && GetAncestor(WindowFromPoint(last), 2) == handle,
                            "Scrollbar input ownership changed; dragging stopped.");
                        last = new CapturePoint { X = start.X, Y = start.Y + distance * step / 8 };
                        var move = new ScrollInput { Mouse = new()
                        {
                            X = (int)Math.Round((last.X - left) * 65535d / (width - 1)),
                            Y = (int)Math.Round((last.Y - top) * 65535d / (height - 1)),
                            Flags = 0xC001
                        } };
                        Require(SendInput(1, [move], Marshal.SizeOf<ScrollInput>()) == 1, "Scrollbar movement failed.");
                        Thread.Sleep(25);
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            finally
            {
                bool released = true;
                if (pressed)
                {
                    var release = new ScrollInput { Mouse = new() { Flags = 0x0004 } };
                    released = SendInput(1, [release], Marshal.SizeOf<ScrollInput>()) == 1;
                }
                if (GetCursorPos(out var current) && Math.Abs(current.X - last.X) <= 1 && Math.Abs(current.Y - last.Y) <= 1)
                    SetCursorPos(original.X, original.Y);
                Require(released, "Scrollbar release failed.");
            }
            double afterDrag = await Position();
            File.WriteAllText(Path.Combine(OutputDirectory, "browser-inputs.json"), JsonSerializer.Serialize(new
            {
                NativeKeyboardScrollY = down, Before = before.RootElement, AfterDrag = afterDrag,
                ViewWidth = view.ActualWidth, ViewHeight = view.ActualHeight, Scale = scale,
                DragX = start.X, DragY = start.Y, DragDistance = distance
            }));
            if (afterDrag <= 500) await SaveWindowScreenshotAsync(window, "browser-inputs-failure");
            await WaitFor(async () => await Position() > 500, "Native scrollbar dragging");
            return new { NativeArrowKeys = true, NativeScrollbarDrag = true, PhysicalTouchpad = false };
        }
        finally
        {
            await content.GoBackAsync();
            await WaitFor(() => Task.FromResult(!content.ProbeShowingWebPage && content.ProbeArticleReady), "Reader after input checks");
        }
    }
}
#endif
