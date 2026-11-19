#if PARITY_PROBE
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using WinNewsWire.AppShared;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using WinNewsWire.Account;
using WinNewsWire.AppShared.ArticleThemes;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.AppShared.Timeline;
using WinNewsWire.AppShared.Toolbar;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.Helpers;
using WinRT.Interop;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    public static string OutputDirectory { get; } = OutputPath();
    private sealed record Result(string Name, bool Passed, string? Error, object? Details);
    private static readonly List<Result> Results = new();

    private static string OutputPath()
    {
        var argument = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault()
            ?? throw new ArgumentException("The parity probe requires an absolute output directory.");
        if (!Path.IsPathFullyQualified(argument)) throw new ArgumentException("The parity probe output directory must be absolute.");
        var directory = Path.GetFullPath(argument);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static async Task Run(MainWindow window)
    {
        var server = new FixtureServer();
        try
        {
            if (Environment.GetCommandLineArgs().Contains("--com-automation"))
            {
                await CheckAsync("Installed COM automation, live UI and provider scenarios", () => CheckComAutomationAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--window-resources"))
            {
                await CheckAsync("Pure WinUI and application window resource comparison", () => CheckWindowResourcesAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--bugbash-restart"))
            {
                await CheckAsync("Bugbash restart preserves subscriptions and read/star state", () => CheckBugbashRestartAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--bugbash"))
            {
                await RunBugbashAsync(window, server.Url);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--refresh-scrolling"))
            {
                await CheckAsync("Launched Debug app scrolling during full-runtime feed refresh",
                    () => CheckRefreshScrollingAsync(window, server.Url));
                return;
            }
            var account = AccountManager.Shared.DefaultAccount!;
            var feed = account.AddFeed(server.Url + "/feed", "Probe feed", null);
            feed.FaviconUrl = server.Url + "/icon.png";
            feed.HomePageUrl = server.Url + "/";
            var status = new ArticleStatus("probe-article", true, false, DateTime.UtcNow);
            var article = new Article(account.AccountID, status.ArticleID, feed.FeedID, "probe-unique",
                "A <b>formatted</b> title", """
                <p id="probe-body">WinUI article fixture</p>
                <div class="instapaper_ignore">Remove this</div>
                <table id="probe-table"><tr><td>Cell</td></tr></table>
                <script>window.articleScriptRan = true;</script>
                """, null, null, server.Url + "/article", null, null, null, DateTime.UtcNow, null, null, null, status);
            await account.Database.UpdateArticlesAsync([article]);
            AppDefaults.Shared.ArticleContentJavascriptEnabled = true;
            window.Activate();
            await WaitFor(() => Task.FromResult(window.ProbeContent.IsLoaded && window.ProbeContent.ProbeWebViewReady), "WinUI/WebView initialization");
            window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(window.CaptureState().Bounds with
                { Width = (int)Math.Ceiling(1350 * window.ProbeContent.XamlRoot.RasterizationScale) }));
            await WaitFor(() => Task.FromResult(window.ProbeContent.ActualWidth > 1000), "Expanded reader test layout");
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                var runtime = process.Modules.Cast<System.Diagnostics.ProcessModule>().Single(module =>
                    module.ModuleName.Equals("Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase));
                File.WriteAllText(Path.Combine(OutputDirectory, "runtime.json"), JsonSerializer.Serialize(new
                {
                    runtime.FileName, runtime.FileVersionInfo.FileVersion,
                    GraphicsDrivers = process.Modules.Cast<System.Diagnostics.ProcessModule>()
                        .Where(module => module.ModuleName.StartsWith("igd", StringComparison.OrdinalIgnoreCase)
                            || module.ModuleName.StartsWith("nvwg", StringComparison.OrdinalIgnoreCase))
                        .Select(module => new { module.ModuleName, module.FileVersionInfo.FileVersion }).ToArray(),
                    BrowserVersion = window.ProbeContent.ProbeWebView.CoreWebView2.Environment.BrowserVersionString,
                    SoftwareRasterizationEnabled = AppRuntime.WebViewEnvironment.UsesSoftwareRasterization,
                    DirectScrollingEnabled = AppRuntime.WebViewEnvironment.UsesDirectScrolling,
                    BrowserArguments = AppRuntime.WebViewEnvironment.ProbeBrowserArguments,
                    BrowserProcessID = window.ProbeContent.ProbeWebView.CoreWebView2.BrowserProcessId,
                    UserDataFolder = window.ProbeContent.ProbeWebView.CoreWebView2.Environment.UserDataFolder
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (Environment.GetCommandLineArgs().Contains("--installed-runtime"))
                    Require(!runtime.FileName.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
                        "The probe did not load the installed Windows App Runtime used by normal Debug.");
            }

            if (Environment.GetCommandLineArgs().Contains("--theme-parity"))
            {
                await CheckAsync("All bundled themes render through native menus with live CSS, fonts and isolated local assets",
                    () => CheckThemeParityAsync(window, server.Url));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--theme-import"))
            {
                await CheckAsync("Theme imports require metadata and overwrite confirmation before changing installed files",
                    () => CheckThemeImportConfirmationAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--network-startup"))
            {
                await CheckAsync("Cold thumbnail initialization does not wait for the portable network monitor",
                    CheckNetworkStartupResponsivenessAsync);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--thumbnail-viewport")
                || Environment.GetCommandLineArgs().Contains("--thumbnail-prefetch"))
            {
                await CheckAsync("Visible-only native timeline thumbnail loading", async () =>
                {
                    await CheckVisibleOnlyThumbnailLoadingAsync(window);
                    return new { Downward = true, Upward = true, NoOffscreenDownloads = true,
                        MetadataDiscovery = true, UnchangedViewportSuppressed = true };
                });
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--thumbnail-sizes"))
            {
                await CheckAsync("DPI-aware oriented thumbnail display-size decoding", async () =>
                {
                    await CheckThumbnailDisplaySizesAsync(window);
                    return new { Landscape = true, Rotated = true, Wide = true, Portrait = true, Cached = true };
                });
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--thumbnail-decodes"))
            {
                await CheckAsync("Bounded native thumbnail decoding, promotion, overflow and shutdown", async () =>
                {
                    await CheckThumbnailDecodeQueueAsync(window);
                    return new { FourActive = true, SixtyWaiting = true, VisiblePromotion = true,
                        NewestFirst = true, SeparateModes = true, OverflowCanRetry = true, Shutdown = true };
                });
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-separators"))
            {
                await CheckAsync("Timeline separators suppress selected and final rows across item changes",
                    () => CheckTimelineSeparatorsAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--appearance-palette"))
            {
                await CheckAsync("Appearance settings apply and synchronize system, light and dark modes",
                    () => CheckAppearancePaletteAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--grayscale-setting"))
            {
                await CheckAsync("The grayscale icon preference persists and synchronizes",
                    () => CheckGrayscalePreferenceAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--grayscale-icons"))
            {
                await CheckAsync("Grayscale icon pixels are cached while thumbnails and source files retain color",
                    () => CheckGrayscaleIconSourcesAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--grayscale-live"))
            {
                await CheckAsync("Grayscale changes update sidebar, timeline and reader icons without reloading the reader",
                    () => CheckLiveGrayscaleIconsAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--grayscale-revision-sharing"))
            {
                await CheckAsync("Different view revisions share grayscale work while real file changes update every view",
                    () => CheckGrayscaleRevisionSharingAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-measurement-retention"))
            {
                await CheckAsync("Timeline layout does not retain a second copy of every measured article title",
                    CheckTimelineMeasurementRetentionAsync);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--icon-contrast"))
            {
                await CheckAsync("Cached icon luminance, system fills and file invalidation", () => CheckIconContrastAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--performance-only"))
            {
                await CheckAsync("Reader workload measurements and bounded header work", () => CheckPerformanceAsync(window, server.Url));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--article-scrolling-only"))
            {
                await CheckAsync("Direct article-body scrolling performance", () => CheckArticleScrollingAsync(window, server.Url));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--native-article-scrolling"))
            {
                await CheckAsync("Article lists retain native scrolling in both layouts", () => CheckNativeArticleScrollingAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--reader-input-latency")
                || Environment.GetCommandLineArgs().Contains("--input-policy"))
            {
                await CheckAsync("Sidebar wheel policy leaves article scrolling native", () => CheckNativeWheelPolicyAsync(window));
                await CheckAsync("Direct wheel policy and retained touchscreen momentum",
                    () => CheckReaderInputLatencyAsync(window, server.Url));
                await CheckAsync("Embedded webpages use the same direct wheel policy",
                    () => CheckPublishedWheelPolicyAsync(window, server.Url));
                await CheckAsync("Wheel inertia preference, restart boundary and rollback",
                    () => CheckRenderingPreferenceAsync(window, scrolling: true));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--statusbar-invalidation"))
            {
                await CheckAsync("Status-bar text updates do not invalidate unrelated toolbar layout",
                    () => CheckStatusBarInvalidationScopeAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--icon-retention"))
            {
                await CheckAsync("Desktop icon variants remain cached without the removed automatic memory monitor", async () =>
                {
                    Require(typeof(AppRuntime.AppService).Assembly.GetType("WinNewsWire.AppRuntime.MemoryPressureMonitor") is null,
                        "The automatic desktop memory-pressure monitor is still present.");
                    return await CheckGrayscaleIconSourcesAsync(window);
                });
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--sidebar-delete-identity"))
            {
                await CheckAsync("Confirmed sidebar deletion uses live identities and selects a surviving row",
                    () => CheckSidebarDeletionIdentityAsync(window, server.Url));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--thumbnail-network-policy"))
            {
                await CheckAsync("Connected constrained networks allow thumbnails but not background icon revalidation",
                    () => CheckConstrainedThumbnailPolicyAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--sidebar-icons"))
            {
                await CheckAsync("Sidebar feed, folder and smart-feed icons follow preferences without replacing rows",
                    () => CheckSidebarIconsAsync(window, server.Url));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--sidebar-text-size"))
            {
                await CheckAsync("Sidebar text size updates labels, icons and native row heights while preserving selection",
                    () => CheckSidebarTextSizeAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--smart-feed-visibility"))
            {
                await CheckAsync("Smart-feed visibility updates outline, navigation and future windows without destroying feeds",
                    () => CheckSmartFeedVisibilityAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-icon-policy"))
            {
                await CheckAsync("Timeline icon policy works in standard and column layouts without empty icon slots",
                    () => CheckTimelineIconPolicyAsync(window, server.Url));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-byline-fallback"))
            {
                await CheckAsync("Timeline bylines choose authors for single feeds and never leave a missing-author gap",
                    () => CheckTimelineIconPolicyAsync(window, server.Url, byline: true));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-icon-recheck"))
            {
                await CheckAsync("Automatic timeline icons recheck avatars when article content changes without changing count or scope",
                    () => CheckTimelineIconPolicyAsync(window, server.Url, recheck: true));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-icon-size"))
            {
                await CheckAsync("Timeline icon sizes update native rows and preserve compact columns",
                    () => CheckTimelineIconSizeAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-text-size"))
            {
                await CheckAsync("Timeline typography follows preferences in standard and column layouts",
                    () => CheckTimelineTextSizeAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-lines"))
            {
                await CheckAsync("Timeline title and preview share the selected line budget, including titleless articles",
                    () => CheckTimelineLinesAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-row-heights"))
            {
                await CheckAsync("Timeline rows size independently and reserve the full small-thumbnail height",
                    () => CheckVariableTimelineHeightsAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-identity-heights"))
            {
                await CheckAsync("Rows with the same article ID in different accounts keep independent native heights",
                    () => CheckTimelineIdentityHeightsAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--thumbnail-byline"))
            {
                await CheckAsync("The top date spans small thumbnails while the thumbnail aligns with the title",
                    () => CheckVariableTimelineHeightsAsync(window, byline: true));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-full-titles"))
            {
                await CheckAsync("Full-title mode hides previews, caps titles and restores the saved normal line budget",
                    () => CheckTimelineFullTitlesAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-read-fading"))
            {
                await CheckAsync("Read fading preserves selections, unread rows and separators in both layouts",
                    () => CheckTimelineReadFadingAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-byline"))
            {
                await CheckAsync("Timeline byline and date move together above or below the title",
                    () => CheckTimelineBylineLayoutAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-titleless-limit"))
            {
                await CheckAsync("Titleless articles use at most four lines in full-title mode",
                    () => CheckTitlelessFullTitleLimitAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--thumbnail-squares"))
            {
                await CheckThumbnailSquaresAsync(window);
                await CheckAsync("Large thumbnails retain oriented aspect-fit decoding", async () =>
                {
                    await CheckThumbnailDisplaySizesAsync(window);
                    return new { Landscape = true, Rotated = true, Wide = true, Portrait = true, Cached = true };
                });
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--thumbnail-fit-geometry"))
            {
                await CheckAsync("Whole-point centered thumbnail aspect-fit geometry", () =>
                {
                    var container = new Rect(20, 40, 400, 180);
                    Require(ThumbnailView.AspectFitRect(container, new Size(1500, 1000))
                        .Equals(new Rect(85, 40, 270, 180)), "A landscape image was not fitted and centered.");
                    Require(ThumbnailView.AspectFitRect(new Rect(0, 0, 80, 80), new Size(40, 120))
                        .Equals(new Rect(26, 0, 27, 80)), "A fractional portrait fit was not rounded to whole points.");
                    Require(ThumbnailView.AspectFitRect(container, new Size(0, 1000)).Equals(container)
                        && ThumbnailView.AspectFitRect(container, new Size(1500, 0)).Equals(container),
                        "An unloaded image did not retain its full placeholder rectangle.");
                    return Task.FromResult<object?>(new { Landscape = true, RoundedPortrait = true, EmptyPlaceholder = true });
                });
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--thumbnail-presentation"))
            {
                await CheckAsync("Native thumbnail placeholders and image-sized rounded clipping",
                    () => CheckThumbnailPresentationAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--thumbnail-fit-corners"))
            {
                await CheckAsync("Thumbnail corners clip the fitted image, not its slot",
                    () => CheckThumbnailPresentationAsync(window, checkPlaceholder: false));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--icon-system-fill"))
            {
                await CheckAsync("System-themed icon fills blend over their backing surface",
                    () => CheckIconSystemFillAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--feed-icon-recovery"))
            {
                await CheckAsync("Recovered feed icons replace native timeline fallbacks", async () =>
                {
                    await CheckRecoveredFeedIconsAsync(window);
                    await CheckRecoveredFeedIconsAsync(window, newHomepageIcon: true);
                    return new { NativeFallbackReplacement = true, ChangedHomepageIcon = true,
                        DispatchedOnUI = true, UnrelatedRowsPreserved = true };
                });
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--favicon-recovery"))
            {
                await CheckAsync("Recovered favicons update native sidebar and timeline without replacing preferred icons", async () =>
                {
                    await CheckRecoveredFaviconsAsync(window, preferredIcon: false);
                    await CheckRecoveredFaviconsAsync(window, preferredIcon: true);
                    return new { NativeRecovery = true, SidebarRecovery = true, PreferredIconPreserved = true,
                        DispatchedOnUI = true, UnrelatedRowsPreserved = true };
                });
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--feed-image-settings"))
            {
                foreach (var setting in new[] { "icon", "favicon", "homepage" })
                    await CheckAsync($"Warm {setting} changes replace native feed images",
                        () => CheckChangedFeedImageSettingsAsync(window, setting));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--changed-feed-icons"))
            {
                foreach (bool movingPath in new[] { false, true })
                    await CheckAsync($"Changed feed icons refresh native and reader pixels with moving path={movingPath}", async () =>
                    {
                        await CheckChangedFeedIconsAsync(window, movingPath);
                        return new { NativePixels = true, ReaderPixels = true, MovingPath = movingPath, NoNavigation = true };
                    });
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--hidden-feed-icon-lookups"))
            {
                await CheckAsync("Hidden timeline icons skip lookups while the reader and later visible rows still load",
                    () => CheckHiddenFeedIconLookupsAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--thumbnail-network-heights"))
            {
                await CheckAsync("Network changes update large thumbnail heights without moving the viewport",
                    () => CheckThumbnailNetworkHeightsAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--reader-thumbnail-layout"))
            {
                await CheckAsync("Reader image sharing follows standard layout and thumbnail visibility",
                    () => CheckReaderThumbnailLayoutAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--thumbnail-choice-order"))
            {
                await CheckAsync("Thumbnail choices put None first without changing the saved mode",
                    () => CheckThumbnailChoiceOrderAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--view-menu-sections"))
            {
                await CheckAsync("View menu groups sidebar, timeline, status bar and toolbar commands",
                    () => CheckViewMenuSectionsAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--reader-resize-anchor")
                || Environment.GetCommandLineArgs().Contains("--reader-resize-fraction")
                || Environment.GetCommandLineArgs().Contains("--reader-resize-gap"))
            {
                bool fractional = Environment.GetCommandLineArgs().Contains("--reader-resize-fraction");
                bool gap = Environment.GetCommandLineArgs().Contains("--reader-resize-gap");
                await CheckAsync($"Reader resize preserves the article paragraph and saved position with fractional={fractional}, gap={gap}",
                    () => CheckReaderResizeAnchorAsync(window, fractional, gap));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--thumbnail-cache-retention"))
            {
                await CheckAsync("Thumbnail choices survive desktop display-mode changes", () => CheckThumbnailCacheRetentionAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--thumbnails"))
            {
                await CheckAsync("Worker-decoded native thumbnails, mode caches and lifetime",
                    () => CheckThumbnailsAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--version-labels"))
            {
                await CheckAsync("Copyable application version labels", CheckVersionLabelsAsync);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--status-bar"))
            {
                await CheckAsync("Desktop status bar commands, links, progress and shared visibility",
                    () => CheckStatusBarAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--pane-headers"))
            {
                await CheckAsync("Article renderer, native scripts and five text sizes", () => CheckArticleAsync(window));
                await CheckAsync("Pane headers, title-bar input and column presentation", () => CheckPaneHeadersAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--window-chrome"))
            {
                await CheckAsync("Top-right settings, flush pane edges and reduced sidebar gutter", () => CheckWindowChromeAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--pane-footer-seam"))
            {
                await CheckAsync("Left pane shadow stays above the square single-separator footer seam",
                    () => CheckPaneFooterSeamAsync(window));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--window-restoration"))
            {
                bool scripts = AppDefaults.Shared.ArticleContentJavascriptEnabled;
                try
                {
                    AppDefaults.Shared.ArticleContentJavascriptEnabled = false;
                    await CheckAsync("Window restoration, independent sorting, saved selection and reading position",
                        () => CheckWindowRestorationAsync(window, server.Url));
                }
                finally { AppDefaults.Shared.ArticleContentJavascriptEnabled = scripts; }
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-visibility"))
            {
                await CheckAsync("Timeline navigation respects pinned headers in both layouts",
                    () => CheckTimelineVisibilityAsync(window, server.Url));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--reader-regressions"))
            {
                await CheckAsync("Notification registration leaves the UI responsive and drains on close", () => CheckNotificationRegistrationAsync(window));
                await CheckAsync("Compatibility rendering preference, restart boundary and shared browser", () => CheckRenderingPreferenceAsync(window));
                await CheckAsync("Direct scrolling preference and restart boundary", () => CheckRenderingPreferenceAsync(window, scrolling: true));
                await CheckAsync("Article renderer, native scripts and five text sizes", () => CheckArticleAsync(window));
                await CheckAsync("Explicit derived-cache cleanup preserves live article identity", () => CheckMemoryCleanupAsync(window));
                await CheckAsync("Native browser arrow keys and scrollbar dragging", () => CheckBrowserScrollInputsAsync(window, server.Url));
                await CheckAsync("Unrequested data documents cannot replace the article", () => CheckArticleNavigationPolicyAsync(window));
                await CheckAsync("Trusted page processing with article JavaScript disabled", () => CheckScriptPolicyAsync(window));
                await CheckAsync("Decoded feed-image cache, bitmap binding and visible icon pixels", () => CheckFeedImageAsync(window, feed));
                await CheckAsync("Independent main windows share statuses without sharing selection or undo", () => CheckMultipleWindowsAsync(window));
                await CheckAsync("Reading unread articles preserves timeline and sidebar rows", () => CheckReadingStateAsync(window, server.Url));
                await CheckAsync("Blocked homepage, feed bodies and late favicon without document reload", () => CheckReadingFaviconAsync(window));
                if (Environment.GetCommandLineArgs().Contains("--header-layout"))
                {
                    await CheckAsync("Pane headers, title-bar input and column presentation", () => CheckPaneHeadersAsync(window));
                    await CheckAsync("Native column layout, table cells, sorting, reordering and split restoration", () => CheckColumnLayoutAsync(window, server.Url));
                    await CheckAsync("Window minimum width follows the article pane across layouts", () => CheckMinimumWindowWidthAsync(window));
                    await CheckAsync("Responsive menu overflow preserves commands, nested menus and keyboard shortcuts", () => CheckMenuOverflowAsync(window));
                    await CheckAsync("Shared header customization, overflow and fixed title controls", () => CheckToolbarAsync(window));
                    await CheckAsync("Theme command insertion and acrylic header overlay", () => CheckThemeCommandAndAcrylicAsync(window));
                    await CheckAsync("Fixed sidebar commands, contiguous column headers and article read filtering",
                        () => CheckColumnHeaderControlsAsync(window, server.Url));
                    await CheckAsync("Native title-bar button motion and compact left inset", () => CheckTitleBarMotionAsync(window));
                }
                if (Environment.GetCommandLineArgs().Contains("--sidebar-layout"))
                {
                    await CheckAsync("Static sidebar headings, folder icons and fade-down disclosure", () => CheckSidebarPresentationAsync(window));
                    await CheckAsync("Independent sidebar selection, scope filters, rebuild and batch undo", () => CheckSidebarSelectionAsync(window, server.Url));
                    await CheckAsync("Folder disclosure, static accounts and sidebar filter restoration", () => CheckSidebarFilterAsync(window, server.Url));
                    await CheckAsync("Unread count, dot and hidden modes across controls and windows", () => CheckUnreadCountDisplayAsync(window));
                    await CheckAsync("Native sidebar drop restrictions and mixed-account transfer semantics", () => CheckSidebarDropsAsync(window, server.Url));
                    await CheckAsync("Native feed dragging, saved order and folder placement", () => CheckSidebarDraggingAsync(window, server.Url));
                    await CheckAsync("Delete key is scoped to sidebar rows, not text or article controls", () => CheckSidebarDeleteKeyAsync(window, server.Url));
                }
                if (Environment.GetCommandLineArgs().Contains("--toolbar-controls"))
                {
                    await CheckAsync("Reading-status icons and customization scrollbar gutters", () => CheckToolbarControlsAsync(window));
                }
                if (Environment.GetCommandLineArgs().Contains("--navigation"))
                {
                    await CheckAsync("Back across articles, feeds and embedded web pages", () => CheckBrowsingAsync(window, server.Url));
                    await CheckAsync("New webpage loads never expose the previous document", () => CheckWebPagePresentationAsync(window));
                    Check("Original taskbar icon and separate small window icon", () => CheckWindowIcons(window));
                    await CheckAsync("Browser preference, background inversion and external article navigation", () => CheckBrowserAsync(window, server.Url));
                }
                if (Environment.GetCommandLineArgs().Contains("--atlantic"))
                    await CheckAsync("Live Atlantic subscription, every article and actual favicons", () => CheckAtlanticAsync(window));
                if (Environment.GetCommandLineArgs().Contains("--performance"))
                {
                    await CheckAsync("Cached icon luminance, contrast and file invalidation", () => CheckIconContrastAsync(window));
                    await CheckAsync("Reader workload measurements and bounded header work", () => CheckPerformanceAsync(window, server.Url));
                }
                if (Environment.GetCommandLineArgs().Contains("--article-scrolling"))
                {
                    await CheckAsync("Trusted reader shortcuts, editing and hover with publisher scripts disabled", () => CheckIsolatedEventsAsync(window));
                    await CheckAsync("Direct article-body scrolling performance", () => CheckArticleScrollingAsync(window, server.Url));
                }
                if (Environment.GetCommandLineArgs().Contains("--long-session"))
                    await CheckAsync("Long-session reader retention and callback stability", () => CheckLongSessionAsync(window, server.Url));
                if (Environment.GetCommandLineArgs().Contains("--opml-fixture"))
                    await CheckAsync("Supplied OPML import and large-profile scrolling", () => CheckOpmlPerformanceAsync(window));
                return;
            }

            Check("Attributed HTML WinUI font and decoration traits", CheckInlines);
            await CheckAsync("Compatibility rendering preference, restart boundary and shared browser", () => CheckRenderingPreferenceAsync(window));
            await CheckAsync("Direct scrolling preference and restart boundary", () => CheckRenderingPreferenceAsync(window, scrolling: true));
            Check("SingleLineTextFieldSizerTests.CellSizeMatchesFittingSize", CheckSingleLine);
            Check("MultilineTextFieldSizerTests.CellSizeMatchesFittingSize", CheckMultiline);
            await CheckAsync("Article renderer, native scripts and five text sizes", () => CheckArticleAsync(window));
            await CheckAsync("Native browser arrow keys and scrollbar dragging", () => CheckBrowserScrollInputsAsync(window, server.Url));
            await CheckAsync("Unrequested data documents cannot replace the article", () => CheckArticleNavigationPolicyAsync(window));
            await CheckAsync("Custom theme CSS and relative asset mapping", () => CheckThemeAsync(window));
            await CheckAsync("Trusted page processing with article JavaScript disabled", () => CheckScriptPolicyAsync(window));
            await CheckAsync("Trusted reader shortcuts, editing and hover with publisher scripts disabled", () => CheckIsolatedEventsAsync(window));
            await CheckAsync("Reader mode retains the selected theme", () => CheckReaderAsync(window));
            await CheckAsync("Decoded feed-image cache, bitmap binding and visible icon pixels", () => CheckFeedImageAsync(window, feed));
            await CheckAsync("Add-feed dialog destination, normalization and cancellation", () => CheckAddFeedDialogAsync(window));
            await CheckAsync("Independent main windows share statuses without sharing selection or undo", () => CheckMultipleWindowsAsync(window));
            await CheckAsync("Explicit derived-cache cleanup preserves live article identity", () => CheckMemoryCleanupAsync(window));
            await CheckAsync("Account statistics, accessible column resizing, sorting and database vacuum", () => CheckAccountStatsAsync(window, server.Url));
            await CheckAsync("Static sidebar headings, folder icons and fade-down disclosure", () => CheckSidebarPresentationAsync(window));
            await CheckAsync("Independent sidebar selection, scope filters, rebuild and batch undo", () => CheckSidebarSelectionAsync(window, server.Url));
            await CheckAsync("Folder disclosure, static accounts and sidebar filter restoration", () => CheckSidebarFilterAsync(window, server.Url));
            await CheckAsync("Native sidebar drop restrictions and mixed-account transfer semantics", () => CheckSidebarDropsAsync(window, server.Url));
            await CheckAsync("Native feed dragging, saved order and folder placement", () => CheckSidebarDraggingAsync(window, server.Url));
            await CheckAsync("Delete key is scoped to sidebar rows, not text or article controls", () => CheckSidebarDeleteKeyAsync(window, server.Url));
            await CheckAsync("Notification registration leaves the UI responsive and drains on close", () => CheckNotificationRegistrationAsync(window));
            await CheckAsync("Window restoration, independent sorting, saved selection and reading position", () => CheckWindowRestorationAsync(window, server.Url));
            await CheckAsync("Native column layout, table cells, sorting, reordering and split restoration", () => CheckColumnLayoutAsync(window, server.Url));
            await CheckAsync("Window minimum width follows the article pane across layouts", () => CheckMinimumWindowWidthAsync(window));
            await CheckAsync("Responsive menu overflow preserves commands, nested menus and keyboard shortcuts", () => CheckMenuOverflowAsync(window));
            await CheckAsync("Shared toolbar customization, display, tracking, overflow and search restoration", () => CheckToolbarAsync(window));
            await CheckAsync("Theme command insertion and acrylic header overlay", () => CheckThemeCommandAndAcrylicAsync(window));
            await CheckAsync("Fixed sidebar commands, contiguous column headers and article read filtering",
                () => CheckColumnHeaderControlsAsync(window, server.Url));
            await CheckAsync("Native title-bar button motion and compact left inset", () => CheckTitleBarMotionAsync(window));
            await CheckAsync("Full-screen menu, normal/maximized placement and restored presentation", () => CheckFullScreenAsync(window));
            await CheckAsync("Browser preference, background inversion and external article navigation", () => CheckBrowserAsync(window, server.Url));
            await CheckAsync("Back across articles, feeds and embedded web pages", () => CheckBrowsingAsync(window, server.Url));
            await CheckAsync("New webpage loads never expose the previous document", () => CheckWebPagePresentationAsync(window));
            Check("Original taskbar icon and separate small window icon", () => CheckWindowIcons(window));
            await CheckAsync("Article and external URL menu commands, selection and clipboard failures", () => CheckUrlCopyCommandsAsync(window, server.Url));
            await CheckAsync("Go smart scopes and native Help command routing", () => CheckGoAndHelpCommandsAsync(window));
            await CheckAsync("Unread count, dot and hidden modes across controls and windows", () => CheckUnreadCountDisplayAsync(window));
            await CheckAsync("Remembered folder destinations and dialog cancellation", () => CheckAddFolderDialogAsync(window));
            await CheckAsync("Next unread across timelines, folders and smart scopes", () => CheckNextUnreadAsync(window, server.Url));
            await CheckAsync("Native article Find, matching options, selection and navigation", () => CheckArticleFindAsync(window));
            await CheckAsync("Dinosaurs thresholds, selection and all-membership deletion undo", () => CheckDinosaursAsync(window, server.Url));
            await CheckAsync("Current Activity live states, completion linger and window lifetime", CheckCurrentActivityAsync);
            await CheckAsync("Shared-link data, subscription cancellation and failure propagation", () => CheckSharedFeedAsync(window, server.Url));
            await CheckAsync("Opt-in current-user automation, native references and shutdown", () => CheckAutomationAsync(window));
            await CheckAsync("Reader speech selection, ownership, cancellation and errors", () => CheckSpeechAsync(window));
            await CheckAsync("Reading unread articles preserves timeline and sidebar rows", () => CheckReadingStateAsync(window, server.Url));
            await CheckAsync("Blocked homepage, feed bodies and late favicon without document reload", () => CheckReadingFaviconAsync(window));
            if (Environment.GetCommandLineArgs().Contains("--quit"))
            {
                var other = App.OpenMainWindow();
                await WaitFor(() => Task.FromResult(other.ProbeContent.IsLoaded && other.ProbeContent.ProbeWebViewReady), "Quit companion window");
                other.ProbeContent.ViewModel.SetSortParameters(new(ArticleSortKey.Starred));
            }
        }
        catch (Exception error)
        {
            Results.Add(new("Probe initialization", false, error.ToString(), null));
        }
        finally
        {
            try { await server.DisposeAsync(); }
            catch (Exception error) { Results.Add(new("Fixture server shutdown", false, error.ToString(), null)); }
            await CheckAsync("Window state capture before shutdown", async () =>
            {
                Require(await App.RefreshWindowScrollPositionsAsync(App.MainWindows), "Could not capture the final reading positions.");
                var expected = App.MainWindows.OrderBy(mainWindow => ReferenceEquals(mainWindow, App.MainWindow) ? 1 : 0)
                    .Select(mainWindow => mainWindow.CaptureState()).ToArray();
                File.WriteAllText(Path.Combine(OutputDirectory, Environment.GetCommandLineArgs().Contains("--bugbash-restart")
                    ? "restart-expected-windows.json" : "expected-windows.json"), JsonSerializer.Serialize(expected));
                return new { ExpectedWindows = expected.Length };
            });
            ArticleThemesManager.Shared.Dispose();
            int shutdownResult = Results.Count;
            Results.Add(new("Runtime shutdown drains account work", false, "The last window has not closed.", null));
            var lastWindow = App.MainWindows.Last();
            lastWindow.Closed += (_, _) =>
            {
                Results.RemoveAt(shutdownResult);
                Check("Runtime shutdown drains account work", () =>
                {
                    Require(App.IsShuttingDown && AccountManager.Shared.Accounts.Count == 0, "The last window closed without draining the shared runtime.");
                    return new { AccountsClosed = true, ActualWindowClose = true };
                });
                WriteResults();
            };
            WriteResults();
            if (Environment.GetCommandLineArgs().Contains("--quit")) await App.QuitAsync();
            else RequestSystemClose(window);
        }
    }

    private static void WriteResults()
    {
        File.WriteAllText(Path.Combine(OutputDirectory, Environment.GetCommandLineArgs().Contains("--bugbash-restart")
            ? "restart-results.json" : "results.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = Results.All(result => result.Passed) ? 0 : 1;
    }

    private static void Check(string name, Func<object?> check)
    {
        try { Results.Add(new(name, true, null, check())); }
        catch (Exception error) { Results.Add(new(name, false, error.ToString(), null)); }
        WriteResults();
    }

    private static async Task CheckAsync(string name, Func<Task<object?>> check)
    {
        try { Results.Add(new(name, true, null, await check())); }
        catch (Exception error) { Results.Add(new(name, false, error.ToString(), null)); }
        WriteResults();
    }

    private static async Task<object?> CheckFeedImageAsync(MainWindow window, Feed feed)
    {
        var previousIcons = AppDefaults.Shared.GetTimelineFeedIconsDisplay();
        try
        {
            var path = await AppShared.Favicons.FaviconDownloader.Shared.FaviconPathAsync(feed);
            Require(path is not null && File.Exists(path), "The feed-image pipeline did not produce a cached image.");
            Require(path == Path.Combine(AppConfig.CachesDirectory, "DownloadedImages", feed.FaviconUrl!.Md5Hex()),
                "The feed-image pipeline did not use the shared source cache directory and filename.");
            Require(!Directory.Exists(Path.Combine(AppConfig.CachesDirectory, "Favicons")),
                "The feed-image pipeline recreated the retired favicon cache.");
            var model = window.ProbeContent.ViewModel;
            var list = (ListView)window.ProbeContent.FindName("ArticleListView");
            var row = model.SelectedArticle!;
            await WaitFor(() => Task.FromResult(row.FaviconPath == path && list.ContainerFromItem(row) is not null), "Feed image row");
            var icon = Descendants((DependencyObject)list.ContainerFromItem(row)).OfType<IconView>().Single();
            await WaitFor(() => Task.FromResult(icon.ImageSource is BitmapImage { PixelWidth: 1, PixelHeight: 1 }), "Feed image binding");
            Require(model.SelectedSidebarItems.Count == 1 && model.SelectedSidebarItems[0].Tag is Feed
                && model.ArticleItems.All(item => item.SourceArticle?.Authors?.Any(author => author.AvatarUrl is not null) != true),
                "The automatic-icon fixture must be a single feed without author avatars.");
            AppDefaults.Shared.SetTimelineFeedIconsDisplay(TimelineFeedIconsDisplay.Automatic);
            await WaitFor(() => Task.FromResult(icon.Visibility == Visibility.Collapsed), "Automatic hides redundant single-feed icons");
            AppDefaults.Shared.SetTimelineFeedIconsDisplay(TimelineFeedIconsDisplay.Always);
            await WaitFor(() => Task.FromResult(icon.Visibility == Visibility.Visible), "Explicit timeline feed icon");
            var displayedImage = Descendants(icon).OfType<Image>().Single();
            await WaitFor(() => Task.FromResult(HasRedIconPixel(window, displayedImage)), "Visible timeline icon pixels");
            return new { CachedBytes = new FileInfo(path!).Length, PixelWidth = 1, PixelHeight = 1,
                VisiblePixels = true, ActualTimelineRow = true, AutomaticHidesSingleFeedWithoutAvatars = true };
        }
        finally { AppDefaults.Shared.SetTimelineFeedIconsDisplay(previousIcons); }
    }

    private static bool HasRedIconPixel(MainWindow window, Image image)
    {
        var capture = CaptureWindow(window);
        var center = image.TransformToVisual(null).TransformPoint(new(image.ActualWidth / 2, image.ActualHeight / 2));
        double scale = image.XamlRoot.RasterizationScale;
        int x = (int)Math.Round(center.X * scale) + capture.ClientOrigin.X - capture.Bounds.Left;
        int y = (int)Math.Round(center.Y * scale) + capture.ClientOrigin.Y - capture.Bounds.Top;
        Require(x >= 0 && x < capture.Width && y >= 0 && y < capture.Height, "The icon is outside the probe window.");
        int offset = checked((y * capture.Width + x) * 4);
        return capture.Data[offset + 2] > 200 && capture.Data[offset + 1] < 40 && capture.Data[offset] < 40;
    }

    private sealed record WindowPixels(byte[] Data, int Width, int Height, CaptureRect Bounds, CapturePoint ClientOrigin);

    private static WindowPixels CaptureWindow(MainWindow window, Rect? region = null)
    {
        var handle = WindowNative.GetWindowHandle(window);
        Require(GetWindowRect(handle, out var bounds), "Could not capture the probe window bounds.");
        var clientOrigin = new CapturePoint();
        Require(ClientToScreen(handle, ref clientOrigin), "Could not locate the probe window client area.");
        if (region is { } area)
        {
            Require(GetForegroundWindow() == handle, "Region capture requires the private window to be in the foreground.");
            double scale = ((FrameworkElement)window.Content).XamlRoot.RasterizationScale;
            bounds = new CaptureRect
            {
                Left = clientOrigin.X + (int)Math.Floor(area.Left * scale),
                Top = clientOrigin.Y + (int)Math.Floor(area.Top * scale),
                Right = clientOrigin.X + (int)Math.Ceiling(area.Right * scale),
                Bottom = clientOrigin.Y + (int)Math.Ceiling(area.Bottom * scale)
            };
        }
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        Require(width > 0 && height > 0, "The probe window has no capture area.");
        var info = new CaptureBitmapInfo
        {
            Size = (uint)Marshal.SizeOf<CaptureBitmapInfo>(), Width = width, Height = -height,
            Planes = 1, BitCount = 32
        };
        var dc = CreateCompatibleDC(IntPtr.Zero);
        Require(dc != IntPtr.Zero, "Could not create the probe capture surface.");
        IntPtr bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            bitmap = CreateDIBSection(dc, ref info, 0, out var pixels, IntPtr.Zero, 0);
            Require(bitmap != IntPtr.Zero && pixels != IntPtr.Zero, "Could not allocate the probe capture bitmap.");
            previous = SelectObject(dc, bitmap);
            Require(previous != IntPtr.Zero && previous != new IntPtr(-1), "Could not select the probe capture bitmap.");
            if (region is null)
                Require(PrintWindow(handle, dc, 2), "Could not capture the probe window.");
            else
            {
                var screen = GetDC(IntPtr.Zero);
                Require(screen != IntPtr.Zero, "Could not read the private animation's screen region.");
                try { Require(BitBlt(dc, 0, 0, width, height, screen, bounds.Left, bounds.Top, 0x00CC0020), "Could not capture the animation frame."); }
                finally { ReleaseDC(IntPtr.Zero, screen); }
            }
            var data = new byte[checked(width * height * 4)];
            Marshal.Copy(pixels, data, 0, data.Length);
            return new(data, width, height, bounds, clientOrigin);
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    private static async Task WaitForRenderedFramesAsync()
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int frames = 0;
        void Rendering(object? sender, object args) { if (++frames >= 2) rendered.TrySetResult(); }
        CompositionTarget.Rendering += Rendering;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= Rendering; }
    }

    private static async Task SaveWindowScreenshotAsync(MainWindow window, string name, Rect? region = null)
    {
        await WaitForRenderedFramesAsync();
        var capture = CaptureWindow(window, region);
        using var file = File.Create(Path.Combine(OutputDirectory, name + ".png"));
        using var stream = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(file);
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, (uint)capture.Width, (uint)capture.Height, 96, 96, capture.Data);
        await encoder.FlushAsync();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CapturePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSizingLimits
    {
        public CapturePoint Reserved, MaximumSize, MaximumPosition, MinimumTrackSize, MaximumTrackSize;
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendSizingMessage(IntPtr window, uint message, IntPtr parameter, ref WindowSizingLimits value);
    [StructLayout(LayoutKind.Sequential)]
    private struct CaptureRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CaptureBitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out CaptureRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref CapturePoint point);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref CaptureBitmapInfo info,
        uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    private static async Task<object?> CheckReadingStateAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var account = AccountManager.Shared.DefaultAccount!;
        var feed = account.AddFeed(serverUrl + "/reading-state-feed", "Reading state fixture", null);
        feed.FaviconUrl = serverUrl + "/icon.png";
        var now = DateTime.UtcNow;
        var articles = Enumerable.Range(0, 3).Select(index => new Article(account.AccountID,
            "reading-state-" + index, feed.FeedID, "reading-state-" + index, "Reading state " + index,
            "<p>Reading state fixture body.</p>", null, null, serverUrl + "/article", null, null, null,
            now.AddSeconds(-index), null, null, null, new ArticleStatus("reading-state-" + index, false, false, now))).ToArray();
        try
        {
            await account.Database.UpdateArticlesAsync(articles);
            await account.RecalculateUnreadCountsAsync();
            model.IsReaderMode = false;
            model.BuildSidebar();
            model.SetSelectedSidebarItems([model.SidebarItems.SelectMany(section => section.Children)
                .Single(row => ReferenceEquals(row.Tag, AppShared.SmartFeeds.SmartFeedsController.Shared.UnreadFeed))]);
            await model.FilterArticlesAsync();
            await model.ProbeDrainViewUpdatesAsync();
            var collection = model.ArticleItems;
            var rows = collection.ToArray();
            var sidebar = model.SidebarItems.ToArray();
            var list = (ListView)content.FindName("ArticleListView");
            int collectionChanges = 0, sidebarRebuilds = 0;
            void Changed(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => collectionChanges++;
            void Rebuilt() => sidebarRebuilds++;
            collection.CollectionChanged += Changed;
            model.SidebarRebuilt += Rebuilt;
            try
            {
                foreach (var article in articles.Take(2))
                {
                    var row = rows.Single(item => item.Id == article.ArticleID);
                    list.SelectedItem = row;
                    await model.PendingAutomaticReads.WaitAsync(TimeSpan.FromSeconds(10));
                    await model.ProbeDrainViewUpdatesAsync();
                    Require(row.IsRead && (await account.Database.FetchArticlesByIDsAsync([row.Id])).Single().Status.Read,
                        "Selecting an unread article did not persist its read status.");
                    Require(ReferenceEquals(collection, model.ArticleItems), "Reading an article replaced the entire timeline collection.");
                    Require(rows.SequenceEqual(model.ArticleItems), "Reading an article replaced, removed or reordered timeline rows.");
                    Require(sidebar.SequenceEqual(model.SidebarItems) && sidebarRebuilds == 0, "Reading an article rebuilt the sidebar.");
                    Require(collectionChanges == 0, "Reading an article changed the collection instead of updating its read indicator.");
                    Require(ReferenceEquals(list.SelectedItem, row), "The selected ListView row was replaced.");
                }
                var pendingRow = rows.Single(item => item.Id == articles[2].ArticleID);
                int staleReadChanges = 0;
                void ReadChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(Models.FeedItem.IsRead)) staleReadChanges++;
                }
                pendingRow.PropertyChanged += ReadChanged;
                try
                {
                    // Queue both notifications before the UI can observe either one.
                    var writes = Task.Run(async () =>
                    {
                        await account.MarkAsync([pendingRow.Id], ArticleStatus.Key.Read, true);
                        await account.MarkAsync([pendingRow.Id], ArticleStatus.Key.Read, false);
                    });
                    Require(writes.Wait(TimeSpan.FromSeconds(10)), "The background status writes did not complete.");
                    await writes;
                    await model.ProbeDrainViewUpdatesAsync();
                    Require(!pendingRow.IsRead && !pendingRow.SourceArticle!.Status.Read && staleReadChanges == 0,
                        "Delayed UI notifications replayed stale values into committed article state.");
                }
                finally { pendingRow.PropertyChanged -= ReadChanged; }
                model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
                await model.FilterArticlesAsync();
                await model.ProbeDrainViewUpdatesAsync();
                var feedRows = model.ArticleItems;
                var existingFeedRows = feedRows.ToArray();
                int beforeDownload = sidebarRebuilds;
                var unrelated = (await account.Database.FetchArticlesByIDsAsync(["probe-article"])).Single();
                account.RaiseNewArticlesDownloaded([unrelated]);
                await model.ProbeDrainViewUpdatesAsync();
                Require(ReferenceEquals(feedRows, model.ArticleItems) && sidebarRebuilds == beforeDownload,
                    "An unrelated feed download rebuilt the active reading view.");
                var added = new Article(account.AccountID, "reading-state-added", feed.FeedID, "reading-state-added",
                    "New article in the active feed", "<p>New content</p>", null, null, serverUrl + "/article",
                    null, null, null, now.AddSeconds(1), null, null, null, new ArticleStatus("reading-state-added", false, false, now));
                await account.Database.UpdateArticlesAsync([added]);
                account.RaiseNewArticlesDownloaded([added]);
                await model.ProbeDrainViewUpdatesAsync();
                Require(model.ArticleItems.Any(item => item.Id == added.ArticleID), "An active-feed download did not update the timeline.");
                Require(ReferenceEquals(feedRows, model.ArticleItems) && existingFeedRows.All(model.ArticleItems.Contains),
                    "Downloaded articles replaced the collection or recreated existing timeline rows.");
                Require(sidebarRebuilds == beforeDownload, "New article content unnecessarily rebuilt the subscription tree.");
                var unchangedRows = model.ArticleItems.ToArray();
                model.BuildSidebar();
                await model.FilterArticlesAsync();
                Require(sidebarRebuilds == beforeDownload && unchangedRows.SequenceEqual(model.ArticleItems),
                    "An unchanged refresh recreated sidebar or timeline objects.");
                int refreshes = model.ProbeSubscriptionRefreshes;
                for (int index = 0; index < 4; index++)
                {
                    account.RaiseNewArticlesDownloaded([added]);
                    await Task.Delay(40);
                }
                await model.ProbeDrainViewUpdatesAsync();
                Require(model.ProbeSubscriptionRefreshes - refreshes == 1, "A download burst was not coalesced into one UI update.");
                refreshes = model.ProbeSubscriptionRefreshes;
                var burst = System.Diagnostics.Stopwatch.StartNew();
                while (burst.Elapsed < TimeSpan.FromMilliseconds(2400))
                {
                    account.RaiseNewArticlesDownloaded([added]);
                    await Task.Delay(80);
                }
                Require(model.ProbeSubscriptionRefreshes > refreshes,
                    "A continuous download burst postponed its timeline update beyond the two-second coalescing limit.");
                await model.ProbeDrainViewUpdatesAsync();
                await CheckTimelinePreparationAsync(window);
                return new { ArticlesRead = 2, StableTimeline = true, StableSidebar = true,
                    UnreadRowsRetained = true, CommittedStatusOnly = true, UnrelatedDownloadsIgnored = true,
                    ActiveDownloadsDisplayed = true, IncrementalRefresh = true, CoalescedDownloads = true,
                    PreparationOffUi = true, SelectionDuringPreparation = true, StalePreparationDiscarded = true };
            }
            finally
            {
                collection.CollectionChanged -= Changed;
                model.SidebarRebuilt -= Rebuilt;
            }
        }
        finally
        {
            account.RemoveFeed(feed);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
        }
    }

    private static async Task<object?> CheckReadingFaviconAsync(MainWindow window)
    {
        await using var server = new FixtureServer(holdFavicon: true);
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var previousIcons = AppDefaults.Shared.GetTimelineFeedIconsDisplay();
        var account = AccountManager.Shared.DefaultAccount!;
        Feed? feed = null;
        try
        {
            AppDefaults.Shared.SetTimelineFeedIconsDisplay(TimelineFeedIconsDisplay.Always);
            feed = await account.CreateFeedAsync(server.Url + "/reading-feed")
                ?? throw new InvalidOperationException("The local Atom feed was not subscribed.");
            await account.RecalculateUnreadCountsAsync();
            model.IsReaderMode = false;
            model.BuildSidebar();
            model.SetSelectedSidebarItems([model.SidebarItems.SelectMany(section => section.Children)
                .Single(row => ReferenceEquals(row.Tag, feed))]);
            await model.FilterArticlesAsync();
            await model.ProbeDrainViewUpdatesAsync(waitForArticleIcons: false);
            Require(model.ArticleItems.Count == 25, "The Atom download did not reach the timeline.");
            Require(model.ArticleItems.All(row => row.Content.Contains("readable-feed-body", StringComparison.Ordinal)),
                "Media RSS metadata replaced an Atom article body.");
            await server.FaviconRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var row = model.ArticleItems[0];
            var list = (ListView)content.FindName("ArticleListView");
            var navigations = new List<object>();
            int acceptedNavigations = 0;
            void Starting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
            {
                if (!args.Cancel) acceptedNavigations++;
                navigations.Add(new { args.NavigationId, args.Uri, args.Cancel, SelectedArticle = model.SelectedArticle?.Id });
                File.WriteAllText(Path.Combine(OutputDirectory, "reader-favicon-navigations.json"), JsonSerializer.Serialize(navigations));
            }
            var core = content.ProbeWebView.CoreWebView2;
            core.NavigationStarting += Starting;
            try
            {
                list.SelectedItem = row;
                await model.PendingAutomaticReads.WaitAsync(TimeSpan.FromSeconds(10));
                await model.ProbeDrainViewUpdatesAsync(waitForArticleIcons: false);
                await WaitFor(async () => await ScriptBool(content,
                    "!!document.getElementById('readable-feed-body') && window.__wnwArticleProcessed === true"), "Feed article body");
                Require(content.ProbeWebView.Visibility == Visibility.Visible && content.ProbeWebView.ActualWidth > 0
                    && content.ProbeWebView.ActualHeight > 0, "The article document exists but is not displayed.");
                await core.ExecuteScriptAsync("window.readingDocumentSentinel = true; window.scrollTo(0,200);");
                string displayedDocument = model.ArticleHtml;
                server.ReleaseFavicon.TrySetResult();
                await WaitFor(() => Task.FromResult(row.FaviconPath is not null), "Favicon fallback after HTTP 403");
                await WaitFor(async () => await ScriptBool(content,
                    "document.getElementById('nnwImageIcon')?.naturalWidth === 1"), "Article header favicon");
                await WaitFor(() => Task.FromResult(list.ContainerFromItem(row) is not null), "Timeline row realization");
                var icon = Descendants((DependencyObject)list.ContainerFromItem(row)).OfType<IconView>().Single();
                await WaitFor(() => Task.FromResult(icon.ImageSource is BitmapImage { PixelWidth: > 0 }), "Timeline favicon");
                var displayedImage = Descendants(icon).OfType<Image>().Single();
                await WaitFor(() => Task.FromResult(HasRedIconPixel(window, displayedImage)), "Visible fallback favicon pixels");
                Require(await ScriptBool(content, "window.readingDocumentSentinel === true && Math.abs(window.scrollY - 200) < 2"),
                    "Favicon arrival reloaded the article or lost its reading position.");
                account.Database.EmptyArticleCache();
                await model.FilterArticlesAsync();
                await model.ProbeDrainViewUpdatesAsync();
                model.RefreshArticleHtml();
                Require(model.SelectedArticle?.Id == row.Id && model.ArticleHtml == displayedDocument,
                    "Rebinding a selected article baked its new icon load state into the document.");
                Require(await ScriptBool(content, "window.readingDocumentSentinel === true && Math.abs(window.scrollY - 200) < 2"),
                    "Refreshing the selected row after favicon arrival replaced its document.");
                Require(acceptedNavigations == 1, $"One article selection navigated the document {acceptedNavigations} times.");
                using var screenshot = File.Create(Path.Combine(OutputDirectory, "reader-article.png"));
                using var imageStream = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(screenshot);
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, imageStream);
                return new { ParsedArticles = 25, VisibleArticle = true, HomepageForbidden = true,
                    TimelineFavicon = true, HeaderFavicon = true, DocumentNavigations = acceptedNavigations, ReadingPositionPreserved = true };
            }
            finally { core.NavigationStarting -= Starting; }
        }
        finally
        {
            server.ReleaseFavicon.TrySetResult();
            if (feed is not null) account.RemoveFeed(feed);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            AppDefaults.Shared.SetTimelineFeedIconsDisplay(previousIcons);
        }
    }

    private static async Task<object?> CheckArticleNavigationPolicyAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var core = content.ProbeWebView.CoreWebView2;
        await WaitFor(async () => await ScriptBool(content, "document.readyState === 'complete' && window.__wnwArticleProcessed === true"),
            "Reader navigation prerequisite");
        await core.ExecuteScriptAsync("window.readingNavigationSentinel = true;");
        var completed = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var destination = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("<p>Unrequested document</p>"));
        ulong attemptedNavigation = 0;
        void Starting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (args.Uri == destination) attemptedNavigation = args.NavigationId;
        }
        void Finished(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.NavigationId == attemptedNavigation) completed.TrySetResult(args);
        }
        core.NavigationStarting += Starting;
        core.NavigationCompleted += Finished;
        try
        {
            core.Navigate(destination);
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(!result.IsSuccess && result.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled,
                "The reader permitted an unrequested data-URI document.");
            Require(await ScriptBool(content, "window.readingNavigationSentinel === true"),
                "Rejecting an unrequested navigation removed the current article.");
            return new { UnrequestedNavigationBlocked = true, CurrentDocumentPreserved = true };
        }
        finally
        {
            core.NavigationStarting -= Starting;
            core.NavigationCompleted -= Finished;
        }
    }

    private static async Task<object?> CheckAtlanticAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var account = AccountManager.Shared.DefaultAccount!;
        Feed? feed = null;
        try
        {
            feed = await account.CreateFeedAsync("https://www.theatlantic.com/feed/all/")
                ?? throw new InvalidOperationException("The live Atlantic feed was not subscribed.");
            await account.RecalculateUnreadCountsAsync();
            model.IsReaderMode = false;
            model.BuildSidebar();
            model.SetSelectedSidebarItems([model.SidebarItems.SelectMany(section => section.Children)
                .Single(row => ReferenceEquals(row.Tag, feed))]);
            await model.FilterArticlesAsync();
            await model.ProbeDrainViewUpdatesAsync();
            var collection = model.ArticleItems;
            var rows = collection.ToArray();
            Require(rows.Length > 0 && rows.All(row => row.SourceArticle?.ContentHtml is { Length: > 0 }),
                "The live feed did not deliver stored article bodies.");
            var list = (ListView)content.FindName("ArticleListView");
            int rendered = 0;
            foreach (var row in rows)
            {
                list.SelectedItem = row;
                await model.PendingAutomaticReads.WaitAsync(TimeSpan.FromSeconds(10));
                await model.ProbeDrainViewUpdatesAsync();
                var iconPrefix = ViewModels.MainViewModel.ArticleIconUrl + "?article="
                    + Uri.EscapeDataString(row.AccountID + "|" + row.Id) + "&";
                await WaitFor(async () => await ScriptBool(content,
                    "window.__wnwArticleProcessed === true && document.getElementById('bodyContainer')?.innerText.trim().length > 0"
                    + " && document.getElementById('nnwImageIcon')?.src.startsWith(" + JsonSerializer.Serialize(iconPrefix) + ")"),
                    "Live Atlantic article " + (rendered + 1));
                Require(content.ProbeWebView.Visibility == Visibility.Visible, "The Atlantic article was hidden.");
                Require(ReferenceEquals(collection, model.ArticleItems) && rows.SequenceEqual(model.ArticleItems),
                    "Reading the live feed rebuilt its timeline.");
                rendered++;
            }
            var selected = model.SelectedArticle!;
            await WaitFor(() => Task.FromResult(selected.FaviconPath is not null), "Live Atlantic favicon download");
            await WaitFor(async () => await ScriptBool(content,
                "document.getElementById('nnwImageIcon')?.src.includes('state=ready') && document.getElementById('nnwImageIcon').naturalWidth > 0"),
                "Live Atlantic header favicon");
            await WaitFor(() => Task.FromResult(list.ContainerFromItem(selected) is not null), "Live Atlantic timeline row");
            var icon = Descendants((DependencyObject)list.ContainerFromItem(selected)).OfType<IconView>().Single();
            await WaitFor(() => Task.FromResult(icon.ImageSource is BitmapImage { PixelWidth: > 0 }), "Live Atlantic timeline favicon");
            using var screenshot = File.Create(Path.Combine(OutputDirectory, "atlantic-article.png"));
            using var imageStream = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(screenshot);
            await content.ProbeWebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, imageStream);
            return new { StoredArticles = rows.Length, RenderedArticles = rendered, StableTimeline = true,
                HeaderFavicon = true, TimelineFavicon = true, PrivateProfile = true };
        }
        finally
        {
            if (feed is not null) account.RemoveFeed(feed);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
        }
    }

    private static async Task<object?> CheckSidebarDropsAsync(MainWindow window, string serverUrl)
    {
        var manager = AccountManager.Shared;
        var accounts = new List<Account.Account>();
        Account.Account NewAccount(AccountType type, string name)
        {
            IAccountDelegate accountDelegate = type == AccountType.OnMyMac ? new LocalAccountDelegate() : new DropPolicyDelegate(type);
            var account = manager.CreateRemoteAccount(type, name, accountDelegate);
            accounts.Add(account);
            return account;
        }
        static Models.SidebarItem Row(Feed feed, Folder? parent = null)
            => new() { Tag = feed, ParentFolder = parent, ItemType = Models.SidebarItemType.Feed };
        static Models.SidebarItem FolderRow(Folder folder)
            => new() { Tag = folder, ItemType = Models.SidebarItemType.Folder };
        void Expect(IReadOnlyList<Models.SidebarItem> rows, Account.Account account, Folder? folder,
            bool copy, Windows.ApplicationModel.DataTransfer.DataPackageOperation expected)
            => Require(MainContent.ProbeSidebarDropOperation(rows, account, folder, copy) == expected,
                "Unexpected sidebar drop operation for " + account.Name + (copy ? " (copy)" : " (move)"));
        var none = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        var move = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        var copy = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        try
        {
            var target = NewAccount(AccountType.OnMyMac, "Drop target");
            var other = NewAccount(AccountType.OnMyMac, "Drop source");
            var source = target.AddFolder("Source");
            var destination = target.AddFolder("Destination");
            var duplicate = target.AddFolder("Duplicate");
            var localFeed = target.AddFeed(serverUrl + "/drop-local", folder: source);
            var foreignFeed = other.AddFeed(serverUrl + "/drop-foreign");
            var existingForeign = target.AddFeed(foreignFeed.Url);
            target.AddFeed(localFeed.Url, folder: duplicate);
            var external = new Windows.ApplicationModel.DataTransfer.DataPackage();
            external.SetWebLink(new Uri(serverUrl + "/external-feed"));
            var externalData = external.GetView();
            foreach (var row in new Models.SidebarItem?[]
            {
                null,
                new() { ItemType = Models.SidebarItemType.SmartFeed, Title = "All Unread" }
            })
            {
                var resolved = MainContent.ProbeExternalFeedDropDestination(row);
                Require(ReferenceEquals(resolved.Account, manager.DefaultAccount) && resolved.Folder is null
                    && MainContent.ProbeCanDropExternalFeed(externalData, row),
                    "An external URL outside a container did not target the default local account.");
            }
            var folderDestination = MainContent.ProbeExternalFeedDropDestination(Row(localFeed, source));
            Require(ReferenceEquals(folderDestination.Account, target) && ReferenceEquals(folderDestination.Folder, source),
                "Dropping on a feed did not target its parent container.");
            var accountDestination = MainContent.ProbeExternalFeedDropDestination(new() { Tag = other });
            Require(ReferenceEquals(accountDestination.Account, other) && accountDestination.Folder is null,
                "An explicit account destination was replaced by the default account.");
            Require(!MainContent.ProbeCanDropExternalFeed(new Windows.ApplicationModel.DataTransfer.DataPackage().GetView(), null),
                "A drag without a link or text was accepted.");
            Require(!MainContent.ProbeCanDropExternalFeed(externalData, Row(foreignFeed, source)),
                "An invalid external destination was accepted.");
            Expect([Row(localFeed, source)], target, destination, false, move);
            Expect([Row(localFeed, source)], target, destination, true, copy);
            Expect([Row(localFeed, source)], target, duplicate, false, none);
            Expect([Row(localFeed, source), Row(foreignFeed)], target, duplicate, false, none);
            Expect([Row(foreignFeed)], target, null, false, none);
            Expect([FolderRow(source)], target, null, false, none);
            var colliding = other.AddFolder("Source");
            var unique = other.AddFolder("Unique");
            Expect([FolderRow(colliding)], target, null, false, none);
            Expect([FolderRow(unique)], target, null, false, copy);
            Expect([FolderRow(unique)], target, destination, false, none);
            Expect([Row(localFeed, source), FolderRow(unique)], target, destination, false, none);
            Expect([Row(localFeed, source)], target, unique, false, none);
            target.IsActive = false;
            Expect([Row(localFeed, source)], target, destination, false, none);
            Require(!MainContent.ProbeCanDropExternalFeed(externalData, FolderRow(source)),
                "An inactive account accepted an external feed.");
            target.IsActive = true;

            var feedbin = NewAccount(AccountType.Feedbin, "Drop Feedbin");
            var feedbinFolder = feedbin.AddFolder("Feedbin source");
            var feedbinFeed = feedbin.AddFeed(serverUrl + "/drop-feedbin", folder: feedbinFolder);
            Expect([Row(feedbinFeed, feedbinFolder)], feedbin, null, true, none);
            Expect([Row(feedbinFeed, feedbinFolder)], feedbin, null, false, move);
            var feedly = NewAccount(AccountType.Feedly, "Drop Feedly");
            Expect([Row(localFeed, source)], feedly, null, false, none);
            Require(!MainContent.ProbeCanDropExternalFeed(externalData, new() { Tag = feedly }),
                "An external feed bypassed Feedly's root-folder restriction.");
            Require(MainContent.ProbeCanDropExternalFeed(externalData, FolderRow(feedly.AddFolder("Feedly target"))),
                "An external feed could not target a valid remote folder.");
            var reader = NewAccount(AccountType.Inoreader, "Drop Reader");
            var readerSource = reader.AddFolder("Reader source");
            var readerDestination = reader.AddFolder("Reader destination");
            var readerFeed = reader.AddFeed(serverUrl + "/drop-reader", folder: readerSource);
            reader.AddFeed(foreignFeed.Url, folder: readerSource);
            Expect([Row(readerFeed, readerSource)], reader, readerDestination, true, none);
            Expect([Row(readerFeed, readerSource)], reader, readerDestination, false, move);
            Expect([Row(foreignFeed)], reader, readerDestination, false, none);
            Expect([Row(localFeed, source)], reader, readerDestination, false, copy);

            var mixed = new[] { Row(localFeed, source), Row(foreignFeed) };
            Expect(mixed, target, destination, false, copy);
            await window.ProbeContent.ProbeTransferSidebarItemsAsync(mixed, target, destination, copyRequested: false);
            Require(!source.Feeds.Contains(localFeed) && destination.Feeds.Contains(localFeed),
                "A mixed-account drag copied the local feed instead of moving it.");
            Require(other.TopLevelFeeds.Contains(foreignFeed) && target.TopLevelFeeds.Contains(existingForeign)
                && destination.Feeds.Contains(existingForeign), "A cross-account copy lost a source or existing destination membership.");
            return new { DuplicateFeedsRejected = true, DuplicateFoldersRejected = true, ServiceCopyRules = true,
                MixedAccountsMoveAndCopyPerItem = true, ExternalDefaultDestination = true, ExternalDestinationValidation = true,
                NoRemoteProviderRequests = true, PointerDragNotSimulated = true };
        }
        finally
        {
            window.ProbeContent.ViewModel.UndoManager.RemoveAll();
            foreach (var account in accounts.AsEnumerable().Reverse()) await manager.RemoveAccountAsync(account);
            window.Activate();
        }
    }

    private sealed class DropPolicyDelegate(AccountType type) : IAccountDelegate
    {
        public AccountType Type => type;
        public Task RefreshAllAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
            => Task.FromException(new InvalidOperationException("The drop policy probe must not contact a provider."));
        public Task<Feed?> CreateFeedAsync(Account.Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
            => Task.FromException<Feed?>(new InvalidOperationException("The drop policy probe must not contact a provider."));
    }

    private static object CheckInlines()
    {
        var block = new TextBlock();
        HtmlInlines.Render(block, SimpleHtml.Parse("<b>Bold</b> <i>Italic</i> <u>Underline</u> <s>Strike</s> <sup>Super</sup> <sub>Sub</sub>"));
        var runs = block.Inlines.OfType<Run>().ToArray();
        Require(runs.Single(run => run.Text == "Bold").FontWeight.Weight == FontWeights.Bold.Weight, "Bold font was not applied.");
        Require(runs.Single(run => run.Text == "Italic").FontStyle == global::Windows.UI.Text.FontStyle.Italic, "Italic font was not applied.");
        Require(runs.Single(run => run.Text == "Underline").TextDecorations.HasFlag(global::Windows.UI.Text.TextDecorations.Underline), "Underline was not applied.");
        Require(runs.Single(run => run.Text == "Strike").TextDecorations.HasFlag(global::Windows.UI.Text.TextDecorations.Strikethrough), "Strikethrough was not applied.");
        Require(Typography.GetVariants(runs.Single(run => run.Text == "Super")) == Microsoft.UI.Xaml.FontVariants.Superscript, "Superscript was not applied.");
        Require(Typography.GetVariants(runs.Single(run => run.Text == "Sub")) == Microsoft.UI.Xaml.FontVariants.Subscript, "Subscript was not applied.");
        HtmlInlines.Render(block, SimpleHtml.Parse("e\u0301\U0001F389x"), 2);
        Require(string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text)) == "e\u0301\U0001F389\u2026", "Truncation split a grapheme.");
        HtmlInlines.Render(block, SimpleHtml.Parse("Plain title"));
        Require(block.Text == "Plain title" && block.Inlines.OfType<Run>().All(run => run.FontWeight.Weight != FontWeights.Bold.Weight),
            "Recycling formatted text into the plain-text path retained its styling.");
        HtmlInlines.Render(block, SimpleHtml.Parse("<b>Bold again</b>"));
        Require(block.Inlines.OfType<Run>().Single().FontWeight.Weight == FontWeights.Bold.Weight,
            "Recycling plain text back to formatted text lost its styling.");
        return new { FontAndDecorationChecks = 6, GraphemeBoundaryChecked = true };
    }

    private static object CheckSingleLine()
    {
        // DesiredSize is WinUI's fitting-size contract; arranged bounds can clip or reflow tabs.
        var maxWidthDelta = 0.0;
        var maxHeightDelta = 0.0;
        var cases = 0;
        foreach (var font in new[] { new TextFont(11), new TextFont(12), new TextFont(13), new TextFont(14, 700) })
        {
            var baseline = SingleLineTextFieldSizer.CreateTextBlock(font);
            foreach (var text in SingleLineCorpus)
            {
                baseline.Text = text;
                baseline.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var candidate = SingleLineTextFieldSizer.SizeFor(text, font);
                maxWidthDelta = Math.Max(maxWidthDelta, Math.Abs(Math.Ceiling(baseline.DesiredSize.Width) - candidate.Width));
                maxHeightDelta = Math.Max(maxHeightDelta, Math.Abs(Math.Ceiling(baseline.DesiredSize.Height) - candidate.Height));
                cases++;
            }
        }
        Require(maxWidthDelta <= 1 && maxHeightDelta <= 1, $"Single-line width/height drift: {maxWidthDelta}/{maxHeightDelta}.");
        return new { Cases = cases, MaxWidthDelta = maxWidthDelta, MaxHeightDelta = maxHeightDelta };
    }

    private static object CheckMultiline()
    {
        var maxDelta = 0.0;
        var cases = 0;
        var worst = "";
        foreach (var text in MultilineCorpus)
        foreach (var font in new[] { new TextFont(11), new TextFont(13), new TextFont(14, 700) })
        foreach (var lines in new[] { 2, 4, 6 })
        {
            var baseline = SingleLineTextFieldSizer.CreateTextBlock(font, lines, wrap: true);
            baseline.Text = text;
            for (var width = 101; width <= 699; width += 5)
            {
                baseline.Measure(new Size(width, double.PositiveInfinity));
                var candidate = MultilineTextFieldSizer.SizeFor(text, font, lines, width);
                var delta = Math.Abs(Math.Ceiling(baseline.DesiredSize.Height) - candidate.Size.Height);
                if (delta > maxDelta)
                {
                    maxDelta = delta;
                    worst = $"width={width}, lines={lines}, font={font}, desired={baseline.DesiredSize.Height}, candidate={candidate.Size.Height}, text={text}";
                }
                cases++;
            }
        }
        Require(maxDelta <= 1, $"Multiline height drift: {maxDelta}; {worst}");
        return new { Cases = cases, MaxHeightDelta = maxDelta };
    }

    private static async Task<object?> CheckArticleAsync(MainWindow window)
    {
        CheckInlines();
        var content = window.ProbeContent;
        var core = content.ProbeWebView.CoreWebView2;
        var navigations = new List<string>();
        void Starting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
            => navigations.Add($"Starting {args.NavigationId}: {args.Uri[..Math.Min(args.Uri.Length, 120)]}; cancelled={args.Cancel}");
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
            => navigations.Add($"Completed {args.NavigationId}: success={args.IsSuccess}; error={args.WebErrorStatus}");
        core.NavigationStarting += Starting;
        core.NavigationCompleted += Completed;
        try
        {
            content.ViewModel.SelectedSidebarItem = content.ViewModel.SidebarItems.SelectMany(section => section.Children)
                .Single(item => item.Tag is Feed);
            await content.ViewModel.FilterArticlesAsync();
            await WaitFor(() => Task.FromResult(content.ViewModel.ArticleItems.Count == 1), "Isolated feed timeline");
            content.ViewModel.SelectedArticle = content.ViewModel.ArticleItems[0];
            await WaitFor(async () => await ScriptBool(content, "!!document.getElementById('probe-body')"), "Article document");
        }
        catch (TimeoutException)
        {
            File.WriteAllText(Path.Combine(OutputDirectory, "article-navigation.json"), JsonSerializer.Serialize(new
            {
                Navigations = navigations,
                core.Source,
                content.ViewModel.StatusMessage,
                HtmlLength = content.ViewModel.ArticleHtml.Length,
                HtmlContainsBody = content.ViewModel.ArticleHtml.Contains("probe-body", StringComparison.Ordinal),
                Visibility = content.ProbeWebView.Visibility.ToString(),
                content.ProbeWebView.ActualWidth,
                content.ProbeWebView.ActualHeight,
                Document = await core.ExecuteScriptAsync("({url:location.href,body:document.body?.innerHTML.slice(0,300)})")
            }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally
        {
            core.NavigationStarting -= Starting;
            core.NavigationCompleted -= Completed;
        }
        await WaitFor(async () => await ScriptBool(content, "window.__wnwArticleProcessed === true"), "Trusted article processing");
        Require(await ScriptBool(content, "!!document.querySelector('.articleTitle b')"), "Title formatting is absent.");
        Require(await ScriptBool(content, "!document.querySelector('.instapaper_ignore')"), "Shared page processing did not run.");
        Require(await ScriptBool(content, "document.getElementById('probe-table').parentElement.classList.contains('nnw-overflow')"), "Table wrapping is absent.");
        Require(await ScriptBool(content, "window.articleScriptRan === true"), "The enabled article script did not run.");
        Require(content.KeyboardAcceleratorPlacementMode == Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden,
            "Reader-wide shortcuts can generate stray tooltip text over the article.");
        var nextUnreadShortcut = content.KeyboardAccelerators.Single(accelerator =>
            accelerator.Key == global::Windows.System.VirtualKey.N
            && accelerator.Modifiers == global::Windows.System.VirtualKeyModifiers.None);
        content.ProbeToolbarSearch!.Focus(FocusState.Programmatic);
        await WaitFor(() => Task.FromResult(!nextUnreadShortcut.IsEnabled), "Shortcut suspended while typing");
        ((ListView)content.FindName("ArticleListView")).Focus(FocusState.Programmatic);
        await WaitFor(() => Task.FromResult(nextUnreadShortcut.IsEnabled), "Next Unread shortcut restored without a container tooltip");
        Require(ToolTipService.GetToolTip((Button)content.FindName("NextUnreadButton")) as string
            == AppStrings.Get("Toolbar_nextUnread") + " (N)", "The intentional Next Unread button tooltip was removed.");
        var sizes = new List<string>();
        for (var size = 0; size <= 4; size++)
        {
            AppDefaults.Shared.ArticleTextSizeRaw = size;
            var expected = (14 + size * 2).ToString(CultureInfo.InvariantCulture) + "px";
            await WaitFor(async () => await ScriptString(content, "document.getElementById('bodyContainer') && getComputedStyle(document.getElementById('bodyContainer')).fontSize") == expected, "Article text size");
            sizes.Add(expected);
            Require(await ScriptBool(content, """
                (() => {
                    const style = getComputedStyle(document.body);
                    const articleSize = parseFloat(getComputedStyle(document.getElementById('bodyContainer')).fontSize);
                    return Math.abs(parseFloat(style.maxWidth) / articleSize - 36) < 0.01
                        && parseFloat(style.paddingLeft) === 40 && parseFloat(style.paddingRight) === 40
                        && document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1;
                })()
                """), "The reader column width did not follow the article text size or preserve responsive side padding.");
        }
        return new { Sizes = sizes, MaximumReadingWidthEm = 36, ReadingWidthTracksArticleText = true,
            SidePaddingPx = 40, NoHorizontalOverflow = true };
    }

    private static async Task<object?> CheckThemeAsync(MainWindow window)
    {
        Require(window.ProbeContent.ViewModel.SelectedArticle is not null, "The article rendering prerequisite did not complete.");
        var manager = ArticleThemesManager.Shared;
        var folder = Path.Combine(manager.FolderPath, "Probe.nnwtheme");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Info.json"), """{"name":"Probe","creatorName":"Test","version":1}""");
        File.WriteAllText(Path.Combine(folder, "stylesheet.css"), manager.CurrentTheme.StylesheetCss + "\n.articleBody { color: rgb(11,22,33); }");
        File.WriteAllText(Path.Combine(folder, "asset.svg"), """<svg xmlns="http://www.w3.org/2000/svg" width="3" height="3"><rect width="3" height="3"/></svg>""");
        manager.Rescan();
        manager.SelectByName("Probe");
        var content = window.ProbeContent;
        await WaitFor(async () => await ScriptString(content, "document.getElementById('bodyContainer') && getComputedStyle(document.getElementById('bodyContainer')).color") == "rgb(11, 22, 33)", "Custom stylesheet");
        await content.ProbeWebView.ExecuteScriptAsync("""
            window.probeAsset = "loading";
            const image = new Image();
            image.onload = () => window.probeAsset = image.naturalWidth === 3 ? "loaded" : "wrong-size";
            image.onerror = () => window.probeAsset = "failed";
            image.src = "https://winnewswire-theme.local/asset.svg";
            """);
        await WaitFor(async () => await ScriptString(content, "window.probeAsset") == "loaded", "Mapped theme asset");
        return new { Theme = manager.CurrentTheme.Name, RelativeAssetLoaded = true };
    }

    private static async Task<object?> CheckScriptPolicyAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        Require(content.ViewModel.SelectedArticle is not null, "The article rendering prerequisite did not complete.");
        AppDefaults.Shared.ArticleContentJavascriptEnabled = false;
        await WaitFor(async () => await ScriptBool(content, "!!document.getElementById('probe-body') && window.articleScriptRan !== true"), "Disabled article script");
        await WaitFor(async () => await ScriptBool(content, "window.__wnwArticleProcessed === true"), "Trusted processing with JavaScript disabled");
        Require(await ScriptBool(content, "!document.querySelector('.instapaper_ignore')"), "Trusted scripts stopped when article JavaScript was disabled.");
        Require(await ScriptBool(content, "document.getElementById('probe-table').parentElement.classList.contains('nnw-overflow')"), "Trusted table processing stopped.");
        return new { UntrustedScriptBlocked = true, TrustedPageProcessingRetained = true };
    }

    private static async Task<object?> CheckReaderAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        Require(content.ViewModel.SelectedArticle is not null, "The article rendering prerequisite did not complete.");
        content.ViewModel.IsReaderMode = true;
        await WaitFor(async () => await ScriptBool(content, "!!document.getElementById('bodyContainer') && document.getElementById('bodyContainer').innerText.includes('Reader fixture paragraph')"), "Reader extraction");
        Require(await ScriptString(content, "getComputedStyle(document.getElementById('bodyContainer')).color") == "rgb(11, 22, 33)", "Reader mode lost its theme.");
        return new { LocalReaderExtraction = true, ThemeRetained = true };
    }

    private static async Task<object?> CheckIsolatedEventsAsync(MainWindow window)
    {
        var core = window.ProbeContent.ProbeWebView.CoreWebView2;
        Require(!core.Settings.IsScriptEnabled, "The isolated-event check requires disabled publisher scripts.");
        using var frameTree = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
        string frameID = frameTree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
        using var world = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
            JsonSerializer.Serialize(new { frameId = frameID, worldName = "WinNewsWire-event-probe" })));
        int context = world.RootElement.GetProperty("executionContextId").GetInt32();
        using var installed = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new
            {
                contextId = context,
                expression = "globalThis.wnwEventProbe = 0; document.addEventListener('keydown', () => globalThis.wnwEventProbe++);",
                returnByValue = true
            })));
        Require(!installed.RootElement.TryGetProperty("exceptionDetails", out _), "Could not install the isolated event listener.");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
            """{"type":"keyDown","key":"F8","code":"F8","windowsVirtualKeyCode":119,"nativeVirtualKeyCode":119}""");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
            """{"type":"keyUp","key":"F8","code":"F8","windowsVirtualKeyCode":119,"nativeVirtualKeyCode":119}""");
        using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new { contextId = context, expression = "globalThis.wnwEventProbe", returnByValue = true })));
        int callbacks = result.RootElement.GetProperty("result").GetProperty("value").GetInt32();
        Require(callbacks == 1, "The isolated world did not receive the native browser key event: " + callbacks);
        var content = window.ProbeContent;
        var model = content.ViewModel;
        await WaitFor(() => Task.FromResult(content.ProbeReaderInteractionReady), "Trusted reader bridge");
        Require(await ScriptString(content, "typeof globalThis.__winNewsWireReaderMessage") == "undefined",
            "The publisher's JavaScript world can access the trusted host binding.");
        var messages = new List<string>();
        var receiver = core.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled");
        void ObserveReaderMessage(Microsoft.Web.WebView2.Core.CoreWebView2 sender,
            Microsoft.Web.WebView2.Core.CoreWebView2DevToolsProtocolEventReceivedEventArgs args) => messages.Add(args.ParameterObjectAsJson);
        void ObserveReaderSelection(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(model.SelectedArticle) && model.SelectedArticle is null)
                messages.Add("Selection cleared: " + Environment.StackTrace);
        }
        receiver.DevToolsProtocolEventReceived += ObserveReaderMessage;
        model.PropertyChanged += ObserveReaderSelection;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", """{"enabled":true}""");
            await content.ProbeWebView.ExecuteScriptAsync("window.readerDocumentProbe = 'preserved';");
            await content.ProbeWebView.ExecuteScriptAsync("""
                document.dispatchEvent(new KeyboardEvent('keydown', {key:'l', code:'KeyL', bubbles:true}));
                document.dispatchEvent(new KeyboardEvent('keydown', {key:'F11', code:'F11', bubbles:true}));
                """);
            await DispatchBrowserKeyAsync(core, "l", "KeyL", 76);
            await WaitFor(() => Task.FromResult(model.SelectedArticle?.IsStarred == true), "Script-disabled star shortcut");
            await (model.ToggleStarredCommand.ExecutionTask ?? Task.CompletedTask);
            int starCommands = messages.Count(message =>
            {
                if (!message.StartsWith("{", StringComparison.Ordinal)) return false;
                using var envelope = JsonDocument.Parse(message);
                if (!envelope.RootElement.TryGetProperty("payload", out var encoded)) return false;
                using var payload = JsonDocument.Parse(encoded.GetString()!);
                return payload.RootElement.TryGetProperty("cmd", out var command) && command.GetString() == "toggleStar";
            });
            Require(starCommands == 1 && !window.IsFullScreen, "Synthetic publisher key events invoked a reader command.");
            await WaitFor(() => Task.FromResult(content.ProbeReaderInteractionReady), "Reader interaction after status change");
            Require(await ScriptString(content, "window.readerDocumentProbe") == "preserved", "A status-only timeline refresh reloaded the reader document.");
            await DispatchBrowserKeyAsync(core, "l", "KeyL", 76);
            await WaitFor(() => Task.FromResult(model.SelectedArticle?.IsStarred == false), "Script-disabled unstar shortcut");
            await (model.ToggleStarredCommand.ExecutionTask ?? Task.CompletedTask);
            await content.ProbeWebView.ExecuteScriptAsync("""
                var input = document.createElement('input');
                input.id = 'reader-input-probe';
                input.style.cssText = 'position:fixed;top:10px;left:10px;z-index:10000';
                document.body.appendChild(input);
                input.focus();
                """);
            await DispatchBrowserKeyAsync(core, "l", "KeyL", 76, text: "l");
            await WaitFor(async () => await ScriptString(content, "document.getElementById('reader-input-probe').value") == "l", "Reader text-input typing");
            Require(model.SelectedArticle?.IsStarred == false, "Typing in an article input triggered a reader shortcut.");
            await content.ProbeWebView.ExecuteScriptAsync("""
                document.getElementById('reader-input-probe').remove();
                var editor = document.createElement('div');
                editor.id = 'reader-editor-probe';
                editor.contentEditable = true;
                editor.style.cssText = 'position:fixed;top:10px;left:10px;width:100px;height:30px;z-index:10000';
                document.body.appendChild(editor);
                editor.focus();
                """);
            await DispatchBrowserKeyAsync(core, "l", "KeyL", 76, text: "l");
            await WaitFor(async () => await ScriptString(content, "document.getElementById('reader-editor-probe').innerText") == "l", "Reader contenteditable typing");
            Require(model.SelectedArticle?.IsStarred == false, "Typing in editable article content triggered a shortcut.");
            string hover = model.SelectedArticle!.Link + "?hover-probe";
            await content.ProbeWebView.ExecuteScriptAsync("""
                document.getElementById('reader-editor-probe').remove();
                var link = document.createElement('a');
                link.id = 'reader-hover-probe';
                link.textContent = 'Hover target';
                link.style.cssText = 'position:fixed;top:10px;left:10px;width:160px;height:30px;z-index:10000;background:white';
                document.body.appendChild(link);
                """ + "link.href = " + JsonSerializer.Serialize(hover) + ";");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", """{"type":"mouseMoved","x":20,"y":20}""");
            await WaitFor(() => Task.FromResult(model.DetailStatus == hover), "Script-disabled link hover");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", """{"type":"mouseMoved","x":1,"y":1}""");
            await WaitFor(() => Task.FromResult(model.DetailStatus != hover), "Cleared hover status");
            await content.ProbeWebView.ExecuteScriptAsync("""
                document.getElementById('reader-hover-probe').remove();
                var spacer = document.createElement('div');
                spacer.id = 'reader-space-probe';
                spacer.innerHTML = '<p>Reader scrolling fixture with enough article text to continue below the visible viewport.</p>'.repeat(80);
                document.getElementById('bodyContainer').appendChild(spacer);
                window.scrollTo(0, 0);
                """);
            await WaitFor(async () => await ScriptBool(content,
                "!!document.getElementById('reader-space-probe') && (document.scrollingElement || document.documentElement).scrollHeight > window.innerHeight"),
                "Scrollable Space fixture");
            string articleID = model.SelectedArticle.Id;
            await DispatchBrowserKeyAsync(core, " ", "Space", 32);
            await WaitFor(async () => await ScriptBool(content, "window.scrollY > 0"), "Script-disabled Space scrolling");
            Require(model.SelectedArticle?.Id == articleID && !core.Settings.IsScriptEnabled,
                "Space changed the article before reaching the bottom or enabled publisher scripts.");
            await DispatchBrowserKeyAsync(core, "F11", "F11", 122);
            await WaitFor(() => Task.FromResult(window.IsFullScreen), "Reader F11 full-screen entry");
            await DispatchBrowserKeyAsync(core, "F11", "F11", 122);
            await WaitFor(() => Task.FromResult(!window.IsFullScreen), "Reader F11 full-screen exit");
            return new { PublisherScriptsDisabled = true, IsolatedCallbacks = callbacks, HostBindingIsolated = true,
                Shortcuts = true, SyntheticKeysRejected = true, TextEditingPreserved = true, IsolatedHover = true, SpaceScroll = true, FullScreenShortcut = true };
        }
        catch (TimeoutException error)
        {
            var document = await content.ProbeWebView.ExecuteScriptAsync(
                "({focused:document.activeElement && document.activeElement.id,input:document.getElementById('reader-input-probe')?.value,body:document.body.innerText.slice(0,80),navigation:window.__wnwNavigationId,height:document.documentElement.scrollHeight,bodyHeight:document.body.scrollHeight,viewport:innerHeight,mode:document.compatMode,spacer:document.getElementById('reader-space-probe')?.offsetHeight})");
            throw new InvalidOperationException($"Reader state: article={model.SelectedArticle?.Id}, starred={model.SelectedArticle?.IsStarred}, selected={model.SelectedArticles.Count}, running={model.ToggleStarredCommand.IsRunning}, ready={content.ProbeReaderInteractionReady}, status={model.StatusMessage}; DOM={document}; messages={JsonSerializer.Serialize(messages)}", error);
        }
        finally
        {
            model.PropertyChanged -= ObserveReaderSelection;
            receiver.DevToolsProtocolEventReceived -= ObserveReaderMessage;
            if (window.IsFullScreen) await InvokeFullScreenMenuAsync(window);
            await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", """{"enabled":false}""");
            await content.ProbeWebView.ExecuteScriptAsync("""
                for (const id of ['reader-input-probe','reader-editor-probe','reader-hover-probe','reader-space-probe'])
                    document.getElementById(id)?.remove();
                window.scrollTo(0, 0);
                """);
            if (model.SelectedArticle?.IsStarred == true) await model.ToggleStarredCommand.ExecuteAsync(null);
        }
    }

    private static async Task DispatchBrowserKeyAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core,
        string key, string code, int virtualKey, string text = "", int modifiers = 0)
    {
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
            JsonSerializer.Serialize(new { type = "keyDown", key, code, windowsVirtualKeyCode = virtualKey, nativeVirtualKeyCode = virtualKey, text, modifiers }));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
            JsonSerializer.Serialize(new { type = "keyUp", key, code, windowsVirtualKeyCode = virtualKey, nativeVirtualKeyCode = virtualKey, modifiers }));
    }

    private static async Task<object?> CheckAddFeedDialogAsync(MainWindow window)
    {
        var root = window.ProbeContent.XamlRoot;
        var account = AccountManager.Shared.DefaultAccount!;
        var folder = account.AddFolder("Probe destination");
        ContentDialog? dialog = null;
        ContentDialog? OpenDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(root)
            .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault();
        try
        {
            var requestTask = Dialogs.AddFeedDialog.ShowAsync(root, "feeds://example.com/feed", account, folder);
            await WaitFor(() => Task.FromResult((dialog = OpenDialog())?.IsLoaded == true), "Add-feed dialog");
            var controls = Descendants(dialog!).OfType<ComboBox>().ToList();
            var accountChoice = controls.Single(control => AutomationProperties.GetName(control) == "Account");
            var folderChoice = controls.Single(control => AutomationProperties.GetName(control) == "Folder");
            Require(ReferenceEquals((accountChoice.SelectedItem as ComboBoxItem)?.Tag, account), "Explicit account was not selected.");
            Require(ReferenceEquals((folderChoice.SelectedItem as ComboBoxItem)?.Tag, folder), "Explicit folder was not selected.");
            InvokeDialogButton(dialog!, "PrimaryButton");
            var request = await requestTask.WaitAsync(TimeSpan.FromSeconds(10));
            Require(request?.Url == "https://example.com/feed", "The feed URL wrapper was not normalized.");
            Require(ReferenceEquals(request?.Account, account) && request?.FolderName == folder.Name, "The chosen destination was lost.");
            dialog = null;
            await WaitFor(() => Task.FromResult(OpenDialog() is null), "Closing the accepted add-feed dialog");

            var cancelledTask = Dialogs.AddFeedDialog.ShowAsync(root, "example.com");
            await WaitFor(() => Task.FromResult((dialog = OpenDialog())?.IsLoaded == true), "Remembered add-feed dialog");
            var cancelDialog = dialog ?? throw new InvalidOperationException("The add-feed dialog closed unexpectedly.");
            folderChoice = Descendants(cancelDialog).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "Folder");
            Require(ReferenceEquals((folderChoice.SelectedItem as ComboBoxItem)?.Tag, folder), "The previous destination was not remembered.");
            folderChoice.SelectedIndex = 0;
            var url = Descendants(cancelDialog).OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "Feed or website URL");
            url.Text = "";
            await WaitFor(() => Task.FromResult(!cancelDialog.IsPrimaryButtonEnabled), "Empty URL disables Add");
            InvokeDialogButton(cancelDialog, "CloseButton");
            Require(await cancelledTask.WaitAsync(TimeSpan.FromSeconds(10)) is null, "Cancel returned a subscription request.");
            Require(AppDefaults.Shared.GetValue<string?>(AppDefaults.Key.AddFeedFolderName) == folder.Name, "Cancel changed the remembered destination.");
            dialog = null;
            return new { DestinationRetained = true, UrlNormalized = true, CancelPreservedPreferences = true };
        }
        finally { (dialog ?? OpenDialog())?.Hide(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root is null) yield break;
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task<object?> CheckMemoryCleanupAsync(MainWindow window)
    {
        var article = window.ProbeContent.ViewModel.SelectedArticle?.SourceArticle
            ?? throw new InvalidOperationException("The probe article is no longer selected.");
        var account = AccountManager.Shared.Accounts.Single(account => account.AccountID == article.AccountID);
        var formatter = ArticleStringFormatter.Shared;
        var title = formatter.AttributedTruncatedTitle(article);
        var measured = SingleLineTextFieldSizer.SizeFor("Memory pressure", new TextFont(14));
        AppRuntime.AppService.Shared.ReleaseMemoryCaches();
        Require(!ReferenceEquals(title, formatter.AttributedTruncatedTitle(article)), "The derived article-title cache was not released.");
        Require(measured == SingleLineTextFieldSizer.SizeFor("Memory pressure", new TextFont(14)), "Text measurement changed after cache cleanup.");
        var reloaded = (await account.Database.FetchArticlesByIDsAsync([article.ArticleID])).Single();
        Require(ReferenceEquals(article, reloaded), "Memory cleanup duplicated an article still retained by a reading window.");
        Require(ReferenceEquals(article.Status, reloaded.Status), "Memory cleanup discarded a live canonical article status.");
        return new { ExplicitCleanup = true, DerivedCacheReleased = true, LiveStatusPreserved = true };
    }

    private static async Task<object?> CheckMultipleWindowsAsync(MainWindow window)
    {
        var original = window.ProbeContent.ViewModel;
        var originalArticle = original.SelectedArticle ?? throw new InvalidOperationException("No article is selected.");
        var account = AccountManager.Shared.Accounts.Single(account => account.AccountID == originalArticle.AccountID);
        var second = App.OpenMainWindow();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int systemCloseRequests = 0;
        second.AppWindow.Closing += (_, _) => systemCloseRequests++;
        second.Closed += (_, _) => closed.TrySetResult();
        try
        {
            await WaitFor(() => Task.FromResult(second.ProbeContent.IsLoaded && second.ProbeContent.ProbeWebViewReady), "Second main window");
            Require(App.MainWindows.Count == 2, "The second main window was not registered.");
            Require(ReferenceEquals(App.WindowForXamlRoot(second.ProbeContent.XamlRoot), second), "A window's share/dialog root resolved to another window.");
            var other = second.ProbeContent.ViewModel;
            Require(!ReferenceEquals(original, other) && !ReferenceEquals(original.UndoManager, other.UndoManager), "Window-local state was shared.");
            var oldSearch = original.SearchQuery;
            other.SearchQuery = "window-specific search";
            Require(original.SearchQuery == oldSearch, "Searching a second window changed the first.");
            other.SearchQuery = "";
            other.SelectedSidebarItem = other.SidebarItems.SelectMany(item => item.Children)
                .Single(item => item.Tag is Feed);
            await other.FilterArticlesAsync();
            other.SelectedArticle = other.ArticleItems.Single();
            Require(!ReferenceEquals(original.SelectedArticle, other.SelectedArticle), "Windows reused the same timeline view-model row.");
            var originalRows = original.ArticleItems;
            var otherRows = other.ArticleItems;
            var otherArticle = other.SelectedArticle;
            await account.MarkAsync([originalArticle.Id], ArticleStatus.Key.Starred, true);
            await WaitFor(() => Task.FromResult(original.SelectedArticle?.IsStarred == true && other.SelectedArticle?.IsStarred == true), "Shared status propagation");
            Require(ReferenceEquals(originalRows, original.ArticleItems) && ReferenceEquals(otherRows, other.ArticleItems)
                && ReferenceEquals(originalArticle, original.SelectedArticle) && ReferenceEquals(otherArticle, other.SelectedArticle),
                "Cross-window status propagation replaced timeline collections or selected rows.");
            RequestSystemClose(second);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(systemCloseRequests == 1, "The system close did not use AppWindow.Closing.");
            Require(App.MainWindows.Count == 1 && window.ProbeContent.ProbeWebViewReady, "Closing one window shut down the other.");
            Require((await account.Database.FetchArticlesByIDsAsync([originalArticle.Id])).Count == 1, "Closing a window closed shared storage.");
            await account.MarkAsync([originalArticle.Id], ArticleStatus.Key.Starred, false);
            window.Activate();
            return new { IndependentSelection = true, IndependentUndo = true, SharedStatus = true, NonLastClosePreservedRuntime = true };
        }
        finally
        {
            if (!closed.Task.IsCompleted) await App.CloseMainWindowAsync(second);
        }
    }

    private static void InvokeDialogButton(ContentDialog dialog, string name)
    {
        var button = Descendants(dialog).OfType<Button>().Single(button => button.Name == name);
        InvokeButton(button);
    }

    private static void InvokeButton(Button button)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(button) ?? FrameworkElementAutomationPeer.CreatePeerForElement(button);
        var invoke = peer?.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new InvalidOperationException("The button does not expose its invoke pattern.");
        invoke.Invoke();
    }

    private static async Task<object?> CheckAccountStatsAsync(MainWindow window, string serverUrl)
    {
        var manager = AccountManager.Shared;
        var inactive = manager.CreateRemoteAccount(AccountType.Feedbin, "Stats inactive");
        inactive.IsActive = false;
        var folder = inactive.AddFolder("Stats folder");
        var feed = inactive.AddFeed(serverUrl + "/stats-feed", "Stats feed", folder);
        Article Make(string id, string feedID, bool read, bool starred) => new(inactive.AccountID, id, feedID, id,
            id, "<p>Statistics fixture</p>", null, null, serverUrl + "/" + id, null, null, null, DateTime.UtcNow, null,
            null, null, new ArticleStatus(id, read, starred, DateTime.UtcNow));
        await inactive.Database.UpdateArticlesAsync([
            Make("stats-read", feed.FeedID, true, false),
            Make("stats-star", feed.FeedID, false, true),
            Make("stats-retired", "not-subscribed", false, true)
        ]);
        await inactive.Database.CreateStatusesIfNeededAsync(["stats-missing"]);
        await inactive.SyncDatabase.InsertStatusesAsync([new("stats-star", SyncDatabase.SyncStatus.SyncKey.Starred, true)]);
        var stats = AppWindows.AccountStatsWindow.Show();
        try
        {
            await stats.RefreshAsync();
            await WaitFor(() => Task.FromResult(stats.Content is FrameworkElement { IsLoaded: true }), "Statistics window");
            var list = Descendants(stats.Content).OfType<ListView>().Single(control => control.Name == "StatsList");
            var rows = list.Items.OfType<AppWindows.AccountStatsWindow.Row>().ToList();
            Require(rows.Count == 3, "Account statistics did not include inactive accounts and a totals row.");
            var row = rows.Single(item => item.Name.StartsWith("Stats inactive", StringComparison.Ordinal));
            Require(row.Articles == "2" && row.Statuses == "4" && row.Unread == "1" && row.Starred == "1",
                "Article counts were not feed-scoped, or orphaned statuses were omitted.");
            Require(row.Opacity < 1, "The inactive account was not visually distinguished.");
            Require(rows[^1].Name == "Totals" && rows[^1].Articles == "3" && rows[^1].Statuses == "5", "The totals row is incorrect.");
            var resize = Descendants(stats.Content).OfType<ColumnResizeThumb>()
                .Single(control => AutomationProperties.GetName(control) == "Resize Account column");
            var resizePeer = FrameworkElementAutomationPeer.FromElement(resize) ?? FrameworkElementAutomationPeer.CreatePeerForElement(resize);
            var range = resizePeer?.GetPattern(PatternInterface.RangeValue) as IRangeValueProvider
                ?? throw new InvalidOperationException("The column resize handle has no range-value automation pattern.");
            Require(!range.IsReadOnly && range.SmallChange == 8 && range.LargeChange == 32 && range.Minimum > 40,
                "The resize range did not expose keyboard increments or fit its localized header.");
            bool rejected = false;
            try { range.SetValue(range.Minimum - 1); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "The resize handle accepted a width below its minimum.");
            range.SetValue(340);
            Grid? rowGrid = null;
            await WaitFor(() =>
            {
                rowGrid = Descendants(stats.Content).OfType<Grid>().FirstOrDefault(grid => grid.Name == "StatsRow");
                return Task.FromResult(rowGrid?.ColumnDefinitions[0].Width.Value == 340);
            }, "Statistics row width binding");
            var header = Descendants(stats.Content).OfType<Grid>().Single(grid => grid.Name == "StatsHeader");
            var headerCell = header.Children.OfType<Button>().Single(button => button.Tag as string == "DatabaseSizeBytes");
            var dataCell = rowGrid!.Children.OfType<TextBlock>().Single(block => Grid.GetColumn(block) == 1);
            var headerX = headerCell.TransformToVisual((UIElement)stats.Content).TransformPoint(new Point()).X;
            var rowX = dataCell.TransformToVisual((UIElement)stats.Content).TransformPoint(new Point()).X;
            Require(Math.Abs(headerX - rowX) <= 1, $"Resizing misaligned the header and rows: {headerX} vs {rowX}.");
            Require(AppDefaults.Shared.GetValue<Dictionary<string, double>>("AccountStatsColumnWidths")?.GetValueOrDefault("Name") == 340,
                "The resized column width was not persisted.");
            var sort = Descendants(stats.Content).OfType<Button>().Single(button => button.Tag as string == "ArticleCount");
            InvokeButton(sort);
            InvokeButton(sort);
            Require(list.Items.OfType<AppWindows.AccountStatsWindow.Row>().First().Name.StartsWith("Stats inactive", StringComparison.Ordinal),
                "The article-count column did not sort numerically.");
            var before = await inactive.FetchArticleCountsAsync();
            InvokeButton(Descendants(stats.Content).OfType<Button>().Single(button => button.Name == "VacuumButton"));
            var status = Descendants(stats.Content).OfType<TextBlock>().Single(control => control.Name == "StatusText");
            await WaitFor(() => Task.FromResult(status.Text.StartsWith("Vacuum completed.", StringComparison.Ordinal)), "Database vacuum");
            Require(before == await inactive.FetchArticleCountsAsync(), "Vacuum changed article or status counts.");
            Require(await inactive.SyncDatabase.SelectPendingCountAsync() == 1, "Vacuum discarded pending service changes.");
            Require((await inactive.Database.SearchAsync("stats-star")).Count > 0, "Vacuum damaged the search index.");
            Require(WinNewsWire.ActivityLog.ActivityLog.Shared.CompletedActivitiesFor(inactive.ActivityOwner)
                .Count(activity => activity.Kind == WinNewsWire.ActivityLog.ActivityKind.VacuumDatabase
                    && activity.State == WinNewsWire.ActivityLog.ActivityState.Completed) == 3,
                "The account's article, feed-settings and sync databases were not all vacuumed.");
            stats.Close();
            stats = AppWindows.AccountStatsWindow.Show();
            await stats.RefreshAsync();
            await WaitFor(() => Task.FromResult(stats.Content is FrameworkElement { IsLoaded: true }), "Reopened statistics window");
            var restoredResize = Descendants(stats.Content).OfType<ColumnResizeThumb>()
                .Single(control => AutomationProperties.GetName(control) == "Resize Account column");
            await WaitFor(() => Task.FromResult(restoredResize.Column?.Width.Value == 340), "Restored statistics column width");
            Require(restoredResize.Column?.Width.Value == 340, "Reopening statistics lost the resized column.");
            return new { IncludesInactiveAccounts = true, ScopedCounts = true, TotalsAndSorting = true,
                AccessibleResize = true, AlignedAndPersistentColumns = true, VacuumPreservedData = true };
        }
        finally
        {
            stats.Close();
            await manager.RemoveAccountAsync(inactive);
            window.Activate();
        }
    }

    private static async Task<object?> CheckSidebarPresentationAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var root = (FrameworkElement)window.Content;
        var originalTheme = root.RequestedTheme;
        var originalMode = AppDefaults.Shared.GetUnreadCountDisplay();
        var account = AccountManager.Shared.DefaultAccount!;
        var originalFeed = account.FlattenedFeeds().Single();
        var folder = account.AddFolder("Sidebar presentation folder");
        var childFeeds = Enumerable.Range(1, 3).Select(index =>
            account.AddFeed(originalFeed.Url + "?sidebar-child=" + index, "Folder child " + index, folder)).ToArray();
        var childFeed = childFeeds[0];
        bool originalRead = (await account.Database.FetchArticlesByIDsAsync(["probe-article"])).Single().Status.Read;
        var list = SidebarList(content);
        var originalScrollbarVisibility = ScrollViewer.GetVerticalScrollBarVisibility(list);
        Models.SidebarItem Row(string title) => list.Items.OfType<Models.SidebarItem>().Single(item => item.Title == title);
        ListViewItem Container(Models.SidebarItem item) => (ListViewItem)list.ContainerFromItem(item);
        TextBlock Title(Models.SidebarItem item) => Descendants(Container(item)).OfType<TextBlock>().Single(text => text.Text == item.Title);
        Rect Bounds(FrameworkElement element) => element.TransformToVisual(list)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        try
        {
            window.Activate();
            SetForegroundWindow(WindowNative.GetWindowHandle(window));
            await WaitFor(() => Task.FromResult(GetForegroundWindow() == WindowNative.GetWindowHandle(window)), "Foreground sidebar presentation");
            model.IsUnifiedLayout = false;
            model.IsReaderMode = false;
            model.IsSidebarVisible = true;
            model.IsSidebarReadFiltered = false;
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Visible);
            model.SetSelectedArticles([]);
            await model.PendingAutomaticReads;
            await model.ProbeDrainViewUpdatesAsync();
            await account.MarkAsync(["probe-article"], ArticleStatus.Key.Read, false);
            await account.RecalculateUnreadCountsAsync();
            AppDefaults.Shared.SetUnreadCountDisplay(UnreadCountDisplay.Count);
            model.BuildSidebar();
            list.SelectedItem = Row("All Unread");
            await WaitFor(() => Task.FromResult(ReferenceEquals(model.SelectedSidebarItem, Row("All Unread"))), "Visible unread scope selection");
            await model.FilterArticlesAsync();
            model.SetSelectedArticles([]);
            await WaitFor(() => Task.FromResult(list.Items.OfType<Models.SidebarItem>().All(item => list.ContainerFromItem(item) is not null)),
                "Sidebar presentation rows");
            var heading = Row("Smart Feeds");
            var selected = Row("All Unread");
            var accountRow = list.Items.OfType<Models.SidebarItem>().Single(item => ReferenceEquals(item.Tag, account));
            await WaitFor(() => Task.FromResult(Title(selected).ActualWidth > 0 && Bounds(Title(selected)).Left > 0
                && Title(accountRow).ActualWidth > 0 && Bounds(Title(accountRow)).Left > 0), "Arranged sidebar labels");
            await WaitFor(() => Task.FromResult(Descendants(list).OfType<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>()
                .Any(bar => bar.Orientation == Orientation.Vertical && bar.Visibility == Visibility.Visible && bar.ActualWidth > 0)),
                "Rendered sidebar scrollbar");
            var scrollbar = Descendants(list).OfType<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>()
                .Single(bar => bar.Orientation == Orientation.Vertical && bar.Visibility == Visibility.Visible && bar.ActualWidth > 0);
            var splitter = (FrameworkElement)content.FindName("SidebarSplitter");
            var sidebarColumn = (ColumnDefinition)content.FindName("SidebarColumn");
            await WaitFor(() => Task.FromResult(scrollbar.ActualWidth > 0
                && Math.Abs(Bounds(scrollbar).Right - list.ActualWidth) < 0.5 / list.XamlRoot.RasterizationScale
                && Math.Abs(Bounds(scrollbar).Right - sidebarColumn.ActualWidth - 4) < 0.5 / list.XamlRoot.RasterizationScale),
                "Sidebar scrollbar at the right edge");
            Require(Bounds(scrollbar).Right <= Bounds(splitter).Left && splitter.ActualWidth == 8,
                "The sidebar scrollbar clips or narrows the resize handle.");
            var presenter = Descendants(Container(selected)).OfType<Microsoft.UI.Xaml.Controls.Primitives.ListViewItemPresenter>().Single();
            var headingPresenter = Descendants(Container(heading)).OfType<Microsoft.UI.Xaml.Controls.Primitives.ListViewItemPresenter>().Single();
            var measurements = new
            {
                HeadingLeft = Bounds(Title(heading)).Left,
                AccountLeft = Bounds(Title(accountRow)).Left,
                TitleLeft = Bounds(Title(selected)).Left,
                RowSpacing = Bounds(Title(selected)).Top - Bounds(Title(Row("Today"))).Top,
                ScrollbarRightInset = list.ActualWidth - Bounds(scrollbar).Right,
                ScrollbarGutterOffset = Bounds(scrollbar).Right - sidebarColumn.ActualWidth,
                presenter.SelectionIndicatorVisualEnabled,
                CornerRadius = presenter.CornerRadius.TopLeft,
                headingPresenter.DisabledOpacity
            };
            File.WriteAllText(Path.Combine(OutputDirectory, "sidebar-measurements.json"),
                JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                await Task.Delay(300);
                Require(Container(selected).IsSelected && ReferenceEquals(model.SelectedSidebarItem, selected),
                    "The sidebar presentation fixture lost its selected unread row.");
                await SaveWindowScreenshotAsync(window, "sidebar-" + theme.ToString().ToLowerInvariant());
                Require(presenter.SelectionIndicatorVisualEnabled && presenter.CornerRadius.TopLeft == 4,
                    "The sidebar lost its rounded selection highlight and accent indicator.");
                var accent = (presenter.SelectionIndicatorBrush as SolidColorBrush)?.Color
                    ?? throw new InvalidOperationException("The sidebar has no themed selection-indicator brush.");
                var capture = CaptureWindow(window);
                var center = Container(selected).TransformToVisual(root).TransformPoint(new(5.5, Container(selected).ActualHeight / 2));
                int x = (int)Math.Round(center.X * root.XamlRoot.RasterizationScale) + capture.ClientOrigin.X - capture.Bounds.Left;
                int y = (int)Math.Round(center.Y * root.XamlRoot.RasterizationScale) + capture.ClientOrigin.Y - capture.Bounds.Top;
                int offset = checked((y * capture.Width + x) * 4);
                Require(Math.Abs(capture.Data[offset + 2] - accent.R) < 12
                    && Math.Abs(capture.Data[offset + 1] - accent.G) < 12 && Math.Abs(capture.Data[offset] - accent.B) < 12,
                    "The selected sidebar row has no visible accent bar in " + theme + " mode.");
            }
            Require(Container(selected).Padding.Left == 10 && Math.Abs(measurements.HeadingLeft - 20) < 1
                && Math.Abs(measurements.AccountLeft - measurements.HeadingLeft) < 0.1
                && Math.Abs(measurements.TitleLeft - 52) < 1 && measurements.TitleLeft - measurements.HeadingLeft >= 20
                && Math.Abs(measurements.RowSpacing - 32) < 1,
                "Sidebar headings are misaligned or the requested tree gutter is missing.");
            Require(Title(heading).FontSize == 11 && Title(heading).FontWeight.Weight == FontWeights.SemiBold.Weight
                && Title(heading).CharacterSpacing == 60 && Title(selected).FontSize == 14
                && headingPresenter.DisabledOpacity == 1 && !Container(heading).IsEnabled && !Container(heading).IsTabStop,
                "The non-interactive section heading lost its original readable typography.");
            var indicator = Descendants(Container(selected)).OfType<Border>().Single(border => border.Name == "SidebarUnreadIndicator");
            var count = Descendants(indicator).OfType<TextBlock>().Single();
            Require(indicator.Padding == new Thickness(6, 0, 6, 0) && indicator.Background is null
                && count.FontSize == 12 && count.HorizontalAlignment == HorizontalAlignment.Right && count.Text == selected.UnreadCountText,
                "Unread counts are no longer plain, right-aligned secondary text.");
            var feedRow = list.Items.OfType<Models.SidebarItem>().Single(item => ReferenceEquals(item.Tag, originalFeed));
            var icon = Descendants(Container(feedRow)).OfType<Image>().Single();
            Require(icon.Width == 16 && icon.Height == 16 && icon.Visibility == Visibility.Visible && Math.Abs(Bounds(icon).Left - 30) < 1
                && Math.Abs(Bounds(Title(feedRow)).Left - Bounds(icon).Right - 6) < 0.1,
                "The sidebar favicon slot was removed or misaligned.");
            foreach (var (title, glyph) in new[] { ("Today", "\uE706"), ("All Unread", "\uE7BC"), ("Starred", "\uE734") })
            {
                var smart = Container(Row(title));
                var symbol = Descendants(smart).OfType<FontIcon>().Single(item => Grid.GetColumn(item) == 1);
                Require(symbol.Glyph == glyph && symbol.Visibility == Visibility.Visible
                    && symbol.FontSize == 14 && symbol.Width == 16 && symbol.Height == 16
                    && Bounds(symbol).Left > measurements.HeadingLeft
                    && Math.Abs(Bounds(Title(Row(title))).Left - Bounds(symbol).Right - 6) < 0.1
                    && Descendants(smart).OfType<Image>().Single().Visibility == Visibility.Collapsed,
                    "The smart-feed icon is missing or incorrect: " + title);
            }
            model.SelectedSidebarItem = accountRow;
            await model.FilterArticlesAsync();
            await WaitFor(() => Task.FromResult(!Container(accountRow).IsSelected), "Non-selectable account heading");
            var accountContainer = Container(accountRow);
            var accountPresenter = Descendants(accountContainer).OfType<Microsoft.UI.Xaml.Controls.Primitives.ListViewItemPresenter>().Single();
            Require(!accountRow.IsExpandable && !accountRow.IsSelectable && !accountContainer.IsEnabled && !accountContainer.IsTabStop
                && !Descendants(accountContainer).OfType<Button>().Any(),
                "The account heading still behaves like an interactive row.");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                await Task.Delay(300);
                await SaveWindowScreenshotAsync(window, "sidebar-account-" + theme.ToString().ToLowerInvariant());
                var labelBounds = Bounds(Title(accountRow));
                var containerBounds = Bounds(accountContainer);
                Require(Math.Abs(labelBounds.Top + labelBounds.Height / 2 - containerBounds.Top - containerBounds.Height / 2) < 0.7
                    && accountContainer.Padding.Top == accountContainer.Padding.Bottom,
                    "The account title is not vertically centered with equal padding.");
                Require(!accountContainer.IsSelected && !accountPresenter.SelectionIndicatorVisualEnabled
                    && Math.Abs(labelBounds.Left - Bounds(Title(heading)).Left) < 0.1,
                    "The static account heading is not aligned with Smart Feeds.");
                var capture = CaptureWindow(window);
                var point = accountContainer.TransformToVisual(root).TransformPoint(new(5.5, accountContainer.ActualHeight / 2));
                int x = (int)Math.Round(point.X * root.XamlRoot.RasterizationScale) + capture.ClientOrigin.X - capture.Bounds.Left;
                int y = (int)Math.Round(point.Y * root.XamlRoot.RasterizationScale) + capture.ClientOrigin.Y - capture.Bounds.Top;
                int offset = checked((y * capture.Width + x) * 4);
                var accent = ((SolidColorBrush)accountPresenter.SelectionIndicatorBrush).Color;
                Require(Math.Abs(capture.Data[offset + 2] - accent.R) >= 12
                    || Math.Abs(capture.Data[offset + 1] - accent.G) >= 12 || Math.Abs(capture.Data[offset] - accent.B) >= 12,
                    "The selected account header still paints the blue selection mark.");
            }
            Models.SidebarItem FolderRow() => list.Items.OfType<Models.SidebarItem>().Single(item => ReferenceEquals(item.Tag, folder));
            bool ChildVisible() => list.Items.OfType<Models.SidebarItem>().Any(item => ReferenceEquals(item.Tag, childFeed));
            var folderRow = FolderRow();
            model.SelectedSidebarItem = folderRow;
            await model.FilterArticlesAsync();
            await WaitFor(() => Task.FromResult(Container(folderRow) is { IsLoaded: true, IsSelected: true }), "Selected folder row");
            var folderContainer = Container(folderRow);
            var disclosure = Descendants(folderContainer).OfType<Button>().Single(button => ReferenceEquals(button.Tag, folderRow));
            var chevron = Descendants(disclosure).OfType<FontIcon>().Single();
            var folderIcon = Descendants(folderContainer).OfType<FontIcon>().Single(glyph => Grid.GetColumn(glyph) == 1);
            Require(Bounds(disclosure).Left >= 8 && Bounds(chevron).Left >= 12,
                "The folder disclosure still touches the window edge.");
            Require(folderIcon.Glyph == "\uE8B7" && folderIcon.FontSize == 14 && folderIcon.Width == 16 && folderIcon.Height == 16
                && folderIcon.Visibility == Visibility.Visible
                && Math.Abs(Bounds(folderIcon).Left - Bounds(icon).Left) < 0.1
                && Math.Abs(Bounds(Title(folderRow)).Left - Bounds(folderIcon).Right - 6) < 0.1
                && Descendants(folderContainer).OfType<Image>().Single().Visibility == Visibility.Collapsed,
                "A folder does not have a visible, aligned folder icon.");
            var stableRows = list.Items.OfType<Models.SidebarItem>()
                .Where(item => !ReferenceEquals(item.ParentFolder, folder)).ToDictionary(item => item, Container);
            var notifications = (INotifyCollectionChanged)list.ItemsSource;
            int resets = 0, selectionChanges = 0, headingUnloads = 0;
            void RowsChanged(object? sender, NotifyCollectionChangedEventArgs args)
            {
                if (args.Action == NotifyCollectionChangedAction.Reset) resets++;
            }
            void SelectionChanged(object sender, SelectionChangedEventArgs args) => selectionChanges++;
            void HeadingUnloaded(object sender, RoutedEventArgs args) => headingUnloads++;
            notifications.CollectionChanged += RowsChanged;
            list.SelectionChanged += SelectionChanged;
            accountContainer.Unloaded += HeadingUnloaded;
            try
            {
                await InvokeDisclosureAsync(content, FolderRow);
                await WaitFor(() => Task.FromResult(!ChildVisible()), "Folder collapse");
                Require(!folderRow.IsExpanded && list.Items.OfType<Models.SidebarItem>().Any(item => ReferenceEquals(item.Tag, originalFeed)),
                    "Folder collapse hid unrelated subscriptions.");
                await SaveWindowScreenshotAsync(window, "sidebar-folder-collapsed");
                Require(stableRows.All(pair => ReferenceEquals(Container(pair.Key), pair.Value)),
                    "Collapsing a folder replaced an unchanged sidebar container.");
                await InvokeDisclosureAsync(content, FolderRow);
                await WaitFor(() => Task.FromResult(ChildVisible()), "Folder expansion");
                if (new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                    Require(content.ProbeSidebarRevealCount == childFeeds.Length, "Folder expansion did not animate all visible children.");
                var childRow = list.Items.OfType<Models.SidebarItem>().Single(item => ReferenceEquals(item.Tag, childFeed));
                var childIcon = Descendants(Container(childRow)).OfType<Image>().Single();
                Require(Math.Abs(Bounds(Title(childRow)).Left - Bounds(childIcon).Right - 6) < 0.1
                    && Math.Abs(Bounds(Title(childRow)).Left - Bounds(Title(folderRow)).Left - 20) < 0.1,
                    "Nested feed labels lost their icon spacing or indentation.");
                var childRoot = (FrameworkElement)Container(childRow).ContentTemplateRoot;
                var motion = new List<(double Opacity, double Y)>();
                for (int sample = 0; sample < 15; sample++)
                {
                    motion.Add((childRoot.Opacity, (childRoot.RenderTransform as TranslateTransform)?.Y ?? 0));
                    Require(stableRows.Values.All(container => ((FrameworkElement)container.ContentTemplateRoot).Opacity == 1),
                        "Folder expansion faded an unchanged sidebar row.");
                    await Task.Delay(20);
                }
                File.WriteAllText(Path.Combine(OutputDirectory, "sidebar-folder-motion.json"), JsonSerializer.Serialize(
                    motion.Select(sample => new { sample.Opacity, sample.Y }), new JsonSerializerOptions { WriteIndented = true }));
                if (new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                    Require(motion.Any(sample => sample.Opacity < 0.95 && sample.Y < -0.5),
                        "New folder items did not fade while moving downward.");
                Require(childRoot.Opacity == 1 && content.ProbeSidebarRevealCount == 0
                    && childRoot.RenderTransform is not TranslateTransform,
                    "A completed folder animation left opacity, translation, or callbacks behind.");
                await SaveWindowScreenshotAsync(window, "sidebar-folder-expanded");
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    await InvokeDisclosureAsync(content, FolderRow);
                    await WaitFor(() => Task.FromResult(!ChildVisible()), "Rapid folder collapse");
                    await InvokeDisclosureAsync(content, FolderRow);
                    await WaitFor(() => Task.FromResult(ChildVisible()), "Rapid folder expansion");
                }
                await WaitFor(() => Task.FromResult(content.ProbeSidebarRevealCount == 0), "Settled folder reveal");
                Require(resets == 0 && selectionChanges == 0 && headingUnloads == 0
                    && stableRows.All(pair => ReferenceEquals(Container(pair.Key), pair.Value)),
                    "Folder disclosure reset sidebar rows, selection, or the static heading.");
            }
            finally
            {
                notifications.CollectionChanged -= RowsChanged;
                list.SelectionChanged -= SelectionChanged;
                accountContainer.Unloaded -= HeadingUnloaded;
            }
            var restoredFeed = list.Items.OfType<Models.SidebarItem>().Single(item => ReferenceEquals(item.Tag, originalFeed));
            model.SelectedSidebarItem = restoredFeed;
            await WaitFor(() => Task.FromResult(list.ContainerFromItem(restoredFeed) is ListViewItem), "Feed row after account expansion");
            Require(Descendants(Container(restoredFeed)).OfType<Microsoft.UI.Xaml.Controls.Primitives.ListViewItemPresenter>()
                .Single().SelectionIndicatorVisualEnabled, "Account styling removed the selection marker from ordinary feed rows.");
            await CheckSidebarGutterInputAsync(window, list);
            return new { measurements, LightAndDarkAccentPixels = true, PlainCounts = true, FaviconSlotPreserved = true,
                StaticAlignedAccountHeader = true, InsetFolderDisclosure = true, FolderIcon = true, FadeDownChildrenOnly = true,
                NoDisclosureReset = true, RetainedSidebarContainers = true, NativeScrollbarAndResizeInput = true };
        }
        finally
        {
            root.RequestedTheme = originalTheme;
            ScrollViewer.SetVerticalScrollBarVisibility(list, originalScrollbarVisibility);
            AppDefaults.Shared.SetUnreadCountDisplay(originalMode);
            await account.MarkAsync(["probe-article"], ArticleStatus.Key.Read, originalRead);
            await account.RecalculateUnreadCountsAsync();
            foreach (var child in childFeeds) account.RemoveFeed(child);
            account.RemoveFolder(folder);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
        }
    }

    private static async Task CheckSidebarGutterInputAsync(MainWindow window, ListView list)
    {
        var content = window.ProbeContent;
        var scroller = Descendants(list).OfType<ScrollViewer>().Single(view => view.Name == "ScrollViewer");
        var splitter = (FrameworkElement)content.FindName("SidebarSplitter");
        var column = (ColumnDefinition)content.FindName("SidebarColumn");
        double height = list.Height, offset = scroller.VerticalOffset;
        var alignment = list.VerticalAlignment;
        var width = column.Width;
        var handle = WindowNative.GetWindowHandle(window);
        try
        {
            list.VerticalAlignment = VerticalAlignment.Top;
            list.Height = 96;
            await WaitFor(() => Task.FromResult(list.ActualHeight == 96 && scroller.ScrollableHeight > 0),
                "Scrollable sidebar gutter fixture");
            scroller.ChangeView(null, 0, null, disableAnimation: true);
            await WaitFor(() => Task.FromResult(scroller.VerticalOffset == 0), "Sidebar at top before native input");
            var rail = Descendants(list).OfType<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>()
                .Single(bar => bar.Orientation == Orientation.Vertical && bar.Visibility == Visibility.Visible && bar.ActualWidth > 0 && bar.Maximum > 0);
            var arrow = Descendants(rail).OfType<Microsoft.UI.Xaml.Controls.Primitives.RepeatButton>()
                .Single(button => button.Name == "VerticalSmallIncrease");
            await WaitFor(() => Task.FromResult(rail.ActualHeight > 0 && rail.ActualHeight <= list.ActualHeight),
                "Arranged scrollbar rail in the shortened sidebar");
            // Auto-hidden scrollbar parts retain stale bounds until the native mouse presentation is shown.
            Require(VisualStateManager.GoToState(scroller, "MouseIndicatorFull", false)
                && VisualStateManager.GoToState(rail, "ExpandedWithoutAnimation", false),
                "The native expanded scrollbar presentation is unavailable.");
            await WaitFor(() =>
            {
                var arrowBounds = arrow.TransformToVisual(list).TransformBounds(new(0, 0, arrow.ActualWidth, arrow.ActualHeight));
                return Task.FromResult(arrow.ActualHeight > 0 && arrow.Opacity > 0 && rail.ActualHeight <= list.ActualHeight
                    && arrowBounds.Top >= 0 && arrowBounds.Bottom <= list.ActualHeight + 1);
            }, "Arranged scrollbar arrow in the shortened sidebar");
            var click = arrow.TransformToVisual((UIElement)window.Content)
                .TransformPoint(new(arrow.ActualWidth * 0.9, arrow.ActualHeight / 2));
            File.WriteAllText(Path.Combine(OutputDirectory, "sidebar-gutter-input.json"), JsonSerializer.Serialize(new
            {
                scroller.ScrollableHeight, scroller.VerticalOffset, rail.Maximum, arrow.ActualWidth, arrow.ActualHeight,
                click, Hits = VisualTreeHelper.FindElementsInHostCoordinates(click, (UIElement)window.Content)
                    .OfType<FrameworkElement>().Take(10).Select(element => new { element.Name, Type = element.GetType().Name })
            }, new JsonSerializerOptions { WriteIndented = true }));
            await SaveWindowScreenshotAsync(window, "sidebar-gutter-before");
            await ClickElementAsync(arrow, () => scroller.VerticalOffset > 0,
                "Native scrollbar click in the divider gutter", 0.9);
            await SaveWindowScreenshotAsync(window, "sidebar-gutter-scroll");
            list.Height = height;
            list.VerticalAlignment = alignment;
            scroller.ChangeView(null, offset, null, disableAnimation: true);
            await WaitFor(() => Task.FromResult(list.ActualHeight > 96), "Sidebar restored after gutter click");

            var origin = new CapturePoint();
            Require(ClientToScreen(handle, ref origin), "Could not locate the sidebar resize handle.");
            Require(GetCursorPos(out var originalCursor), "Could not save the pointer position.");
            var position = splitter.TransformToVisual((UIElement)window.Content)
                .TransformPoint(new(splitter.ActualWidth - 1, splitter.ActualHeight / 2));
            double scale = content.XamlRoot.RasterizationScale;
            var start = new CapturePoint { X = origin.X + (int)Math.Round(position.X * scale),
                Y = origin.Y + (int)Math.Round(position.Y * scale) };
            var end = new CapturePoint { X = start.X + (int)Math.Round(8 * scale), Y = start.Y };
            double initialWidth = column.ActualWidth;
            var resizeHits = VisualTreeHelper.FindElementsInHostCoordinates(position, (UIElement)window.Content).ToArray();
            File.WriteAllText(Path.Combine(OutputDirectory, "sidebar-gutter-resize.json"), JsonSerializer.Serialize(new
            {
                initialWidth, position, splitter.ActualWidth, splitter.Visibility, splitter.IsHitTestVisible,
                Hits = resizeHits.OfType<FrameworkElement>().Take(10).Select(element => new { element.Name, Type = element.GetType().Name })
            }, new JsonSerializerOptions { WriteIndented = true }));
            Require(resizeHits.FirstOrDefault() is { } hit
                && (ReferenceEquals(hit, splitter) || Descendants(splitter).Contains(hit)),
                "The content covers the far edge of the sidebar resize handle.");
            bool pressed = false;
            Require(GetForegroundWindow() == handle && GetAncestor(WindowFromPoint(start), 2) == handle
                && SetCursorPos(start.X, start.Y), "The private resize handle does not own pointer input.");
            try
            {
                pressed = SendInput(1, [new() { Mouse = new() { Flags = 2 } }],
                    System.Runtime.InteropServices.Marshal.SizeOf<ScrollInput>()) == 1;
                Require(pressed, "The sidebar resize press failed.");
                await Task.Delay(100);
                Require(GetForegroundWindow() == handle && GetCursorPos(out var current)
                    && current.X == start.X && current.Y == start.Y,
                    "The sidebar resize gesture lost pointer ownership.");
                int left = BrowserInputSystemMetric(76), top = BrowserInputSystemMetric(77);
                int desktopWidth = BrowserInputSystemMetric(78), desktopHeight = BrowserInputSystemMetric(79);
                Require(desktopWidth > 1 && desktopHeight > 1, "The resize input desktop has no usable bounds.");
                var move = new ScrollInput { Mouse = new()
                {
                    X = (int)Math.Round((end.X - left) * 65535d / (desktopWidth - 1)),
                    Y = (int)Math.Round((end.Y - top) * 65535d / (desktopHeight - 1)),
                    Flags = 0xC001
                } };
                Require(SendInput(1, [move], System.Runtime.InteropServices.Marshal.SizeOf<ScrollInput>()) == 1,
                    "The native sidebar resize movement failed.");
                await WaitFor(() => Task.FromResult(Math.Abs(column.ActualWidth - initialWidth - 8) <= 1),
                    "Native drag at the far edge of the resize handle");
            }
            finally
            {
                if (pressed) Require(SendInput(1, [new() { Mouse = new() { Flags = 4 } }],
                    System.Runtime.InteropServices.Marshal.SizeOf<ScrollInput>()) == 1, "The sidebar resize release failed.");
                if (GetCursorPos(out var current) && current.Y == start.Y && (current.X == start.X || current.X == end.X))
                    SetCursorPos(originalCursor.X, originalCursor.Y);
            }
        }
        finally
        {
            list.Height = height;
            list.VerticalAlignment = alignment;
            column.Width = width;
            scroller.ChangeView(null, offset, null, disableAnimation: true);
            VisualStateManager.GoToState(scroller, "NoIndicator", false);
        }
    }

    private static async Task<object?> CheckSidebarSelectionAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var account = AccountManager.Shared.DefaultAccount!;
        var originalFeed = account.FlattenedFeeds().Single();
        var folder = account.AddFolder("Selection folder");
        var first = account.AddFeed(serverUrl + "/selection-first", "Selection first", folder);
        account.AddFeed(first.Url);
        var second = account.AddFeed(serverUrl + "/selection-second", "Selection second");
        Article Make(string id, Feed feed, bool read, string title) => new(account.AccountID, id, feed.FeedID, id,
            title, "<p>Selection fixture</p>", null, null, serverUrl + "/" + id, null, null, null, DateTime.UtcNow, null,
            null, null, new ArticleStatus(id, read, false, DateTime.UtcNow));
        await account.Database.UpdateArticlesAsync([
            Make("selection-first", first, true, "Firstneedle"),
            Make("selection-unread", first, false, "Unread"),
            Make("selection-second", second, true, "Second")
        ]);
        try
        {
            model.BuildSidebar();
            var list = Descendants(content).OfType<ListView>().Single(control => control.Name == "SidebarListView");
            Require(list.SelectionMode == ListViewSelectionMode.Extended, "The sidebar does not support independent row selection.");
            Models.SidebarItem Row(object value, Folder? parent = null) => list.Items.OfType<Models.SidebarItem>()
                .Single(item => ReferenceEquals(item.Tag, value) && ReferenceEquals(item.ParentFolder, parent));
            var folderRow = Row(folder);
            list.SelectedItems.Clear();
            list.SelectedItems.Add(folderRow);
            await model.FilterArticlesAsync();
            await WaitFor(() => Task.FromResult(model.ArticleItems.Count == 1 && model.ArticleItems[0].Id == "selection-unread"),
                "Folder's filtered timeline");
            Require(list.SelectedItems.Count == 1 && model.SelectedSidebarItems.Count == 1,
                "Selecting a folder also selected its descendants.");
            Require(model.ArticleItems.Count == 1 && model.ArticleItems[0].Id == "selection-unread", "A folder's default read filter was lost.");
            list.SelectedItems.Add(Row(first));
            list.SelectedItems.Add(Row(second));
            await model.FilterArticlesAsync();
            await WaitFor(() => Task.FromResult(model.ArticleItems.Count == 3), "Combined sidebar timeline");
            Require(model.SelectedSidebarItems.Count == 3 && model.SelectedSidebarItem is null && !model.CanToggleReadFilter,
                "Mixed selection did not clear single-item command state.");
            Require(model.ArticleItems.Count == 3 && model.ArticleItems.Select(item => item.Id).Distinct().Count() == 3,
                "Overlapping folder/feed scopes duplicated or omitted articles.");
            model.SearchQuery = "Firstneedle";
            await model.FilterArticlesAsync();
            await WaitFor(() => Task.FromResult(model.ArticleItems.Count == 1 && model.ArticleItems[0].Id == "selection-first"),
                "Scoped sidebar search");
            Require(model.ArticleItems.Count == 1 && model.ArticleItems[0].Id == "selection-first", "Search escaped or lost the selected scopes.");
            model.SearchQuery = "";
            await model.FilterArticlesAsync();
            var keys = model.SelectedSidebarItems.Select(item => item.Identity).ToHashSet();
            model.BuildSidebar();
            await model.FilterArticlesAsync();
            Require(keys.SetEquals(model.SelectedSidebarItems.Select(item => item.Identity)), "A rebuild lost the selected sidebar identities.");
            folderRow = Row(folder);
            list.ScrollIntoView(folderRow);
            await WaitFor(() => Task.FromResult(list.ContainerFromItem(folderRow) is not null), "Folder row realization");
            var expander = Descendants(list.ContainerFromItem(folderRow)).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Collapse Selection folder");
            InvokeButton(expander);
            await WaitFor(() => Task.FromResult(!Row(folder).IsExpanded), "Folder collapse");
            Require(!Row(folder).IsExpanded && !list.Items.OfType<Models.SidebarItem>().Any(item => ReferenceEquals(item.ParentFolder, folder)),
                "The folder did not collapse independently of its selection.");
            await content.MarkAllReadInSelectedAsync();
            Require((await account.Database.FetchArticlesByIDsAsync(["selection-unread"])).Single().Status.Read, "Bulk mark read did not include all selected scopes.");
            await model.UndoAsync();
            Require(!(await account.Database.FetchArticlesByIDsAsync(["selection-unread"])).Single().Status.Read, "Bulk status undo did not restore the changed item.");
            var deletion = content.DeleteSelectedAsync();
            ContentDialog? dialog = null;
            await WaitFor(() => Task.FromResult((dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(content.XamlRoot)
                .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault())?.IsLoaded == true), "Batch delete confirmation");
            InvokeDialogButton(dialog!, "PrimaryButton");
            await deletion.WaitAsync(TimeSpan.FromSeconds(10));
            Require(account.ExistingFeedWithUrl(first.Url) is null && account.ExistingFeedWithUrl(second.Url) is null
                && !account.Folders.Contains(folder), "Batch deletion did not remove all selected memberships.");
            await model.UndoAsync();
            Require(account.TopLevelFeeds.Contains(first) && account.TopLevelFeeds.Contains(second)
                && account.Folders.Contains(folder) && folder.Feeds.Contains(first), "One undo did not restore the folder and both feed memberships.");
            return new { IndependentRows = true, ScopedFilters = true, DeduplicatedUnion = true, PreservedSelection = true, BatchUndo = true };
        }
        finally
        {
            account.RemoveFeed(first);
            account.RemoveFeed(second);
            account.RemoveFolder(folder);
            model.UndoManager.RemoveAll();
            model.BuildSidebar();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, originalFeed.FeedID);
            await model.FilterArticlesAsync();
        }
    }

    private static Models.MainWindowState[] ReadSavedWindows()
        => new AppDefaults(Path.Combine(AppConfig.DataDirectory, "AppDefaults.json"))
            .GetValue<Models.MainWindowState[]>(App.MainWindowStatesKey) ?? [];

    private static ListView SidebarList(MainContent content)
        => (ListView)content.FindName("SidebarListView");

    private static async Task InvokeDisclosureAsync(MainContent content, Func<Models.SidebarItem> currentItem)
    {
        var list = SidebarList(content);
        list.ScrollIntoView(currentItem());
        Button? button = null;
        await WaitFor(() =>
        {
            var item = currentItem();
            if (list.ContainerFromItem(item) is { } container)
                button = Descendants(container).OfType<Button>()
                    .FirstOrDefault(candidate => candidate.Tag is Models.SidebarItem row && row.Identity == item.Identity);
            return Task.FromResult(button is not null);
        }, "Sidebar disclosure realization");
        InvokeButton(button!);
    }

    private static async Task<object?> CheckSidebarFilterAsync(MainWindow window, string serverUrl)
    {
        var original = window.CaptureState();
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var account = AccountManager.Shared.DefaultAccount!;
        var originalFeed = account.FlattenedFeeds().Single();
        var folder = account.AddFolder("Filter unread folder");
        var readFolder = account.AddFolder("Filter read folder");
        var empty = account.AddFolder("Filter empty folder");
        var read = account.AddFeed(serverUrl + "/filter-read", "Filter read");
        var unread = account.AddFeed(serverUrl + "/filter-unread", "Filter unread");
        var nestedUnread = account.AddFeed(serverUrl + "/filter-child", "Filter child", folder);
        var sharedRead = account.AddFeed(serverUrl + "/filter-shared", "Filter shared", readFolder);
        account.AddFeed(sharedRead.Url);
        WinNewsWire.Account.Account? addedAccount = null;
        Article Make(string id, Feed feed, bool isRead) => new(account.AccountID, id, feed.FeedID, id, id,
            "<p>Sidebar filtering fixture</p>", null, null, serverUrl + "/" + id, null, null, null,
            DateTime.UtcNow, null, null, null, new ArticleStatus(id, isRead, false, DateTime.UtcNow));
        await account.Database.UpdateArticlesAsync([
            Make("filter-read", read, true), Make("filter-unread", unread, false),
            Make("filter-child", nestedUnread, false), Make("filter-shared", sharedRead, true)
        ]);
        await account.RecalculateUnreadCountsAsync();
        try
        {
            model.IsUnifiedLayout = false;
            model.IsSidebarVisible = true;
            model.BuildSidebar();
            var list = SidebarList(content);
            Models.SidebarItem Row(object item, Folder? parent = null) => list.Items.OfType<Models.SidebarItem>()
                .Single(row => ReferenceEquals(row.Tag, item) && ReferenceEquals(row.ParentFolder, parent));
            bool Visible(object item, Folder? parent = null) => list.Items.OfType<Models.SidebarItem>()
                .Any(row => ReferenceEquals(row.Tag, item) && ReferenceEquals(row.ParentFolder, parent));
            model.SelectedSidebarItem = Row(originalFeed);
            var toggle = (Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)content.FindName("SidebarReadFilterButton");
            var peer = FrameworkElementAutomationPeer.FromElement(toggle) ?? FrameworkElementAutomationPeer.CreatePeerForElement(toggle);
            var togglePattern = peer?.GetPattern(PatternInterface.Toggle) as IToggleProvider
                ?? throw new InvalidOperationException("The sidebar filter has no accessible toggle pattern.");
            togglePattern.Toggle();
            await WaitFor(() => Task.FromResult(model.IsSidebarReadFiltered), "Sidebar unread filter");
            Require(Visible(originalFeed) && Visible(unread) && Visible(folder) && Visible(nestedUnread, folder),
                "The filter removed selected or unread subscriptions.");
            Require(!Visible(read) && !Visible(sharedRead) && !Visible(readFolder) && !Visible(empty),
                "The filter kept unselected read-only or empty subscriptions.");
            Require(list.Items.OfType<Models.SidebarItem>().Count(row => row.Tag is AppShared.SmartFeeds.IPseudoFeed) == 3,
                "The filter hid a smart feed with no unread articles.");
            Require(Visible(account) && !Row(account).IsExpandable, "The filter hid the static account heading or made it expandable.");
            Require(model.SidebarReadFilterLabel == "Show Read Feeds", "The filter command label did not reflect its state.");

            model.SelectedSidebarItem = Row(unread);
            model.BuildSidebar();
            Require(!Visible(originalFeed), "A previous selection was retained after filter exceptions reset.");
            await content.MarkAllReadInSelectedAsync();
            await WaitFor(() => Task.FromResult(Row(unread).UnreadCount == 0), "Selected feed unread-count update");
            Require(Visible(unread) && model.SelectedSidebarItem?.Tag == unread, "The newly read selected feed disappeared.");
            var nestedRead = model.SidebarItems.SelectMany(section => section.Children).Single(row => ReferenceEquals(row.Tag, readFolder))
                .Children.Single(row => ReferenceEquals(row.Tag, sharedRead));
            model.SelectedSidebarItem = nestedRead;
            await WaitFor(() => Task.FromResult(Visible(sharedRead, readFolder)), "Programmatic hidden-feed reveal");
            Require(Visible(readFolder) && Row(readFolder).IsExpanded && Visible(sharedRead), "Feed and parent-folder exceptions did not match native membership semantics.");
            model.BuildSidebar();
            Require(Visible(sharedRead, readFolder) && !Visible(unread), "A rebuild did not preserve only the current filter exceptions.");
            var expandedFolderKey = Row(readFolder).Identity!.Value;
            await InvokeDisclosureAsync(content, () => Row(readFolder));
            await WaitFor(() => Task.FromResult(!Row(readFolder).IsExpanded), "Folder collapse");
            Require(model.SelectedSidebarItem?.Tag == readFolder && Visible(readFolder) && !Visible(sharedRead, readFolder),
                "Collapsing a folder did not replace its hidden child selection.");
            model.BuildSidebar();
            Require(!Row(readFolder).IsExpanded && !Row(account).IsExpandable, "A rebuild lost the folder state or restored account disclosure.");
            var saved = window.CaptureState();
            Require(saved.Sidebar.IsReadFiltered && !(saved.Sidebar.ExpandedContainers ?? []).Contains(expandedFolderKey)
                && !(saved.Sidebar.ExpandedContainers ?? []).Any(key => key.Kind == Models.SidebarItemType.Account),
                "Saved state lost the collapsed folder or persisted account disclosure.");
            var restored = App.OpenMainWindow(saved);
            Require(await restored.Restoration.WaitAsync(TimeSpan.FromSeconds(30)), "The filtered sidebar did not restore.");
            var restoredList = SidebarList(restored.ProbeContent);
            Require(restored.ProbeContent.ViewModel.IsSidebarReadFiltered
                && !restoredList.Items.OfType<Models.SidebarItem>().Single(row => ReferenceEquals(row.Tag, account)).IsExpandable
                && !restoredList.Items.OfType<Models.SidebarItem>().Single(row => ReferenceEquals(row.Tag, readFolder)).IsExpanded,
                "Window restoration lost the folder state, static heading, or sidebar filter.");
            restored.ProbeContent.ViewModel.IsSidebarReadFiltered = false;
            Require(model.IsSidebarReadFiltered, "The sidebar filter leaked between windows.");
            await InvokeDisclosureAsync(restored.ProbeContent, () => restoredList.Items.OfType<Models.SidebarItem>().Single(row => ReferenceEquals(row.Tag, readFolder)));
            await WaitFor(() => Task.FromResult(restoredList.Items.OfType<Models.SidebarItem>().Any(row => ReferenceEquals(row.Tag, sharedRead)
                && ReferenceEquals(row.ParentFolder, readFolder))), "Restored folder expansion");
            await CloseWindowAsync(restored);

            foreach (var legacySidebar in new[]
            {
                new Models.SidebarWindowState { ExpandedFolders = [expandedFolderKey] },
                new Models.SidebarWindowState { SelectedItems = [Row(account).Identity!.Value], ExpandedContainers = [expandedFolderKey] }
            })
            {
                var legacy = App.OpenMainWindow(saved with { Sidebar = legacySidebar });
                Require(await legacy.Restoration.WaitAsync(TimeSpan.FromSeconds(30)), "Legacy sidebar state did not migrate.");
                var legacyRows = SidebarList(legacy.ProbeContent).Items.OfType<Models.SidebarItem>().ToArray();
                Require(!legacyRows.Single(row => ReferenceEquals(row.Tag, account)).IsExpandable
                    && legacyRows.Single(row => ReferenceEquals(row.Tag, readFolder)).IsExpanded
                    && legacyRows.Any(row => ReferenceEquals(row.Tag, sharedRead) && ReferenceEquals(row.ParentFolder, readFolder)),
                    "An old collapsed-account setting hid subscriptions or lost its expanded folder.");
                await CloseWindowAsync(legacy);
            }

            addedAccount = AccountManager.Shared.CreateRemoteAccount(AccountType.Feedbin, "A static account");
            await WaitFor(() => Task.FromResult(Visible(addedAccount)), "New account in sidebar");
            Require(!Row(addedAccount).IsExpandable && list.Items.OfType<Models.SidebarItem>().Select(row => row.Tag).OfType<Account.Account>()
                .SequenceEqual(AccountManager.Shared.SortedActiveAccounts),
                "Accounts did not retain the default-local-first, display-name ordering.");
            addedAccount.Name = "ZZZ static account";
            await WaitFor(() => Task.FromResult(list.Items.OfType<Models.SidebarItem>().Last(row => row.Tag is WinNewsWire.Account.Account).Tag == addedAccount),
                "Renamed account ordering");
            addedAccount.IsActive = false;
            await WaitFor(() => Task.FromResult(!Visible(addedAccount)), "Inactive account removal");
            addedAccount.IsActive = true;
            await WaitFor(() => Task.FromResult(Visible(addedAccount)), "Reactivated account");
            Require(!Row(addedAccount).IsExpandable && !Row(account).IsExpandable && !Row(readFolder).IsExpanded,
                "Reactivation restored account disclosure or changed another account's folder state.");
            return new { AccessibleToggle = true, SelectedExceptions = true, SmartFeedsAlwaysVisible = true,
                IndependentWindows = true, StaticAccountsAndFolderRestoration = true, LegacyStateMigration = true, AccountReactivation = true };
        }
        finally
        {
            foreach (var other in App.MainWindows.Where(other => !ReferenceEquals(other, window)).ToArray()) await CloseWindowAsync(other);
            if (addedAccount is not null) await AccountManager.Shared.RemoveAccountAsync(addedAccount);
            foreach (var feed in new[] { read, unread, nestedUnread, sharedRead }) account.RemoveFeed(feed);
            foreach (var removed in new[] { folder, readFolder, empty }) account.RemoveFolder(removed);
            model.UndoManager.RemoveAll();
            await account.RecalculateUnreadCountsAsync();
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            window.Activate();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint key, uint mapping);

    private static void RequestSystemClose(MainWindow window)
    {
        if (!PostMessageW(WindowNative.GetWindowHandle(window), 0x0112, new IntPtr(0xF060), IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void PostWindowKey(MainWindow window, global::Windows.System.VirtualKey key)
    {
        var handle = WindowNative.GetWindowHandle(window);
        uint scan = MapVirtualKeyW((uint)key, 4);
        uint parameter = 1 | ((scan & 0xff) << 16);
        if ((scan & 0xff00) == 0xe000) parameter |= 1u << 24;
        if (!PostMessageW(handle, 0x0100, new IntPtr((int)key), new IntPtr(parameter))
            || !PostMessageW(handle, 0x0101, new IntPtr((int)key), new IntPtr(parameter | 0xc0000000u)))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private static async Task InvokeFullScreenMenuAsync(MainWindow window)
    {
        var bar = Descendants(window.Content).OfType<MenuBar>().Single(control => control.Name == "AppMenuBar");
        var view = bar.Items.Single(item => item.Title == "View");
        var item = view.Items.OfType<ToggleMenuFlyoutItem>().Single(control => control.Name == "MenuFullScreen");
        var peer = FrameworkElementAutomationPeer.FromElement(item) ?? FrameworkElementAutomationPeer.CreatePeerForElement(item);
        var toggle = peer?.GetPattern(PatternInterface.Toggle) as IToggleProvider
            ?? throw new InvalidOperationException("The Full Screen command cannot be toggled through accessibility.");
        bool previous = window.IsFullScreen;
        toggle.Toggle();
        await WaitFor(() => Task.FromResult(window.IsFullScreen != previous), "Full-screen command");
    }

    private static async Task CloseWindowAsync(MainWindow window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        Require(await App.CloseMainWindowAsync(window), "A window rejected its close request.");
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<object?> CheckWindowRestorationAsync(MainWindow window, string serverUrl)
    {
        var originalState = window.CaptureState();
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var account = AccountManager.Shared.DefaultAccount!;
        var originalFeed = account.FlattenedFeeds().Single();
        var folder = account.AddFolder("Restored folder");
        var collapsed = account.AddFolder("Collapsed folder");
        var feed = account.AddFeed(serverUrl + "/restored-feed", "Restored feed", folder);
        var otherFeed = account.AddFeed(serverUrl + "/collapsed-feed", "Collapsed feed", collapsed);
        var body = "<div id=\"restoration-body\">" + string.Concat(Enumerable.Repeat("<p>Window restoration fixture with enough content to restore a reading position well below the first screen.</p>", 80)) + "</div>";
        Article Make(string id, string title, bool read) => new(account.AccountID, id, feed.FeedID, id, title, body,
            null, null, serverUrl + "/article", null, null, null, DateTime.UtcNow, null, null, null,
            new ArticleStatus(id, read, false, DateTime.UtcNow));
        await account.Database.UpdateArticlesAsync([Make("restoration-read", "A restoration article", true), Make("restoration-unread", "Z restoration article", false)]);
        var trace = new List<string>();
        void ObserveSelection(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(model.SelectedArticle) or nameof(model.SelectedArticles) or nameof(model.ArticleItems) or nameof(model.SelectedSidebarItems))
                trace.Add($"{args.PropertyName}: article={model.SelectedArticle?.Id}; restoring={model.IsRestoringArticleSelection}; scopes={string.Join(",", model.SelectedSidebarItems.Select(item => item.Title))}\n"
                    + (args.PropertyName == nameof(model.SelectedArticle) && model.SelectedArticle is null ? Environment.StackTrace : ""));
        }
        model.PropertyChanged += ObserveSelection;
        try
        {
            model.BuildSidebar();
            model.IsUnifiedLayout = false;
            model.IsReaderMode = false;
            var list = Descendants(content).OfType<ListView>().Single(control => control.Name == "SidebarListView");
            Models.SidebarItem FolderRow(Folder value) => list.Items.OfType<Models.SidebarItem>().Single(item => ReferenceEquals(item.Tag, value));
            model.SelectedSidebarItem = FolderRow(folder);
            await model.ToggleReadFilterCommand.ExecuteAsync(null);
            model.SetSelectedSidebarItems([FolderRow(folder), model.FindSidebarItemForFeed(account.AccountID, originalFeed.FeedID)!]);
            model.SetSortParameters(new(ArticleSortKey.Title, false));
            await model.FilterArticlesAsync();
            var ids = model.ArticleItems.Select(item => item.Id).ToList();
            Require(ids.IndexOf("restoration-read") < ids.IndexOf("restoration-unread"), "The first window did not apply its title sort.");
            var row = FolderRow(collapsed);
            list.ScrollIntoView(row);
            await WaitFor(() => Task.FromResult(list.ContainerFromItem(row) is not null), "Collapsed folder row");
            var collapseButton = Descendants(list.ContainerFromItem(row)).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Collapse Collapsed folder");
            collapseButton.Click += (_, _) => trace.Add("Collapsed folder click: " + (collapseButton.DataContext as Models.SidebarItem)?.Title);
            row.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(row.IsExpanded)) trace.Add("Captured folder expanded: " + row.IsExpanded); };
            InvokeButton(collapseButton);
            await WaitFor(() => Task.FromResult(!FolderRow(collapsed).IsExpanded), "Saved folder collapse");
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "restoration-unread");
            await WaitFor(async () => (await account.Database.FetchArticlesByIDsAsync(["restoration-unread"])).Single().Status.Read, "Initial article selection");
            await account.MarkAsync(["restoration-unread"], ArticleStatus.Key.Read, false);
            await model.FilterArticlesAsync();
            await WaitFor(async () => await ScriptBool(content, "!!document.getElementById('restoration-body') && window.__wnwArticleProcessed === true"), "Restoration article document");
            Require(model.SelectedArticle?.Id == "restoration-unread", "A timeline refresh discarded the selected article.");
            ((ColumnDefinition)content.FindName("SidebarColumn")).Width = new GridLength(320);
            ((ColumnDefinition)content.FindName("ArticleListColumn")).Width = new GridLength(420);
            ((Grid)content.FindName("MainContentGrid")).UpdateLayout();
            await WaitFor(async () =>
            {
                if (!content.ProbeReaderInteractionReady) return false;
                var anchor = await content.ProbeResizeAnchorStateAsync();
                return !anchor.GetProperty("changingSize").GetBoolean()
                    && Math.Abs(anchor.GetProperty("viewportWidth").GetDouble() - content.ProbeWebView.ActualWidth) < 1
                    && await ScriptBool(content, "Math.abs(innerHeight - "
                        + content.ProbeWebView.ActualHeight.ToString(System.Globalization.CultureInfo.InvariantCulture) + ") < 1");
            }, "Restored pane geometry settles before scrolling");
            Require(!content.ProbeWebView.CoreWebView2.Settings.IsScriptEnabled, "This regression requires article JavaScript to be disabled.");
            await content.ProbeWebView.ExecuteScriptAsync("window.scrollTo(0, 700);");
            AppDefaults.Shared.ArticleTextSizeRaw = 3;
            try
            {
                await WaitFor(async () => await ScriptBool(content, "!!document.getElementById('restoration-body') && getComputedStyle(document.getElementById('bodyContainer')).fontSize === '20px' && Math.abs(window.scrollY - 700) <= 2"),
                    "Reading position after text-size change");
            }
            catch (TimeoutException error)
            {
                var document = await content.ProbeWebView.ExecuteScriptAsync(
                    "({scroll:scrollY,width:innerWidth,height:innerHeight,font:getComputedStyle(document.getElementById('bodyContainer')).fontSize,fixture:!!document.getElementById('restoration-body')})");
                throw new InvalidOperationException("Text-size restoration: " + document + "; state: "
                    + JsonSerializer.Serialize(window.CaptureState()) + "; resize anchor: " + await content.ProbeResizeAnchorStateAsync(), error);
            }
            AppDefaults.Shared.ArticleTextSizeRaw = 4;
            await WaitFor(async () => await ScriptBool(content, "!!document.getElementById('restoration-body') && getComputedStyle(document.getElementById('bodyContainer')).fontSize === '22px' && Math.abs(window.scrollY - 700) <= 2"),
                "Reading position after text-size restoration");
            try { await WaitFor(async () => (await window.CaptureStateAsync()).Detail.ScrollY >= 699, "Saved article scroll position"); }
            catch (TimeoutException error)
            {
                var document = await content.ProbeWebView.ExecuteScriptAsync(
                    "({scroll:window.scrollY,height:document.documentElement.scrollHeight,viewport:window.innerHeight,processed:window.__wnwArticleProcessed,fixture:!!document.getElementById('restoration-body')})");
                throw new InvalidOperationException($"Saved scroll={window.CaptureState().Detail.ScrollY}; article={model.SelectedArticle?.Id}; document={document}", error);
            }

            var second = App.OpenMainWindow();
            await WaitFor(() => Task.FromResult(second.ProbeContent.IsLoaded && second.ProbeContent.ProbeWebViewReady), "Saved second window");
            var other = second.ProbeContent.ViewModel;
            other.SetSortParameters(new(ArticleSortKey.Unread));
            other.SearchQuery = "not persisted";
            other.IsSidebarVisible = true;
            other.IsSidebarVisible = false;
            other.IsUnifiedLayout = true;
            ((Microsoft.UI.Windowing.OverlappedPresenter)second.AppWindow.Presenter).Maximize();
            await other.FilterArticlesAsync();
            Require(model.SortParameters == new ArticleSortParameters(ArticleSortKey.Title, false), "Another window changed the original sort parameters.");
            Require(await App.RefreshWindowScrollPositionsAsync([window, second]), "The open windows could not capture their reading positions.");
            Require(App.TrySaveMainWindows([window, second]), "The window session could not be saved.");
            var saved = ReadSavedWindows();
            File.WriteAllText(Path.Combine(OutputDirectory, "saved-windows.json"), JsonSerializer.Serialize(saved));
            Require(saved.Length == 2, "The saved session omitted an open window.");
            var firstState = saved.Single(state => state.Timeline.SelectedArticle?.ArticleID == "restoration-unread");
            Require(firstState.Sidebar.SelectedItems.Length == 2 && firstState.SidebarWidth == 320 && firstState.TimelineWidth == 420,
                "Selection or pane dimensions were not persisted.");
            await CloseWindowAsync(second);
            Require(ReadSavedWindows().Length == 1, "Closing a non-last window left it in the next session.");
            AppDefaults.Shared.SetValue(App.MainWindowStatesKey, saved);
            var reopened = App.OpenSavedMainWindows();
            foreach (var restored in reopened)
            {
                Require(await restored.Restoration.WaitAsync(TimeSpan.FromSeconds(30)), "Window restoration failed: " + restored.ProbeContent.ViewModel.StatusMessage);
                await WaitFor(() => Task.FromResult(restored.ProbeContent.ProbeWebViewReady), "Restored WebView");
            }
            var restoredFirst = reopened.Single(item => item.ProbeContent.ViewModel.SortParameters.Key == ArticleSortKey.Title);
            var restoredSecond = reopened.Single(item => item.ProbeContent.ViewModel.SortParameters.Key == ArticleSortKey.Unread);
            var restoredModel = restoredFirst.ProbeContent.ViewModel;
            Require(restoredModel.SelectedArticle?.Id == "restoration-unread" && !restoredModel.SelectedArticle.IsRead,
                "Restoration lost the selected article or marked it read.");
            Require(!(await account.Database.FetchArticlesByIDsAsync(["restoration-unread"])).Single().Status.Read, "Restoring a selection changed its stored read status.");
            Require(firstState.Sidebar.SelectedItems.ToHashSet().SetEquals(restoredModel.SelectedSidebarItems.Select(item => item.Identity!.Value)),
                "Restoration lost the independent sidebar selections.");
            Require((restoredFirst.CaptureState().Sidebar.ExpandedContainers ?? []).ToHashSet().SetEquals(firstState.Sidebar.ExpandedContainers ?? []),
                "Restoration changed the expanded containers.");
            Require(restoredFirst.CaptureState().Timeline.ReadFilters.SequenceEqual(firstState.Timeline.ReadFilters), "Per-scope read filters were not restored.");
            Require(restoredFirst.CaptureState().SidebarWidth == 320 && restoredFirst.CaptureState().TimelineWidth == 420, "Restored pane widths were overwritten by defaults.");
            await WaitFor(async () => await ScriptBool(restoredFirst.ProbeContent, "!!document.getElementById('restoration-body') && Math.abs(window.scrollY - 700) <= 2"), "Restored reading position");
            Require(restoredSecond.ProbeContent.ViewModel.SearchQuery == "" && restoredSecond.ProbeContent.ViewModel.IsUnifiedLayout
                && !restoredSecond.ProbeContent.ViewModel.IsSidebarVisible,
                "Search or layout restoration differs from the saved window contract: " + JsonSerializer.Serialize(restoredSecond.CaptureState()));
            Require(((FrameworkElement)restoredSecond.ProbeContent.FindName("TimelinePanel")).Visibility == Visibility.Collapsed,
                "The restored reading-only layout did not hide its timeline.");
            var placement = restoredSecond.CaptureState();
            var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)restoredSecond.AppWindow.Presenter;
            Require(placement.IsMaximized && presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized, "The maximized presentation was not restored.");
            presenter.Minimize();
            Require(restoredSecond.CaptureState().Bounds == placement.Bounds && restoredSecond.CaptureState().IsMaximized, "Minimizing overwrote the normal placement.");
            presenter.Restore();

            var beforeFailure = ReadSavedWindows();
            using (var locked = new FileStream(Path.Combine(AppConfig.DataDirectory, "AppDefaults.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                int count = App.MainWindows.Count;
                Require(!await App.CloseMainWindowAsync(restoredSecond), "A locked preferences file did not reject the close request.");
                Require(App.MainWindows.Count == count && App.MainWindows.Contains(restoredSecond), "A failed state write did not cancel window close.");
                Require(restoredFirst.ProbeContent.ViewModel.StatusMessage.StartsWith("Save windows failed:", StringComparison.Ordinal),
                    "The failed window-state write was not reported.");
                Require(restoredSecond.ProbeContent.ViewModel.StatusMessage.StartsWith("Save windows failed:", StringComparison.Ordinal),
                    "The window whose close was canceled did not display the save error.");
            }
            Require(JsonSerializer.Serialize(beforeFailure) == JsonSerializer.Serialize(ReadSavedWindows()), "A failed save replaced the previous session.");

            var early = App.OpenMainWindow(firstState, activate: false);
            Require(early.CaptureState().Timeline.SelectedArticle == firstState.Timeline.SelectedArticle, "An initializing window discarded its original saved selection.");
            await CloseWindowAsync(early);
            Require(!await early.Restoration.WaitAsync(TimeSpan.FromSeconds(10)), "Closing an initializing window did not cancel restoration.");

            var staleState = firstState with
            {
                Bounds = new(1_000_000, 1_000_000, 2400, 1600),
                Sidebar = new() { SelectedItems = [new(Models.SidebarItemType.Feed, account.AccountID, "missing-feed", null)] },
                Timeline = firstState.Timeline with { SelectedArticle = new(account.AccountID, "missing-article") }
            };
            var stale = App.OpenMainWindow(staleState);
            Require(await stale.Restoration.WaitAsync(TimeSpan.FromSeconds(30)), "Stale window references did not restore gracefully.");
            Require(stale.ProbeContent.ViewModel.SelectedArticle is null && stale.ProbeContent.ViewModel.SelectedSidebarItems.Count == 0,
                "A removed feed or article was restored as a live selection.");
            var clamped = MainWindow.ClampToDisplay(staleState.Bounds);
            Require(stale.AppWindow.Position.X == clamped.X && stale.AppWindow.Position.Y == clamped.Y
                && stale.AppWindow.Size.Width == clamped.Width && stale.AppWindow.Size.Height == clamped.Height,
                "An offscreen saved window was not clamped to the current display.");
            await CloseWindowAsync(stale);

            var reader = App.OpenMainWindow(firstState with
            {
                Sidebar = new(),
                Timeline = firstState.Timeline with { SelectedArticle = new(account.AccountID, "probe-article") },
                Detail = new(true, 700)
            });
            Require(await reader.Restoration.WaitAsync(TimeSpan.FromSeconds(30)), "The saved reader window did not restore.");
            try
            {
                await WaitFor(async () => reader.ProbeContent.ProbeWebViewReady && await ScriptBool(reader.ProbeContent,
                    "!!document.getElementById('bodyContainer') && document.getElementById('bodyContainer').innerText.includes('Reader fixture paragraph') && Math.abs(window.scrollY - 700) <= 2"),
                    "Restored extracted article and scroll");
            }
            catch (TimeoutException error)
            {
                var document = await reader.ProbeContent.ProbeWebView.ExecuteScriptAsync(
                    "({scroll:window.scrollY,width:innerWidth,height:document.documentElement.scrollHeight,viewport:window.innerHeight,reader:document.body.innerText.includes('Reader fixture paragraph')})");
                var anchor = await reader.ProbeContent.ProbeResizeAnchorStateAsync();
                throw new InvalidOperationException("Restored reader document: " + document + "; state: "
                    + JsonSerializer.Serialize(reader.CaptureState()) + "; resize anchor: " + anchor, error);
            }
            Require(reader.ProbeContent.ViewModel.SelectedArticle?.Id == "probe-article" && reader.ProbeContent.ViewModel.SelectedArticle.IsRead,
                "The saved read article was not retained as an exception to the unread scope.");
            return new { IndependentSorting = true, SessionRoundTrip = true, SidebarAndReadFilters = true,
                UnreadSelectionPreserved = true, ReaderAndScroll = true, Placement = true, FailedSaveAndEarlyClose = true, StaleReferences = true };
        }
        catch
        {
            File.WriteAllText(Path.Combine(OutputDirectory, "window-restoration-trace.json"), JsonSerializer.Serialize(trace));
            throw;
        }
        finally
        {
            model.PropertyChanged -= ObserveSelection;
            foreach (var other in App.MainWindows.Where(other => !ReferenceEquals(other, window)).ToArray()) await CloseWindowAsync(other);
            await content.RestoreStateAsync(originalState);
            account.RemoveFeed(feed);
            account.RemoveFeed(otherFeed);
            account.RemoveFolder(folder);
            account.RemoveFolder(collapsed);
            window.Activate();
        }
    }

    private static async Task<bool> ScriptBool(MainContent content, string expression)
        => JsonSerializer.Deserialize<bool?>(await content.ProbeWebView.ExecuteScriptAsync("Boolean(" + expression + ")")) == true;

    private static async Task<object?> CheckMinimumWindowWidthAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        bool oldColumn = AppDefaults.Shared.UseColumnLayout;
        var browser = content.ProbeWebView.CoreWebView2;
        var measurements = new List<object>();
        var menuBar = Descendants(window.Content).OfType<MenuBar>().Single(view => view.Name == "AppMenuBar");
        var menuOverflow = menuBar.Items.Single(item => item.Name == "MenuOverflowItem");
        MainWindow? restored = null;
        try
        {
            foreach (var sample in new[] { (Scale: 1d, Width: 302), (Scale: 1.25, Width: 377),
                (Scale: 1.5, Width: 454), (Scale: 2d, Width: 604) })
                Require(content.MinimumReaderWidthInPixels(sample.Scale) == sample.Width, "Reader edges were not rounded individually for display scaling.");
            foreach (var layout in new[] { (Name: "standard", Column: false, ReadingOnly: false),
                (Name: "column", Column: true, ReadingOnly: false), (Name: "reading", Column: false, ReadingOnly: true) })
            {
                AppDefaults.Shared.UseColumnLayout = layout.Column;
                model.IsUnifiedLayout = layout.ReadingOnly;
                int frame = window.AppWindow.Size.Width - window.AppWindow.ClientSize.Width;
                int minimum = content.MinimumReaderWidthInPixels(content.XamlRoot.RasterizationScale) + frame;
                Require(window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter
                    && presenter.PreferredMinimumWidth == minimum, "The native minimum does not match the scaled reader width.");
                var limits = new WindowSizingLimits();
                SendSizingMessage(WindowNative.GetWindowHandle(window), 0x0024, IntPtr.Zero, ref limits);
                Require(limits.MinimumTrackSize.X == minimum, "Windows is not enforcing the requested minimum resize width.");
                window.AppWindow.Resize(new(minimum, original.Bounds.Height));
                await WaitFor(() => Task.FromResult(content.ProbeWebView.ActualWidth >= MainContent.ArticlePaneMinimumWidth - 0.01
                    && content.ProbeWebView.ActualWidth < MainContent.ArticlePaneMinimumWidth + 1
                    && ((Grid)content.FindName("SidebarPanel")).Visibility == Visibility.Collapsed
                    && ((Grid)content.FindName("TimelinePanel")).Visibility == (layout.Column ? Visibility.Visible : Visibility.Collapsed)),
                    "Minimum article width in " + layout.Name);
                var panel = (Grid)content.FindName("ArticleDetailPanel");
                var bounds = panel.TransformToVisual(content).TransformBounds(new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
                await SaveWindowScreenshotAsync(window, "minimum-width-" + layout.Name);
                Require(bounds.Left >= -1 && bounds.Right <= content.ActualWidth + 1,
                    $"The {layout.Name} reader clips content: left={bounds.Left}, right={bounds.Right}, width={content.ActualWidth}.");
                Require(ReferenceEquals(browser, content.ProbeWebView.CoreWebView2), "Resizing recreated the article browser.");
                await WaitFor(() => Task.FromResult(menuOverflow.Visibility == Visibility.Visible
                    && menuOverflow.Items.OfType<MenuFlyoutSubItem>().Any(item => item.Text == "Help")), "Compact menu overflow");
                MenuExpansion(menuOverflow).Expand();
                var help = menuOverflow.Items.OfType<MenuFlyoutSubItem>().Single(item => item.Text == "Help");
                await WaitFor(() => Task.FromResult(help.IsLoaded), "Help in menu overflow");
                MenuExpansion(help).Expand();
                await WaitFor(() => Task.FromResult(help.Items.All(item => item.IsLoaded)), "Original Help commands");
                await SaveWindowScreenshotAsync(window, "minimum-width-" + layout.Name + "-menus");
                await CloseOverflowMenuAsync(window, menuOverflow);
                measurements.Add(new { layout.Name, MinimumPixels = minimum, NativeMinimum = limits.MinimumTrackSize.X,
                    ArticleWidth = content.ProbeWebView.ActualWidth, content.XamlRoot.RasterizationScale });
            }
            var state = window.CaptureState();
            restored = App.OpenMainWindow(state with { Bounds = state.Bounds with { Width = state.Bounds.Width - 20 } });
            Require(await restored.Restoration.WaitAsync(TimeSpan.FromSeconds(30)), "The compact saved window did not restore.");
            await WaitFor(() => Task.FromResult(restored.AppWindow.Size.Width == window.AppWindow.Size.Width
                && restored.ProbeContent.ProbeWebView.ActualWidth >= MainContent.ArticlePaneMinimumWidth - 0.01
                && restored.ProbeContent.ProbeWebView.ActualWidth < MainContent.ArticlePaneMinimumWidth + 1),
                "Compact window restored at the reader minimum");
            File.WriteAllText(Path.Combine(OutputDirectory, "minimum-window-width.json"),
                JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
            return new { NativeResizeLimits = true, Layouts = measurements, DpiRoundingCases = 4,
                CompactRestoration = true, BrowserPreserved = true, AllMenusReachable = true };
        }
        finally
        {
            if (restored is not null) await CloseWindowAsync(restored);
            await CloseOverflowMenuAsync(window, menuOverflow);
            AppDefaults.Shared.UseColumnLayout = oldColumn;
            window.AppWindow.MoveAndResize(new(original.Bounds.X, original.Bounds.Y, original.Bounds.Width, original.Bounds.Height));
            model.RestoreWindowLayout(original);
            window.Activate();
        }
    }

    private static async Task<object?> CheckColumnLayoutAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        bool oldLayout = AppDefaults.Shared.UseColumnLayout;
        bool oldReadingDefault = AppDefaults.Shared.UnifiedLayout;
        var account = AccountManager.Shared.DefaultAccount!;
        var originalFeed = account.FlattenedFeeds().Single();
        var feed = account.AddFeed(serverUrl + "/column-feed", "Column feed");
        Article Make(string id, string? title, bool read, bool starred) => new(account.AccountID, id, feed.FeedID, id, title,
            "<p>Column fallback text for an untitled article.</p>", null, null, serverUrl + "/" + id, null, null, null,
            DateTime.UtcNow, null, null, null, new ArticleStatus(id, read, starred, DateTime.UtcNow));
        await account.Database.UpdateArticlesAsync([Make("column-untitled", null, false, true), Make("column-titled", "A column article", true, false)]);
        await account.RecalculateUnreadCountsAsync();
        try
        {
            AppDefaults.Shared.UnifiedLayout = false;
            model.IsUnifiedLayout = false;
            model.IsReaderMode = false;
            model.BuildSidebar();
            model.SetSelectedSidebarItems([
                model.FindSidebarItemForFeed(account.AccountID, originalFeed.FeedID)!,
                model.FindSidebarItemForFeed(account.AccountID, feed.FeedID)!
            ]);
            await model.FilterArticlesAsync();
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "probe-article");
            await WaitFor(async () => content.ProbeArticleReady && await ScriptBool(content, "!!document.getElementById('probe-body')"),
                "Column-layout article prerequisite");
            await content.ProbeWebView.ExecuteScriptAsync("window.columnLayoutSentinel = 'preserved';");
            var webView = content.ProbeWebView.CoreWebView2;
            var frame = window.CaptureState().Bounds;
            AppDefaults.Shared.UseColumnLayout = true;
            var panel = (Grid)content.FindName("ArticleDetailPanel");
            var header = (Grid)content.FindName("TimelineColumnsHeader");
            var timeline = (Grid)content.FindName("TimelinePanel");
            await WaitFor(() => Task.FromResult(Grid.GetRow(panel) == 2 && header.Visibility == Visibility.Visible), "Column layout");
            Require(ReferenceEquals(webView, content.ProbeWebView.CoreWebView2) && window.CaptureState().Bounds == frame,
                "Changing layout recreated the browser or changed the window frame.");
            Require(await ScriptString(content, "window.columnLayoutSentinel") == "preserved" && model.SelectedArticle?.Id == "probe-article",
                "Changing layout lost the selected article or document.");
            var timelineBounds = timeline.TransformToVisual(content).TransformBounds(new Rect(0, 0, timeline.ActualWidth, timeline.ActualHeight));
            var detailBounds = panel.TransformToVisual(content).TransformBounds(new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
            Require(timelineBounds.Bottom <= detailBounds.Top && timeline.ActualHeight >= 120 && panel.ActualHeight >= 200,
                "The column layout did not place a noncollapsed timeline above the article.");
            Require(header.ColumnDefinitions.Count == 5 && header.ColumnDefinitions[0].Width.Value == 22
                && header.ColumnDefinitions[1].Width.Value == 22, "The native status columns do not have fixed widths.");
            var list = (ListView)content.FindName("ArticleListView");
            var untitled = model.ArticleItems.Single(item => item.Id == "column-untitled");
            list.ScrollIntoView(untitled);
            Grid? row = null;
            await WaitFor(() =>
            {
                row = Descendants(list.ContainerFromItem(untitled)).OfType<Grid>().FirstOrDefault(grid => grid.Name == "TimelineColumnRow");
                return Task.FromResult(row is not null);
            }, "Column row realization");
            Require(row!.Children.OfType<Microsoft.UI.Xaml.Shapes.Ellipse>().Single().Visibility == Visibility.Visible
                && row.Children.OfType<FontIcon>().Single().Visibility == Visibility.Visible, "Unread and starred indicators did not remain independent.");
            foreach (var item in model.ArticleItems.Where(item => !model.SelectedArticles.Contains(item)))
                if (list.ContainerFromItem(item) is ListViewItem container)
                    Require(container.Background is null or SolidColorBrush { Color.A: 0 }, "Column rows still have alternating background bands.");
            var titleCell = row.Children.OfType<TextBlock>().Single(block => block.Tag as string == "Title");
            Require(titleCell.Text.StartsWith("Column fallback", StringComparison.Ordinal) && titleCell.FontWeight == FontWeights.Bold,
                "An unread untitled row lost its bold summary fallback.");
            Require(row.Children.OfType<TextBlock>().Single(block => block.Tag as string == "Date").Text
                == ArticleStringFormatter.Shared.DateString(untitled.PublishedDate.UtcDateTime), "The date cell did not use the native formatter.");
            var titleHeader = header.Children.OfType<Button>().Single(button => button.Tag as string == "Title");
            InvokeButton(titleHeader);
            await WaitFor(() => Task.FromResult(model.SortParameters == new ArticleSortParameters(ArticleSortKey.Title, false)), "Ascending title header sort");
            InvokeButton(titleHeader);
            await WaitFor(() => Task.FromResult(model.SortParameters == new ArticleSortParameters(ArticleSortKey.Title, true)), "Descending title header sort");
            Require(model.SelectedArticle?.Id == "probe-article", "Sorting column layout lost the selected article.");

            var resize = header.Children.OfType<ColumnResizeThumb>().Single(handle => handle.Tag as string == "Feed");
            var peer = FrameworkElementAutomationPeer.FromElement(resize) ?? FrameworkElementAutomationPeer.CreatePeerForElement(resize);
            var range = peer?.GetPattern(PatternInterface.RangeValue) as IRangeValueProvider
                ?? throw new InvalidOperationException("The timeline column has no accessible resize pattern.");
            range.SetValue(225);
            var menu = (MenuFlyout)titleHeader.ContextFlyout;
            menu.ShowAt(titleHeader);
            var move = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "Move Column Left");
            await WaitFor(() => Task.FromResult(move.IsLoaded), "Column ordering menu");
            var movePeer = FrameworkElementAutomationPeer.FromElement(move) ?? FrameworkElementAutomationPeer.CreatePeerForElement(move);
            var invoke = movePeer?.GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("Column ordering is not invokable by accessibility.");
            invoke.Invoke();
            await WaitFor(() => Task.FromResult(Grid.GetColumn(titleHeader) == 2), "Column reorder");
            menu.Hide();
            await WaitFor(() => Task.FromResult(!VisualTreeHelper.GetOpenPopupsForXamlRoot(content.XamlRoot)
                .SelectMany(popup => Descendants(popup.Child)).OfType<MenuFlyoutPresenter>().Any()), "Column menu dismissal");
            Require(AppDefaults.Shared.GetValue<Models.TimelineColumnsState>("TimelineColumns") is { } columns
                && columns.Order[2] == ArticleSortKey.Title && columns.Widths[ArticleSortKey.Feed] == 225,
                "The column widths/order were not saved.");

            var splitter = (Control)content.FindName("ArticleSplitter");
            Require(splitter.Focus(FocusState.Programmatic), "The vertical divider could not receive keyboard focus.");
            double oldHeight = ((RowDefinition)content.FindName("ColumnTimelineRow")).ActualHeight;
            PostWindowKey(window, global::Windows.System.VirtualKey.Down);
            await WaitFor(() => Task.FromResult(Math.Abs(((RowDefinition)content.FindName("ColumnTimelineRow")).ActualHeight - oldHeight - 8) < 1),
                "Keyboard timeline-height resize by eight pixels");
            double savedHeight = window.CaptureState().ColumnLayoutTimelineHeight;
            var second = App.OpenMainWindow();
            await WaitFor(() => Task.FromResult(second.ProbeContent.IsLoaded && second.ProbeContent.ProbeWebViewReady
                && Grid.GetRow((Grid)second.ProbeContent.FindName("ArticleDetailPanel")) == 2), "Global column layout in a new window");
            second.ProbeContent.ViewModel.SetSortParameters(new(ArticleSortKey.Date));
            Require(model.SortParameters.Key == ArticleSortKey.Title, "Column preferences overwrote another window's sort.");
            var secondHeader = (Grid)second.ProbeContent.FindName("TimelineColumnsHeader");
            Require(Grid.GetColumn(secondHeader.Children.OfType<Button>().Single(button => button.Tag as string == "Title")) == 2
                && ((ColumnDefinition)second.ProbeContent.FindName("TimelineFeedColumn")).Width.Value == 225,
                "A new window lost the saved column order or width.");
            AppDefaults.Shared.UseColumnLayout = false;
            await WaitFor(() => Task.FromResult(Grid.GetRow(panel) == 0 && Grid.GetColumn(panel) == 2
                && Grid.GetColumn((Grid)second.ProbeContent.FindName("ArticleDetailPanel")) == 2), "Global standard-layout restoration");
            Require(window.CaptureState().TimelineWidth == original.TimelineWidth, "Column layout overwrote the standard timeline width.");
            AppDefaults.Shared.UseColumnLayout = true;
            await WaitFor(() => Task.FromResult(Grid.GetRow(panel) == 2
                && Math.Abs(((RowDefinition)content.FindName("ColumnTimelineRow")).ActualHeight - savedHeight) < 1), "Remembered column split height");
            var state = window.CaptureState();
            var restored = App.OpenMainWindow(state);
            Require(await restored.Restoration.WaitAsync(TimeSpan.FromSeconds(30)), "Column-layout window restoration failed.");
            await WaitFor(() => Task.FromResult(Math.Abs(restored.CaptureState().ColumnLayoutTimelineHeight - savedHeight) < 1), "Saved column split restoration");
            Require(restored.ProbeContent.ViewModel.SortParameters == model.SortParameters
                && restored.ProbeContent.ViewModel.SelectedArticle?.Id == "probe-article", "Column restoration lost sort or selection.");
            return new { VerticalSplit = true, SharedLayoutPreference = true, IndependentSorts = true,
                NativeCells = true, HeaderSorting = true, ColumnOrderAndWidths = true, KeyboardDivider = true, GeometryAndDocumentPreserved = true };
        }
        finally
        {
            foreach (var other in App.MainWindows.Where(other => !ReferenceEquals(other, window)).ToArray()) await CloseWindowAsync(other);
            AppDefaults.Shared.UseColumnLayout = oldLayout;
            AppDefaults.Shared.UnifiedLayout = oldReadingDefault;
            account.RemoveFeed(feed);
            await account.RecalculateUnreadCountsAsync();
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            window.Activate();
        }
    }

    private static async Task<object?> CheckThemeCommandAndAcrylicAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var original = content.CurrentToolbarConfiguration;
        bool oldColumn = AppDefaults.Shared.UseColumnLayout;
        var articles = (ListView)content.FindName("ArticleListView");
        var oldHeader = articles.Header;
        try
        {
            Require(content.SaveToolbarConfiguration(ToolbarConfiguration.Default) is null, "Could not establish header defaults.");
            var customizing = content.CustomizeToolbarAsync();
            await WaitFor(() => Task.FromResult(content.ProbeToolbarDialog?.IsLoaded == true), "Theme customization dialog");
            var dialog = content.ProbeToolbarDialog!;
            var list = Descendants(dialog).OfType<ListView>().Single();
            var available = Descendants(dialog).OfType<ComboBox>().Single(combo =>
                AutomationProperties.GetName(combo) == AppStrings.Get("ToolbarAvailableItems"));
            string? Identifier(object item) => item.GetType().GetProperty("Identifier")?.GetValue(item) as string;
            Button Action(string key) => Descendants(dialog).OfType<Button>().Single(button => button.Content as string == AppStrings.Get(key));
            Require(list.ItemsPanelRoot is StackPanel, "The bounded customization list still uses native virtualization.");
            for (int attempt = 0; attempt < 4; attempt++)
            {
                available.IsDropDownOpen = true;
                await WaitFor(() => Task.FromResult(available.IsDropDownOpen), "Available command dropdown");
                available.SelectedItem = available.Items.Cast<object>().Single(item => Identifier(item) == "articleThemeMenu");
                available.IsDropDownOpen = false;
                list.SelectedIndex = list.Items.Count - 1;
                InvokeButton(Action("ToolbarShowItem"));
                Require(Identifier(list.SelectedItem) == "articleThemeMenu" && list.ContainerFromItem(list.SelectedItem) is ListViewItem,
                    "The new theme command did not receive a realized row.");
                await WaitFor(() =>
                {
                    var row = (FrameworkElement)list.ContainerFromItem(list.SelectedItem);
                    var bounds = row.TransformToVisual(list).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
                    return Task.FromResult(bounds.Top < list.ActualHeight && bounds.Bottom > 0);
                }, "New command scrolled into the visible list");
                Require(!content.CurrentToolbarConfiguration.Items.Contains("articleThemeMenu"), "An unsaved draft changed the live header.");
                InvokeButton(Action("ToolbarMoveEarlier"));
                InvokeButton(Action("ToolbarMoveLater"));
                if (attempt < 3) InvokeButton(Action("ToolbarHideItem"));
            }
            InvokeDialogButton(dialog, "PrimaryButton");
            await customizing;
            Require(content.CurrentToolbarConfiguration.Items.Contains("articleThemeMenu"), "The theme command was not saved.");
            var themeButton = (DropDownButton)content.FindName("ArticleThemeButton");
            var themes = (MenuFlyout)themeButton.Flyout;
            themes.ShowAt(themeButton);
            await WaitFor(() => Task.FromResult(themes.Items.OfType<ToggleMenuFlyoutItem>().Any(item => item.IsLoaded && item.IsChecked)),
                "New theme command menu");
            themes.Hide();

            var glass = (AcrylicBrush)content.Resources["TimelineHeaderAcrylic"];
            var header = (Grid)content.FindName("TimelineToolbar");
            var columns = (Grid)content.FindName("TimelineColumnsHeader");
            var scrollHost = (ScrollViewer)content.FindName("TimelineHorizontalScroll");
            var colours = new Grid { Height = Math.Max(1200, articles.ActualHeight * 3) };
            colours.ColumnDefinitions.Add(new());
            colours.ColumnDefinitions.Add(new());
            colours.Children.Add(new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Red) });
            var blue = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Blue) };
            Grid.SetColumn(blue, 1);
            colours.Children.Add(blue);
            articles.Header = colours;
            int glassModes = 0;
            foreach (bool column in new[] { false, true })
            {
                AppDefaults.Shared.UseColumnLayout = column;
                await WaitFor(() => Task.FromResult(columns.Visibility == (column ? Visibility.Visible : Visibility.Collapsed)), "Acrylic timeline layout");
                articles.UpdateLayout();
                var scrolling = Descendants(articles).OfType<ScrollViewer>().First();
                scrolling.ChangeView(null, articles.Padding.Top + 60, null, true);
                await WaitFor(() => Task.FromResult(scrolling.VerticalOffset >= articles.Padding.Top + 50), "Content behind acrylic");
                Require(header.Background is AcrylicBrush && !glass.AlwaysUseFallback
                    && Grid.GetRow(scrollHost) == 0 && Grid.GetRowSpan(scrollHost) == 2
                    && articles.Padding.Top >= header.ActualHeight + (column ? columns.ActualHeight : 0) - 1,
                    "The acrylic header has no scrolling content behind it or obscures the first row.");
                await SaveWindowScreenshotAsync(window, column ? "acrylic-column" : "acrylic-regular");
                var capture = CaptureWindow(window);
                (byte R, byte B) Pixel(double fraction)
                {
                    var position = header.TransformToVisual((UIElement)window.Content)
                        .TransformPoint(new(header.ActualWidth * fraction, header.ActualHeight - 4));
                    int x = (int)Math.Round(position.X * header.XamlRoot.RasterizationScale) + capture.ClientOrigin.X - capture.Bounds.Left;
                    int y = (int)Math.Round(position.Y * header.XamlRoot.RasterizationScale) + capture.ClientOrigin.Y - capture.Bounds.Top;
                    int offset = checked((y * capture.Width + x) * 4);
                    return (capture.Data[offset + 2], capture.Data[offset]);
                }
                var left = Pixel(0.3);
                var right = Pixel(0.7);
                bool tinted = left.R > left.B + 10 && right.B > right.R + 10;
                bool effects = new global::Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled
                    && !new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
                Require(!effects || tinted, "The active acrylic header still paints a flat background instead of the content behind it.");
                if (tinted) glassModes++;
            }
            return new { ThemeInsertMoveRemoveCycles = 4, NonVirtualizedCommandList = true,
                ThemeCommandSavedAndOpened = true, AcrylicOverlayModes = 2, VisiblyTintedModes = glassModes };
        }
        finally
        {
            content.ProbeToolbarDialog?.Hide();
            articles.Header = oldHeader;
            AppDefaults.Shared.UseColumnLayout = oldColumn;
            content.SaveToolbarConfiguration(original);
            if (Descendants(articles).OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
                scroll.ChangeView(null, 0, null, true);
        }
    }

    private static async Task<object?> CheckPaneHeadersAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var oldToolbar = content.CurrentToolbarConfiguration;
        bool oldColumn = AppDefaults.Shared.UseColumnLayout;
        bool hadColumns = AppDefaults.Shared.ContainsKey("TimelineColumns");
        var oldColumns = AppDefaults.Shared.GetValue<JsonElement>("TimelineColumns");
        var search = content.ProbeToolbarSearch!;
        double oldFont = search.FontSize;
        var account = AccountManager.Shared.DefaultAccount!;
        var article = model.SelectedArticle ?? throw new InvalidOperationException("The header check needs an article.");
        bool oldRead = article.IsRead;
        T Element<T>(string name) where T : DependencyObject => (T)content.FindName(name);
        Rect Bounds(FrameworkElement element) => element.TransformToVisual((UIElement)window.Content)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        double TextRight(TextBlock text) => text.TransformToVisual((UIElement)window.Content)
            .TransformPoint(new(text.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Right, 0)).X;
        void Resize(double width)
        {
            double scale = content.XamlRoot.RasterizationScale;
            window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(original.Bounds with { Width = (int)Math.Ceiling(width * scale) }));
        }
        try
        {
            window.Activate();
            Resize(1280);
            model.IsUnifiedLayout = false;
            model.IsSidebarVisible = true;
            AppDefaults.Shared.UseColumnLayout = false;
            Require(content.SaveToolbarConfiguration(ToolbarConfiguration.Default) is null, "Could not restore default header commands.");
            await account.MarkAsync([article.Id], ArticleStatus.Key.Read, false);
            await account.RecalculateUnreadCountsAsync();
            await model.ProbeDrainViewUpdatesAsync();
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Settled reader before header inspection");
            var titleBar = Descendants(window.Content).OfType<Grid>().Single(grid => grid.Name == "AppTitleBar");
            var title = Descendants(window.Content).OfType<StackPanel>().Single(panel => panel.Name == "TitlePanel");
            var toggle = Descendants(titleBar).OfType<Button>().Single(button => button.Name == "ToggleSidebarButton");
            var back = Descendants(titleBar).OfType<Button>().Single(button => button.Name == "NavigateBackButton");
            var side = Element<Grid>("SidebarToolbarItemsPanel");
            var timeline = Element<Grid>("TimelineToolbarItemsPanel");
            var actions = Element<Grid>("ToolbarItemsPanel");
            await WaitFor(() => Task.FromResult(side.Children.Contains(Element<Button>("ReloadButton"))
                && timeline.Children.Contains(Element<Button>("MarkAllReadButton")) && actions.Children.Contains(Element<Button>("StarButton"))
                && Element<Grid>("SidebarPanel").Visibility == Visibility.Visible), "Restored pane header controls");
            await SaveWindowScreenshotAsync(window, "headers-regular-initial");
            Require(titleBar.Children.OfType<AutoSuggestBox>().Any(box => box.Name == search.Name)
                && Descendants(titleBar).OfType<Button>().Any(button => button.Name == toggle.Name),
                "Title-bar control mismatch: search=" + search.Name + ", toggle=" + toggle.Name
                + ", children=" + string.Join(",", titleBar.Children.OfType<FrameworkElement>().Select(element => element.Name))
                + ", buttons=" + string.Join(",", Descendants(titleBar).OfType<Button>().Select(button => button.Name)));
            Require(Bounds(back).Right <= Bounds(toggle).Left + 1 && Bounds(toggle).Right <= Bounds(title).Left + 1
                && Bounds(title).Right <= Bounds(search).Left + 1 && !Descendants(titleBar).OfType<Image>().Any(),
                "The title-bar controls are not Back, sidebar toggle, title and search, or the old title icon remains.");
            Require(Bounds(search).Right <= titleBar.ActualWidth - window.AppWindow.TitleBar.RightInset / content.XamlRoot.RasterizationScale + 1,
                "The search field overlaps the native caption buttons.");
            await WaitFor(() => Task.FromResult(TitleBarPassesPointerInput(window, search) && TitleBarPassesPointerInput(window, toggle)
                && TitleBarPassesPointerInput(window, back)),
                "Title-bar input passthrough");
            foreach (double scale in new[] { 1, 1.25, 1.5, 2 })
            {
                var rect = new Rect(12.3, 4.4, 218.2, 31.1);
                var pixels = MainWindow.TitleBarInputRect(rect, scale);
                Require(pixels.X <= rect.Left * scale && pixels.Y <= rect.Top * scale
                    && pixels.X + pixels.Width >= rect.Right * scale && pixels.Y + pixels.Height >= rect.Bottom * scale,
                    "DPI rounding clipped a title-bar input region.");
            }
            var caption = Element<StackPanel>("TimelineHeaderCaption");
            var unread = Element<Border>("TimelineUnreadCount");
            Require(unread.Visibility == Visibility.Visible && caption.Children.Contains(unread)
                && unread.Background is null && Bounds(unread).Top >= Bounds(Element<TextBlock>("ArticleListTitleText")).Bottom,
                "Unread count is not a plain subtitle under the feed title.");
            Require(side.Children.Contains(Element<DropDownButton>("AddDropdown"))
                && timeline.Children.Contains(Element<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("ReadArticlesFilterButton")),
                "Feed or timeline header commands are misplaced.");
            Require(Element<TextBlock>("ArticleDetailStatus").TextAlignment == TextAlignment.Left
                && Element<TextBlock>("WebPageStatus").TextAlignment == TextAlignment.Left,
                "Standard-layout footer alignment changed.");
            await SaveWindowScreenshotAsync(window, "headers-regular");

            AppDefaults.Shared.UseColumnLayout = true;
            var combinedHost = Element<ContentControl>("CombinedToolbarHost");
            var toolbar = Element<ToolbarPanel>("MainToolbar");
            var heading = Element<Grid>("TimelineToolbar");
            var columns = Element<Grid>("TimelineColumnsHeader");
            await WaitFor(() => Task.FromResult(ReferenceEquals(combinedHost.Content, toolbar)
                && side.Children.Contains(Element<Button>("ReloadButton")) && actions.Children.Contains(Element<Button>("MarkAllReadButton"))),
                "Combined column header");
            Require(Bounds(columns).Top >= Bounds(heading).Bottom - 1
                && Math.Abs(Bounds(columns).Top - Bounds(heading).Bottom) <= 1, "Column headers have a gap below the pane header.");
            Require(Bounds(Element<Button>("ToolbarOverflowButton")).Right >= Bounds(heading).Right - 14,
                "Column header options are not right-aligned.");
            Require(columns.BorderThickness.Top == 0 && columns.BorderThickness.Left == 0 && columns.BorderThickness.Right == 0
                && columns.Children.OfType<Button>().All(button => button.Tag as string is not ("Unread" or "Starred")),
                "The column header retained boxed borders or status icons.");
            Require(Element<Border>("TimelineFooter").Visibility == Visibility.Collapsed
                && Element<TextBlock>("CombinedTimelineStatus").Visibility == Visibility.Visible
                && Bounds(Element<TextBlock>("CombinedTimelineStatus")).Bottom <= Bounds(Element<Border>("DetailFooter")).Bottom,
                "Column timeline status was not moved to the bottom footer.");
            var detailStatus = Element<TextBlock>("ArticleDetailStatus");
            var webStatus = Element<TextBlock>("WebPageStatus");
            var counts = Element<TextBlock>("CombinedTimelineStatus");
            double FooterTextRight() => Element<DropDownButton>("StatusBarThemeButton").Visibility == Visibility.Visible
                ? Bounds(Element<DropDownButton>("StatusBarThemeButton")).Left - 8
                : Bounds(Element<Border>("DetailFooter")).Right - 12;
            Require(detailStatus.TextAlignment == TextAlignment.Right && webStatus.TextAlignment == TextAlignment.Right
                && counts.TextAlignment == TextAlignment.Left,
                "Column detail text must be right-aligned without moving the timeline counts.");
            await SaveWindowScreenshotAsync(window, "headers-column-footer");
            File.WriteAllText(Path.Combine(OutputDirectory, "column-footer.json"), JsonSerializer.Serialize(new
            {
                detailStatus.Text, detailStatus.ActualWidth, detailStatus.IsTextTrimmed,
                Alignment = detailStatus.TextAlignment.ToString(),
                Start = detailStatus.ContentStart.GetCharacterRect(LogicalDirection.Forward),
                End = detailStatus.ContentEnd.GetCharacterRect(LogicalDirection.Backward),
                RenderedRight = TextRight(detailStatus),
                ExpectedRight = FooterTextRight()
            }, new JsonSerializerOptions { WriteIndented = true }));
            await WaitFor(() => Task.FromResult(!string.IsNullOrEmpty(detailStatus.Text) && !detailStatus.IsTextTrimmed
                && Math.Abs(TextRight(detailStatus) - FooterTextRight()) < 1),
                "Rendered article footer right edge");
            Require(Bounds(counts).Right + 11 <= Bounds(detailStatus).Left, "Column footer text lost its separation.");
            var titleButton = columns.Children.OfType<Button>().Single(button => button.Tag as string == "Title");
            var beforePress = Bounds(titleButton);
            VisualStateManager.GoToState(titleButton, "Pressed", true);
            Require(Bounds(titleButton) == beforePress && titleButton.Scale == System.Numerics.Vector3.One,
                "Pressing a column header scales or moves it.");
            VisualStateManager.GoToState(titleButton, "Normal", false);
            InvokeButton(titleButton);
            var chevron = Descendants(titleButton).OfType<FontIcon>().Single();
            Require(chevron.Visibility == Visibility.Visible && chevron.Glyph is "\uE70D" or "\uE70E",
                "The sort direction is not a line chevron.");
            await SaveWindowScreenshotAsync(window, "headers-column");
            await content.OpenSelectedInWebViewAsync();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady && webStatus.Visibility == Visibility.Visible
                && !string.IsNullOrEmpty(webStatus.Text) && !webStatus.IsTextTrimmed
                && Math.Abs(TextRight(webStatus) - FooterTextRight()) < 1),
                "Rendered webpage footer right edge");
            await SaveWindowScreenshotAsync(window, "headers-column-webpage-footer");
            await content.GoBackAsync();
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady && !content.ProbeShowingWebPage),
                "Article restored after column footer check");
            foreach (var resize in columns.Children.OfType<ColumnResizeThumb>())
            {
                var peer = FrameworkElementAutomationPeer.FromElement(resize) ?? FrameworkElementAutomationPeer.CreatePeerForElement(resize);
                var range = (IRangeValueProvider)peer!.GetPattern(PatternInterface.RangeValue)!;
                range.SetValue(range.Minimum);
            }
            var table = Element<Grid>("TimelineTableContainer");
            var scroll = Element<ScrollViewer>("TimelineHorizontalScroll");
            await WaitFor(() => Task.FromResult(table.ActualWidth < scroll.ActualWidth - 10), "Narrow table");
            Require(Math.Abs(Bounds(table).Left - Bounds(scroll).Left) <= 1, "A narrow table is centered instead of left-aligned.");
            var list = Element<ListView>("ArticleListView");
            list.ScrollIntoView(model.SelectedArticle);
            await WaitFor(() => Task.FromResult(list.ContainerFromItem(model.SelectedArticle) is not null), "Column row");
            var row = Descendants((DependencyObject)list.ContainerFromItem(model.SelectedArticle)).OfType<Grid>().Single(grid => grid.Name == "TimelineColumnRow");
            Require(row.BorderThickness.Bottom == 1 && row.Padding.Bottom == 5 && row.Padding.Top == 3,
                "Column rows lost their thin divider or bottom padding.");
            await SaveWindowScreenshotAsync(window, "headers-column-narrow-table");

            Resize(600);
            search.FontSize = 24;
            window.FocusArticleSearch();
            await WaitFor(() => Task.FromResult(title.Visibility == Visibility.Collapsed && search.ActualHeight > 32
                && Bounds(search).Bottom <= Bounds(titleBar).Bottom
                && Element<Grid>("SidebarPanel").Visibility == Visibility.Collapsed), "Scaled compact title bar");
            Require(Element<TextBlock>("ArticleListTitleText").TextWrapping == TextWrapping.NoWrap
                && Element<TextBlock>("CombinedTimelineStatus").TextWrapping == TextWrapping.NoWrap
                && detailStatus.TextWrapping == TextWrapping.NoWrap && webStatus.TextWrapping == TextWrapping.NoWrap
                && detailStatus.TextTrimming == TextTrimming.CharacterEllipsis && webStatus.TextTrimming == TextTrimming.CharacterEllipsis
                && Bounds(counts).Right + 11 <= Bounds(detailStatus).Left
                && heading.ActualHeight < 80, "A narrow header or footer wrapped into a tall block.");
            Require(Bounds(search).Right <= titleBar.ActualWidth - window.AppWindow.TitleBar.RightInset / content.XamlRoot.RasterizationScale + 1,
                "Scaled search overlaps caption buttons in a narrow window.");
            await WaitFor(() => Task.FromResult(TitleBarPassesPointerInput(window, search) && TitleBarPassesPointerInput(window, toggle)),
                "Resized title-bar input regions");
            await SaveWindowScreenshotAsync(window, "headers-compact-title");
            AppDefaults.Shared.UseColumnLayout = false;
            await WaitFor(() => Task.FromResult(detailStatus.TextAlignment == TextAlignment.Left && webStatus.TextAlignment == TextAlignment.Left),
                "Standard footer alignment restored");
            return new { PaneCommands = true, FixedTitleControls = true, InputPassthrough = true,
                DpiMathCases = 4, ScaledSearch = true, UnreadSubtitle = true, CombinedHeader = true,
                FlatSortHeaders = true, LineChevrons = true, RowDividers = true, LeftAlignedTable = true, MergedFooter = true,
                RightAlignedArticleAndWebFooter = true, StandardFooterAlignmentRestored = true };
        }
        finally
        {
            search.FontSize = oldFont;
            window.AppWindow.MoveAndResize(new(original.Bounds.X, original.Bounds.Y, original.Bounds.Width, original.Bounds.Height));
            if (hadColumns) AppDefaults.Shared.SetValue("TimelineColumns", oldColumns);
            else AppDefaults.Shared.RemoveValue("TimelineColumns");
            AppDefaults.Shared.UseColumnLayout = oldColumn;
            content.SaveToolbarConfiguration(oldToolbar);
            await account.MarkAsync([article.Id], ArticleStatus.Key.Read, oldRead);
            await account.RecalculateUnreadCountsAsync();
            await content.RestoreStateAsync(original);
        }
    }

    private static async Task<object?> CheckColumnHeaderControlsAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var toolbar = content.CurrentToolbarConfiguration;
        bool originalColumn = AppDefaults.Shared.UseColumnLayout;
        var originalFilters = AppDefaults.Shared.ReadFilters;
        var title = (TextBlock)content.FindName("ArticleListTitleText");
        double originalFont = title.FontSize;
        var account = AccountManager.Shared.DefaultAccount!;
        var feed = account.AddFeed(serverUrl + "/header-filter-feed", "Header filter fixture");
        Article Make(string id, bool read) => new(account.AccountID, id, feed.FeedID, id, id,
            "<p>Header filter fixture body.</p>", null, null, serverUrl + "/article", null, null, null,
            DateTime.UtcNow, null, null, null, new ArticleStatus(id, read, false, DateTime.UtcNow));
        await account.Database.UpdateArticlesAsync([
            Make("header-filter-unread", false), Make("header-filter-read", true), Make("header-filter-selected", true)
        ]);
        await account.RecalculateUnreadCountsAsync();
        var header = (Grid)content.FindName("TimelineToolbar");
        var columns = (Grid)content.FindName("TimelineColumnsHeader");
        var side = (Grid)content.FindName("SidebarHeader");
        var feedButtons = new FrameworkElement[]
        {
            (Button)content.FindName("ReloadButton"), (DropDownButton)content.FindName("AddDropdown"),
            (Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)content.FindName("SidebarReadFilterButton")
        };
        var filter = (Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)content.FindName("ReadArticlesFilterButton");
        Rect Bounds(FrameworkElement element) => element.TransformToVisual((UIElement)window.Content)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        async Task Toggle(bool expected, bool saveFails = false)
        {
            var peer = FrameworkElementAutomationPeer.FromElement(filter) ?? FrameworkElementAutomationPeer.CreatePeerForElement(filter);
            (peer?.GetPattern(PatternInterface.Toggle) as IToggleProvider
                ?? throw new InvalidOperationException("The article filter does not expose its toggle pattern.")).Toggle();
            await model.ProbeDrainViewUpdatesAsync();
            var execution = model.ToggleReadFilterCommand.ExecutionTask
                ?? throw new InvalidOperationException("The filter control did not execute its command.");
            bool failed = false;
            try { await execution; }
            catch (Exception error) when (saveFails && error is IOException or UnauthorizedAccessException) { failed = true; }
            Require(failed == saveFails, "The read-filter save did not have the expected outcome.");
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            Require(model.IsReadFiltered == expected && filter.IsChecked == expected,
                $"The article filter control changed to {filter.IsChecked}, but its actual filter is {model.IsReadFiltered}; expected {expected}.");
        }
        var geometry = new List<object>();
        bool sidebarFixed = true;
        double largestGap = 0;
        try
        {
            double scale = content.XamlRoot.RasterizationScale;
            window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(original.Bounds with { Width = (int)Math.Ceiling(1280 * scale) }));
            await WaitFor(() => Task.FromResult(((Grid)content.FindName("MainContentGrid")).ActualWidth >= 1000),
                "Wide header test window");
            model.IsUnifiedLayout = false;
            model.IsSidebarVisible = true;
            model.IsSidebarReadFiltered = false;
            model.BuildSidebar();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            model.SetSelectedArticles([]);
            await model.ProbeDrainViewUpdatesAsync();
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            await WaitFor(() => Task.FromResult(((Grid)content.FindName("SidebarPanel")).Visibility == Visibility.Visible),
                "Visible feeds pane");
            await Task.Delay(300);
            foreach (var mode in Enum.GetValues<ToolbarDisplayMode>())
            {
                content.SaveToolbarConfiguration(ToolbarConfiguration.Default.WithDisplayMode(mode));
                foreach (double font in new[] { originalFont, 24d })
                {
                    title.FontSize = font;
                    AppDefaults.Shared.UseColumnLayout = false;
                    await WaitFor(() => Task.FromResult(side.Visibility == Visibility.Visible
                        && Descendants(side).Contains(feedButtons[0])), "Regular sidebar command positions");
                    await SaveWindowScreenshotAsync(window, $"fixed-sidebar-regular-{mode}-{font}");
                    var before = feedButtons.Select(Bounds).ToArray();
                    AppDefaults.Shared.UseColumnLayout = true;
                    await WaitFor(() => Task.FromResult(columns.Visibility == Visibility.Visible), "Column header visibility");
                    await SaveWindowScreenshotAsync(window, $"fixed-sidebar-column-{mode}-{font}");
                    var after = feedButtons.Select(Bounds).ToArray();
                    bool fixedPosition = side.Visibility == Visibility.Visible && feedButtons.All(button => Descendants(side).Contains(button))
                        && before.Zip(after).All(pair => Math.Abs(pair.First.Left - pair.Second.Left) < 0.01
                            && Math.Abs(pair.First.Top - pair.Second.Top) < 0.01);
                    double gap = Bounds(columns).Top - Bounds(header).Bottom;
                    sidebarFixed &= fixedPosition;
                    largestGap = Math.Max(largestGap, Math.Abs(gap) * scale);
                    geometry.Add(new
                    {
                        Mode = mode.ToString(), Font = font, FixedSidebar = fixedPosition, GapPixels = gap * scale,
                        HeaderBottom = Bounds(header).Bottom, ColumnsTop = Bounds(columns).Top,
                        SidebarBefore = before.Select(rect => new { rect.X, rect.Y }),
                        SidebarAfter = after.Select(rect => new { rect.X, rect.Y }),
                        ScrollTop = Bounds((ScrollViewer)content.FindName("TimelineHorizontalScroll")).Top,
                        TableTop = Bounds((Grid)content.FindName("TimelineTableContainer")).Top, MarginTop = columns.Margin.Top
                    });
                }
            }
            File.WriteAllText(Path.Combine(OutputDirectory, "column-control-measurements.json"),
                JsonSerializer.Serialize(geometry, new JsonSerializerOptions { WriteIndented = true }));
            title.FontSize = originalFont;
            content.SaveToolbarConfiguration(ToolbarConfiguration.Default);
            foreach (bool column in new[] { false, true })
            {
                AppDefaults.Shared.UseColumnLayout = column;
                await WaitFor(() => Task.FromResult(columns.Visibility == (column ? Visibility.Visible : Visibility.Collapsed)),
                    "Read filter layout");
                if (model.IsReadFiltered) await model.ToggleReadFilterCommand.ExecuteAsync(null);
                model.SetSelectedArticles([]);
                await model.FilterArticlesAsync();
                await model.WaitForCurrentTimelineAsync(CancellationToken.None);
                Require(model.ArticleItems.Count == 3 && filter.IsEnabled, "The read-filter fixture is not showing all three articles.");
                await Toggle(true);
                Require(model.ArticleItems.Select(item => item.Id).SequenceEqual(["header-filter-unread"]),
                    "Hide Read Articles did not remove unselected read articles.");
                await Toggle(false);
                Require(model.ArticleItems.Count == 3, "Show Read Articles did not restore read rows.");
                Require(ToolTipService.GetToolTip(filter) as string == AppStrings.Get("ToolbarHideReadArticlesHelp"),
                    "The read-filter tooltip does not explain the retained current article.");
                model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "header-filter-selected");
                await Toggle(true);
                Require(model.ArticleItems.Select(item => item.Id).ToHashSet().SetEquals(["header-filter-unread", "header-filter-selected"])
                    && model.SelectedArticle?.Id == "header-filter-selected", "Filtering discarded the current article or retained unrelated read rows.");
                await Toggle(false);
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    model.SetSelectedArticles([]);
                    await account.MarkAsync(["header-filter-unread", "header-filter-read", "header-filter-selected"], ArticleStatus.Key.Read, false);
                    await model.ProbeDrainViewUpdatesAsync();
                    foreach (var row in model.ArticleItems.ToArray()) model.SelectedArticle = row;
                    var selectedID = model.SelectedArticle!.Id;
                    await Toggle(true);
                    await model.PendingAutomaticReads;
                    await model.ProbeDrainViewUpdatesAsync();
                    Require(model.ArticleItems.Select(item => item.Id).SequenceEqual([selectedID]),
                        "Activating the read filter during automatic read updates left other read articles visible.");
                    await Toggle(false);
                }
                model.SetSelectedArticles([]);
                await account.MarkAsync(["header-filter-unread"], ArticleStatus.Key.Read, false);
                await model.ProbeDrainViewUpdatesAsync();
            }
            using (var locked = new FileStream(Path.Combine(AppConfig.DataDirectory, "AppDefaults.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                await Toggle(false, saveFails: true);
            Require(((InfoBar)content.FindName("ToolbarErrorInfoBar")).IsOpen, "A failed read-filter save was not reported.");
            Require(sidebarFixed, "Feed commands moved out of the sidebar when switching timeline layouts.");
            Require(largestGap < 0.05, $"The title/table header seam is {largestGap:F3} physical pixels wide.");
            Require(header.BorderThickness.Bottom == 0, "The combined title/table header still has a divider at its junction.");
            ((Control)feedButtons[0]).Focus(FocusState.Programmatic);
            model.IsSidebarVisible = false;
            await WaitFor(() => Task.FromResult(content.ProbeToolbarLayout.OverflowItems.Contains("refresh")
                && content.ProbeToolbarLayout.OverflowItems.Contains("newSidebarItemMenu")
                && content.ProbeToolbarLayout.OverflowItems.Contains("toggleReadFeedsFilter")),
                "Hidden-sidebar commands in overflow");
            return new { FixedSidebar = true, HeaderModes = geometry.Count, LargestHeaderGapPixels = largestGap,
                ActualTogglePattern = true, HideAndShowRead = true, SelectedArticleRetained = true,
                PendingAutomaticReadCycles = 8, FailedSaveRollback = true };
        }
        finally
        {
            title.FontSize = originalFont;
            ((InfoBar)content.FindName("ToolbarErrorInfoBar")).IsOpen = false;
            AppDefaults.Shared.ReadFilters = originalFilters;
            AppDefaults.Shared.UseColumnLayout = originalColumn;
            content.SaveToolbarConfiguration(toolbar);
            account.RemoveFeed(feed);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            window.AppWindow.MoveAndResize(new(original.Bounds.X, original.Bounds.Y, original.Bounds.Width, original.Bounds.Height));
        }
    }

    private sealed record TitleIconPixels(int Left, int Top, int Right, int Bottom, double Ink)
    {
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;
    }

    private static async Task<object?> CheckTitleBarMotionAsync(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        var titleBar = Descendants(root).OfType<Grid>().Single(grid => grid.Name == "AppTitleBar");
        var back = Descendants(titleBar).OfType<Button>().Single(button => button.Name == "NavigateBackButton");
        var menu = Descendants(titleBar).OfType<Button>().Single(button => button.Name == "ToggleSidebarButton");
        var backIcon = Descendants(back).OfType<AnimatedIcon>().Single();
        var menuIcon = Descendants(menu).OfType<AnimatedIcon>().Single();
        var theme = root.RequestedTheme;
        bool backEnabled = back.IsEnabled, menuEnabled = menu.IsEnabled;
        var backForeground = backIcon.ReadLocalValue(IconElement.ForegroundProperty);
        var menuForeground = menuIcon.ReadLocalValue(IconElement.ForegroundProperty);
        var frames = new List<object>();
        Rect Bounds(FrameworkElement element) => element.TransformToVisual(root)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        TitleIconPixels Pixels(Button button)
        {
            var bounds = Bounds(button);
            var capture = CaptureWindow(window, bounds);
            int width = capture.Width, height = capture.Height;
            int left = width, right = -1, top = height, bottom = -1;
            double ink = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * capture.Width + x) * 4;
                    byte r = capture.Data[offset + 2], g = capture.Data[offset + 1], b = capture.Data[offset];
                    if (r < g + 40 || r < b + 40) continue;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    ink += (r - (g + b) / 2d) / 255d;
                }
            Require(right >= left && bottom >= top, "The animated title-bar glyph did not render.");
            return new(left, top, right, bottom, ink);
        }
        try
        {
            root.RequestedTheme = ElementTheme.Light;
            back.IsEnabled = menu.IsEnabled = true;
            backIcon.Foreground = menuIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
            window.Activate();
            SetForegroundWindow(WindowNative.GetWindowHandle(window));
            await WaitFor(() => Task.FromResult(GetForegroundWindow() == WindowNative.GetWindowHandle(window)), "Owned title-bar capture");
            Require(backIcon.Source is Microsoft.UI.Xaml.Controls.AnimatedVisuals.AnimatedBackVisualSource
                && menuIcon.Source is Microsoft.UI.Xaml.Controls.AnimatedVisuals.AnimatedGlobalNavigationButtonVisualSource,
                "The title buttons do not use the native Windows navigation animations.");
            Require(backIcon.Width == 18 && backIcon.Height == 18
                && backIcon.FallbackIconSource is FontIconSource { FontSize: 14 }
                && menuIcon.Width == 16 && menuIcon.Height == 16,
                "The back icon was not reduced independently of the menu icon.");
            double inset = window.AppWindow.TitleBar.LeftInset / root.XamlRoot.RasterizationScale;
            Require(Math.Abs(titleBar.Padding.Left - Math.Max(8, inset + 8)) < 0.01,
                "The compact title-bar left inset was not applied.");
            bool animations = new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            foreach (var button in new[] { back, menu })
            {
                Require(VisualStateManager.GoToState(button, "Normal", false), "The title button has no normal state.");
                await Task.Delay(450);
                var normal = Pixels(button);
                var bounds = Bounds(button);
                var work = window.ProbeContent.ProbeToolbarPerformance;
                Require(VisualStateManager.GoToState(button, "Pressed", true), "The title button has no pressed state.");
                await Task.Delay(250);
                var pressed = Pixels(button);
                Require(pressed.Width < normal.Width, "Pressing the title button did not compress its glyph.");
                if (ReferenceEquals(button, back))
                    Require(pressed.Left > normal.Left && pressed.Right <= normal.Right + 1,
                        "The Back icon did not shrink toward the right.");
                else Require(Math.Abs(pressed.Height - normal.Height) <= 1, "The menu icon compressed vertically instead of only horizontally.");
                VisualStateManager.GoToState(button, "Normal", true);
                var release = new List<TitleIconPixels>();
                for (int frame = 0; frame < 36; frame++)
                {
                    await Task.Delay(6);
                    release.Add(Pixels(button));
                }
                await Task.Delay(200);
                var settled = Pixels(button);
                frames.Add(new { Button = button.Name, Normal = normal, Pressed = pressed, Release = release, Settled = settled });
                Require(Math.Abs(settled.Ink - normal.Ink) < 1 && settled.Width == normal.Width,
                    "The title-bar glyph did not settle back to its normal size.");
                Require(!animations || release.Any(frame => frame.Width >= normal.Width
                    && (frame.Left < normal.Left || frame.Right > normal.Right || frame.Ink > normal.Ink * 1.01)),
                    "The native release animation did not show its rebound.");
                Require(Bounds(button) == bounds && button.ActualWidth == 32 && button.ActualHeight == 32
                    && TitleBarPassesPointerInput(window, button), "Icon motion changed the hit target or title-bar input routing.");
                Require(window.ProbeContent.ProbeToolbarPerformance.Columns == work.Columns,
                    "The icon animation rebuilt header layout.");
                VisualStateManager.GoToState(button, "Pressed", true);
                await Task.Delay(30);
                VisualStateManager.GoToState(button, "Normal", true);
                await Task.Delay(30);
                VisualStateManager.GoToState(button, "Pressed", true);
                button.IsEnabled = false;
                await Task.Delay(450);
                button.IsEnabled = true;
                VisualStateManager.GoToState(button, "Normal", false);
                await Task.Delay(400);
                var recovered = Pixels(button);
                frames.Add(new { Button = button.Name, Recovered = recovered });
                Require(Math.Abs(recovered.Width - normal.Width) <= 1 && recovered.Width > pressed.Width
                    && Math.Abs(recovered.Ink - normal.Ink) < 2,
                    $"Interrupted motion did not recover: normal={normal.Width}/{normal.Ink:F2}, recovered={recovered.Width}/{recovered.Ink:F2}.");
            }
            return new { NativeAnimationSources = true, SmallerBack = true, CompactInset = true,
                PressAndReboundPixels = animations, StableHitTargets = true, InterruptedPressRecovery = true };
        }
        finally
        {
            File.WriteAllText(Path.Combine(OutputDirectory, "titlebar-motion.json"),
                JsonSerializer.Serialize(frames, new JsonSerializerOptions { WriteIndented = true }));
            if (ReferenceEquals(backForeground, DependencyProperty.UnsetValue)) backIcon.ClearValue(IconElement.ForegroundProperty);
            else backIcon.SetValue(IconElement.ForegroundProperty, backForeground);
            if (ReferenceEquals(menuForeground, DependencyProperty.UnsetValue)) menuIcon.ClearValue(IconElement.ForegroundProperty);
            else menuIcon.SetValue(IconElement.ForegroundProperty, menuForeground);
            back.IsEnabled = backEnabled;
            menu.IsEnabled = menuEnabled;
            VisualStateManager.GoToState(back, backEnabled ? "Normal" : "Disabled", false);
            VisualStateManager.GoToState(menu, menuEnabled ? "Normal" : "Disabled", false);
            root.RequestedTheme = theme;
        }
    }

    private static bool TitleBarPassesPointerInput(MainWindow window, FrameworkElement element)
    {
        var bounds = element.TransformToVisual((UIElement)window.Content)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var expected = MainWindow.TitleBarInputRect(bounds, element.XamlRoot.RasterizationScale);
        var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
        return source.GetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough).Any(rectangle =>
            rectangle.X <= expected.X && rectangle.Y <= expected.Y
            && rectangle.X + rectangle.Width >= expected.X + expected.Width
            && rectangle.Y + rectangle.Height >= expected.Y + expected.Height);
    }

    private static async Task<object?> CheckToolbarAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        bool oldLayout = AppDefaults.Shared.UseColumnLayout;
        bool hadConfiguration = AppDefaults.Shared.ContainsKey(ToolbarConfiguration.PreferenceKey);
        var originalConfiguration = AppDefaults.Shared.GetValue<JsonElement>(ToolbarConfiguration.PreferenceKey);
        var toolbar = (ToolbarPanel)content.FindName("MainToolbar");
        var overflowButton = (Button)content.FindName("ToolbarOverflowButton");
        var overflow = (MenuFlyout)overflowButton.Flyout;
        var floating = (Button)content.FindName("FloatingToolbarButton");
        var floatingMenu = (MenuFlyout)floating.Flyout;
        var timelineList = (ListView)content.FindName("ArticleListView");
        var oldScrollbarVisibility = ScrollViewer.GetVerticalScrollBarVisibility(timelineList);
        var root = (FrameworkElement)window.Content;
        var originalTheme = root.RequestedTheme;
        var errorBar = (InfoBar)content.FindName("ToolbarErrorInfoBar");
        var search = content.ProbeToolbarSearch!;
        var menuBar = Descendants(window.Content).OfType<MenuBar>().Single(bar => bar.Name == "AppMenuBar");
        var view = menuBar.Items.Single(item => item.Title == "View");
        var visibility = view.Items.OfType<MenuFlyoutItem>().Single(item => item.Name == "MenuShowToolbar");
        MainWindow? other = null;
        T Element<T>(string name) where T : FrameworkElement => (T)content.FindName(name);
        Rect Bounds(FrameworkElement element) => element.TransformToVisual(content)
            .TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        bool HeadersHidden(MainContent target) =>
            new[] { "MainToolbar", "SidebarHeader", "TimelineToolbar", "ArticleToolbarHost", "CombinedToolbarHost" }
                .All(name => ((FrameworkElement)target.FindName(name)).Visibility == Visibility.Collapsed)
            && ((Button)target.FindName("FloatingToolbarButton")).Visibility == Visibility.Visible
            && ((Border)target.FindName("FloatingToolbarSurface")).Visibility == Visibility.Visible;
        async Task VerifyHiddenHeadersAsync(string layout)
        {
            var detail = Element<Grid>("ArticleDetailPanel");
            var timeline = Element<Grid>("TimelinePanel");
            var sidebar = Element<Grid>("SidebarPanel");
            var columns = Element<Grid>("TimelineColumnsHeader");
            var list = Element<ListView>("ArticleListView");
            await WaitFor(() => Task.FromResult(HeadersHidden(content)
                && detail.RowDefinitions[0].ActualHeight == 0
                && (timeline.Visibility == Visibility.Collapsed || timeline.RowDefinitions[0].ActualHeight == 0)
                && (sidebar.Visibility == Visibility.Collapsed || sidebar.RowDefinitions[0].ActualHeight == 0)
                && Element<Grid>("MainContentGrid").ActualWidth <= content.ActualWidth + 1
                && columns.Margin.Top == 0 && floating.ActualWidth == 36 && floating.ActualHeight == 36),
                "Fully hidden headers in " + layout);
            if (timeline.Visibility == Visibility.Visible)
                Require(Math.Abs(list.Padding.Top - (model.UseColumnLayout ? columns.ActualHeight : 0)) <= 1,
                    "Hidden headers left empty timeline padding in " + layout);
            double ContentTop() => model.UseColumnLayout && !model.IsUnifiedLayout
                ? Bounds(columns).Bottom : Bounds(content.ProbeWebView).Top;
            await WaitFor(() =>
            {
                var buttonBounds = Bounds(floating);
                double contentTop = ContentTop();
                return Task.FromResult(Math.Abs(content.ActualWidth - buttonBounds.Right - 32) <= 1
                    && buttonBounds.Top >= contentTop - 1 && buttonBounds.Top <= contentTop + 8);
            }, "Floating toolbar position in " + layout);
            bool column = model.UseColumnLayout && !model.IsUnifiedLayout;
            var background = column ? Element<Grid>("TimelineToolbar").Background : Element<Border>("ContentContainer").Background;
            Require(ReferenceEquals(floating.Background, background),
                "The floating button does not share the hidden toolbar's background in " + layout);
            Require(Element<Border>("FloatingToolbarSurface").Background is SolidColorBrush { Color.A: 255 },
                "Article content can bleed through the floating toolbar background in " + layout);
            double scrollbarLeft;
            if (column)
            {
                var rail = Descendants(list).OfType<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>()
                    .Single(bar => bar.Orientation == Orientation.Vertical && bar.Visibility == Visibility.Visible && bar.ActualWidth > 0);
                scrollbarLeft = Bounds(rail).Left;
            }
            else
            {
                await WaitFor(() => ScriptBool(content, "window.innerWidth > document.documentElement.clientWidth"),
                    "Visible reader scrollbar in " + layout);
                double ratio = JsonSerializer.Deserialize<double>(await content.ProbeWebView.ExecuteScriptAsync(
                    "document.documentElement.clientWidth / window.innerWidth"));
                var reader = Bounds(content.ProbeWebView);
                scrollbarLeft = reader.Left + reader.Width * ratio;
            }
            double scrollbarGap = scrollbarLeft - Bounds(floating).Right;
            Require(scrollbarGap >= 8, "The floating button overlaps the scrollbar in " + layout + ": gap=" + scrollbarGap);
            Require(floating.IsTabStop && AutomationProperties.GetName(floating) == AppStrings.Get("ToolbarOptions"),
                "The floating options button is inaccessible in " + layout);
            File.WriteAllText(Path.Combine(OutputDirectory, "toolbar-hidden-" + layout + ".json"),
                JsonSerializer.Serialize(new { Button = Bounds(floating), content.ActualWidth, ContentTop = ContentTop(),
                    ScrollbarLeft = scrollbarLeft, ScrollbarGap = scrollbarGap,
                    BackgroundSource = column ? "TimelineToolbar" : "ContentContainer" }));
            await SaveWindowScreenshotAsync(window, "toolbar-hidden-" + layout);
        }
        try
        {
            model.IsUnifiedLayout = false;
            model.IsReaderMode = false;
            var account = AccountManager.Shared.DefaultAccount!;
            var feed = account.FlattenedFeeds().Single();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "probe-article");
            await WaitFor(async () => content.ProbeArticleReady && await ScriptBool(content, "!!document.getElementById('probe-body')"),
                "Toolbar article prerequisite");
            var core = content.ProbeWebView.CoreWebView2;
            await content.ProbeWebView.ExecuteScriptAsync("window.toolbarSentinel = 'preserved';");
            await content.ProbeWebView.ExecuteScriptAsync("""
                (() => {
                    const style = document.createElement('style');
                    style.id = 'probe-toolbar-scrollbars';
                    style.textContent = 'html { overflow-y: scroll !important; }';
                    document.head.append(style);
                })()
                """);
            ScrollViewer.SetVerticalScrollBarVisibility(timelineList, ScrollBarVisibility.Visible);
            var selected = model.SelectedArticle;
            var sorting = model.SortParameters;
            var frame = window.CaptureState().Bounds;
            bool readerMode = model.IsReaderMode;
            string query = model.SearchQuery;
            AppDefaults.Shared.UseColumnLayout = false;
            Require(content.SaveToolbarConfiguration(ToolbarConfiguration.Default) is null, "Could not establish the native toolbar defaults.");
            await WaitFor(() => Task.FromResult(content.ProbeToolbarLayout.Slots.Any(slot => slot.Identifier == "markRead")
                && Element<Grid>("ArticleDetailPanel").RowDefinitions[0].ActualHeight > 0), "Default header commands");
            var toolbarPeer = FrameworkElementAutomationPeer.FromElement(toolbar) ?? FrameworkElementAutomationPeer.CreatePeerForElement(toolbar);
            Require(toolbarPeer?.GetAutomationControlType() == AutomationControlType.ToolBar, "The toolbar has no accessible toolbar role.");
            Require(content.CurrentToolbarConfiguration.Items.SequenceEqual(ToolbarConfiguration.DefaultItems), "The default toolbar order differs.");

            double readerHeight = content.ProbeWebView.ActualHeight;
            double headerHeight = Element<Grid>("ArticleDetailPanel").RowDefinitions[0].ActualHeight;
            Require(overflowButton.Focus(FocusState.Programmatic), "Could not focus the original toolbar.");
            InvokeMenuItem(visibility);
            await VerifyHiddenHeadersAsync("standard");
            Require(content.ProbeWebView.ActualHeight >= readerHeight + headerHeight - 1
                && ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(content.XamlRoot), floating)
                && !ToolbarConfiguration.Load(new AppDefaults()).IsVisible,
                "Hiding the full header failed to recover content space, focus, or saved visibility.");
            root.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(300);
            await VerifyHiddenHeadersAsync("standard-dark");
            root.RequestedTheme = originalTheme;
            await Task.Delay(300);
            AppDefaults.Shared.UseColumnLayout = true;
            await WaitFor(() => Task.FromResult(ReferenceEquals(Element<ContentControl>("CombinedToolbarHost").Content, toolbar)),
                "Hidden column-layout header routing");
            await VerifyHiddenHeadersAsync("column");
            model.IsUnifiedLayout = true;
            await WaitFor(() => Task.FromResult(ReferenceEquals(Element<ContentControl>("ArticleToolbarHost").Content, toolbar)
                && Element<Grid>("TimelinePanel").Visibility == Visibility.Collapsed), "Hidden reading-only header routing");
            await VerifyHiddenHeadersAsync("reading-only");
            AppDefaults.Shared.UseColumnLayout = false;
            model.IsUnifiedLayout = false;
            await content.ShowArticleFindAsync();
            await WaitFor(() => Task.FromResult(Element<Border>("ArticleFindBar").Visibility == Visibility.Visible
                && Bounds(floating).Top >= Bounds(Element<Border>("ArticleFindBar")).Bottom), "Floating options clear the Find controls");
            InvokeButton(Element<Button>("ArticleFindClose"));
            await VerifyHiddenHeadersAsync("find-closed");
            int minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)window.AppWindow.Presenter).PreferredMinimumWidth
                ?? throw new InvalidOperationException("The window has no native minimum width.");
            window.AppWindow.Resize(new(minimum, frame.Height));
            await WaitFor(() => Task.FromResult(window.AppWindow.Size.Width == minimum
                && Math.Abs(content.ActualWidth * content.XamlRoot.RasterizationScale - window.AppWindow.ClientSize.Width) <= 1),
                "Minimum-width hidden toolbar");
            await VerifyHiddenHeadersAsync("minimum-width");
            window.AppWindow.MoveAndResize(new(frame.X, frame.Y, frame.Width, frame.Height));
            await WaitFor(() => Task.FromResult(window.AppWindow.Size.Width == frame.Width
                && Element<Grid>("TimelinePanel").Visibility == Visibility.Visible), "Restored wide reader");
            other = App.OpenMainWindow();
            await WaitFor(() => Task.FromResult(other.ProbeContent.IsLoaded && HeadersHidden(other.ProbeContent)),
                "New window with fully hidden headers");
            window.Activate();
            window.FocusArticleSearch();
            await WaitFor(() => Task.FromResult(search.Visibility == Visibility.Visible
                && (ReferenceEquals(search, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(content.XamlRoot))
                    || Descendants(search).Contains(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(content.XamlRoot) as DependencyObject))
                && content.KeyboardAccelerators.Where(accelerator => accelerator.Modifiers == global::Windows.System.VirtualKeyModifiers.None)
                    .All(accelerator => !accelerator.IsEnabled)), "Title-bar search focus");
            Require(!content.CurrentToolbarConfiguration.IsVisible, "Focusing title-bar search unexpectedly showed header commands.");
            Require(content.KeyboardAccelerators.Where(accelerator => accelerator.Modifiers == global::Windows.System.VirtualKeyModifiers.None)
                .All(accelerator => !accelerator.IsEnabled), "Search typing did not suspend single-letter commands.");
            await ClickElementAsync(floating, () => floatingMenu.IsOpen
                && floatingMenu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == AppStrings.Get("CustomizeToolbar") && item.IsLoaded),
                "Native floating toolbar options");
            InvokeMenuItem(floatingMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == AppStrings.Get("CustomizeToolbar")));
            await WaitFor(() => Task.FromResult(content.ProbeToolbarDialog?.IsLoaded == true), "Customization from the floating toolbar");
            Require(Descendants(content.ProbeToolbarDialog!).OfType<CheckBox>()
                .Single(box => box.Content as string == AppStrings.Get("ShowToolbar")).IsChecked == false,
                "Floating customization did not retain hidden toolbar visibility.");
            InvokeDialogButton(content.ProbeToolbarDialog!, "CloseButton");
            await WaitFor(() => Task.FromResult(content.ProbeToolbarDialog is null && HeadersHidden(content)),
                "Cancelled hidden-toolbar customization");
            await content.OpenSelectedInWebViewAsync();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Embedded browser behind floating toolbar");
            await ClickElementAsync(floating, () => floatingMenu.IsOpen
                && floatingMenu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == AppStrings.Get("ShowToolbar") && item.IsLoaded),
                "Floating options above the embedded browser");
            InvokeMenuItem(floatingMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == AppStrings.Get("ShowToolbar")));
            await WaitFor(() => Task.FromResult(content.CurrentToolbarConfiguration.IsVisible
                && toolbar.Visibility == Visibility.Visible && floating.Visibility == Visibility.Collapsed
                && Element<Border>("FloatingToolbarSurface").Visibility == Visibility.Collapsed
                && other.ProbeContent.CurrentToolbarConfiguration.IsVisible
                && ((Button)other.ProbeContent.FindName("FloatingToolbarButton")).Visibility == Visibility.Collapsed),
                "Full headers restored through the floating menu in both windows");
            await content.GoBackAsync();
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady && !content.ProbeShowingWebPage), "Article restored after toolbar recovery");

            foreach (var mode in Enum.GetValues<ToolbarDisplayMode>())
            {
                Require(content.SaveToolbarConfiguration(content.CurrentToolbarConfiguration.WithDisplayMode(mode)) is null, "Could not save toolbar display.");
                var stack = (StackPanel)((Button)content.FindName("StarButton")).Content;
                Require(stack.Children.OfType<FontIcon>().Single().Visibility == (mode == ToolbarDisplayMode.TextOnly ? Visibility.Collapsed : Visibility.Visible)
                    && stack.Children.OfType<TextBlock>().Single().Visibility == (mode == ToolbarDisplayMode.IconsOnly ? Visibility.Collapsed : Visibility.Visible),
                    "The toolbar did not apply the requested display mode.");
            }

            var customizeTask = content.CustomizeToolbarAsync();
            await WaitFor(() => Task.FromResult(content.ProbeToolbarDialog?.IsLoaded == true), "Toolbar customization dialog");
            var dialog = content.ProbeToolbarDialog!;
            Require(content.KeyboardAccelerators.Where(accelerator => accelerator.Modifiers == global::Windows.System.VirtualKeyModifiers.None)
                .All(accelerator => !accelerator.IsEnabled), "Customization type-to-select left article letter shortcuts enabled.");
            var list = Descendants(dialog).OfType<ListView>().Single();
            Button DialogAction(string resource) => Descendants(dialog).OfType<Button>()
                .Single(button => button.Content as string == AppStrings.Get(resource));
            list.SelectedIndex = 0;
            InvokeButton(DialogAction("ToolbarMoveLater"));
            InvokeButton(DialogAction("ToolbarHideItem"));
            var available = Descendants(dialog).OfType<ComboBox>()
                .Single(combo => AutomationProperties.GetName(combo) == AppStrings.Get("ToolbarAvailableItems"));
            available.SelectedIndex = 0;
            InvokeButton(DialogAction("ToolbarShowItem"));
            Require(content.CurrentToolbarConfiguration.Items.SequenceEqual(ToolbarConfiguration.DefaultItems),
                "Editing a draft changed the live toolbar before saving.");
            InvokeDialogButton(dialog, "PrimaryButton");
            await customizeTask;
            var customized = content.CurrentToolbarConfiguration;
            Require(customized.Items.Count == ToolbarConfiguration.DefaultItems.Count
                && ToolbarLayout.HeaderItems(customized, ToolbarSection.Combined).Take(3)
                    .SequenceEqual(new[] { "newSidebarItemMenu", "markAllAsRead", "refresh" }),
                "Move/hide/show customization did not preserve its edited command order.");
            customizeTask = content.CustomizeToolbarAsync();
            await WaitFor(() => Task.FromResult(content.ProbeToolbarDialog?.IsLoaded == true), "Toolbar cancel dialog");
            dialog = content.ProbeToolbarDialog!;
            InvokeButton(DialogAction("ToolbarRestoreDefaults"));
            InvokeDialogButton(dialog, "CloseButton");
            await customizeTask;
            Require(content.CurrentToolbarConfiguration.Items.SequenceEqual(customized.Items), "Cancel committed the restore-defaults draft.");

            Require(other.ProbeContent.CurrentToolbarConfiguration.Items.SequenceEqual(customized.Items)
                && other.ProbeContent.CurrentToolbarConfiguration.DisplayMode == ToolbarDisplayMode.TextOnly,
                "The companion window did not receive the shared toolbar configuration.");
            window.Activate();
            Require(content.SaveToolbarConfiguration(ToolbarConfiguration.Default) is null, "Could not restore default toolbar.");
            Require(other.ProbeContent.CurrentToolbarConfiguration.Items.SequenceEqual(ToolbarConfiguration.DefaultItems),
                "A toolbar change did not propagate to the other window.");
            search.Focus(FocusState.Programmatic);
            AppDefaults.Shared.UseColumnLayout = false;
            await WaitFor(() => Task.FromResult(ReferenceEquals(((Button)content.FindName("MarkAllReadButton")).Parent,
                content.FindName("TimelineToolbarItemsPanel"))), "Separate pane command headers");
            AppDefaults.Shared.UseColumnLayout = true;
            await WaitFor(() => Task.FromResult(ReferenceEquals(((Button)content.FindName("MarkAllReadButton")).Parent,
                content.FindName("ToolbarItemsPanel"))), "Combined column-layout header");
            Require(content.CurrentToolbarConfiguration.Items.SequenceEqual(ToolbarConfiguration.DefaultItems),
                "Changing layouts discarded the timeline separator from the saved toolbar.");
            AppDefaults.Shared.UseColumnLayout = false;
            await WaitFor(() => Task.FromResult(ReferenceEquals(((Button)content.FindName("MarkAllReadButton")).Parent,
                content.FindName("TimelineToolbarItemsPanel"))), "Restored pane headers");

            ((ListView)content.FindName("ArticleListView")).Focus(FocusState.Programmatic);
            content.ProbeLayoutToolbar(36);
            Require(content.ProbeToolbarLayout.OverflowItems.Contains("markStar"), "A narrow article header lost its overflow commands.");
            overflow.ShowAt(overflowButton);
            string starLabel = AutomationProperties.GetName((Button)content.FindName("StarButton"));
            await WaitFor(() => Task.FromResult(overflow.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == starLabel && item.IsLoaded)),
                "Labeled star overflow command");
            Require(!overflow.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == ToolbarConfiguration.Label("flexibleSpace")),
                "Overflow exposed a nonfunctional spacer command.");
            bool wasStarred = selected!.IsStarred;
            InvokeMenuItem(overflow.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == starLabel));
            await WaitFor(() => Task.FromResult(selected.IsStarred != wasStarred), "Overflow star action");
            await model.ToggleStarredCommand.ExecuteAsync(null);
            Require(search.Visibility == Visibility.Visible && !content.ProbeToolbarLayout.OverflowItems.Contains("search"),
                "Header overflow moved or hid title-bar search.");
            Require(ReferenceEquals(search, content.ProbeToolbarSearch), "Overflow recreated the search control.");
            content.ProbeLayoutToolbar(double.NaN);

            var removed = content.CurrentToolbarConfiguration.WithItems(["markStar", "readerView"]).WithVisibility(false);
            Require(content.SaveToolbarConfiguration(removed) is null, "Could not remove the search item.");
            await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", """{"enabled":true}""");
            content.ProbeWebView.Focus(FocusState.Programmatic);
            await DispatchBrowserKeyAsync(core, "f", "KeyF", 70, modifiers: 3);
            await WaitFor(() => Task.FromResult(search.Visibility == Visibility.Visible
                && (ReferenceEquals(search, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(content.XamlRoot))
                    || Descendants(search).Contains(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(content.XamlRoot) as DependencyObject))),
                "Reader Ctrl+Alt+F focuses fixed title-bar search");
            Require(!content.CurrentToolbarConfiguration.IsVisible
                && content.CurrentToolbarConfiguration.Items.SequenceEqual(new[] { "markStar", "readerView" }),
                "Focusing search changed header customization.");
            Require(content.SaveToolbarConfiguration(content.CurrentToolbarConfiguration.WithVisibility(true)) is null,
                "Could not restore header visibility.");

            var beforeFailure = content.CurrentToolbarConfiguration;
            customizeTask = content.CustomizeToolbarAsync();
            await WaitFor(() => Task.FromResult(content.ProbeToolbarDialog?.IsLoaded == true), "Toolbar save-failure dialog");
            dialog = content.ProbeToolbarDialog!;
            InvokeButton(DialogAction("ToolbarRestoreDefaults"));
            using (var locked = new FileStream(Path.Combine(AppConfig.DataDirectory, "AppDefaults.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                InvokeDialogButton(dialog, "PrimaryButton");
                Require(!customizeTask.IsCompleted && content.CurrentToolbarConfiguration.Items.SequenceEqual(beforeFailure.Items),
                    "A failed customization write closed the dialog or replaced the current toolbar.");
                Require(Descendants(dialog).OfType<TextBlock>().Any(text => text.Text.StartsWith(
                    AppStrings.Get("ToolbarSaveError").Split("{0}")[0], StringComparison.CurrentCulture)),
                    "The customization dialog did not announce its save failure.");
            }
            InvokeDialogButton(dialog, "CloseButton");
            await customizeTask;
            using (var locked = new FileStream(Path.Combine(AppConfig.DataDirectory, "AppDefaults.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                InvokeMenuItem(visibility);
                Require(content.CurrentToolbarConfiguration.IsVisible && toolbar.Visibility == Visibility.Visible
                    && floating.Visibility == Visibility.Collapsed && errorBar.IsOpen,
                    "A failed visibility write changed live state or failed to report the error.");
                Require(other.ProbeContent.CurrentToolbarConfiguration.IsVisible, "A failed write changed another window's toolbar.");
            }
            Require(ToolbarConfiguration.Load(new AppDefaults()).Items.SequenceEqual(beforeFailure.Items)
                && ToolbarConfiguration.Load(new AppDefaults()).IsVisible, "A failed toolbar write changed the saved configuration.");
            Require(content.SaveToolbarConfiguration(beforeFailure.WithVisibility(false)) is null,
                "Could not prepare hidden-toolbar save failure.");
            floatingMenu.ShowAt(floating);
            await WaitFor(() => Task.FromResult(floatingMenu.Items.OfType<MenuFlyoutItem>()
                .Any(item => item.Text == AppStrings.Get("ShowToolbar") && item.IsLoaded)), "Floating restore action before save failure");
            using (var locked = new FileStream(Path.Combine(AppConfig.DataDirectory, "AppDefaults.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                InvokeMenuItem(floatingMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == AppStrings.Get("ShowToolbar")));
                Require(HeadersHidden(content) && HeadersHidden(other.ProbeContent) && errorBar.IsOpen,
                    "A failed restore lost the floating recovery control, changed headers, or hid the error.");
            }
            Require(!ToolbarConfiguration.Load(new AppDefaults()).IsVisible, "A failed restore changed saved toolbar visibility.");
            floatingMenu.Hide();
            Require(content.SaveToolbarConfiguration(beforeFailure) is null, "Could not restore the toolbar after the failed write.");
            var preserved = new
            {
                Selection = ReferenceEquals(selected, model.SelectedArticle), Sorting = model.SortParameters == sorting,
                ReaderMode = model.IsReaderMode == readerMode, Query = model.SearchQuery == query,
                Frame = window.CaptureState().Bounds == frame, Browser = ReferenceEquals(core, content.ProbeWebView.CoreWebView2),
                Document = await ScriptString(content, "window.toolbarSentinel") == "preserved"
            };
            Require(preserved.Selection && preserved.Sorting && preserved.ReaderMode && preserved.Query && preserved.Frame
                && preserved.Browser && preserved.Document, "Toolbar/layout state changed: " + JsonSerializer.Serialize(preserved));
            return new { SharedConfiguration = true, DisplayModes = 3, CustomizationAndCancel = true, FixedTitleBarSearch = true,
                OverflowAction = true, AccessibleToolbar = true, TypingGuard = true, PaneRouting = true, FailedWriteRollback = true, DocumentPreserved = true,
                EntireHeadersHidden = true, FloatingConfiguration = true, HiddenLayoutSwitching = true, HiddenStartup = true, MinimumWidthRecovery = true,
                FloatingScrollbarClearance = true, MatchingFloatingBackground = true };
        }
        finally
        {
            content.ProbeToolbarDialog?.Hide();
            overflow.Hide();
            floatingMenu.Hide();
            root.RequestedTheme = originalTheme;
            ScrollViewer.SetVerticalScrollBarVisibility(timelineList, oldScrollbarVisibility);
            content.ProbeLayoutToolbar(double.NaN);
            errorBar.IsOpen = false;
            if (other is not null) await CloseWindowAsync(other);
            AppDefaults.Shared.UseColumnLayout = oldLayout;
            if (hadConfiguration) AppDefaults.Shared.SetValue(ToolbarConfiguration.PreferenceKey, originalConfiguration);
            else AppDefaults.Shared.RemoveValue(ToolbarConfiguration.PreferenceKey);
            if (content.ProbeWebView.CoreWebView2 is { } core)
            {
                await core.ExecuteScriptAsync("document.getElementById('probe-toolbar-scrollbars')?.remove();");
                await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", """{"enabled":false}""");
            }
            window.AppWindow.MoveAndResize(new(original.Bounds.X, original.Bounds.Y, original.Bounds.Width, original.Bounds.Height));
            await content.RestoreStateAsync(original);
            window.Activate();
        }
    }

    private static async Task<object?> CheckFullScreenAsync(MainWindow window)
    {
        var original = window.CaptureState();
        try
        {
            window.Activate();
            await InvokeFullScreenMenuAsync(window);
            await WaitFor(() => Task.FromResult(window.IsFullScreen), "Full-screen entry");
            var full = window.CaptureState();
            Require(full.IsFullScreen && full.Bounds == original.Bounds && full.IsMaximized == original.IsMaximized,
                "Full-screen entry overwrote the normal placement.");
            await InvokeFullScreenMenuAsync(window);
            await WaitFor(() => Task.FromResult(!window.IsFullScreen), "Full-screen exit");
            Require(window.CaptureState().Bounds == original.Bounds, "Leaving full screen changed the normal placement.");
            ((Microsoft.UI.Windowing.OverlappedPresenter)window.AppWindow.Presenter).Maximize();
            await InvokeFullScreenMenuAsync(window);
            await WaitFor(() => Task.FromResult(window.IsFullScreen), "Maximized full-screen entry");
            var saved = window.CaptureState();
            Require(saved.IsMaximized && saved.IsFullScreen && saved.Bounds == original.Bounds, "The pre-full-screen maximized state was lost.");
            var restored = App.OpenMainWindow(saved);
            Require(await restored.Restoration.WaitAsync(TimeSpan.FromSeconds(30)), "Full-screen window restoration failed.");
            Require(restored.IsFullScreen && restored.CaptureState().Bounds == saved.Bounds, "The saved full-screen presentation was not restored.");
            await InvokeFullScreenMenuAsync(restored);
            await WaitFor(() => Task.FromResult(!restored.IsFullScreen), "Restored full-screen exit");
            Require(restored.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
                { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized }, "Leaving restored full screen did not restore maximization.");
            await CloseWindowAsync(restored);
            return new { NativeFullScreen = true, AccessibleMenu = true, NormalBoundsPreserved = true, MaximizedStatePreserved = true, RestoredAfterActivation = true };
        }
        finally
        {
            foreach (var other in App.MainWindows.Where(other => !ReferenceEquals(other, window)).ToArray()) await CloseWindowAsync(other);
            if (window.IsFullScreen)
            {
                await InvokeFullScreenMenuAsync(window);
                await WaitFor(() => Task.FromResult(!window.IsFullScreen), "Full-screen cleanup");
            }
            if (!original.IsMaximized && window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                presenter.Restore();
            window.Activate();
        }
    }

    private static async Task<string?> ScriptString(MainContent content, string expression)
        => JsonSerializer.Deserialize<string>(await content.ProbeWebView.ExecuteScriptAsync(expression));

    private static async Task<object?> CheckBrowsingAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var originalSearch = model.SearchQuery;
        var originalToolbar = content.CurrentToolbarConfiguration;
        bool originalScripts = AppDefaults.Shared.ArticleContentJavascriptEnabled;
        bool originalColumn = AppDefaults.Shared.UseColumnLayout;
        var originalLauncher = Browser.ProbeLaunch;
        var launches = new List<Web.BrowserLaunchRequest>();
        global::Windows.ApplicationModel.DataTransfer.DataPackageView? shared = null;
        MainWindow? other = null;
        var account = AccountManager.Shared.DefaultAccount!;
        var firstFeed = account.AddFeed(serverUrl + "/navigation-feed", "Navigation feed");
        var otherFeed = account.AddFeed(serverUrl + "/navigation-other-feed", "Other navigation feed");
        Article Article(string id, Feed feed) => new(account.AccountID, id, feed.FeedID, id, "Navigation " + id,
            "<div id='navigation-article' style='min-height:4000px'>Stored article fixture</div>",
            null, null, serverUrl + "/navigation-first", null, null, null, DateTime.UtcNow, null, null, null,
            new ArticleStatus(id, true, false, DateTime.UtcNow));
        await account.Database.UpdateArticlesAsync([Article("navigation-a", firstFeed), Article("navigation-b", firstFeed), Article("navigation-c", otherFeed)]);
        var back = Descendants(window.Content).OfType<Button>().Single(button => button.Name == "NavigateBackButton");
        async Task Page(string suffix, int depth)
        {
            try
            {
                await WaitFor(() => Task.FromResult(content.ProbeWebPageReady
                    && content.ProbeWebPageView.CoreWebView2.Source == serverUrl + suffix
                    && content.ProbeWebPageHistoryCount == depth), "Embedded page " + suffix + " at history depth " + depth);
            }
            catch (TimeoutException)
            {
                File.WriteAllText(Path.Combine(OutputDirectory, "navigation-failure.json"), JsonSerializer.Serialize(new
                {
                    Expected = suffix, Depth = depth, content.ProbeShowingWebPage, content.ProbeWebPageReady,
                    content.ProbeWebPageHistoryCount, Source = content.ProbeWebPageView.CoreWebView2?.Source, model.StatusMessage,
                    History = await content.ProbeWebPageView.CoreWebView2!.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}")
                }, new JsonSerializerOptions { WriteIndented = true }));
                await SaveWindowScreenshotAsync(window, "navigation-failure");
                throw;
            }
        }
        async Task Reader(string id)
        {
            await WaitFor(() => Task.FromResult(!content.ProbeShowingWebPage && model.SelectedArticle?.Id == id && content.ProbeArticleReady),
                "Restored article " + id);
        }
        static async Task Click(CoreWebView2 core, string id)
        {
            using var tree = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
            string frameId = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
            using var world = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
                JsonSerializer.Serialize(new { frameId, worldName = "WinNewsWire.LinkClickProbe" })));
            using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new
                {
                    contextId = world.RootElement.GetProperty("executionContextId").GetInt32(),
                    expression = "new Promise(resolve => { const e = document.getElementById(" + JsonSerializer.Serialize(id)
                        + "); e.scrollIntoView({behavior:'instant',block:'center'}); requestAnimationFrame(()=>requestAnimationFrame(()=>{"
                        + "const r=e.getBoundingClientRect(),x=r.x+r.width/2,y=r.y+r.height/2;"
                        + "resolve({x,y,hit:document.elementFromPoint(x,y)?.closest('a')===e}); })); })",
                    awaitPromise = true, returnByValue = true
                })).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            var bounds = result.RootElement.GetProperty("result").GetProperty("value");
            Require(bounds.GetProperty("hit").GetBoolean(), "The browser-link fixture is not visible and hit-testable.");
            double x = bounds.GetProperty("x").GetDouble(), y = bounds.GetProperty("y").GetDouble();
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x, y }));
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = "left", clickCount = 1 }));
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }));
        }
        try
        {
            Browser.ProbeLaunch = launches.Add;
            content.ProbeShareWriter = package => shared = package.GetView();
            AppDefaults.Shared.ArticleContentJavascriptEnabled = false;
            AppDefaults.Shared.UseColumnLayout = false;
            model.IsReaderMode = false;
            model.IsUnifiedLayout = false;
            model.IsSidebarVisible = true;
            model.SearchQuery = "";
            model.BuildSidebar();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, firstFeed.FeedID);
            if (model.IsReadFiltered) await model.ToggleReadFilterCommand.ExecuteAsync(null);
            await model.ProbeDrainViewUpdatesAsync();
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            await WaitFor(() => Task.FromResult(model.ArticleItems.Any(item => item.Id == "navigation-a")
                && model.ArticleItems.Any(item => item.Id == "navigation-b")), "Navigation article fixtures");
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "navigation-a");
            await Reader("navigation-a");
            int originalHistoryCount = content.ProbeBackHistoryCount;
            other = App.OpenMainWindow();
            Require(await other.Restoration.WaitAsync(TimeSpan.FromSeconds(30)), "The independent navigation window did not restore.");
            Require(!other.ProbeContent.CanGoBack && content.ProbeBackHistoryCount == originalHistoryCount,
                "New windows inherited another window's navigation history.");
            await CloseWindowAsync(other);
            other = null;
            window.Activate();
            var rows = model.ArticleItems;
            var sidebarRows = SidebarList(content).ItemsSource;
            int sidebarChanges = 0;
            void Changed(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => sidebarChanges++;
            ((System.Collections.Specialized.INotifyCollectionChanged)sidebarRows).CollectionChanged += Changed;
            try
            {
                model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "navigation-b");
                await Reader("navigation-b");
                Require(back.IsEnabled && TitleBarPassesPointerInput(window, back), "Back is disabled or captured by title-bar dragging.");
                InvokeButton(back);
                await Reader("navigation-a");
                Require(ReferenceEquals(rows, model.ArticleItems) && sidebarChanges == 0,
                    "Article Back rebuilt the timeline or sidebar rather than changing selection in place.");
            }
            finally { ((System.Collections.Specialized.INotifyCollectionChanged)sidebarRows).CollectionChanged -= Changed; }

            model.SearchQuery = "Navigation";
            await model.FilterArticlesAsync();
            await Reader("navigation-a");
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, otherFeed.FeedID);
            await model.FilterArticlesAsync();
            Require(model.SelectedArticle is null && model.SearchQuery == "", "Changing feeds did not clear the old article and search.");
            await content.GoBackAsync();
            await Reader("navigation-a");
            Require(model.SelectedSidebarItem?.Tag == firstFeed && model.SearchQuery == "Navigation"
                && Descendants(window.Content).OfType<AutoSuggestBox>().Single(box => box.Name == "TitleBarSearchBox").Text == "Navigation",
                "Feed Back lost its article, search scope or visible search text.");
            model.SetSelectedArticles(model.ArticleItems.Where(item => item.Id is "navigation-a" or "navigation-b"));
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, otherFeed.FeedID);
            await model.FilterArticlesAsync();
            await content.GoBackAsync();
            Require(model.SelectedArticles.Select(item => item.Id).ToHashSet().SetEquals(["navigation-a", "navigation-b"]),
                "Back lost an independent multi-article selection.");
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "navigation-a");
            await Reader("navigation-a");

            Require(content.SaveToolbarConfiguration(ToolbarConfiguration.Default.WithHeaderItems(
                ["share", "openInWebView", "openInBrowser"])) is null, "Could not configure the web commands.");
            await content.ProbeWebView.ExecuteScriptAsync("window.readerHistorySentinel='preserved'; window.scrollTo(0,260);");
            InvokeButton((Button)content.FindName("OpenInWebViewButton"));
            await Page("/navigation-first", 1);
            var web = content.ProbeWebPageView.CoreWebView2;
            Require(web.Settings.IsScriptEnabled && !content.ProbeWebView.CoreWebView2.Settings.IsScriptEnabled,
                "Website script settings leaked into the stored article.");
            Require(JsonSerializer.Deserialize<bool>(await web.ExecuteScriptAsync(
                "window.publisherPageScriptRan === true && typeof window.__winNewsWireReaderMessage === 'undefined' && typeof window.processPage === 'undefined'")),
                "The website lost its own scripts or received privileged article-reader bindings.");
            await web.ExecuteScriptAsync("document.getElementById('page-input').value='preserved form value';");
            await Click(web, "next-page");
            await Page("/navigation-second", 2);
            await content.ShowArticleFindAsync();
            ((TextBox)content.FindName("ArticleFindText")).Text = "Publisher page";
            await WaitFor(() => Task.FromResult(content.ProbeArticleFind?.MatchCount > 0), "Find in the displayed webpage");
            content.HideArticleFind();
            await content.GoBackAsync();
            await Page("/navigation-first", 1);
            Require(JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("document.getElementById('page-input').value")) == "preserved form value",
                "Webpage Back reloaded a URL instead of restoring browser history/form state.");
            await web.ExecuteScriptAsync("history.pushState({},'', '#one');");
            await Page("/navigation-first#one", 2);
            await web.ExecuteScriptAsync("history.replaceState({},'', '#renamed');");
            await Page("/navigation-first#renamed", 2);
            await web.ExecuteScriptAsync("history.pushState({},'', '#two');");
            await Page("/navigation-first#two", 3);
            await content.GoBackAsync();
            await Page("/navigation-first#renamed", 2);
            await content.GoBackAsync();
            await Page("/navigation-first", 1);
            await content.GoBackAsync();
            await Reader("navigation-a");
            Require(await ScriptString(content, "window.readerHistorySentinel") == "preserved"
                && JsonSerializer.Deserialize<double>(await content.ProbeWebView.ExecuteScriptAsync("window.scrollY")) >= 250,
                "Returning from a webpage replaced the article document or lost its reading position.");

            await content.ProbeWebView.ExecuteScriptAsync(
                "const a=document.createElement('a'); a.id='default-web-link'; a.href=" + JsonSerializer.Serialize(serverUrl + "/navigation-first")
                + "; a.textContent='Open link'; document.body.prepend(a);");
            await content.ProbeWebView.ExecuteScriptAsync("document.getElementById('default-web-link').click();");
            await WaitFor(() => Task.FromResult(model.StatusMessage.Contains("automatic article navigation", StringComparison.Ordinal)),
                "Synthetic article-link activation blocked");
            Require(!content.ProbeShowingWebPage, "A synthetic click invoked the reader's privileged link handler.");
            await Click(content.ProbeWebView.CoreWebView2, "default-web-link");
            await Page("/navigation-first", 1);
            await Click(web, "popup-page");
            await Page("/navigation-second", 2);
            Require(launches.Count == 0, "Default links or target=_blank links escaped to an external browser.");
            content.OpenSelectedInBrowser();
            await WaitFor(() => Task.FromResult(launches.Count == 1), "Explicit external browser command");
            Require(launches.Single().Url.AbsoluteUri == serverUrl + "/navigation-second", "Open in Browser used the original article rather than the displayed page.");
            content.ShareSelected();
            Require(shared is not null && (await shared.GetWebLinkAsync()).AbsoluteUri == serverUrl + "/navigation-second",
                "Share used the original article rather than the displayed webpage.");
            await content.GoBackAsync();
            await Page("/navigation-first", 1);
            await content.GoBackAsync();
            await Reader("navigation-a");

            await content.OpenSelectedInWebViewAsync();
            await Page("/navigation-first", 1);
            await Click(web, "next-page");
            await Page("/navigation-second", 2);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "navigation-b");
            await Reader("navigation-b");
            await content.OpenSelectedInWebViewAsync();
            await Page("/navigation-first", 1);
            await content.GoBackAsync();
            await Reader("navigation-b");
            await content.GoBackAsync();
            await Page("/navigation-second", 2);
            await content.GoBackAsync();
            await Page("/navigation-first", 1);
            await content.GoBackAsync();
            await Reader("navigation-a");

            await content.OpenSelectedInWebViewAsync();
            await Page("/navigation-first", 1);
            bool blocked = false;
            void NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
            {
                if (args.Uri.StartsWith("data:", StringComparison.Ordinal)) blocked = args.Cancel;
            }
            web.NavigationStarting += NavigationStarting;
            try
            {
                web.Navigate("data:text/html,Unrequested page");
                await WaitFor(() => Task.FromResult(blocked), "Unsupported webpage navigation blocked");
                Require(web.Source == serverUrl + "/navigation-first", "An unsupported document replaced the webpage.");
            }
            finally { web.NavigationStarting -= NavigationStarting; }
            web.Navigate(serverUrl + "/navigation-redirect");
            await Page("/navigation-second", 2);
            await content.GoBackAsync();
            await Page("/navigation-first", 1);
            web.Navigate("http://127.0.0.1:1/unavailable");
            await WaitFor(() => Task.FromResult(((InfoBar)content.FindName("WebPageError")).IsOpen), "Visible webpage load error");
            await content.GoBackAsync();
            await Page("/navigation-first", 1);
            await content.GoBackAsync();
            await Reader("navigation-a");
            await SaveWindowScreenshotAsync(window, "navigation-reader-restored");
            return new { ArticleBackWithoutCollectionRebuild = true, FeedAndSearchBack = true, NativePageHistory = true,
                FormStatePreserved = true, PushAndReplaceState = true, DefaultLinksEmbedded = true, NewWindowsEmbedded = true,
                InterleavedReaderAndWebHistory = true, IsolatedArticleRenderer = true, VisiblePageFind = true,
                UnsupportedDocumentsBlocked = true, SyntheticClicksBlocked = true, Redirects = true, FailedPageBack = true,
                IndependentWindowHistories = true, VisiblePageSharing = true };
        }
        finally
        {
            Browser.ProbeLaunch = originalLauncher;
            content.ProbeShareWriter = null;
            if (other is not null) await CloseWindowAsync(other);
            AppDefaults.Shared.ArticleContentJavascriptEnabled = originalScripts;
            AppDefaults.Shared.UseColumnLayout = originalColumn;
            content.SaveToolbarConfiguration(originalToolbar);
            account.RemoveFeed(firstFeed);
            account.RemoveFeed(otherFeed);
            await account.RecalculateUnreadCountsAsync();
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            model.SearchQuery = originalSearch;
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendWindowMessage(IntPtr window, uint message, IntPtr parameter, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadProbeIcon(IntPtr instance, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static extern int GetIconMetric(int metric);
    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyProbeIcon(IntPtr icon);
    [DllImport("user32.dll", EntryPoint = "DrawIconEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawProbeIcon(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);

    private static object CheckWindowIcons(MainWindow window)
    {
        byte[] Pixels(IntPtr icon, int width, int height)
        {
            var info = new CaptureBitmapInfo { Size = (uint)Marshal.SizeOf<CaptureBitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var dc = CreateCompatibleDC(IntPtr.Zero);
            Require(dc != IntPtr.Zero, "Could not allocate the icon capture DC.");
            IntPtr bitmap = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
                Require(bitmap != IntPtr.Zero, "Could not allocate the icon capture bitmap.");
                var result = new byte[width * height * 4];
                Marshal.Copy(result, 0, bits, result.Length);
                previous = SelectObject(dc, bitmap);
                Require(DrawProbeIcon(dc, 0, 0, icon, width, height, 0, IntPtr.Zero, 3), "Could not render the registered window icon.");
                Marshal.Copy(bits, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                DeleteDC(dc);
            }
        }
        foreach (var (kind, file, widthMetric, heightMetric) in new[] { (1, "wnw.ico", 11, 12), (0, "wnw-flat.ico", 49, 50) })
        {
            int width = GetIconMetric(widthMetric), height = GetIconMetric(heightMetric);
            var actual = SendWindowMessage(WindowNative.GetWindowHandle(window), 0x007F, new IntPtr(kind), IntPtr.Zero);
            var expected = LoadProbeIcon(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", file), 1, width, height, 0x10);
            try
            {
                Require(actual != IntPtr.Zero && expected != IntPtr.Zero, "A required native window icon is missing.");
                Require(Pixels(actual, width, height).SequenceEqual(Pixels(expected, width, height)),
                    "The registered " + (kind == 1 ? "taskbar" : "small window") + " icon does not match " + file + ".");
            }
            finally { if (expected != IntPtr.Zero) DestroyProbeIcon(expected); }
        }
        return new { NativeLargeIcon = "wnw.ico", NativeSmallIcon = "wnw-flat.ico", RenderedPixelsMatched = true };
    }

    private static async Task<object?> CheckBrowserAsync(MainWindow window, string serverUrl)
    {
        string? previousBrowser = AppDefaults.Shared.DefaultBrowserID;
        bool previousBackground = AppDefaults.Shared.OpenInBrowserInBackground;
        var original = window.CaptureState();
        var directory = Path.Combine(AppConfig.DataDirectory, "browser-fixtures");
        var firstDirectory = Path.Combine(directory, "A");
        var secondDirectory = Path.Combine(directory, "B");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstPath = Path.Combine(firstDirectory, "browser.exe");
        var secondPath = Path.Combine(secondDirectory, "browser.exe");
        File.WriteAllText(firstPath, "Probe fixture; never executed.");
        File.WriteAllText(secondPath, "Probe fixture; never executed.");
        var first = new Web.WindowsWebBrowser("Example Browser", firstPath, "first-browser");
        var second = new Web.WindowsWebBrowser("Example Browser", secondPath, "second-browser");
        var requests = new List<Web.BrowserLaunchRequest>();
        var installed = Web.WindowsWebBrowser.SortedBrowsers();
        Require(installed.Select(browser => browser.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == installed.Count,
            "Installed-browser discovery duplicated an executable.");
        Browser.ProbeBrowsers = [second, first, first];
        Browser.ProbeLaunch = requests.Add;
        AppWindows.PreferencesWindow? preferences = null;
        try
        {
            AppDefaults.Shared.DefaultBrowserID = null;
            AppDefaults.Shared.OpenInBrowserInBackground = false;
            preferences = new AppWindows.PreferencesWindow();
            preferences.Activate();
            await WaitFor(() => Task.FromResult(preferences.Content is FrameworkElement { IsLoaded: true }), "Browser preferences");
            var combo = Descendants(preferences.Content).OfType<ComboBox>().Single(control => control.Name == "BrowserCombo");
            Require(combo.Items.Count == 3 && combo.SelectedIndex == 0, "Browser preferences did not include the system default and unique browser paths.");
            var choices = combo.Items.OfType<ComboBoxItem>().Where(item => item.Tag is string).ToArray();
            Require(choices.Select(item => item.Content?.ToString()).Distinct().Count() == 2
                && choices.All(item => item.Content?.ToString()?.StartsWith("Example Browser - ", StringComparison.Ordinal) == true),
                "Duplicate browser names were not disambiguated by path.");
            combo.SelectedItem = choices.Single(item => item.Tag as string == secondPath);
            var background = Descendants(preferences.Content).OfType<ToggleSwitch>().Single(control => control.Name == "OpenBackgroundToggle");
            background.IsOn = true;
            Require(AppDefaults.Shared.DefaultBrowserID == secondPath && AppDefaults.Shared.OpenInBrowserInBackground,
                "Browser preferences were not persisted.");
            preferences.Close();
            preferences = new AppWindows.PreferencesWindow();
            preferences.Activate();
            await WaitFor(() => Task.FromResult(preferences.Content is FrameworkElement { IsLoaded: true }), "Reopened browser preferences");
            combo = Descendants(preferences.Content).OfType<ComboBox>().Single(control => control.Name == "BrowserCombo");
            Require((combo.SelectedItem as ComboBoxItem)?.Tag as string == secondPath, "Reopening preferences lost the selected browser.");
            preferences.Close();
            preferences = null;
            window.Activate();
            var content = window.ProbeContent;
            var model = content.ViewModel;
            var account = AccountManager.Shared.DefaultAccount!;
            var feed = account.FlattenedFeeds().Single();
            model.IsReaderMode = false;
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.FilterArticlesAsync();
            await WaitFor(() => Task.FromResult(model.ArticleItems.Any(item => item.Id == "probe-article")), "Browser article selection");
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "probe-article");
            await WaitFor(async () => content.ProbeArticleReady && await ScriptBool(content, "!!document.getElementById('probe-body')"), "Browser article document");
            InvokeButton((Button)content.FindName("OpenInBrowserButton"));
            await WaitFor(() => Task.FromResult(requests.Count == 1), "Selected-browser toolbar command");
            Require(requests[0].BrowserPath == secondPath && requests[0].InBackground, "The toolbar ignored browser selection or background preference.");
            content.OpenSelectedInBrowser(invertPreference: true);
            await WaitFor(() => Task.FromResult(requests.Count == 2), "Inverted browser command");
            Require(!requests[1].InBackground && requests[1].BrowserPath == secondPath, "The inverted command did not request foreground opening.");
            string link = serverUrl + "/external-browser-link";
            await content.ProbeWebView.ExecuteScriptAsync("""
                var anchor = document.createElement('a');
                anchor.id = 'browser-link-probe';
                anchor.textContent = 'External link';
                anchor.style.cssText = 'position:fixed;top:10px;left:10px;width:160px;height:30px;z-index:10000;background:white';
                document.body.appendChild(anchor);
                window.browserNavigationProbe = 'preserved';
                """ + "anchor.href = " + JsonSerializer.Serialize(link) + ";");
            var core = content.ProbeWebView.CoreWebView2;
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                """{"type":"mousePressed","x":20,"y":20,"button":"left","clickCount":1}""");
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                """{"type":"mouseReleased","x":20,"y":20,"button":"left","clickCount":1}""");
            try
            {
                await WaitFor(() => Task.FromResult(content.ProbeWebPageReady && content.ProbeWebPageView.CoreWebView2.Source == link), "Embedded article link");
            }
            catch (TimeoutException)
            {
                File.WriteAllText(Path.Combine(OutputDirectory, "browser-link-failure.json"), JsonSerializer.Serialize(new
                {
                    content.ProbeArticleReady, content.ProbeShowingWebPage, content.ProbeWebPageReady, model.StatusMessage,
                    Source = content.ProbeWebPageView.CoreWebView2?.Source,
                    Document = await core.ExecuteScriptAsync(
                        "({anchor:!!document.getElementById('browser-link-probe'),target:document.elementFromPoint(20,20)?.id,sentinel:window.browserNavigationProbe})")
                }, new JsonSerializerOptions { WriteIndented = true }));
                throw;
            }
            Require(requests.Count == 2, "An ordinary article link still launched an external browser.");
            Require(await ScriptString(content, "window.browserNavigationProbe") == "preserved", "The article link replaced the in-app document.");
            content.OpenSelectedInBrowser();
            await WaitFor(() => Task.FromResult(requests.Count == 3), "Explicit browser command for the embedded page");
            Require(requests[2].Url.AbsoluteUri == link && requests[2].BrowserPath == secondPath,
                "Open in Browser did not use the visible webpage and selected browser.");
            await content.GoBackAsync();
            Require(!content.ProbeShowingWebPage, "Back did not return from the web page to the article.");
            await content.ProbeWebView.ExecuteScriptAsync("location.href = " + JsonSerializer.Serialize(serverUrl + "/automatic-redirect") + ";");
            await WaitFor(() => Task.FromResult(model.StatusMessage.Contains("automatic article navigation", StringComparison.Ordinal)), "Blocked automatic article navigation");
            Require(requests.Count == 3 && await ScriptString(content, "window.browserNavigationProbe") == "preserved",
                "A publisher-initiated redirect opened an external browser or replaced the article.");
            await Browser.OpenAsync(new Uri("mailto:reader@example.test"));
            Require(requests[^1].BrowserPath is null, "Email links did not use the system mail handler.");
            File.Delete(secondPath);
            content.OpenSelectedInBrowser();
            await WaitFor(() => Task.FromResult(requests.Count == 5), "Removed-browser fallback");
            Require(requests[^1].BrowserPath is null && requests[^1].SelectedBrowserUnavailable
                && model.StatusMessage.Contains("selected browser is unavailable", StringComparison.Ordinal),
                "An unavailable selected browser was not reported when falling back to Windows.");
            return new { InstalledBrowsersRead = installed.Count, DuplicateNames = true, PreferenceRoundTrip = true,
                SelectedBrowserUsed = true, BackgroundInversion = true, ExternalNavigation = true, AutomaticNavigationBlocked = true,
                NoBrowserProcessLaunched = true };
        }
        finally
        {
            preferences?.Close();
            Browser.ProbeLaunch = null;
            Browser.ProbeBrowsers = null;
            AppDefaults.Shared.DefaultBrowserID = previousBrowser;
            AppDefaults.Shared.OpenInBrowserInBackground = previousBackground;
            await window.ProbeContent.RestoreStateAsync(original);
            File.Delete(firstPath);
            File.Delete(secondPath);
            Directory.Delete(firstDirectory);
            Directory.Delete(secondDirectory);
            Directory.Delete(directory);
            window.Activate();
        }
    }

    private static async Task<object?> CheckUrlCopyCommandsAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var originalItems = model.ArticleItems;
        var copied = new List<Windows.ApplicationModel.DataTransfer.DataPackageView>();
        var bar = Descendants(window.Content).OfType<MenuBar>().Single(control => control.Name == "AppMenuBar");
        var edit = bar.Items.Single(item => item.Title == "Edit");
        var articleMenu = edit.Items.OfType<MenuFlyoutItem>().Single(item => item.Name == "MenuCopyArticleUrl");
        var externalMenu = edit.Items.OfType<MenuFlyoutItem>().Single(item => item.Name == "MenuCopyExternalUrl");
        static void Invoke(MenuFlyoutItem item)
        {
            var peer = FrameworkElementAutomationPeer.FromElement(item) ?? FrameworkElementAutomationPeer.CreatePeerForElement(item);
            (peer?.GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("The URL copy command has no Invoke pattern.")).Invoke();
        }
        try
        {
            content.ProbeClipboardWriter = package => copied.Add(package.GetView());
            var first = new Models.FeedItem { Id = "copy-first", Link = serverUrl + "/first", ExternalLink = serverUrl + "/external", IsRead = true };
            var second = new Models.FeedItem { Id = "copy-second", Link = serverUrl + "/second", IsRead = true };
            var fallback = new Models.FeedItem { Id = "copy-fallback", Link = serverUrl + "/fallback", ExternalLink = serverUrl + "/fallback", IsRead = true };
            var empty = new Models.FeedItem { Id = "copy-empty", IsRead = true };
            var otherEmpty = new Models.FeedItem { Id = "copy-other-empty", IsRead = true };
            model.ArticleItems = new([first, second, fallback, empty, otherEmpty]);
            model.SetSelectedArticles([empty, fallback, second, first]);
            Require(articleMenu.IsEnabled && externalMenu.IsEnabled, "Multi-selection did not enable both URL commands.");
            Invoke(articleMenu);
            Invoke(externalMenu);
            Require(copied.Count == 2, "The URL menu commands did not publish two clipboard payloads.");
            Require(await copied[0].GetTextAsync() == string.Join(Environment.NewLine, first.Link, second.Link, fallback.Link),
                "Article URL copying lost timeline order, fallback links or missing-link filtering.");
            Require(await copied[1].GetTextAsync() == string.Join(Environment.NewLine, first.ExternalLink, fallback.ExternalLink),
                "External URL copying substituted an article URL or omitted an equal external URL.");
            var articleKey = articleMenu.KeyboardAccelerators.Single();
            var externalKey = externalMenu.KeyboardAccelerators.Single();
            Require(articleKey.Key == global::Windows.System.VirtualKey.C
                && articleKey.Modifiers == (global::Windows.System.VirtualKeyModifiers.Control | global::Windows.System.VirtualKeyModifiers.Shift)
                && externalKey.Key == global::Windows.System.VirtualKey.C
                && externalKey.Modifiers == (global::Windows.System.VirtualKeyModifiers.Control | global::Windows.System.VirtualKeyModifiers.Menu),
                "The URL copy command modifiers differ from their documented Windows shortcuts.");
            model.SetSelectedArticles([second, empty]);
            Require(articleMenu.IsEnabled && !externalMenu.IsEnabled, "External URL copying was enabled for article-only links.");
            content.ProbeClipboardWriter = _ => throw new COMException("Clipboard is busy.");
            Invoke(articleMenu);
            Require(model.StatusMessage.StartsWith("Could not copy to the clipboard:", StringComparison.Ordinal),
                "A clipboard failure was not reported.");
            model.SetSelectedArticles([empty, otherEmpty]);
            Require(!articleMenu.IsEnabled && !externalMenu.IsEnabled, "A linkless selection enabled URL copying.");
            model.SetSelectedArticles([]);
            Require(!articleMenu.IsEnabled && !externalMenu.IsEnabled, "An empty selection enabled URL copying.");
            return new { MultiSelection = true, DistinctUrlKinds = true, TimelineOrder = true, MenuEnablement = true,
                ClipboardErrors = true, SystemClipboardUntouched = true };
        }
        finally
        {
            content.ProbeClipboardWriter = null;
            model.ArticleItems = originalItems;
            await content.RestoreStateAsync(original);
        }
    }

    private static void InvokeMenuItem(MenuFlyoutItem item)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(item) ?? FrameworkElementAutomationPeer.CreatePeerForElement(item);
        (peer?.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new InvalidOperationException("The menu command has no Invoke pattern.")).Invoke();
    }

    private static async Task<object?> CheckToolbarControlsAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var configuration = content.CurrentToolbarConfiguration;
        var originalSearch = model.SearchQuery;
        ContentDialog? dialog = null;
        var button = (Button)content.FindName("ToolbarMarkReadButton");
        var icon = (PathIcon)content.FindName("ToolbarReadStatusIcon");
        static bool MatchesReadIcon(PathIcon icon, bool markUnread)
            => icon.Data is PathGeometry { FillRule: FillRule.Nonzero } geometry
                && geometry.Figures.Count == (markUnread ? 5 : 6);
        var toolbar = (FrameworkElement)content.FindName("MainToolbar");
        double toolbarWidth = toolbar.Width;
        bool columnLayout = AppDefaults.Shared.UseColumnLayout;
        var overflowButton = (Button)content.FindName("ToolbarOverflowButton");
        var overflow = (MenuFlyout)overflowButton.Flyout;
        try
        {
            var help = Descendants(window.Content).OfType<MenuBar>().Single(bar => bar.Name == "AppMenuBar")
                .Items.Single(item => item.Title == "Help");
            Require(!help.Items.OfType<MenuFlyoutItem>().Any(item => item.Name == "MenuAddUpstreamNews"
                || item.Text == "Add NetNewsWire News Feed"), "Help still exposes the upstream subscription command.");
            AppDefaults.Shared.UseColumnLayout = false;
            model.IsUnifiedLayout = false;
            model.SearchQuery = "";
            var account = AccountManager.Shared.DefaultAccount!;
            var feed = account.FlattenedFeeds().Single();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "probe-article");
            await model.PendingAutomaticReads;
            await WaitFor(() => Task.FromResult(model.SelectedArticle.IsRead && content.ProbeArticleReady), "Read article toolbar");
            Require(content.SaveToolbarConfiguration(ToolbarConfiguration.Default) is null, "Default toolbar could not be restored.");
            var windowRoot = (UIElement)window.Content;
            Require(windowRoot.KeyboardAcceleratorPlacementMode == Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden
                && windowRoot.KeyboardAccelerators.Any(accelerator => accelerator.Key == global::Windows.System.VirtualKey.F11 && accelerator.IsEnabled),
                "The window-wide F11 shortcut still exposes an automatic hover tooltip or was disabled.");
            window.Activate();
            SetForegroundWindow(WindowNative.GetWindowHandle(window));
            await WaitFor(() => Task.FromResult(GetForegroundWindow() == WindowNative.GetWindowHandle(window)), "Owned toolbar keyboard focus");
            ((ListView)content.FindName("ArticleListView")).Focus(FocusState.Programmatic);
            bool wasFullScreen = window.IsFullScreen;
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.F11);
            await WaitFor(() => Task.FromResult(window.IsFullScreen != wasFullScreen), "F11 fullscreen entry");
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.F11);
            await WaitFor(() => Task.FromResult(window.IsFullScreen == wasFullScreen), "F11 fullscreen return");
            Require(MatchesReadIcon(icon, markUnread: true) && icon.Width == 16 && icon.Height == 16
                && AutomationProperties.GetName(button) == AppStrings.Get("ToolbarMarkUnread"), "Mark Unread does not use the textbox icon.");
            var bulkIcon = Descendants((Button)content.FindName("MarkAllReadButton")).OfType<FontIcon>().Single();
            Require(bulkIcon.Glyph == "\uE73E" && bulkIcon.FontSize == 16,
                "Mark All Read does not use a plain checkmark.");
            await SaveWindowScreenshotAsync(window, "toolbar-mark-unread-textbox");
            InvokeButton(button);
            await WaitFor(() => Task.FromResult(!model.SelectedArticle.IsRead
                && MatchesReadIcon(icon, markUnread: false)), "Unread article and mark-read textbox checkmark");
            Require(AutomationProperties.GetName(button) == AppStrings.Get("Toolbar_markRead"), "The read action lost its accessible label.");
            await SaveWindowScreenshotAsync(window, "toolbar-mark-read-textbox-checkmark");
            InvokeButton(button);
            await WaitFor(() => Task.FromResult(model.SelectedArticle.IsRead
                && MatchesReadIcon(icon, markUnread: true)), "Read toggle restored");
            ((ListView)content.FindName("ArticleListView")).Focus(FocusState.Programmatic);
            model.IsUnifiedLayout = true;
            await WaitFor(() => Task.FromResult(((Grid)content.FindName("ToolbarItemsPanel")).Children
                .Contains((Button)content.FindName("MarkAllReadButton"))), "Combined reading toolbar");
            toolbar.Width = 36;
            toolbar.UpdateLayout();
            overflow.ShowAt(overflowButton);
            await WaitFor(() => Task.FromResult(overflow.Items.OfType<MenuFlyoutItem>().Any(item =>
                item.Text == AppStrings.Get("ToolbarMarkUnread") && item.Icon is PathIcon path
                    && MatchesReadIcon(path, markUnread: true) && !ReferenceEquals(path.Data, icon.Data)
                    && path.Width == icon.Width && path.Height == icon.Height)),
                "Unread status icon in overflow");
            Require(overflow.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == AppStrings.Get("Toolbar_markAllAsRead")
                && item.Icon is FontIcon { Glyph: "\uE73E" } copy && !ReferenceEquals(copy, bulkIcon)),
                "Mark All Read lost its independent plain checkmark in overflow.");
            overflow.Hide();
            await WaitFor(() => Task.FromResult(!VisualTreeHelper.GetOpenPopupsForXamlRoot(content.XamlRoot)
                .SelectMany(popup => Descendants(popup.Child)).OfType<MenuFlyoutPresenter>().Any()), "Read-status overflow closed");
            toolbar.Width = toolbarWidth;
            model.IsUnifiedLayout = false;
            toolbar.UpdateLayout();

            var customization = content.CustomizeToolbarAsync();
            await WaitFor(() => Task.FromResult(content.ProbeToolbarDialog?.IsLoaded == true), "Toolbar customization layout");
            dialog = content.ProbeToolbarDialog!;
            dialog.FontSize = 22;
            var outer = (ScrollViewer)dialog.Content;
            outer.Height = 300;
            var list = Descendants(dialog).OfType<ListView>().Single();
            outer.UpdateLayout();
            var inner = Descendants(list).OfType<ScrollViewer>().Single();
            await WaitFor(() => Task.FromResult(outer.ScrollableHeight > 0 && inner.ScrollableHeight > 0), "Both customization scrollbars");
            await SaveWindowScreenshotAsync(window, "toolbar-customization-gutters");
            static ScrollViewer? Owner(DependencyObject element)
            {
                for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                    if (parent is ScrollViewer viewer) return viewer;
                return null;
            }
            static global::Windows.Foundation.Rect Bounds(FrameworkElement element, UIElement root)
                => element.TransformToVisual(root).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
            var outerBar = Descendants(outer).OfType<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>()
                .Single(bar => bar.Orientation == Orientation.Vertical && ReferenceEquals(Owner(bar), outer));
            var innerBar = Descendants(inner).OfType<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>()
                .Single(bar => bar.Orientation == Orientation.Vertical && ReferenceEquals(Owner(bar), inner));
            Require(Bounds((FrameworkElement)outer.Content, outer).Right <= Bounds(outerBar, outer).Left - 2,
                "Dialog content overlaps the outer scrollbar.");
            var rows = Enumerable.Range(0, list.Items.Count).Select(index => list.ContainerFromIndex(index)).OfType<ListViewItem>().ToArray();
            Require(rows.Length == list.Items.Count, "The bounded command list did not realize its rows.");
            foreach (var label in rows.SelectMany(Descendants).OfType<TextBlock>().Where(text => !string.IsNullOrEmpty(text.Text)))
                Require(Bounds(label, inner).Right <= Bounds(innerBar, inner).Left - 2,
                    $"Command '{label.Text}' ends at {Bounds(label, inner).Right}; its scrollbar starts at {Bounds(innerBar, inner).Left}.");
            Require(ReferenceEquals(list.ItemsPanel, Application.Current.Resources["ToolbarCommandItemsPanel"]),
                "Customization reintroduced a virtualized command list.");
            InvokeDialogButton(dialog, "CloseButton");
            await customization;
            dialog = null;
            Require(content.CurrentToolbarConfiguration.Items.SequenceEqual(ToolbarConfiguration.Default.Items), "Cancelling customization changed commands.");
            return new { UnreadTextbox = true, ReadTextboxCheckmark = true, MatchedIconSize = true,
                NoWindowShortcutTooltip = true, FullScreenShortcutVerified = true, TogglePreserved = true, OverflowIcon = true,
                BulkReadCheckmark = true, InnerAndOuterScrollbarGutters = true, LargerText = true, NonVirtualizedCommands = true };
        }
        finally
        {
            dialog?.Hide();
            overflow.Hide();
            toolbar.Width = toolbarWidth;
            content.ProbeLayoutToolbar(double.NaN);
            content.SaveToolbarConfiguration(configuration);
            AppDefaults.Shared.UseColumnLayout = columnLayout;
            await content.RestoreStateAsync(original);
            model.SearchQuery = originalSearch;
        }
    }

    private static async Task<object?> CheckGoAndHelpCommandsAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        string originalSearch = model.SearchQuery;
        var bar = Descendants(window.Content).OfType<MenuBar>().Single(control => control.Name == "AppMenuBar");
        var go = bar.Items.Single(item => item.Title == "Go");
        var help = bar.Items.Single(item => item.Title == "Help");
        try
        {
            model.IsSidebarVisible = true;
            var smart = AppShared.SmartFeeds.SmartFeedsController.Shared;
            foreach (var (name, feed, key) in new[]
            {
                ("MenuGoToday", smart.TodayFeed, global::Windows.System.VirtualKey.Number1),
                ("MenuGoUnread", smart.UnreadFeed, global::Windows.System.VirtualKey.Number2),
                ("MenuGoStarred", smart.StarredFeed, global::Windows.System.VirtualKey.Number3),
                ("MenuGoStarred", smart.StarredFeed, global::Windows.System.VirtualKey.Number3)
            })
            {
                model.SearchQuery = "query that should be cleared";
                var item = go.Items.OfType<MenuFlyoutItem>().Single(item => item.Name == name);
                var shortcut = item.KeyboardAccelerators.Single();
                Require(shortcut.Key == key && shortcut.Modifiers == global::Windows.System.VirtualKeyModifiers.Control,
                    "A Go command has the wrong Windows shortcut.");
                InvokeMenuItem(item);
                await WaitFor(() => Task.FromResult(model.SearchQuery == ""
                    && ReferenceEquals(model.SelectedSidebarItem?.Tag, feed) && model.ArticleListTitle == feed.NameForDisplay), "Go smart scope");
                var sidebar = (ListView)content.FindName("SidebarListView");
                await WaitFor(() => Task.FromResult(Descendants(sidebar)
                    .Contains(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(content.XamlRoot) as DependencyObject)), "Go sidebar focus");
            }

            string? openedFolder = null;
            window.ProbeOpenFolder = folder => { openedFolder = folder.Path; return Task.FromResult(true); };
            var support = help.Items.OfType<MenuFlyoutItem>().Single(item => item.Name == "MenuOpenSupportFolder");
            InvokeMenuItem(support);
            await WaitFor(() => Task.FromResult(openedFolder is not null), "Application support folder request");
            Require(string.Equals(Path.GetFullPath(openedFolder!), Path.GetFullPath(AppConfig.DataDirectory), StringComparison.OrdinalIgnoreCase),
                "The support command opened a directory outside the current profile.");
            window.ProbeOpenFolder = _ => Task.FromResult(false);
            InvokeMenuItem(support);
            await WaitFor(() => Task.FromResult(model.StatusMessage.StartsWith("Could not open the application support folder:", StringComparison.Ordinal)),
                "Failed folder opening");

            Require(!help.Items.OfType<MenuFlyoutItem>().Any(item => item.Name == "MenuAddUpstreamNews"
                || item.Text == "Add NetNewsWire News Feed"), "Help still exposes the upstream subscription command.");
            return new { SmartScopes = true, CurrentScopeSearchCleared = true, SidebarFocus = true, WindowsShortcuts = true,
                NoUpstreamSubscriptionAction = true, FolderOpenFailure = true, NoExternalProcessLaunched = true };
        }
        finally
        {
            window.ProbeOpenFolder = null;
            await content.RestoreStateAsync(original);
            model.SearchQuery = originalSearch;
        }
    }

    private static async Task<object?> CheckUnreadCountDisplayAsync(MainWindow window)
    {
        var original = window.CaptureState();
        var originalMode = AppDefaults.Shared.GetUnreadCountDisplay();
        var account = AccountManager.Shared.DefaultAccount;
        var feed = account.FlattenedFeeds().Single();
        var originalRead = (await account.Database.FetchArticlesByIDsAsync(["probe-article"])).Single().Status.Read;
        AppWindows.PreferencesWindow? preferences = null;
        MainWindow? other = null;
        Models.SidebarItem Row(MainWindow owner) => owner.ProbeContent.ViewModel.FindSidebarItemForFeed(account.AccountID, feed.FeedID)
            ?? throw new InvalidOperationException("The unread display fixture disappeared.");
        ListView List(MainWindow owner) => (ListView)owner.ProbeContent.FindName("SidebarListView");
        try
        {
            window.ProbeContent.ViewModel.SetSelectedArticles([]);
            await account.MarkAsync(["probe-article"], ArticleStatus.Key.Read, false);
            await account.RecalculateUnreadCountsAsync();
            var model = window.ProbeContent.ViewModel;
            model.IsSidebarVisible = true;
            model.SelectedSidebarItem = Row(window);
            await model.FilterArticlesAsync();
            model.SetSelectedArticles([]);
            AppDefaults.Shared.SetUnreadCountDisplay(UnreadCountDisplay.Count);
            await WaitFor(() => Task.FromResult(Row(window).UnreadCount > 0 && model.ArticleListUnreadCount > 0), "Unread display fixture");
            int count = Row(window).UnreadCount;
            var filters = window.CaptureState().Timeline.ReadFilters;
            List(window).ScrollIntoView(Row(window));
            await WaitFor(() => Task.FromResult(List(window).ContainerFromItem(Row(window)) is not null), "Unread row container");
            var row = Row(window);
            var container = List(window).ContainerFromItem(row);
            Require(AutomationProperties.GetName(container) == row.Title + " " + UnreadCountDisplay.Count.AccessibilityTextFor(count),
                "The accessible row name omitted its unread count.");
            Require(((Border)window.ProbeContent.FindName("TimelineUnreadCount")).Visibility == Visibility.Visible,
                "Count mode hid the timeline count.");

            other = App.OpenMainWindow();
            await WaitFor(() => Task.FromResult(other.ProbeContent.IsLoaded && other.ProbeContent.ProbeWebViewReady), "Unread display second window");
            preferences = new AppWindows.PreferencesWindow();
            preferences.Activate();
            await WaitFor(() => Task.FromResult(preferences.Content is FrameworkElement { IsLoaded: true }), "Unread count preferences");
            var combo = Descendants(preferences.Content).OfType<ComboBox>().Single(control => control.Name == "UnreadCountDisplayCombo");
            void Select(UnreadCountDisplay mode) => combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
                .Single(item => item.Tag is UnreadCountDisplay value && value == mode);

            Select(UnreadCountDisplay.Dot);
            await WaitFor(() => Task.FromResult(Row(window).ShowUnreadCountDot && Row(other).ShowUnreadCountDot), "Unread dot in both windows");
            row = Row(window);
            container = List(window).ContainerFromItem(row);
            var dot = Descendants(container).OfType<Microsoft.UI.Xaml.Shapes.Ellipse>().Single(shape => shape.Name == "SidebarUnreadDot");
            Require(dot.Visibility == Visibility.Visible && dot.Width == 8 && dot.Height == 8, "Dot mode did not display the native eight-DIP indicator.");
            Require(AutomationProperties.GetName(container) == row.Title + " " + AppStrings.Get("Unread"),
                "Dot mode announced a numeric count.");
            Require(((Border)window.ProbeContent.FindName("TimelineUnreadCount")).Visibility == Visibility.Collapsed,
                "Dot mode retained a numeric timeline count.");

            Select(UnreadCountDisplay.Hidden);
            await WaitFor(() => Task.FromResult(!Row(window).ShowUnreadIndicator && !Row(other).ShowUnreadIndicator), "Hidden unread indicators");
            row = Row(window);
            container = List(window).ContainerFromItem(row);
            Require(AutomationProperties.GetName(container) == row.Title, "Hidden counts remained in the accessible row name.");
            Require(Descendants(container).OfType<Border>().Single(border => border.Name == "SidebarUnreadIndicator").Visibility == Visibility.Collapsed,
                "The hidden indicator still occupied the visible row.");
            Require(row.UnreadCount == count && filters.SequenceEqual(window.CaptureState().Timeline.ReadFilters)
                && !(await account.Database.FetchArticlesByIDsAsync(["probe-article"])).Single().Status.Read,
                "A display-only change altered unread data or read filters.");
            Require(!model.SidebarStatus.Contains(AppStrings.Get("Unread"), StringComparison.Ordinal)
                && !model.TimelineStatus.Contains(AppStrings.Get("Unread"), StringComparison.Ordinal), "Hidden numeric counts remained in pane status text.");

            Select(UnreadCountDisplay.Dot);
            using (var locked = new FileStream(Path.Combine(AppConfig.DataDirectory, "AppDefaults.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Select(UnreadCountDisplay.Hidden);
                Require((combo.SelectedItem as ComboBoxItem)?.Tag is UnreadCountDisplay.Dot
                    && AppDefaults.Shared.GetUnreadCountDisplay() == UnreadCountDisplay.Dot, "A failed preference write changed the display mode.");
            }
            Require(Descendants(preferences.Content).OfType<TextBlock>().Single(control => control.Name == "UnreadCountsStatus").Text.Length > 0,
                "A failed display preference write was not reported.");
            preferences.Close();
            preferences = new AppWindows.PreferencesWindow();
            preferences.Activate();
            await WaitFor(() => Task.FromResult(preferences.Content is FrameworkElement { IsLoaded: true }), "Reopened unread preferences");
            combo = Descendants(preferences.Content).OfType<ComboBox>().Single(control => control.Name == "UnreadCountDisplayCombo");
            Require((combo.SelectedItem as ComboBoxItem)?.Tag is UnreadCountDisplay.Dot, "Reopening preferences lost the display mode.");
            return new { Count = true, Dot = true, Hidden = true, AccessibleNames = true, ExistingWindowsUpdated = true,
                StatePreserved = true, FailedWriteRollback = true, PreferenceReopened = true };
        }
        finally
        {
            preferences?.Close();
            if (other is not null) await CloseWindowAsync(other);
            AppDefaults.Shared.SetUnreadCountDisplay(originalMode);
            await account.MarkAsync(["probe-article"], ArticleStatus.Key.Read, originalRead);
            await account.RecalculateUnreadCountsAsync();
            await window.ProbeContent.RestoreStateAsync(original);
            window.Activate();
        }
    }

    private static async Task<object?> CheckAddFolderDialogAsync(MainWindow window)
    {
        var defaults = AppDefaults.Shared;
        bool hadPreference = defaults.ContainsKey(AppDefaults.Key.AddFolderAccountID);
        var previous = defaults.GetValue<string?>(AppDefaults.Key.AddFolderAccountID);
        var account = AccountManager.Shared.CreateLocalAccount("Folder dialog destination");
        ContentDialog? dialog = null;
        Task<Folder?>? showing = null;
        var root = window.ProbeContent.XamlRoot;
        ContentDialog? OpenDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(root)
            .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault();
        try
        {
            defaults.RemoveValue(AppDefaults.Key.AddFolderAccountID);
            showing = Dialogs.AddFolderDialog.ShowAsync(root);
            await WaitFor(() => Task.FromResult((dialog = OpenDialog())?.IsLoaded == true), "New folder dialog");
            var choices = Descendants(dialog).OfType<ComboBox>().Single();
            Require(ReferenceEquals((choices.SelectedItem as ComboBoxItem)?.Tag, AccountManager.Shared.DefaultAccount),
                "The folder dialog did not default to the permanent local account.");
            Require(!dialog!.IsPrimaryButtonEnabled, "An empty folder name enabled creation.");
            choices.SelectedItem = choices.Items.OfType<ComboBoxItem>().Single(item => ReferenceEquals(item.Tag, account));
            var name = Descendants(dialog).OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "Folder name");
            name.Text = "Remembered folder";
            await WaitFor(() => Task.FromResult(dialog.IsPrimaryButtonEnabled), "Enabled folder creation");
            Require(dialog.IsPrimaryButtonEnabled, "A valid folder name did not enable creation.");
            InvokeDialogButton(dialog, "PrimaryButton");
            var folder = await showing.WaitAsync(TimeSpan.FromSeconds(10));
            showing = null;
            Require(folder?.NameForDisplay == "Remembered folder" && folder.AccountID == account.AccountID,
                "The folder was not created in the selected account.");
            await WaitFor(() => Task.FromResult(OpenDialog() is null), "Created folder dialog closed");

            showing = Dialogs.AddFolderDialog.ShowAsync(root);
            await WaitFor(() => Task.FromResult((dialog = OpenDialog())?.IsLoaded == true), "Remembered folder destination");
            choices = Descendants(dialog).OfType<ComboBox>().Single();
            Require(ReferenceEquals((choices.SelectedItem as ComboBoxItem)?.Tag, account), "The folder account was not remembered.");
            choices.SelectedIndex = 0;
            InvokeDialogButton(dialog!, "CloseButton");
            Require(await showing.WaitAsync(TimeSpan.FromSeconds(10)) is null
                && defaults.GetValue<string?>(AppDefaults.Key.AddFolderAccountID) == account.AccountID,
                "Cancel changed the remembered folder destination.");
            showing = null;
            await WaitFor(() => Task.FromResult(OpenDialog() is null), "Cancelled folder dialog closed");

            account.IsActive = false;
            showing = Dialogs.AddFolderDialog.ShowAsync(root);
            await WaitFor(() => Task.FromResult((dialog = OpenDialog())?.IsLoaded == true), "Inactive folder destination fallback");
            choices = Descendants(dialog).OfType<ComboBox>().Single();
            Require(ReferenceEquals((choices.SelectedItem as ComboBoxItem)?.Tag, AccountManager.Shared.DefaultAccount),
                "An inactive remembered account remained the folder destination.");
            InvokeDialogButton(dialog!, "CloseButton");
            await showing.WaitAsync(TimeSpan.FromSeconds(10));
            showing = null;
            return new { LocalFirst = true, RememberedAccount = true, EmptyNameRejected = true,
                CancelPreservedPreference = true, InactiveAccountFallback = true };
        }
        finally
        {
            (dialog ?? OpenDialog())?.Hide();
            if (showing is not null) await showing.WaitAsync(TimeSpan.FromSeconds(10));
            await AccountManager.Shared.RemoveAccountAsync(account);
            if (hadPreference) defaults.SetValue(AppDefaults.Key.AddFolderAccountID, previous);
            else defaults.RemoveValue(AppDefaults.Key.AddFolderAccountID);
            window.Activate();
        }
    }

    private static async Task<object?> CheckNextUnreadAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var manager = AccountManager.Shared;
        var formerlyActive = manager.ActiveAccounts.ToArray();
        var account = manager.CreateLocalAccount("Unread navigation");
        Article Make(Feed feed, string id, string title, bool read) => new(account.AccountID, id, feed.FeedID, id, title,
            "<p>Unread navigation fixture.</p>", null, null, serverUrl + "/" + id, null, null, null,
            DateTime.UtcNow, null, null, null, new ArticleStatus(id, read, false, DateTime.UtcNow));
        Models.SidebarItem FeedRow(Feed feed) => model.FindSidebarItemForFeed(account.AccountID, feed.FeedID)!;
        Models.SidebarItem FolderRow(Folder folder) => model.SidebarItems.SelectMany(section => section.Children)
            .Single(item => ReferenceEquals(item.Tag, folder));
        try
        {
            foreach (var previous in formerlyActive) previous.IsActive = false;
            var first = account.AddFeed(serverUrl + "/nav-first", "A first");
            var second = account.AddFeed(serverUrl + "/nav-second", "B second");
            var folder = account.AddFolder("Z navigation folder");
            var nested = account.AddFeed(serverUrl + "/nav-nested", "C nested", folder);
            await account.Database.UpdateArticlesAsync([
                Make(first, "nav-early", "A early", false),
                Make(first, "nav-middle", "M middle", true),
                Make(first, "nav-late", "Z late", false),
                Make(second, "nav-second-first", "A first", false),
                Make(second, "nav-second-last", "Z last", false),
                Make(nested, "nav-nested-only", "C nested", false)
            ]);
            await account.RecalculateUnreadCountsAsync();
            model.BuildSidebar();
            model.IsSidebarVisible = true;
            model.IsSidebarReadFiltered = false;
            model.SearchQuery = "";
            model.SetSortParameters(new(ArticleSortKey.Title, false), persistDefault: false);
            model.SelectedSidebarItem = FeedRow(first);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            await InvokeDisclosureAsync(content, () => FolderRow(folder));
            Require(!FolderRow(folder).IsExpanded, "The navigation folder did not collapse.");
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "nav-middle");
            await content.SelectNextUnreadAsync();
            Require(model.SelectedArticle?.Id == "nav-late" && ReferenceEquals(model.SelectedSidebarItem?.Tag, first),
                "Next unread did not prefer the remainder of its current timeline.");
            await content.SelectNextUnreadAsync();
            Require(model.SelectedArticle?.Id == "nav-second-first" && ReferenceEquals(model.SelectedSidebarItem?.Tag, second),
                "Next unread wrapped within the first feed instead of advancing to the next feed.");
            await content.SelectNextUnreadAsync();
            Require(model.SelectedArticle?.Id == "nav-second-last", "Next unread skipped an unread article in the next feed.");
            await content.SelectNextUnreadAsync();
            Require(model.SelectedArticle?.Id == "nav-nested-only" && ReferenceEquals(model.SelectedSidebarItem?.Tag, folder)
                && !FolderRow(folder).IsExpanded, "A collapsed folder was not selected as an unread scope.");
            await content.SelectNextUnreadAsync();
            Require(model.SelectedArticle?.Id == "nav-early"
                && ReferenceEquals(model.SelectedSidebarItem?.Tag, AppShared.SmartFeeds.SmartFeedsController.Shared.TodayFeed),
                "Sidebar wrapping did not include the first eligible smart scope.");
            await model.PendingAutomaticReads;

            await account.MarkAsync(["nav-nested-only"], ArticleStatus.Key.Read, false);
            await account.RecalculateUnreadCountsAsync();
            model.SelectedSidebarItem = FeedRow(second);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            await InvokeDisclosureAsync(content, () => FolderRow(folder));
            Require(FolderRow(folder).IsExpanded, "The navigation folder did not expand.");
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "nav-second-last");
            await content.SelectNextUnreadAsync();
            Require(model.SelectedArticle?.Id == "nav-nested-only" && ReferenceEquals(model.SelectedSidebarItem?.Tag, nested),
                "Next unread selected an expanded folder instead of its visible feed.");
            await model.PendingAutomaticReads;

            await account.MarkAsync(["nav-early", "nav-late"], ArticleStatus.Key.Read, false);
            model.SelectedSidebarItem = FeedRow(first);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "nav-middle");
            await Task.WhenAll(content.SelectNextUnreadAsync(), content.SelectNextUnreadAsync());
            Require(model.SelectedArticle?.Id == "nav-early", "Queued next-unread commands raced or skipped a scope.");
            await model.PendingAutomaticReads;
            await model.RefreshNavigationUnreadCountsAsync(CancellationToken.None);
            string? selected = model.SelectedArticle?.Id;
            Require(!content.CanSelectNextUnread, "Next unread remained enabled after all articles were read.");
            await content.SelectNextUnreadAsync();
            Require(model.SelectedArticle?.Id == selected, "An empty unread traversal changed the selection.");
            return new { TimelineFirst = true, CrossFeed = true, CollapsedFolder = true, ExpandedFolderSkipped = true,
                SmartScopeWrap = true, QueuedCommands = true, EmptyTraversal = true };
        }
        finally
        {
            await model.PendingAutomaticReads;
            foreach (var previous in formerlyActive) previous.IsActive = true;
            await manager.RemoveAccountAsync(account);
            await content.RestoreStateAsync(original);
            window.Activate();
        }
    }

    private static async Task<object?> CheckArticleFindAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        int originalTextSize = AppDefaults.Shared.ArticleTextSizeRaw;
        var core = content.ProbeWebView.CoreWebView2;
        var bar = (Border)content.FindName("ArticleFindBar");
        var query = (TextBox)content.FindName("ArticleFindText");
        var previous = (Button)content.FindName("ArticleFindPrevious");
        var next = (Button)content.FindName("ArticleFindNext");
        var options = (MenuFlyout)((Button)content.FindName("ArticleFindOptionsButton")).Flyout;
        var matchCase = options.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Name == "ArticleFindMatchCase");
        var wholeWord = options.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Name == "ArticleFindWholeWord");
        var delayedFind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        static void Toggle(ToggleMenuFlyoutItem item)
        {
            var peer = FrameworkElementAutomationPeer.FromElement(item) ?? FrameworkElementAutomationPeer.CreatePeerForElement(item);
            (peer?.GetPattern(PatternInterface.Toggle) as IToggleProvider
                ?? throw new InvalidOperationException("The Find option has no toggle pattern.")).Toggle();
        }
        async Task Count(int count)
        {
            await content.ProbeFindCompletion;
            try { await WaitFor(() => Task.FromResult(content.ProbeArticleFind?.MatchCount == count), "Native Find match count"); }
            catch (TimeoutException error)
            {
                var selection = await content.ProbeWebView.ExecuteScriptAsync(
                    "({selection:getSelection().toString(),fixture:!!document.getElementById('find-selection-probe'),body:document.body.innerText.slice(-120)})");
                throw new InvalidOperationException($"Find expected {count}, actual {content.ProbeArticleFind?.MatchCount}; query={query.Text}; "
                    + $"status={((TextBlock)content.FindName("ArticleFindStatus")).Text}; DOM={selection}", error);
            }
        }
        try
        {
            var account = AccountManager.Shared.DefaultAccount;
            var feed = account.FlattenedFeeds().Single();
            model.IsReaderMode = false;
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "probe-article");
            await WaitFor(async () => content.ProbeArticleReady && await ScriptBool(content, "!!document.getElementById('probe-body')"), "Find article document");
            Require(!core.Settings.IsScriptEnabled, "Find must work without enabling publisher scripts.");
            await content.ProbeWebView.ExecuteScriptAsync("""
                var fixture = document.createElement('section');
                fixture.id = 'article-find-probe';
                fixture.innerHTML = '<p>Needle needle needlework</p><p>needle caf\u00e9 caf\u00e9</p>'
                    + '<p id="find-selection-probe">selection phrase</p><div style="height:1800px"></div>'
                    + '<p id="find-jump-probe">selected text near the end</p>';
                document.getElementById('bodyContainer').appendChild(fixture);
                """);
            await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", """{"enabled":true}""");
            content.ProbeWebView.Focus(FocusState.Programmatic);
            await DispatchBrowserKeyAsync(core, "f", "KeyF", 70, modifiers: 2);
            await WaitFor(() => Task.FromResult(bar.Visibility == Visibility.Visible
                && ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(content.XamlRoot), query)), "Reader Ctrl+F");
            query.Text = "needle";
            await Count(4);
            Toggle(matchCase);
            await Count(3);
            Toggle(wholeWord);
            await Count(2);
            Toggle(matchCase);
            await Count(3);
            query.Text = "caf\u00e9";
            await Count(2);
            Require(content.ProbeArticleFind!.ActiveMatchIndex == 1, "A new find session did not start at its first match.");
            InvokeButton(next);
            await content.ProbeFindCompletion;
            await WaitFor(() => Task.FromResult(content.ProbeArticleFind.ActiveMatchIndex == 2), "Find Next result");
            Require(content.ProbeArticleFind.ActiveMatchIndex == 2, "Find Next did not advance.");
            InvokeButton(next);
            await content.ProbeFindCompletion;
            await WaitFor(() => Task.FromResult(content.ProbeArticleFind.ActiveMatchIndex == 1), "Wrapped Find Next result");
            Require(content.ProbeArticleFind.ActiveMatchIndex == 1, "Find Next did not wrap.");
            InvokeButton(previous);
            await content.ProbeFindCompletion;
            await WaitFor(() => Task.FromResult(content.ProbeArticleFind.ActiveMatchIndex == 2), "Wrapped Find Previous result");
            Require(content.ProbeArticleFind.ActiveMatchIndex == 2, "Find Previous did not wrap.");
            query.Text = "this phrase is absent from the fixture";
            await Count(0);
            Require(!previous.IsEnabled && !next.IsEnabled, "No-match results left navigation enabled.");

            Require(await ScriptString(content, """
                (() => {
                    const range = document.createRange();
                    range.selectNodeContents(document.getElementById('find-selection-probe'));
                    getSelection().removeAllRanges();
                    getSelection().addRange(range);
                    return getSelection().toString();
                })()
                """) == "selection phrase", "The Find fixture did not select its text.");
            await content.UseArticleSelectionForFindAsync();
            await Count(0);
            Require(query.Text == "selection phrase", "Use Selection for Find lost the selected text.");
            Require(ToolTipService.GetToolTip(wholeWord) as string == AppStrings.Get("FindWholeWordsHelp"),
                "Whole-word matching did not explain how to search for a phrase.");
            Toggle(wholeWord);
            await Count(1);
            await content.ProbeWebView.ExecuteScriptAsync("""
                (() => {
                    const range = document.createRange();
                    range.selectNodeContents(document.getElementById('find-jump-probe'));
                    getSelection().removeAllRanges();
                    getSelection().addRange(range);
                    window.scrollTo(0, 0);
                })()
                """);
            await content.JumpToArticleSelectionAsync();
            Require(await ScriptBool(content,
                "document.getElementById('find-jump-probe').getBoundingClientRect().top >= 0 && document.getElementById('find-jump-probe').getBoundingClientRect().bottom <= innerHeight"),
                "Jump to Selection did not reveal the selected range.");

            query.Text = "needle";
            query.Text = "caf\u00e9";
            await Count(2);
            AppDefaults.Shared.ArticleTextSizeRaw = (originalTextSize + 1) % 5;
            await WaitFor(async () => content.ProbeReaderInteractionReady
                && await ScriptBool(content, "!document.getElementById('article-find-probe')"), "Find after article rerender");
            await Count(0);
            Require(bar.Visibility == Visibility.Visible && query.Text == "caf\u00e9",
                "Article navigation discarded the visible Find query.");
            query.Text = "WinUI";
            content.HideArticleFind();
            await content.ProbeFindCompletion;
            Require(bar.Visibility == Visibility.Collapsed && !previous.IsEnabled && !next.IsEnabled,
                "A queued Find request reopened a closed bar.");
            int heldSearches = 0;
            content.ProbeFindStart = (nativeOptions, cancellation) =>
            {
                if (nativeOptions.FindTerm != "held search") return core.Find.StartAsync(nativeOptions).AsTask(cancellation);
                heldSearches++;
                return delayedFind.Task;
            };
            await content.ShowArticleFindAsync();
            query.Text = "held search";
            await WaitFor(() => Task.FromResult(heldSearches > 0), "Pending native Find");
            query.Text = "WinUI";
            await Count(1);
            Require(!delayedFind.Task.IsCompleted, "The cancelled-native-search regression did not leave its native operation pending.");
            int previousHeldSearches = heldSearches;
            query.Text = "held search";
            await WaitFor(() => Task.FromResult(heldSearches > previousHeldSearches), "Pending native Find before close");
            content.HideArticleFind();
            await content.ProbeFindCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Require(!delayedFind.Task.IsCompleted && bar.Visibility == Visibility.Collapsed,
                "Closing Find waited for an obsolete native search.");
            Require(!core.Settings.IsScriptEnabled, "Find enabled publisher scripts.");
            return new { NativeEngine = true, CaseAndWordOptions = true, Unicode = true, NextPreviousWrap = true,
                SelectedText = true, JumpToSelection = true, RerenderRestart = true, RapidQueriesAndClose = true,
                PublisherScriptsDisabled = true, CancelledNativeSearchDoesNotBlock = true };
        }
        finally
        {
            content.HideArticleFind();
            delayedFind.TrySetResult();
            content.ProbeFindStart = null;
            await content.ProbeFindCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", """{"enabled":false}""");
            AppDefaults.Shared.ArticleTextSizeRaw = originalTextSize;
            await content.RestoreStateAsync(original);
        }
    }

    private static async Task<object?> CheckDinosaursAsync(MainWindow window, string serverUrl)
    {
        var original = window.CaptureState();
        int previousThreshold = AppDefaults.Shared.GetValue("DinosaurMonthThreshold", 6);
        bool previousOpen = AppWindows.DinosaursWindow.ShouldOpenAtStartup;
        var account = AccountManager.Shared.CreateLocalAccount("Dinosaurs fixture");
        AppWindows.DinosaursWindow? dinosaurs = null;
        ContentDialog? confirmation = null;
        try
        {
            var first = account.AddFolder("First dinosaur folder");
            var second = account.AddFolder("Second dinosaur folder");
            var feed = account.AddFeed(serverUrl + "/dinosaur", "Dinosaur feed");
            feed.FaviconUrl = serverUrl + "/icon.png";
            account.AddFeed(feed.Url, folder: first);
            account.AddFeed(feed.Url, folder: second);
            feed.LastResponseCode = 410;
            var status = new ArticleStatus("dinosaur-article", false, true, DateTime.UtcNow);
            await account.Database.UpdateArticlesAsync([new Article(account.AccountID, status.ArticleID, feed.FeedID,
                "dinosaur", "Old article", "<p>Old article body.</p>", null, null, serverUrl + "/dinosaur-article",
                null, null, null, DateTime.UtcNow.AddYears(-2), null, null, null, status)]);
            AppDefaults.Shared.SetValue("DinosaurMonthThreshold", 6);
            dinosaurs = AppWindows.DinosaursWindow.Show();
            await WaitFor(() => Task.FromResult(dinosaurs.Content is FrameworkElement { IsLoaded: true }), "Dinosaurs window");
            await dinosaurs.RefreshAsync();
            var list = Descendants(dinosaurs.Content).OfType<ListView>().Single(control => control.Name == "FeedsList");
            var threshold = Descendants(dinosaurs.Content).OfType<NumberBox>().Single();
            AppWindows.DinosaursWindow.Row Row() => list.Items.OfType<AppWindows.DinosaursWindow.Row>()
                .Single(row => row.Data.FeedUrl == feed.Url);
            Button Button(string name) => (Button)((FrameworkElement)dinosaurs.Content).FindName(name);
            Require(Row().LastResponse == "410" && Row().Data.LastArticleDate < DateTime.UtcNow.AddYears(-1), "Stale-feed metadata was not displayed.");
            threshold.Value = 25;
            await dinosaurs.RefreshAsync();
            Require(!list.Items.OfType<AppWindows.DinosaursWindow.Row>().Any(row => row.Data.FeedUrl == feed.Url),
                "The calendar-month threshold did not exclude the newer feed.");
            threshold.Value = double.NaN;
            Require(list.Items.Count == 0 && AppDefaults.Shared.GetValue("DinosaurMonthThreshold", 0) == 25,
                "Clearing the threshold changed its saved value or retained stale rows.");
            threshold.Value = 6;
            await dinosaurs.RefreshAsync();
            list.SelectedItem = Row();
            global::Windows.ApplicationModel.DataTransfer.DataPackageView? copied = null;
            dinosaurs.ProbeClipboardWriter = data => copied = data.GetView();
            InvokeButton(Button("CopyButton"));
            Require(copied is not null && await copied.GetTextAsync() == feed.Url, "Copy Feed URL did not copy the selected URL.");
            InvokeButton(Button("SelectFeedButton"));
            await WaitFor(() => Task.FromResult(window.ProbeContent.ViewModel.SelectedSidebarItem?.Tag is Feed selected
                && selected.AccountID == account.AccountID && selected.FeedID == feed.FeedID), "Select dinosaur in sidebar");
            dinosaurs.Activate();
            await dinosaurs.RefreshAsync();
            list.SelectedItem = Row();
            ContentDialog? OpenConfirmation() => VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)dinosaurs.Content).XamlRoot)
                .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault();
            InvokeButton(Button("DeleteButton"));
            await WaitFor(() => Task.FromResult((confirmation = OpenConfirmation())?.IsLoaded == true), "Stale-feed deletion confirmation");
            Require(confirmation!.DefaultButton == ContentDialogButton.Close, "Deletion did not default to Cancel.");
            InvokeDialogButton(confirmation, "CloseButton");
            await WaitFor(() => Task.FromResult(OpenConfirmation() is null), "Cancelled stale-feed deletion");
            Require(account.TopLevelFeeds.Contains(feed) && first.Feeds.Contains(feed) && second.Feeds.Contains(feed),
                "Cancelling removed a subscription membership.");
            InvokeButton(Button("DeleteButton"));
            await WaitFor(() => Task.FromResult((confirmation = OpenConfirmation())?.IsLoaded == true), "Confirmed stale-feed deletion");
            InvokeDialogButton(confirmation!, "PrimaryButton");
            await WaitFor(() => Task.FromResult(account.ExistingFeedWithUrl(feed.Url) is null && Button("UndoButton").IsEnabled), "Deleting every membership");
            Require((await account.Database.FetchArticlesByIDsAsync([status.ArticleID])).Single().Status.Starred,
                "Deleting a stale subscription deleted its retained article history.");
            InvokeButton(Button("UndoButton"));
            await WaitFor(() => Task.FromResult(account.TopLevelFeeds.Contains(feed) && first.Feeds.Contains(feed)
                && second.Feeds.Contains(feed) && Button("RedoButton").IsEnabled), "Restoring every membership");
            InvokeButton(Button("RedoButton"));
            await WaitFor(() => Task.FromResult(account.ExistingFeedWithUrl(feed.Url) is null && Button("UndoButton").IsEnabled), "Redo stale-feed deletion");
            InvokeButton(Button("UndoButton"));
            await WaitFor(() => Task.FromResult(first.Feeds.Contains(feed) && second.Feeds.Contains(feed) && Button("RedoButton").IsEnabled), "Final membership restoration");
            await dinosaurs.PrepareToCloseAsync();
            dinosaurs.Close();
            dinosaurs = null;
            Require(!AppWindows.DinosaursWindow.ShouldOpenAtStartup, "Closing Dinosaurs retained its open-window flag.");
            return new { Threshold = true, BlankThreshold = true, Metadata = true, SidebarSelection = true,
                Confirmation = true, AllMembershipUndoRedo = true, ArticleHistoryPreserved = true, SystemClipboardUntouched = true };
        }
        finally
        {
            confirmation?.Hide();
            if (dinosaurs is not null) { await dinosaurs.PrepareToCloseAsync(); dinosaurs.Close(); }
            await AccountManager.Shared.RemoveAccountAsync(account);
            AppDefaults.Shared.SetValue("DinosaurMonthThreshold", previousThreshold);
            AppDefaults.Shared.SetValue("DinosaurWindowIsOpen", previousOpen);
            await window.ProbeContent.RestoreStateAsync(original);
            window.Activate();
        }
    }

    private static async Task<object?> CheckCurrentActivityAsync()
    {
        var log = WinNewsWire.ActivityLog.ActivityLog.Shared;
        var owner = WinNewsWire.ActivityLog.ActivityOwner.Account("current-activity-probe", "Current activity fixture");
        int pending = log.CreateActivity(owner, WinNewsWire.ActivityLog.ActivityKind.RefreshArticles, "Pending work");
        int running = log.CreateActivity(owner, WinNewsWire.ActivityLog.ActivityKind.RefreshFeedList, "Running work");
        log.DidStart(running);
        AppWindows.CurrentActivityWindow? current = null;
        try
        {
            current = AppWindows.CurrentActivityWindow.Show();
            await WaitFor(() => Task.FromResult(current.Content is FrameworkElement { IsLoaded: true }), "Current Activity window");
            var list = Descendants(current.Content).OfType<ListView>().Single(control => control.Name == "Activities");
            AppWindows.CurrentActivityWindow.Row? Row(int id) => list.Items.OfType<AppWindows.CurrentActivityWindow.Row>().FirstOrDefault(row => row.ID == id);
            await WaitFor(() => Task.FromResult(Row(pending) is not null && Row(running) is not null), "Current activities");
            var ordered = list.Items.OfType<AppWindows.CurrentActivityWindow.Row>().Select(row => row.ID).ToList();
            Require(ordered.IndexOf(running) < ordered.IndexOf(pending), "Running work did not precede pending work.");
            log.DidStart(pending);
            await WaitFor(() => Task.FromResult(Row(pending)?.StateLabel == AppStrings.Get("Running")), "Pending-to-running row update");
            log.DidFail(pending, new IOException("Current Activity fixture failure."));
            await WaitFor(() => Task.FromResult(Row(pending)?.StateLabel == AppStrings.Get("Failed")), "Failed current activity");
            await WaitFor(() => Task.FromResult(Row(pending) is null), "Completed-activity linger expiry");
            Require(log.CompletedActivities.Any(activity => activity.ID == pending), "Linger expiry removed the historical activity.");
            Require(AppWindows.CurrentActivityWindow.ShouldOpenAtStartup, "The current-activity open state was not stored.");
            current.Close();
            current = null;
            Require(!AppWindows.CurrentActivityWindow.ShouldOpenAtStartup, "Closing Current Activity retained its open state.");
            current = AppWindows.CurrentActivityWindow.Show();
            await WaitFor(() => Task.FromResult(current.Content is FrameworkElement { IsLoaded: true }), "Reopened Current Activity");
            var reopened = Descendants(current.Content).OfType<ListView>().Single(control => control.Name == "Activities");
            await WaitFor(() => Task.FromResult(reopened.Items.OfType<AppWindows.CurrentActivityWindow.Row>().Any(row => row.ID == running)),
                "Current Activity restart");
            return new { RunningBeforePending = true, InPlaceStateChange = true, FailedState = true, TwoSecondLinger = true,
                HistoryRetained = true, CloseAndReopen = true };
        }
        finally
        {
            current?.Close();
            foreach (int id in new[] { pending, running })
                if (log.PendingActivities.Concat(log.RunningActivities).Any(activity => activity.ID == id)) log.DidComplete(id);
        }
    }

    private static async Task<object?> CheckSharedFeedAsync(MainWindow window, string serverUrl)
    {
        var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetWebLink(new Uri(serverUrl + "/shared-feed"));
        package.SetText("https://example.invalid/not-the-shared-link");
        string url = await Services.SharedFeedDataReader.ReadAsync(package.GetView());
        Require(url == serverUrl + "/shared-feed", "Shared text overrode the explicit web link.");
        var text = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        text.SetText("feeds://example.com/feed");
        Require(await Services.SharedFeedDataReader.ReadAsync(text.GetView()) == "https://example.com/feed",
            "Shared feed URL normalization differed from normal subscription input.");
        var unsupported = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        unsupported.SetText("file:///C:/private.opml");
        bool rejected = false;
        try { await Services.SharedFeedDataReader.ReadAsync(unsupported.GetView()); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "A shared file URL became a subscription or file activation.");

        var manager = AccountManager.Shared;
        int before = manager.Accounts.Sum(account => account.FlattenedFeeds().Count());
        var root = window.ProbeContent.XamlRoot;
        ContentDialog? dialog = null;
        Task<bool>? sharing = null;
        Account.Account? rejectedAccount = null;
        ContentDialog? OpenDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(root)
            .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault();
        try
        {
            sharing = window.HandleSharedFeedAsync(url);
            await WaitFor(() => Task.FromResult((dialog = OpenDialog())?.IsLoaded == true), "Shared feed confirmation");
            var inputs = Descendants(dialog).OfType<TextBox>().ToArray();
            Require(inputs.Single(input => AutomationProperties.GetName(input) == "Feed or website URL").Text == url,
                "The share target did not prefill its selected URL.");
            Require(inputs.Single(input => AutomationProperties.GetName(input) == "Custom feed name").Text == "",
                "The share title was incorrectly treated as an edited feed name.");
            InvokeDialogButton(dialog!, "CloseButton");
            Require(!await sharing.WaitAsync(TimeSpan.FromSeconds(10)), "Cancelling a shared feed reported subscription success.");
            sharing = null;
            Require(before == manager.Accounts.Sum(account => account.FlattenedFeeds().Count()), "Cancellation added a feed.");
            var failure = new InvalidOperationException("Fixture cannot subscribe.");
            rejectedAccount = manager.CreateRemoteAccount(AccountType.OnMyMac, "Rejected share", new RejectedShareDelegate(failure));
            Exception? observed = null;
            try
            {
                await window.ProbeContent.ViewModel.AddFeedWithResultAsync(new ViewModels.AddFeedRequest
                {
                    Url = serverUrl + "/rejected-share",
                    Account = rejectedAccount
                });
            }
            catch (InvalidOperationException error) { observed = error; }
            Require(ReferenceEquals(observed, failure), "Subscription failure was swallowed before share completion reporting.");
            return new { ExplicitLinkPrecedence = true, FeedSchemeNormalization = true, UnsafeFormatsRejected = true,
                UserConfirmation = true, CancelNotSuccessful = true, FailurePropagated = true, OsShareBrokerNotSimulated = true };
        }
        finally
        {
            (dialog ?? OpenDialog())?.Hide();
            if (sharing is not null) await sharing.WaitAsync(TimeSpan.FromSeconds(10));
            if (rejectedAccount is not null) await manager.RemoveAccountAsync(rejectedAccount);
            window.Activate();
        }
    }

    private sealed class RejectedShareDelegate(Exception error) : IAccountDelegate
    {
        public AccountType Type => AccountType.OnMyMac;
        public Task RefreshAllAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
            => Task.FromException(error);
        public Task<Feed?> CreateFeedAsync(Account.Account account, string url, string? name, Folder? folder, CancellationToken ct)
            => Task.FromException<Feed?>(error);
    }

    private static async Task<object?> CheckAutomationAsync(MainWindow window)
    {
        bool previous = AppDefaults.Shared.AutomationEnabled;
        var account = AccountManager.Shared.DefaultAccount;
        var article = (await account.Database.FetchArticlesByIDsAsync(["probe-article"])).Single();
        bool previousStar = article.Status.Starred;
        AppWindows.PreferencesWindow? preferences = null;
        string pipeName = LocalAutomationPipe.NameForCurrentUser(AppConfig.DataDirectory);
        async Task<AppShared.Scripting.ScriptingResponse> Send(AppShared.Scripting.ScriptingRequest request)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            await LocalAutomationPipe.WriteFrameAsync(pipe, JsonSerializer.Serialize(request, AppShared.Scripting.ScriptingProtocol.Options),
                LocalAutomationPipe.MaximumRequestBytes, timeout.Token);
            return JsonSerializer.Deserialize<AppShared.Scripting.ScriptingResponse>(
                await LocalAutomationPipe.ReadFrameAsync(pipe, LocalAutomationPipe.MaximumResponseBytes, timeout.Token),
                AppShared.Scripting.ScriptingProtocol.Options)!;
        }
        try
        {
            AppDefaults.Shared.AutomationEnabled = false;
            preferences = new AppWindows.PreferencesWindow();
            preferences.Activate();
            await WaitFor(() => Task.FromResult(preferences.Content is FrameworkElement { IsLoaded: true }), "Automation preferences");
            Descendants(preferences.Content).OfType<Expander>().Single(control => control.Name == "AdvancedOptionsExpander").IsExpanded = true;
            await WaitFor(() => Task.FromResult(Descendants(preferences.Content).OfType<ToggleSwitch>()
                .Any(control => control.Name == "AutomationToggle" && control.IsLoaded)), "Expanded automation preferences");
            var toggle = Descendants(preferences.Content).OfType<ToggleSwitch>().Single(control => control.Name == "AutomationToggle");
            Require(!toggle.IsOn, "Automation was enabled without an opt-in.");
            toggle.IsOn = true;
            await WaitFor(() => Task.FromResult(AppRuntime.AppService.Shared.AutomationStatus == AppStrings.Get("AutomationAvailable")),
                "Current-user automation endpoint");
            var accounts = await Send(new("get", Property: "accounts", ID: "accounts"));
            Require(accounts.Success && accounts.ID == "accounts"
                && accounts.Value!.Value.Deserialize<AppShared.Scripting.ScriptingReference[]>(AppShared.Scripting.ScriptingProtocol.Options)!
                    .Any(reference => reference.AccountID == account.AccountID), "Automation did not expose the native account collection.");
            var reference = new AppShared.Scripting.ScriptingReference(AppShared.Scripting.ScriptingObjectKind.Article,
                account.AccountID, FeedID: article.FeedID, ArticleID: article.UniqueID);
            var identity = await Send(new("get", reference, "id"));
            Require(identity.Success && identity.Value!.Value.GetString() == article.UniqueID,
                "Automation used the database article ID instead of the feed-supplied ID.");
            var marked = await Send(new("set", reference, "starred", JsonSerializer.SerializeToElement(!previousStar)));
            Require(marked.Success && !marked.HasValue
                && (await account.Database.FetchArticlesByIDsAsync([article.ArticleID])).Single().Status.Starred == !previousStar,
                "Automation did not persist the requested article status through Account.");
            var forbidden = await Send(new("get", new(AppShared.Scripting.ScriptingObjectKind.Account, account.AccountID), "password"));
            Require(!forbidden.Success && forbidden.Error?.Code == "invalidRequest", "Automation exposed a non-scriptable credential property.");
            toggle.IsOn = false;
            await WaitFor(() => Task.FromResult(AppRuntime.AppService.Shared.AutomationStatus == AppStrings.Get("AutomationDisabled")), "Automation shutdown");
            using var stopped = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            bool unavailable = false;
            try { await stopped.ConnectAsync(cancellation.Token); }
            catch (OperationCanceledException) { unavailable = true; }
            Require(unavailable, "The automation endpoint remained available after disabling it.");
            return new { OptIn = true, CurrentUserTransport = true, NativeArticleID = true, AccountMutation = true,
                CredentialPropertiesRejected = true, DisableStopsEndpoint = true };
        }
        finally
        {
            preferences?.Close();
            AppDefaults.Shared.AutomationEnabled = previous;
            await account.MarkAsync([article.ArticleID], ArticleStatus.Key.Starred, previousStar);
            window.Activate();
        }
    }

    private static async Task<object?> CheckSpeechAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var speech = AppRuntime.AppService.Shared.Speech;
        var requests = new List<string>();
        Task? first = null, second = null;
        await speech.StopAsync();
        speech.ProbePlayback = async (text, ct) =>
        {
            requests.Add(text);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        try
        {
            var account = AccountManager.Shared.DefaultAccount;
            var feed = account.FlattenedFeeds().Single();
            model.IsReaderMode = false;
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "probe-article");
            await WaitFor(async () => content.ProbeArticleReady && await ScriptBool(content,
                "!!document.getElementById('probe-body')"), "Speech article document");
            Require(await ScriptString(content, """
                (() => {
                    const text = document.createElement('p');
                    text.id = 'speech-selection-probe';
                    text.textContent = 'Speak only this selection.';
                    document.getElementById('bodyContainer').appendChild(text);
                    const range = document.createRange();
                    range.selectNodeContents(text);
                    getSelection().removeAllRanges();
                    getSelection().addRange(range);
                    return getSelection().toString();
                })()
                """) == "Speak only this selection.", "The speech fixture did not select its text.");
            first = content.StartSpeakingAsync();
            await WaitFor(() => Task.FromResult(requests.Count == 1), "Selected text speech request");
            Require(requests[0] == "Speak only this selection." && speech.IsSpeaking,
                "Speech selection mismatch: " + JsonSerializer.Serialize(new { Text = requests[0], speech.IsSpeaking }));
            await speech.StopAsync();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!speech.IsSpeaking, "Stop Speaking retained the speech session.");
            await content.ProbeWebView.ExecuteScriptAsync("getSelection().removeAllRanges();");
            first = content.StartSpeakingAsync();
            await WaitFor(() => Task.FromResult(requests.Count == 2), "Article body speech request");
            Require(requests[1].Contains("WinUI article fixture", StringComparison.Ordinal),
                "Speech did not use the visible article body when there was no selection.");
            var otherOwner = new object();
            second = speech.SpeakAsync(otherOwner, "Another window's speech.");
            await WaitFor(() => Task.FromResult(requests.Count == 3), "Replacement speech request");
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            await speech.StopAsync(content);
            Require(speech.IsSpeaking, "Closing the old reader stopped another reader's speech.");
            await speech.StopAsync(otherOwner);
            await second.WaitAsync(TimeSpan.FromSeconds(5));
            speech.ProbePlayback = (_, _) => Task.FromException(new InvalidOperationException("Fixture voice failure."));
            await content.StartSpeakingAsync();
            Require(!speech.IsSpeaking && model.StatusMessage.Contains("Fixture voice failure.", StringComparison.Ordinal),
                "Speech errors were not reported or left playback active.");
            return new { Selection = true, VisibleBody = true, OnePlayback = true, OwnerScopedStop = true,
                Cancellation = true, FailureReported = true, NoAudioPlayed = true };
        }
        finally
        {
            await speech.StopAsync();
            speech.ProbePlayback = null;
            await content.ProbeWebView.ExecuteScriptAsync("document.getElementById('speech-selection-probe')?.remove(); getSelection()?.removeAllRanges();");
            await content.RestoreStateAsync(original);
        }
    }

    private static async Task WaitFor(Func<Task<bool>> predicate, string operation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            if (await predicate()) return;
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException(operation + " did not complete.");
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static readonly string[] SingleLineCorpus =
    [
        "", "x", "Today", "Yesterday", "Jan 5", "5:42 PM",
        "December 31, 2026 at 11:59 PM", "Daring Fireball", "NetNewsWire Blog", "The Verge \u2014 Tech",
        "A fairly long single-line feed name that goes on for a while",
        "Caf\u00E9 r\u00E9sum\u00E9 \u2014 \u041C\u043E\u0441\u043A\u0432\u0430 \u6771\u4EAC \U0001F389",
        "J. R. R. Tolkien", "12,345 unread"
    ];
    private static readonly string[] MultilineCorpus =
    [
        "Short",
        "A medium length headline that probably wraps once at narrow widths",
        "This is a considerably longer summary of the kind that appears in the timeline when an article has a lot of text and needs several lines to lay out so that wrapping behavior really matters for the height calculation",
        "Title with an explicit\nnewline in the middle",
        "Supercalifragilisticexpialidocious antidisestablishmentarianism pneumonoultramicroscopicsilicovolcanoconiosis",
        "Caf\u00E9 r\u00E9sum\u00E9 na\u00EFve \u2014 \u041C\u043E\u0441\u043A\u0432\u0430 \u6771\u4EAC \u2014 emoji \U0001F389 mixed unicode content that wraps",
        "One two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty",
        "", "x", "Line one\nLine two\nLine three\nLine four\nLine five",
        "Apple announces the next generation of its flagship product line at a special event this fall",
        "Breaking: a remarkably long single sentence with no punctuation that just keeps going on and on so that it has to wrap several times across even a wide timeline column without any natural break points to help it",
        "Trailing whitespace and tabs\t\tand more   ",
        "iOS 26, macOS 26, and the new unified design language across every Apple platform",
        "Q&A: how the team rebuilt the rendering pipeline from scratch \u2014 part one of three",
        "A title. Another sentence. And a third one that pushes it onto a second line at most widths."
    ];

    private sealed class FixtureServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _task;
        private readonly List<Task> _requests = [];
        private readonly bool _holdFavicon;
        private readonly bool _holdWebPage;
        public string Url { get; }
        public TaskCompletionSource FaviconRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFavicon { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WebPageRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWebPage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FixtureServer(bool holdFavicon = false, bool holdWebPage = false)
        {
            _holdFavicon = holdFavicon;
            _holdWebPage = holdWebPage;
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _task = ServeAsync();
        }
        private async Task ServeAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    _requests.Add(HandleClientAsync(client));
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
            catch (SocketException) when (_cancellation.IsCancellationRequested) { }
        }
        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var request = await reader.ReadLineAsync(_cancellation.Token);
                    if (request is null) return;
                    while (await reader.ReadLineAsync(_cancellation.Token) is { Length: > 0 }) { }
                    var path = request?.Split(' ').ElementAtOrDefault(1);
                    var icon = path is "/icon.png" or "/favicon.ico";
                    if (path == "/favicon.ico" && _holdFavicon)
                    {
                        FaviconRequested.TrySetResult();
                        await ReleaseFavicon.Task.WaitAsync(_cancellation.Token);
                    }
                    if (path == "/navigation-held" && _holdWebPage)
                    {
                        WebPageRequested.TrySetResult();
                        await ReleaseWebPage.Task.WaitAsync(_cancellation.Token);
                    }
                    bool bugbashFeed = path?.StartsWith("/bugbash-feed", StringComparison.Ordinal) == true;
                    bool feed = path == "/reading-feed" || bugbashFeed;
                    bool forbidden = path?.StartsWith("/reading/", StringComparison.Ordinal) == true;
                    var body = "<html><head><title>Reader fixture</title></head><body><article><h1>Reader fixture</h1>"
                            + string.Concat(Enumerable.Repeat("<p>Reader fixture paragraph with substantial readable text. This article describes a complete local verification of the reader presentation, article typography, and selected theme. Several sentences provide the content needed by the readability algorithm, without involving any public website or user account.</p>", 12))
                            + "</article></body></html>";
                    bool redirect = path == "/navigation-redirect";
                    if (path is "/navigation-first" or "/navigation-second")
                        body = "<!doctype html><html><head><title>Publisher page</title></head><body>"
                            + "<h1>Publisher page " + (path == "/navigation-first" ? "one" : "two") + "</h1>"
                            + "<input id='page-input'><p><a id='next-page' href='/navigation-second'>Next page</a></p>"
                            + "<p><a id='popup-page' href='/navigation-second' target='_blank'>New window link</a></p>"
                            + "<script>window.publisherPageScriptRan = true;</script></body></html>";
                    if (path is "/navigation-old" or "/navigation-held" or "/navigation-new")
                        body = "<!doctype html><html><head><title>Presentation fixture</title></head><body style='margin:0;background:"
                            + (path == "/navigation-old" ? "rgb(255,0,0)" : path == "/navigation-held" ? "rgb(0,0,255)" : "rgb(0,180,0)")
                            + ";min-height:2000px'><input id='page-input' value='initial'></body></html>";
                    if (path is "/scroll-article" or "/scroll-second")
                        body = "<!doctype html><html><head><title>Controlled scrolling page</title><style>"
                            + "html{overflow-y:scroll;scroll-behavior:auto}body{margin:0;background:#f8f8f8;color:#202020;"
                            + "font:18px/1.6 sans-serif}main{max-width:48rem;margin:auto;padding:32px}"
                            + "img{width:96px;height:96px;float:right;margin:8px}p{min-height:96px}</style></head><body><main>"
                            + "<h1>Feed refresh scrolling</h1>"
                            + string.Concat(Enumerable.Range(0, 100).Select(index =>
                                $"<p><img src='/icon.png' alt=''>Section {index}: a stable, local browser surface for measuring real scrolling during feed updates.</p>"))
                            + "</main></body></html>";
                    var feedBody = feed ? ReadingFeed() : "";
                    if (bugbashFeed) feedBody = feedBody.Replace(Url + "/reading/", Url + "/navigation-first?article=", StringComparison.Ordinal);
                    var bytes = icon ? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==")
                        : Encoding.UTF8.GetBytes(feed ? feedBody : body);
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {(forbidden ? "403 Forbidden" : redirect ? "302 Found" : "200 OK")}\r\n"
                        + (redirect ? "Location: /navigation-second\r\n" : "")
                        + $"Content-Type: {(icon ? "image/png" : feed ? "application/atom+xml" : "text/html; charset=utf-8")}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _cancellation.Token);
                    await stream.WriteAsync(bytes, _cancellation.Token);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
                catch (IOException error) when (error.InnerException is SocketException
                    { SocketErrorCode: SocketError.ConnectionAborted or SocketError.ConnectionReset })
                {
                    System.Diagnostics.Trace.TraceInformation("Fixture client disconnected: " + error.Message);
                }
            }
        }
        private string ReadingFeed()
        {
            var body = "<div id='readable-feed-body'>" + string.Concat(Enumerable.Repeat(
                "<p>This article body was delivered in the feed. Reading it does not require access to the publisher's webpage.</p>", 40)) + "</div>";
            return "<feed xmlns='http://www.w3.org/2005/Atom' xmlns:media='http://search.yahoo.com/mrss/'>"
                + "<title>Reading regression feed</title><id>urn:reading-regression</id>"
                + $"<link rel='alternate' href='{Url}/reading/'/><updated>2026-10-18T12:00:00Z</updated>"
                + string.Concat(Enumerable.Range(0, 25).Select(index =>
                    $"<entry><id>urn:reading:{index}</id><content type='html'>{WebUtility.HtmlEncode(body)}</content>"
                    + "<author><name>Fixture author</name></author>"
                    + "<media:content url='https://example.invalid/photo.jpg'><media:credit>Fixture credit</media:credit></media:content>"
                    + $"<title type='html'>Reading fixture {index:D2}</title><published>2026-10-18T12:00:00Z</published>"
                    + $"<summary type='html'>Fixture summary</summary><link rel='alternate' href='{Url}/reading/{index}'/></entry>"))
                + "</feed>";
        }
        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            await _task;
            await Task.WhenAll(_requests);
            _cancellation.Dispose();
        }
    }
}
#endif
