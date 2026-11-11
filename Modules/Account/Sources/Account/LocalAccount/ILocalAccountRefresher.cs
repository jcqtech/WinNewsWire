using WinNewsWire.Core;

namespace WinNewsWire.Account;

public interface ILocalAccountRefresher : IDisposable, IAsyncDisposable
{
    Task RefreshAllAsync(IProgress<ProgressInfo>? progress, CancellationToken cancellationToken);
    int? ProcessId => null;
    LocalRefreshStatistics? LastStatistics => null;
    bool CanDisposeSynchronously => true;
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed record LocalRefreshStatistics(int ProcessID, int ParsedFeeds, long AllocatedBytes,
    double GcPauseMilliseconds, int NewArticles, int UpdatedArticles, long ClockFrequency,
    IReadOnlyList<long> FeedCompletedTimestamps);
