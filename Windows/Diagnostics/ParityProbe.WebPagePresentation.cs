#if PARITY_PROBE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using WinNewsWire.Account;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.Web;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckWebPageReloadAsync(MainWindow window)
    {
        await using var server = new FixtureServer(holdWebPage: true);
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var error = (InfoBar)content.FindName("WebPageError");
        var loading = (Border)content.FindName("WebPageLoadingState");
        var progress = (ProgressBar)content.FindName("WebPageProgress");
        var requested = new List<string>();
        CoreWebView2? core = null;
        void Starting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) => requested.Add(args.Uri);
        async Task Offline(bool offline) => await core!.CallDevToolsProtocolMethodAsync("Network.emulateNetworkConditions",
            JsonSerializer.Serialize(new { offline, latency = 0, downloadThroughput = -1, uploadThroughput = -1 }));
        try
        {
            await content.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
            var account = AccountManager.Shared.DefaultAccount;
            var feed = account.FlattenedFeeds().Single();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "probe-article");
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Retry fixture reader");
            string selectedLink = model.SelectedArticle.Link;
            await content.ProbeOpenWebPageAsync(server.Url + "/navigation-old");
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Previous webpage before the failed request");
            await content.GoBackAsync();
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady && !content.ProbeShowingWebPage),
                "Reader before failed webpage");
            core = content.ProbeWebPageView.CoreWebView2;
            core.NavigationStarting += Starting;
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            await Offline(true);
            string target = server.Url + "/navigation-held";
            await content.ProbeOpenWebPageAsync(target);
            await WaitFor(() => Task.FromResult(error.IsOpen), "Failed webpage offers recovery");
            File.WriteAllText(Path.Combine(OutputDirectory, "failed-navigation-history.json"),
                await core.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}"));
            var reload = content.FindName("WebPageReloadButton") as Button
                ?? throw new InvalidOperationException("The failed webpage has no Reload action.");
            await WaitFor(() => Task.FromResult(reload.IsLoaded && reload.ActualWidth > 0 && reload.ActualHeight > 0),
                "Visible inline Reload action");
            Require(ReferenceEquals(error.ActionButton, reload) && reload.IsEnabled && reload.Content as string == "Reload",
                "The web-page error does not expose an enabled Reload button.");
            int history = content.ProbeBackHistoryCount;
            int attempts = requested.Count;
            InvokeButton(reload);
            await WaitFor(() => Task.FromResult(requested.Count > attempts && error.IsOpen), "Reload can fail and remain retryable");
            Require(requested[^1] == target && content.ProbeBackHistoryCount == history
                && model.SelectedArticle.Link == selectedLink && reload.IsEnabled,
                "Reload requested an old page/article or added a reader history entry.");

            await Offline(false);
            InvokeButton(reload);
            await server.WebPageRequested.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await WaitFor(() => Task.FromResult(!error.IsOpen && progress.Visibility == Visibility.Visible
                && loading.Visibility == Visibility.Visible && content.ProbeWebPageView.Opacity == 0),
                "Retry clears the error and restores the protected loading state");
            Require(((TextBlock)content.FindName("WebPageLoadingText")).Text == AppShared.AppStrings.Get("WebPageLoading"),
                "A retried request still displays the failed-loading message.");
            server.ReleaseWebPage.TrySetResult();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady && core.Source == target
                && !error.IsOpen && loading.Visibility == Visibility.Collapsed && progress.Visibility == Visibility.Collapsed),
                "Reload reveals the recovered requested page");
            File.WriteAllText(Path.Combine(OutputDirectory, "recovered-navigation-history.json"),
                await core.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}"));
            Require(content.ProbeBackHistoryCount == history && content.ProbeWebPageHistoryCount == 1,
                $"Reload duplicated history: reader={content.ProbeBackHistoryCount}/{history}, pages={content.ProbeWebPageHistoryCount}.");
            await content.GoBackAsync();
            try
            {
                await WaitFor(() => Task.FromResult(content.ProbeArticleReady && !content.ProbeShowingWebPage),
                    "A single Back returns to the original reader");
            }
            catch (TimeoutException failure)
            {
                throw new InvalidOperationException($"Retry Back: webpage={content.ProbeShowingWebPage}, "
                    + $"readerReady={content.ProbeArticleReady}, back={content.ProbeBackHistoryCount}, "
                    + $"pages={content.ProbeWebPageHistoryCount}, source={core.Source}, status={model.StatusMessage}, "
                    + $"native={await core.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}")}", failure);
            }

            await content.ProbeOpenWebPageAsync(server.Url + "/navigation-old");
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Visible page before an in-place navigation failure");
            await core.ExecuteScriptAsync("document.getElementById('page-input').value='preserved after retry';");
            await Offline(true);
            string next = server.Url + "/navigation-new";
            await content.ProbeOpenWebPageAsync(next);
            await WaitFor(() => Task.FromResult(error.IsOpen), "Failed navigation from an existing webpage");
            await Offline(false);
            InvokeButton(reload);
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady && !error.IsOpen && core.Source == next),
                "Reload retries a failed in-place navigation");
            Require(requested[^1] == next, "An in-place retry reloaded the previous webpage.");
            await core.ExecuteScriptAsync("history.pushState({}, '', '/navigation-new?after-retry=1');");
            await WaitFor(() => Task.FromResult(core.Source == next + "?after-retry=1" && content.ProbeWebPageHistoryCount == 3),
                "Same-document navigation after reload adds its own history entry");
            await content.GoBackAsync();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady && core.Source == next),
                "Back preserves same-document history after retry");
            await content.GoBackAsync();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady && core.Source == server.Url + "/navigation-old"),
                "Back after in-place retry restores the previous webpage");
            Require(JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(
                "document.getElementById('page-input').value")) == "preserved after retry",
                "Reload recovery discarded the previous page's native form state.");
            return new { InlineReload = true, RepeatedFailureRetryable = true, FailedUrlRetried = true,
                LoadingStateRestored = true, NoStalePageExposure = true, Recovered = true, HistoryPreserved = true,
                InPlaceNavigationRetry = true, NativeActionInvoked = true };
        }
        finally
        {
            server.ReleaseWebPage.TrySetResult();
            if (core is not null)
            {
                await Offline(false);
                await core.CallDevToolsProtocolMethodAsync("Network.disable", "{}");
                core.NavigationStarting -= Starting;
            }
            await content.RestoreStateAsync(original);
        }
    }

    private static async Task<object?> CheckWebPagePresentationAsync(MainWindow window)
    {
        await using var slow = new FixtureServer(holdWebPage: true);
        await using var cancelled = new FixtureServer(holdWebPage: true);
        await using var replaced = new FixtureServer(holdWebPage: true);
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var originalSearch = model.SearchQuery;
        var oldLauncher = Browser.ProbeLaunch;
        var launches = new List<BrowserLaunchRequest>();
        var account = AccountManager.Shared.DefaultAccount!;
        var feed = account.AddFeed(slow.Url + "/presentation-feed", "Web presentation fixture");
        var loading = (Border)content.FindName("WebPageLoadingState");
        var status = (TextBlock)content.FindName("WebPageStatus");
        var error = (InfoBar)content.FindName("WebPageError");
        global::Windows.ApplicationModel.DataTransfer.DataPackageView? shared = null;
        Article Make(string id, string url) => new(account.AccountID, id, feed.FeedID, id, id,
            "<div style='min-height:3000px'>Stored article for webpage loading.</div>",
            null, null, url, null, null, null, DateTime.UtcNow, null, null, null,
            new ArticleStatus(id, true, false, DateTime.UtcNow));
        await account.Database.UpdateArticlesAsync([
            Make("presentation-old", slow.Url + "/navigation-old"),
            Make("presentation-slow", slow.Url + "/navigation-held"),
            Make("presentation-cancelled", cancelled.Url + "/navigation-held"),
            Make("presentation-replaced", replaced.Url + "/navigation-held"),
            Make("presentation-failed", "http://127.0.0.1:1/unavailable")
        ]);
        async Task Select(string id)
        {
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == id);
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady && !content.ProbeShowingWebPage), "Reader article " + id);
        }
        (byte R, byte G, byte B) Centre()
        {
            var capture = CaptureWindow(window);
            var view = content.ProbeWebPageView;
            var point = view.TransformToVisual((UIElement)window.Content).TransformPoint(new(view.ActualWidth * .75, view.ActualHeight * .7));
            int x = (int)Math.Round(point.X * view.XamlRoot.RasterizationScale) + capture.ClientOrigin.X - capture.Bounds.Left;
            int y = (int)Math.Round(point.Y * view.XamlRoot.RasterizationScale) + capture.ClientOrigin.Y - capture.Bounds.Top;
            int offset = (y * capture.Width + x) * 4;
            return (capture.Data[offset + 2], capture.Data[offset + 1], capture.Data[offset]);
        }
        static bool OldRed((byte R, byte G, byte B) pixel) => pixel.R > 200 && pixel.G < 60 && pixel.B < 60;
        static bool OldBlue((byte R, byte G, byte B) pixel) => pixel.B > 200 && pixel.R < 60 && pixel.G < 60;
        static bool OldGreen((byte R, byte G, byte B) pixel) => pixel.G > 140 && pixel.R < 60 && pixel.B < 60;
        void Covered(string uri)
        {
            Require(content.ProbeShowingWebPage && loading.Visibility == Visibility.Visible
                && loading.Background is SolidColorBrush { Color.A: 255 }
                && content.ProbeWebPageView.Opacity == 0
                && !content.ProbeWebPageView.IsHitTestVisible && !content.HasDisplayedDocument,
                "The previous webpage is not blocked by an opaque loading surface.");
            Require(status.Text == uri, "The loading footer still shows the previous webpage address.");
            var pixel = Centre();
            Require(!OldRed(pixel) && !OldBlue(pixel) && !OldGreen(pixel), "The old webpage is visible through the loading surface.");
        }
        try
        {
            Browser.ProbeLaunch = launches.Add;
            content.ProbeShareWriter = data => shared = data.GetView();
            model.SearchQuery = "";
            model.IsReaderMode = false;
            model.BuildSidebar();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.ProbeDrainViewUpdatesAsync();
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            if (model.IsReadFiltered) await model.ToggleReadFilterCommand.ExecuteAsync(null);
            await Select("presentation-old");
            await content.OpenSelectedInWebViewAsync();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady && OldRed(Centre())), "Visible original red webpage");
            await content.ProbeWebPageView.ExecuteScriptAsync("document.getElementById('page-input').value='preserved';");
            await Select("presentation-slow");
            await content.ProbeWebView.ExecuteScriptAsync(
                "const link=document.createElement('a');link.href=" + JsonSerializer.Serialize(slow.Url + "/navigation-held")
                + ";link.textContent='Open delayed page';link.style='position:fixed;left:10px;top:10px;width:180px;height:32px;z-index:9999';document.body.appendChild(link);");
            var reader = content.ProbeWebView.CoreWebView2;
            await reader.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                """{"type":"mousePressed","x":20,"y":20,"button":"left","clickCount":1}""");
            await reader.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                """{"type":"mouseReleased","x":20,"y":20,"button":"left","clickCount":1}""");
            await slow.WebPageRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100);
            Covered(slow.Url + "/navigation-held");
            content.OpenSelectedInBrowser();
            await WaitFor(() => Task.FromResult(launches.Count == 1), "External opening while a webpage is pending");
            Require(launches[0].Url.AbsoluteUri == slow.Url + "/navigation-held", "Pending Open in Browser used a stale URL.");
            content.ShareSelected();
            Require(shared is not null && (await shared.GetWebLinkAsync()).AbsoluteUri == slow.Url + "/navigation-held"
                && shared.Properties.Title != "Presentation fixture", "Pending Share used the previous page's metadata.");
            var colors = new List<(byte R, byte G, byte B)>();
            bool sampling = true, queued = false;
            void Rendering(object? sender, object args)
            {
                if (queued) return;
                queued = true;
                window.DispatcherQueue.TryEnqueue(() =>
                {
                    queued = false;
                    if (sampling) colors.Add(Centre());
                });
            }
            CompositionTarget.Rendering += Rendering;
            try
            {
                slow.ReleaseWebPage.TrySetResult();
                await WaitFor(() => Task.FromResult(content.ProbeWebPageReady && OldBlue(Centre())), "Requested blue webpage revealed");
            }
            finally { sampling = false; CompositionTarget.Rendering -= Rendering; }
            Require(colors.Count > 0 && !colors.Any(OldRed) && loading.Visibility == Visibility.Collapsed && content.ProbeWebPageHistoryCount == 1,
                "Revealing the requested page flashed old pixels or inserted a blank history entry.");
            await SaveWindowScreenshotAsync(window, "webpage-requested-content");

            await content.GoBackAsync();
            await content.GoBackAsync();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady && OldRed(Centre())), "Previous webpage restored through native history");
            Require(JsonSerializer.Deserialize<string>(await content.ProbeWebPageView.ExecuteScriptAsync(
                "document.getElementById('page-input').value")) == "preserved", "The loading surface discarded native webpage form state.");
            await Select("presentation-cancelled");
            await content.OpenSelectedInWebViewAsync();
            await cancelled.WebPageRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100);
            Covered(cancelled.Url + "/navigation-held");
            await content.GoBackAsync();
            cancelled.ReleaseWebPage.TrySetResult();
            await Task.Delay(300);
            Require(!content.ProbeShowingWebPage && loading.Visibility == Visibility.Collapsed
                && model.SelectedArticle?.Id == "presentation-cancelled", "A cancelled response replaced the restored article.");

            await Select("presentation-replaced");
            await content.OpenSelectedInWebViewAsync();
            await replaced.WebPageRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            model.SelectedArticle!.Link = replaced.Url + "/navigation-new";
            await content.OpenSelectedInWebViewAsync();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady
                && content.ProbeWebPageView.CoreWebView2.Source == replaced.Url + "/navigation-new"), "Superseding webpage");
            replaced.ReleaseWebPage.TrySetResult();
            await Task.Delay(300);
            Require(content.ProbeWebPageReady && Centre().G > 140 && Centre().B < 80, "The superseded response revealed the wrong page.");

            await content.GoBackAsync();
            await Select("presentation-failed");
            await content.OpenSelectedInWebViewAsync();
            await WaitFor(() => Task.FromResult(error.IsOpen), "Explicit failed webpage state");
            Covered("http://127.0.0.1:1/unavailable");
            await content.GoBackAsync();
            Require(!content.ProbeShowingWebPage && loading.Visibility == Visibility.Collapsed, "Back could not leave a failed page.");
            return new { SlowLinkNeverShowsOldPage = true, OpaqueLoadingPixels = true, MatchingPageRevealed = true,
                PendingCommandsUseRequestedUrl = true, CancelledResponseIgnored = true, SupersededResponseIgnored = true,
                FailureDoesNotExposeOldContent = true, BackRetained = true, NativeFormStateRetained = true, NoBlankHistoryEntries = true };
        }
        finally
        {
            slow.ReleaseWebPage.TrySetResult();
            cancelled.ReleaseWebPage.TrySetResult();
            replaced.ReleaseWebPage.TrySetResult();
            content.ProbeShareWriter = null;
            Browser.ProbeLaunch = oldLauncher;
            account.RemoveFeed(feed);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            model.SearchQuery = originalSearch;
            if (model.SelectedArticle is not null) await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Restored reader after page-loading check");
        }
    }
}
#endif
