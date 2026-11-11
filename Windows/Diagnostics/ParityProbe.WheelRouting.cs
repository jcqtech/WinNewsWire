#if PARITY_PROBE
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.AppRuntime;
using WinRT.Interop;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object> CheckBrowserWheelRoutingAsync(Process driver, MainWindow window, WebView2 view)
    {
        var core = view.CoreWebView2;
        async Task<JsonElement> Evaluate(string script)
        {
            using var value = JsonDocument.Parse(await core.ExecuteScriptAsync(script));
            return value.RootElement.Clone();
        }
        async Task<RapidWheelDelivery> Wheel(int duration = 240, int burst = 96)
        {
            window.Activate();
            view.Focus(FocusState.Programmatic);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return await DriveRapidWheelAsync(driver, window, view,
                new("wheel-routing", 16, 120, 1000, duration, BurstMs: burst), Stopwatch.GetTimestamp(), started);
        }
        void Modifier(global::Windows.System.VirtualKey key, bool pressed)
        {
            if (pressed)
                Require(GetForegroundWindow() == WindowNative.GetWindowHandle(window), "The wheel-routing window lost foreground.");
            var input = new ProbeNativeInput { Type = 1, Data = new() { Keyboard = new()
                { VirtualKey = (ushort)key, Flags = pressed ? 0u : 2u } } };
            Require(SendKeyboardInput(1, [input], Marshal.SizeOf<ProbeNativeInput>()) == 1, "The owned modifier was not delivered.");
        }
        await Evaluate("""
            (() => {
                scrollTo({top:500,behavior:"instant"});
                const box=document.createElement("div");
                box.id="wheel-routing";
                box.tabIndex=0;
                box.style.cssText="position:fixed;left:20%;top:20%;width:60%;height:60%;overflow:auto;overscroll-behavior:contain;z-index:2147483647;background:white;color:black";
                box.innerHTML='<div style="height:12000px;width:100%">Nested wheel fixture</div>';
                document.body.append(box);
                box.scrollTop=5000;
                window.__routing={box,root:scrollY,blocked:0};
                box.focus();
                return true;
            })()
            """);
        try
        {
            await Task.Delay(150);
            await Wheel();
            var nested = await Evaluate("({root:scrollY,initialRoot:__routing.root,top:__routing.box.scrollTop})");
            Require(Math.Abs(nested.GetProperty("root").GetDouble() - nested.GetProperty("initialRoot").GetDouble()) < 2
                && nested.GetProperty("top").GetDouble() > 5400, "Nested wheel input escaped its scroll container.");

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var coasting = DriveRapidWheelAsync(driver, window, view,
                new("keyboard-interruption", 16, 120, 1000, 240, BurstMs: 96), Stopwatch.GetTimestamp(), started);
            double beforeKey;
            try
            {
                await started.Task;
                await Task.Delay(150);
                if (coasting.IsFaulted) await coasting;
                beforeKey = (await Evaluate("__routing.box.scrollTop")).GetDouble();
                SendOwnedWindowKey(window, global::Windows.System.VirtualKey.PageUp);
            }
            finally { await coasting; }
            double afterKey = (await Evaluate("__routing.box.scrollTop")).GetDouble();
            Require(afterKey < beforeKey - 20, "Page Up did not interrupt wheel coasting.");

            await Evaluate("""
                __routing.box.scrollTop=5000;
                __routing.block=event=>{event.preventDefault();__routing.blocked++};
                __routing.box.addEventListener("wheel",__routing.block,{passive:false});
                true
                """);
            await Task.Delay(100);
            await Wheel();
            var blocked = await Evaluate("({count:__routing.blocked,top:__routing.box.scrollTop})");
            Require(blocked.GetProperty("count").GetInt32() > 0 && Math.Abs(blocked.GetProperty("top").GetDouble() - 5000) < 2,
                "The inertia controller overrode a page-owned wheel action.");
            await Evaluate("__routing.box.removeEventListener('wheel',__routing.block); true");

            await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia",
                """{"features":[{"name":"prefers-reduced-motion","value":"reduce"}]}""");
            await WaitFor(async () => (await Evaluate("matchMedia('(prefers-reduced-motion: reduce)').matches")).GetBoolean(),
                "Reduced-motion preference");
            var beforeReduced = await WebViewScrolling.ReadProbeStateAsync(core);
            await Wheel();
            var afterReduced = await WebViewScrolling.ReadProbeStateAsync(core);
            Require(beforeReduced.GetProperty("wheels").GetInt32() == afterReduced.GetProperty("wheels").GetInt32(),
                "Reduced motion did not bypass the additional inertia controller.");
            await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", """{"features":[]}""");

            await Evaluate("""
                __routing.box.style.direction="rtl";
                __routing.box.style.overflowY="hidden";
                __routing.box.firstElementChild.style.cssText="width:12000px;height:100%";
                __routing.box.scrollLeft=-5000;
                true
                """);
            await Task.Delay(150);
            Modifier(global::Windows.System.VirtualKey.Shift, true);
            try { await Wheel(); }
            finally { Modifier(global::Windows.System.VirtualKey.Shift, false); }
            double horizontal = (await Evaluate("__routing.box.scrollLeft")).GetDouble();
            Require(horizontal > -4600 && horizontal < -4000, "Shift-wheel scrolling lost the RTL horizontal target.");

            return new { NestedTarget = true, KeyboardInterruption = true, PageOwnedWheel = true,
                ReducedMotion = true, RtlHorizontal = true };
        }
        finally
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", """{"features":[]}""");
            await Evaluate("document.getElementById('wheel-routing')?.remove(); delete window.__routing; true");
        }
    }
}
#endif
