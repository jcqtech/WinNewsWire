using System.Globalization;
using WinNewsWire.Account;
using WinNewsWire.AppShared.Commands;

namespace WinNewsWire.AppShared.Dinosaurs;

public enum DinosaurSortKey { FeedName, FeedUrl, AccountName, LastArticleDate, LastResponseCode }

public sealed record DinosaurRow(Feed Feed, Account.Account Account, string AccountName, string FeedName,
    string FeedUrl, DateTime? LastArticleDate, int? LastResponseCode)
{
    public string Id => FeedUrl + Account.AccountID;
}

public sealed class DinosaursViewModel
{
    private readonly Func<IEnumerable<Account.Account>> _accounts;
    private readonly TimeProvider _clock;
    private int _monthThreshold = 6;
    private int _generation;
    public IReadOnlyList<DinosaurRow> Rows { get; private set; } = [];
    public bool ShowAccountColumn { get; private set; }
    public DinosaurSortKey SortKey { get; private set; } = DinosaurSortKey.FeedName;
    public bool Ascending { get; private set; } = true;

    public int MonthThreshold
    {
        get => _monthThreshold;
        set
        {
            if (value is < 1 or > 350) throw new ArgumentOutOfRangeException(nameof(value));
            _monthThreshold = value;
        }
    }

    public DinosaursViewModel(AccountManager manager, TimeProvider? clock = null)
        : this(() => manager.ActiveAccounts, clock) { }

    internal DinosaursViewModel(Func<IEnumerable<Account.Account>> accounts, TimeProvider? clock = null)
    {
        _accounts = accounts;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        int generation = ++_generation;
        var accounts = _accounts().Where(account => account.IsActive).ToArray();
        var cutoff = CutoffDate(_clock.GetUtcNow(), _clock.LocalTimeZone, MonthThreshold);
        var rows = new List<DinosaurRow>();
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            var dates = await account.FetchLastUpdateDatesAsync(ct);
            foreach (var feed in account.FlattenedFeeds())
                if (dates.TryGetValue(feed.FeedID, out var latest) && latest < cutoff)
                    rows.Add(new(feed, account, account.NameForDisplay, feed.NameForDisplay, feed.Url, latest, feed.LastResponseCode));
        }
        ct.ThrowIfCancellationRequested();
        if (generation != _generation) return;
        Rows = rows;
        ShowAccountColumn = accounts.Length > 1;
        ApplySort();
    }

    internal static DateTime CutoffDate(DateTimeOffset now, TimeZoneInfo zone, int months)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var cutoff = CultureInfo.CurrentCulture.DateTimeFormat.Calendar.AddMonths(local, -months);
        var offset = zone.IsAmbiguousTime(cutoff) ? zone.GetAmbiguousTimeOffsets(cutoff).Max() : zone.GetUtcOffset(cutoff);
        return new DateTimeOffset(cutoff, offset).UtcDateTime;
    }

    public void Clear()
    {
        _generation++;
        Rows = [];
    }

    public void SortBy(DinosaurSortKey key, bool ascending)
    {
        if (!Enum.IsDefined(key)) throw new ArgumentOutOfRangeException(nameof(key));
        SortKey = key;
        Ascending = ascending;
        ApplySort();
    }

    private void ApplySort()
    {
        var rows = Rows.ToArray();
        Array.Sort(rows, (left, right) => Compare(left, right, SortKey, Ascending));
        Rows = rows;
    }

    internal static int Compare(DinosaurRow left, DinosaurRow right, DinosaurSortKey key, bool ascending)
    {
        int result = key switch
        {
            DinosaurSortKey.FeedName => StringComparer.CurrentCultureIgnoreCase.Compare(left.FeedName, right.FeedName),
            DinosaurSortKey.FeedUrl => StringComparer.CurrentCultureIgnoreCase.Compare(left.FeedUrl, right.FeedUrl),
            DinosaurSortKey.AccountName => StringComparer.CurrentCultureIgnoreCase.Compare(left.AccountName, right.AccountName),
            DinosaurSortKey.LastArticleDate => Nullable.Compare(left.LastArticleDate, right.LastArticleDate),
            DinosaurSortKey.LastResponseCode => Nullable.Compare(left.LastResponseCode, right.LastResponseCode),
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        if (result == 0 && key != DinosaurSortKey.FeedUrl)
            result = StringComparer.CurrentCultureIgnoreCase.Compare(left.FeedUrl, right.FeedUrl);
        return ascending ? result : -Math.Sign(result);
    }

    public DeleteCommand? CreateDeletionCommand(IEnumerable<DinosaurRow> selected)
    {
        var active = _accounts().Where(account => account.IsActive).ToHashSet();
        var feeds = selected.DistinctBy(row => row.Id).Select(row =>
        {
            if (!Rows.Any(current => current.Id == row.Id) || !active.Contains(row.Account))
                throw new InvalidOperationException("The selected stale feed is no longer available.");
            var feed = row.Account.ExistingFeedWithUrl(row.FeedUrl)
                ?? throw new InvalidOperationException("The selected stale feed is no longer subscribed.");
            return (row.Account, feed);
        }).ToArray();
        return DeleteCommand.ForAllFeedMemberships(feeds);
    }
}
