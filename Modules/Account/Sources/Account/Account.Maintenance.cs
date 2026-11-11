using WinNewsWire.ActivityLog;
using WinNewsWire.ArticlesDatabase;

namespace WinNewsWire.Account;

public sealed partial class Account
{
    public Task<ArticleCounts> FetchArticleCountsAsync(CancellationToken ct = default)
        => RunOperationAsync(token => Database.FetchArticleCountsAsync(FlattenedFeeds().Select(feed => feed.FeedID), token), ct);

    public Task<Dictionary<string, DateTime>> FetchLastUpdateDatesAsync(CancellationToken ct = default)
        => RunOperationAsync(Database.FetchLastUpdateDatesAsync, ct);

    public Task VacuumDatabasesAsync(CancellationToken ct = default)
        => RunOperationAsync(async token =>
        {
            await StatusMutationGate.WaitAsync(token);
            try
            {
                await LogActivityAsync(ActivityKind.VacuumDatabase, () => Database.VacuumAsync(token), detail: DatabaseDetail("DB.sqlite3"));
                await LogActivityAsync(ActivityKind.VacuumDatabase, () => FeedSettingsDatabase.VacuumAsync(token), detail: DatabaseDetail("FeedSettings.db"));
                token.ThrowIfCancellationRequested();
                await Delegate.VacuumDatabasesAsync(this, token);
            }
            finally { StatusMutationGate.Release(); }
        }, ct);

    internal Task VacuumSyncDatabaseAsync(CancellationToken ct)
        => LogActivityAsync(ActivityKind.VacuumDatabase, () => SyncDatabase.VacuumAsync(ct), detail: DatabaseDetail("Sync.sqlite3"));

    private string DatabaseDetail(string name) => Path.Combine("Accounts", AccountID, name);
}
