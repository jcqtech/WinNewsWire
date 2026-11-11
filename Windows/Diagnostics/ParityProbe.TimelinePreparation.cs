#if PARITY_PROBE
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task CheckTimelinePreparationAsync(MainWindow window)
    {
        var model = window.ProbeContent.ViewModel;
        await model.WaitForCurrentTimelineAsync(CancellationToken.None);
        string query = model.SearchQuery;
        var selection = model.SelectedArticles.Select(item => (item.AccountID, item.Id)).ToHashSet();
        var collection = model.ArticleItems;
        var source = model.ArticleItems.FirstOrDefault(item => item.SourceArticle is not null)?.SourceArticle
            ?? throw new InvalidOperationException("The timeline preparation check requires a loaded article.");
        var lazy = new Models.FeedItem { SourceArticle = source, AccountID = source.AccountID, Id = source.ArticleID };
        Require(!lazy.ProbeHasFormattedText, "An off-screen row eagerly formatted its presentation.");
        Require(lazy.Title == AppShared.Extensions.ArticleStringFormatter.Shared.TruncatedTitle(source)
            && lazy.Summary == AppShared.Extensions.ArticleStringFormatter.Shared.TruncatedSummary(source)
            && lazy.FormattedTitle == AppShared.Extensions.ArticleStringFormatter.Shared.TruncatedTitle(source, forHtml: true)
            && ReferenceEquals(lazy.AttributedTitle, AppShared.Extensions.ArticleStringFormatter.Shared.AttributedTruncatedTitle(source)),
            "Deferred row presentation changed article formatting.");
        bool columnTitleChanged = false;
        lazy.PropertyChanged += (_, args) => columnTitleChanged |= args.PropertyName == nameof(lazy.ColumnTitle);
        lazy.Title = "";
        lazy.Summary = "Deferred   summary";
        Require(columnTitleChanged && lazy.ColumnTitle == "Deferred summary", "Deferred row setters lost column-title notifications.");
        var refreshed = new Articles.Article(source.AccountID, source.ArticleID, source.FeedID, source.UniqueID,
            "Refreshed title", "<p>Refreshed body</p>", null, null, source.RawLink, source.RawExternalLink, null, null,
            source.DatePublished, source.DateModified, source.Authors, source.Attachments, source.Status);
        lazy.UpdateFrom(new Models.FeedItem { SourceArticle = refreshed, AccountID = source.AccountID, Id = source.ArticleID });
        Require(ReferenceEquals(lazy.SourceArticle, refreshed) && lazy.Title == "Refreshed title"
            && lazy.Summary == "Refreshed body", "In-place row updates retained stale article content.");

        async Task HoldPreparationAsync(Func<Task> interact)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            int calls = 0;
            bool preparedOnUi = true;
            model.ProbePreparingTimeline = () =>
            {
                if (Interlocked.Increment(ref calls) != 1) return;
                preparedOnUi = window.DispatcherQueue.HasThreadAccess;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The held timeline preparation was not released.");
            };
            var pending = model.FilterArticlesAsync();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(!preparedOnUi && !pending.IsCompleted, "Timeline preparation ran on the UI dispatcher.");
                var pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Require(window.DispatcherQueue.TryEnqueue(() => pulse.TrySetResult()), "The dispatcher rejected the timeline pulse.");
                await pulse.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await interact();
            }
            finally
            {
                model.ProbePreparingTimeline = null;
                release.Set();
                await pending;
            }
        }

        try
        {
            var next = model.ArticleItems.First(item => !ReferenceEquals(item, model.SelectedArticle));
            await HoldPreparationAsync(async () =>
            {
                model.SelectedArticle = next;
                await model.PendingAutomaticReads;
            });
            Require(ReferenceEquals(model.ArticleItems, collection) && ReferenceEquals(model.SelectedArticle, next),
                "A background refresh discarded the user's newer article selection.");
            await HoldPreparationAsync(async () =>
            {
                model.SearchQuery = "no-match-" + Guid.NewGuid().ToString("N");
                await model.WaitForCurrentTimelineAsync(CancellationToken.None);
                Require(model.ArticleItems.Count == 0, "The newer search did not finish during held preparation.");
            });
            Require(model.ArticleItems.Count == 0, "Stale background preparation replaced the newer search results.");
        }
        finally
        {
            model.ProbePreparingTimeline = null;
            model.SearchQuery = query;
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            model.SetSelectedArticles(model.ArticleItems.Where(item => selection.Contains((item.AccountID, item.Id))).ToArray());
            await model.PendingAutomaticReads;
        }
    }
}
#endif
