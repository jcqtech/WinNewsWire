using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace WinNewsWire.AppRuntime;

internal static class WebViewScrolling
{
    internal static string ReaderScript => WebViewEnvironment.UsesDirectScrolling ? "" : Script;

    internal static async Task InstallAsync(CoreWebView2 core)
    {
        if (WebViewEnvironment.UsesDirectScrolling) return;
        await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Page.addScriptToEvaluateOnNewDocument",
            JsonSerializer.Serialize(new { source = Script, worldName = "WinNewsWire.WheelScrolling", runImmediately = true }));
    }

#if PARITY_PROBE
    internal static async Task<JsonElement> ReadProbeStateAsync(CoreWebView2 core, bool readerDocument = false)
    {
        using var tree = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
        string frameId = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
        using var world = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
            JsonSerializer.Serialize(new { frameId, worldName = readerDocument ? MainContent.ReaderWorldName : "WinNewsWire.WheelScrolling" })));
        int contextId = world.RootElement.GetProperty("executionContextId").GetInt32();
        using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new { contextId, expression = "globalThis.__wnwWheelScrolling ?? null", returnByValue = true })));
        return result.RootElement.GetProperty("result").GetProperty("value").Clone();
    }
#endif

    private const string Script = """
        (() => {
            if (globalThis.__wnwWheelScrolling) return;
            const state = globalThis.__wnwWheelScrolling = { wheels: 0, interruptions: 0, uncancelable: 0, coasting: false };
            const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
            const decayTimeMs = 45;
            let active = null;
            let frame = 0;
            const position = target => target === document.scrollingElement
                ? { left: scrollX, top: scrollY } : { left: target.scrollLeft, top: target.scrollTop };
            const stop = () => {
                cancelAnimationFrame(frame);
                frame = 0;
                active = null;
                state.coasting = false;
                globalThis.__wnwReportScroll?.();
            };
            const advance = () => {
                const now = performance.now();
                frame = 0;
                if (!active || !active.target.isConnected) { stop(); return; }
                const current = position(active.target);
                if (Math.abs(current.left - active.applied.left) > 1 || Math.abs(current.top - active.applied.top) > 1) {
                    stop();
                    return;
                }
                const dx = active.left - current.left, dy = active.top - current.top;
                const finished = Math.abs(dx) <= 1 && Math.abs(dy) <= 1;
                const progress = finished ? 1 : 1 - Math.exp(-Math.max(0, now - active.lastFrame) / decayTimeMs);
                active.lastFrame = now;
                (active.target === document.scrollingElement ? window : active.target).scrollTo({
                    left: current.left + dx * progress, top: current.top + dy * progress, behavior: "instant"
                });
                active.applied = position(active.target);
                if (finished) { stop(); return; }
                frame = requestAnimationFrame(advance);
            };
            addEventListener("pointerdown", stop, { capture: true, passive: true });
            reducedMotion.addEventListener("change", stop);
            document.addEventListener("visibilitychange", () => { if (document.hidden) stop(); });
            addEventListener("keydown", event => {
                if (["ArrowUp","ArrowDown","ArrowLeft","ArrowRight","PageUp","PageDown","Home","End"," "].includes(event.key)) stop();
            }, true);
            addEventListener("wheel", event => {
                if (!event.isTrusted) return;
                if (event.ctrlKey || event.defaultPrevented || reducedMotion.matches) { stop(); return; }
                state.wheels++;
                const path = event.composedPath().filter(node => node instanceof Element);
                if (path.some(node => node.matches("input,select") || node.isContentEditable)) { stop(); return; }
                let dx = event.deltaX, dy = event.deltaY;
                if (event.shiftKey && !dx) { dx = dy; dy = 0; }
                const root = document.scrollingElement;
                if (!root || (!dx && !dy)) return;
                let target = root;
                for (const element of path) {
                    if (element === root) break;
                    const style = getComputedStyle(element);
                    const vertical = /auto|scroll|overlay/.test(style.overflowY) && element.scrollHeight > element.clientHeight;
                    const horizontal = /auto|scroll|overlay/.test(style.overflowX) && element.scrollWidth > element.clientWidth;
                    const maximumX = Math.max(0, element.scrollWidth - element.clientWidth);
                    const minimumX = style.direction === "rtl" ? -maximumX : 0;
                    if ((dy && vertical && (dy < 0 ? element.scrollTop > 0 : element.scrollTop < element.scrollHeight - element.clientHeight - 1))
                        || (dx && horizontal && (dx < 0 ? element.scrollLeft > minimumX : element.scrollLeft < (minimumX < 0 ? 0 : maximumX) - 1))) {
                        target = element;
                        break;
                    }
                    if ((dy && vertical && style.overscrollBehaviorY !== "auto")
                        || (dx && horizontal && style.overscrollBehaviorX !== "auto")) { stop(); return; }
                }
                const x = Math.sign(dx), y = Math.sign(dy);
                if (!event.cancelable) { state.uncancelable++; return; }
                if (getComputedStyle(target).scrollSnapType !== "none") { stop(); return; }
                const current = position(target);
                const width = target === root ? innerWidth : target.clientWidth;
                const height = target === root ? innerHeight : target.clientHeight;
                const maximumX = Math.max(0, target.scrollWidth - width);
                const maximumY = Math.max(0, target.scrollHeight - height);
                const rtl = getComputedStyle(target).direction === "rtl";
                const canMoveX = dx && (dx < 0 ? current.left > (rtl ? -maximumX : 0) : current.left < (rtl ? 0 : maximumX));
                const canMoveY = dy && (dy < 0 ? current.top > 0 : current.top < maximumY);
                if (!canMoveX && !canMoveY) { stop(); return; }
                if (event.deltaMode === WheelEvent.DOM_DELTA_LINE) {
                    const style = getComputedStyle(target);
                    const line = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.2;
                    dx *= line; dy *= line;
                } else if (event.deltaMode === WheelEvent.DOM_DELTA_PAGE) {
                    dx *= target === root ? innerWidth : target.clientWidth;
                    dy *= target === root ? innerHeight : target.clientHeight;
                }
                event.preventDefault();
                if (active && (active.target !== target
                    || Math.abs(current.left - active.applied.left) > 1 || Math.abs(current.top - active.applied.top) > 1
                    || (x && active.x && x !== active.x) || (y && active.y && y !== active.y))) {
                    // Retarget the running frame; restarting RAF here adds an input frame of delay.
                    active = { target, left: current.left, top: current.top,
                        applied: current, lastFrame: active.lastFrame };
                    state.interruptions++;
                }
                if (!active) active = { target, left: current.left, top: current.top, applied: current, lastFrame: performance.now() };
                active.left = Math.max(rtl ? -maximumX : 0, Math.min(rtl ? 0 : maximumX, active.left + dx));
                active.top = Math.max(0, Math.min(maximumY, active.top + dy));
                active.x = x; active.y = y;
                state.coasting = true;
                if (!frame) frame = requestAnimationFrame(advance);
            }, { passive: false });
        })()
        """;
}
