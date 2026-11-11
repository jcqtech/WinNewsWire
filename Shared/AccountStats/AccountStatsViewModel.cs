using WinNewsWire.Account;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.AccountStats;

public sealed record AccountStatsRowData(string AccountID, string Name, string TypeName, bool IsActive,
    long FeedCount, long FolderCount, long ArticleCount, long StatusesCount, long UnreadCount, long StarredCount, long DatabaseSizeBytes);

public sealed record AccountStatsTotals(long FeedCount, long FolderCount, long ArticleCount, long StatusesCount,
    long UnreadCount, long StarredCount, long DatabaseSizeBytes)
{
    public static AccountStatsTotals FromRows(IReadOnlyCollection<AccountStatsRowData> rows)
        => new(rows.Sum(row => row.FeedCount), rows.Sum(row => row.FolderCount), rows.Sum(row => row.ArticleCount),
            rows.Sum(row => row.StatusesCount), rows.Sum(row => row.UnreadCount), rows.Sum(row => row.StarredCount),
            rows.Sum(row => row.DatabaseSizeBytes));
}

public enum AccountStatsSortKey { Name, DatabaseSizeBytes, FeedCount, FolderCount, ArticleCount, StatusesCount, UnreadCount, StarredCount }

public sealed class AccountStatsViewModel(AccountManager manager)
{
    private IReadOnlyList<AccountStatsRowData> _rows = [];
    public IReadOnlyList<AccountStatsRowData> SortedAccountStats { get; private set; } = [];
    public AccountStatsTotals Totals { get; private set; } = new(0, 0, 0, 0, 0, 0, 0);
    public AccountStatsSortKey SortKey { get; set; } = AccountStatsSortKey.Name;
    public bool Descending { get; set; }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var rows = new List<AccountStatsRowData>();
        foreach (var account in manager.SortedAccounts)
        {
            ct.ThrowIfCancellationRequested();
            var counts = await account.FetchArticleCountsAsync(ct);
            rows.Add(new(account.AccountID, account.NameForDisplay, account.Type.DisplayName(), account.IsActive,
                account.FlattenedFeeds().LongCount(), account.Folders.Count, counts.TotalCount, counts.StatusesCount,
                counts.UnreadCount, counts.StarredCount, DatabaseSize(account)));
        }
        _rows = rows;
        Totals = AccountStatsTotals.FromRows(rows);
        ApplySort();
    }

    public void ApplySort()
    {
        if (SortKey == AccountStatsSortKey.Name)
        {
            SortedAccountStats = (Descending
                ? _rows.OrderByDescending(row => row.Name, LocalizedStandardComparer.Shared)
                : _rows.OrderBy(row => row.Name, LocalizedStandardComparer.Shared)).ToArray();
            return;
        }
        Func<AccountStatsRowData, long> key = SortKey switch
        {
            AccountStatsSortKey.DatabaseSizeBytes => row => row.DatabaseSizeBytes,
            AccountStatsSortKey.FeedCount => row => row.FeedCount,
            AccountStatsSortKey.FolderCount => row => row.FolderCount,
            AccountStatsSortKey.ArticleCount => row => row.ArticleCount,
            AccountStatsSortKey.StatusesCount => row => row.StatusesCount,
            AccountStatsSortKey.UnreadCount => row => row.UnreadCount,
            AccountStatsSortKey.StarredCount => row => row.StarredCount,
            _ => throw new ArgumentOutOfRangeException(nameof(SortKey))
        };
        SortedAccountStats = (Descending ? _rows.OrderByDescending(key) : _rows.OrderBy(key)).ToArray();
    }

    private static long DatabaseSize(Account.Account account)
    {
        long size = 0;
        foreach (var name in new[] { "DB.sqlite3", "FeedSettings.db", "Sync.sqlite3" })
        {
            try { size += new FileInfo(Path.Combine(account.AccountDirectory, name)).Length; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return size;
    }
}
