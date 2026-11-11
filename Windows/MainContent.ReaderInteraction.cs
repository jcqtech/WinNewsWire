using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace WinNewsWire;

public sealed partial class MainContent
{
    private const string ReaderBindingName = "__winNewsWireReaderMessage";
    internal const string ReaderWorldName = "WinNewsWire.Reader";
    private CoreWebView2DevToolsProtocolEventReceiver? _readerMessages;
    private int? _readerExecutionContext;
    private ulong _readerNavigation;
    private bool _readerRuntimeEnabled;

#if PARITY_PROBE
    internal bool ProbeReaderInteractionReady => _readerExecutionContext is not null && _readerNavigation == _articleNavigationId;
    internal int ProbeReaderScrollMessages, ProbeReaderHoverMessages;
    internal long ProbeReaderMessageTicks;
    private bool _probeReaderMessagesPaused;
    internal void ProbeEnableReaderMessages(bool enabled)
    {
        if (_readerMessages is null || _probeReaderMessagesPaused == !enabled) return;
        if (enabled) _readerMessages.DevToolsProtocolEventReceived += ReaderMessageReceived;
        else _readerMessages.DevToolsProtocolEventReceived -= ReaderMessageReceived;
        _probeReaderMessagesPaused = !enabled;
    }
#endif

    private async Task InitializeReaderInteractionAsync(CoreWebView2 core)
    {
        _readerMessages = core.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled");
        _readerMessages.DevToolsProtocolEventReceived += ReaderMessageReceived;
        await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        _readerRuntimeEnabled = true;
        if (_shuttingDown) return;
        await core.CallDevToolsProtocolMethodAsync("Runtime.addBinding",
            JsonSerializer.Serialize(new { name = ReaderBindingName, executionContextName = ReaderWorldName }));
    }

    private async Task InstallReaderInteractionAsync(CoreWebView2 core, ulong navigation)
    {
        using var tree = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
        if (_shuttingDown || navigation != _articleNavigationId) return;
        var frame = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()
            ?? throw new InvalidDataException("The browser did not return its main frame.");
        using var world = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
            JsonSerializer.Serialize(new { frameId = frame, worldName = ReaderWorldName })));
        if (_shuttingDown || navigation != _articleNavigationId) return;
        int context = world.RootElement.GetProperty("executionContextId").GetInt32();
        using var installed = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new
            {
                contextId = context,
                expression = "(" + ReaderInteractionScript + ")(" + JsonSerializer.Serialize(navigation.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ");"
                    + AppRuntime.WebViewScrolling.ReaderScript,
                returnByValue = true
            })));
        if (_shuttingDown || navigation != _articleNavigationId) return;
        if (installed.RootElement.TryGetProperty("exceptionDetails", out var error))
            throw new InvalidOperationException("Could not install the reader interaction script: " + error);
        _readerExecutionContext = context;
        _readerNavigation = navigation;
    }

    private void ReaderMessageReceived(CoreWebView2 sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (_shuttingDown || _readerExecutionContext is null || _readerNavigation != _articleNavigationId) return;
#if PARITY_PROBE
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        try
        {
            using var message = JsonDocument.Parse(args.ParameterObjectAsJson);
            var envelope = message.RootElement;
            if (envelope.GetProperty("name").GetString() != ReaderBindingName
                || envelope.GetProperty("executionContextId").GetInt32() != _readerExecutionContext) return;
            using var payload = JsonDocument.Parse(envelope.GetProperty("payload").GetString()
                ?? throw new InvalidDataException("The reader returned an empty message."));
            var root = payload.RootElement;
            if (root.GetProperty("navigation").GetString() != _readerNavigation.ToString(System.Globalization.CultureInfo.InvariantCulture)) return;
            switch (root.GetProperty("type").GetString())
            {
                case "shortcut":
                    DispatchWebViewShortcut(root.GetProperty("cmd").GetString());
                    break;
                case "scroll":
#if PARITY_PROBE
                    ProbeReaderScrollMessages++;
#endif
                    if (_showingWebPage || _pendingArticleDocumentUri is not null
                        || ViewModel.SelectedArticle is not { } scrollingArticle
                        || ViewModels.MainViewModel.Identity(scrollingArticle) != _renderedArticleIdentity) break;
                    double y = root.GetProperty("offset").GetDouble();
                    if (!double.IsFinite(y) || y < 0) throw new InvalidDataException("The reader returned an invalid scroll position.");
                    _articleScrollPosition = y;
                    break;
                case "linkHover":
#if PARITY_PROBE
                    ProbeReaderHoverMessages++;
#endif
                    ViewModel.SetHoveredLink(root.GetProperty("url").GetString());
                    break;
                case "openLink":
                    if (!_showingWebPage && ViewModel.SelectedArticle is { } selected
                        && ViewModels.MainViewModel.Identity(selected) == _renderedArticleIdentity)
                        TrackBrowserTask(OpenLinkInWebViewAsync(root.GetProperty("url").GetString()
                            ?? throw new InvalidDataException("The reader returned an empty link.")));
                    break;
                default:
                    throw new InvalidDataException("The reader returned an unknown message type.");
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            App.LogException("Reader interaction", error);
            ViewModel.StatusMessage = "Could not process the reader action: " + error.Message;
        }
#if PARITY_PROBE
        finally { ProbeReaderMessageTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started; }
#endif
    }

    // The isolated world keeps host callbacks running without enabling publisher scripts.
    private const string ReaderInteractionScript = """
        navigation => {
            if (window.top !== window || globalThis.__wnwReaderInstalled) return;
            const binding = globalThis.__winNewsWireReaderMessage;
            if (typeof binding !== "function") throw new Error("The reader host binding is unavailable.");
            const send = message => binding(JSON.stringify({ ...message, navigation }));
            globalThis.__wnwReaderInstalled = true;
            let scrollTimer = 0;
            let lastScroll = window.scrollY;
            const reportScroll = () => {
                clearTimeout(scrollTimer);
                scrollTimer = 0;
                if (globalThis.__wnwWheelScrolling?.coasting) return;
                const offset = window.scrollY;
                if (offset === lastScroll) return;
                lastScroll = offset;
                send({ type: "scroll", offset });
            };
            globalThis.__wnwReportScroll = reportScroll;
            window.addEventListener("scroll", () => {
                if (globalThis.__wnwWheelScrolling?.coasting) return;
                if (!scrollTimer) scrollTimer = setTimeout(reportScroll, 100);
            }, { passive: true });
            window.addEventListener("scrollend", reportScroll, { passive: true });
            let hoveredLink = "";
            const reportHover = anchor => {
                const url = anchor?.href ?? "";
                if (url === hoveredLink) return;
                hoveredLink = url;
                send({ type: "linkHover", url });
            };
            document.addEventListener("mouseover", event => {
                const anchor = event.target instanceof Element ? event.target.closest("a") : null;
                reportHover(anchor);
            });
            document.addEventListener("mouseout", event => {
                const anchor = event.relatedTarget instanceof Element ? event.relatedTarget.closest("a") : null;
                reportHover(anchor);
            });
            document.addEventListener("click", event => {
                const anchor = event.target instanceof Element ? event.target.closest("a[href]") : null;
                const href = anchor?.getAttribute("href");
                if (!href) return;
                if (!href.startsWith("#")) {
                    if (!event.isTrusted || event.button !== 0) return;
                    event.preventDefault();
                    send({ type: "openLink", url: anchor.href });
                    return;
                }
                if (href.length < 2) return;
                let id = href.slice(1);
                try { id = decodeURIComponent(id); }
                catch (error) { if (!(error instanceof URIError)) throw error; }
                const target = document.getElementById(id) || document.getElementsByName(id)[0];
                if (!target) return;
                event.preventDefault();
                target.scrollIntoView();
            }, true);
            document.addEventListener("keydown", event => {
                if (!event.isTrusted) return;
                if (event.key === "F11" && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey) {
                    event.preventDefault();
                    event.stopPropagation();
                    send({ type: "shortcut", cmd: "fullScreen" });
                    return;
                }
                const key = event.key.toLowerCase();
                const ctrl = event.ctrlKey || event.metaKey;
                const shift = event.shiftKey;
                const alt = event.altKey;
                let globalCommand = null;
                if (ctrl && !alt && !shift) {
                    if (key === "1") globalCommand = "gotoToday";
                    if (key === "2") globalCommand = "gotoUnread";
                    if (key === "3") globalCommand = "gotoStarred";
                    if (key === "f") globalCommand = "findInArticle";
                    if (key === "g") globalCommand = "findNext";
                    if (key === "e") globalCommand = "useSelectionForFind";
                    if (key === "j") globalCommand = "jumpToSelection";
                } else if (ctrl && shift && !alt && key === "c") globalCommand = "copyArticleUrls";
                else if (ctrl && alt && !shift && key === "c") globalCommand = "copyExternalUrls";
                else if (ctrl && shift && !alt && key === "g") globalCommand = "findPrevious";
                else if (ctrl && alt && !shift && key === "f") globalCommand = "articleSearch";
                else if (ctrl && alt && !shift && key === "t") globalCommand = "toggleToolbar";
                else if (!ctrl && !alt && key === "f3") globalCommand = shift ? "findPrevious" : "findNext";
                if (globalCommand) {
                    event.preventDefault();
                    event.stopPropagation();
                    send({ type: "shortcut", cmd: globalCommand });
                    return;
                }
                if (event.composedPath().some(target => target instanceof Element &&
                    (target.matches("input, textarea, select") || target.isContentEditable))) return;
                let cmd = null;
                if (!ctrl && !alt && !shift) {
                    switch (key) {
                        case "j": cmd = "next"; break;
                        case "k": cmd = "prev"; break;
                        case "r": cmd = "refresh"; break;
                        case "n": cmd = "nextUnread"; break;
                        case "m": case "u": cmd = "markRead"; break;
                        case "s": case "l": cmd = "toggleStar"; break;
                        case " ":
                            const scroller = document.scrollingElement || document.documentElement;
                            if (window.scrollY + window.innerHeight < scroller.scrollHeight - 2) {
                                window.scrollBy({ top: window.innerHeight * 0.9, behavior: "smooth" });
                                event.preventDefault();
                                return;
                            }
                            cmd = "nextUnread"; break;
                    }
                } else if (ctrl && !alt && !shift) {
                    switch (key) {
                        case "arrowdown": cmd = "next"; break;
                        case "arrowup": cmd = "prev"; break;
                        case "r": cmd = "refresh"; break;
                        case "k": cmd = "markAllRead"; break;
                        case "i": cmd = "inspector"; break;
                        case "n": cmd = "newFeed"; break;
                        case "l": cmd = "toggleStar"; break;
                        case "b": cmd = "openBrowser"; break;
                    }
                } else if (ctrl && shift && !alt) {
                    switch (key) {
                        case "b": cmd = "toggleSidebar"; break;
                        case "w": cmd = "toggleReaderMode"; break;
                        case "n": cmd = "newFolder"; break;
                    }
                }
                if (cmd) {
                    event.preventDefault();
                    event.stopPropagation();
                    send({ type: "shortcut", cmd });
                }
            }, true);
        }
        """;
}
