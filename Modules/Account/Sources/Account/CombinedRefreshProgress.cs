using WinNewsWire.Core;

namespace WinNewsWire.Account;

/// <summary>
/// Port of NNW <c>CombinedRefreshProgress</c>. Aggregates refresh progress from multiple
/// accounts into a single combined value for use by the UI refresh-status indicator.
/// </summary>
public sealed class CombinedRefreshProgress
{
    public static CombinedRefreshProgress Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, ProgressInfo> _accountProgress = new();
    private readonly HashSet<string> _refreshingAccounts = new();
    private ProgressInfo _combined = new(0, 0, 0);
    private bool _isStarted;
    private long _generation;

    public ProgressInfo Snapshot { get { lock (_gate) return _combined; } }
    public int NumberOfTasks => Snapshot.NumberOfTasks;
    public int NumberCompleted => Snapshot.NumberCompleted;
    public int NumberRemaining => Snapshot.NumberRemaining;
    public bool IsComplete { get { lock (_gate) return !_isStarted || _combined.NumberRemaining < 1; } }

    public event EventHandler<ProgressInfo>? ProgressChanged;

    /// <summary>Begin tracking progress for <paramref name="account"/>.</summary>
    public void AddAccount(Account account)
    {
        lock (_gate) _accountProgress[account.AccountID] = default;
    }

    /// <summary>Stop tracking progress for <paramref name="account"/> and recalculate.</summary>
    public void RemoveAccount(Account account)
    {
        ProgressInfo? updated;
        lock (_gate)
        {
            if (!_accountProgress.Remove(account.AccountID)) return;
            _refreshingAccounts.Remove(account.AccountID);
            updated = Recalculate();
        }
        Notify(updated);
    }

    /// <summary>Get an <see cref="IProgress{ProgressInfo}"/> reporter that feeds progress for
    /// <paramref name="account"/> into this aggregator.</summary>
    public IProgress<ProgressInfo> GetProgressReporter(Account account)
    {
        return new AccountProgressReporter(this, account.AccountID, generation: null);
    }

    /// <summary>Clear all per-account progress and reset the combined total.</summary>
    public void Reset()
    {
        lock (_gate) ResetCore();
        Notify(default(ProgressInfo));
    }

    internal void ReportAccountProgress(string accountID, ProgressInfo info)
    {
        ProgressInfo? updated;
        lock (_gate) updated = ReportCore(accountID, info);
        Notify(updated);
    }

    internal AccountProgressReporter BeginRefresh(string accountID, IProgress<ProgressInfo>? observer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountID);
        ProgressInfo snapshot;
        AccountProgressReporter reporter;
        lock (_gate)
        {
            if (_refreshingAccounts.Count == 0) ResetCore();
            if (!_refreshingAccounts.Add(accountID))
                throw new InvalidOperationException("The account already has an active refresh progress session.");
            _accountProgress[accountID] = default;
            snapshot = _combined;
            reporter = new AccountProgressReporter(this, accountID, _generation, observer, completesRefresh: true);
        }
        Notify(snapshot);
        return reporter;
    }

    private void ResetCore()
    {
        _generation++;
        foreach (var key in _accountProgress.Keys.ToArray()) _accountProgress[key] = default;
        _refreshingAccounts.Clear();
        _combined = default;
        _isStarted = false;
    }

    private ProgressInfo? ReportCore(string accountID, ProgressInfo info)
    {
        if (!_accountProgress.ContainsKey(accountID)) return null;
        _accountProgress[accountID] = info;
        _isStarted = true;
        return Recalculate();
    }

    private ProgressInfo? Recalculate()
    {
        int totalTasks = 0, totalCompleted = 0;
        foreach (var p in _accountProgress.Values)
        {
            totalTasks += p.NumberOfTasks;
            totalCompleted += p.NumberCompleted;
        }

        // Monotonic-increase logic ported from NNW: tasks and completed never decrease
        // so the progress bar doesn't jump backwards.
        if (totalTasks < _combined.NumberOfTasks)
            totalTasks = _combined.NumberOfTasks;
        if (totalCompleted < _combined.NumberCompleted)
            totalCompleted = _combined.NumberCompleted;
        if (totalCompleted > totalTasks)
            totalTasks = totalCompleted;

        int remaining = totalTasks - totalCompleted;
        var updated = new ProgressInfo(remaining, totalCompleted, totalTasks);
        if (updated == _combined) return null;
        _combined = updated;
        return updated;
    }

    private void Notify(ProgressInfo? info)
    {
        if (info is { } updated) ProgressChanged?.Invoke(this, updated);
    }

    /// <summary>Lightweight <see cref="IProgress{T}"/> that avoids
    /// <see cref="Progress{T}"/>'s SynchronizationContext capture.</summary>
    internal sealed class AccountProgressReporter(CombinedRefreshProgress owner, string accountID, long? generation,
        IProgress<ProgressInfo>? observer = null, bool completesRefresh = false) : IProgress<ProgressInfo>, IDisposable
    {
        private bool _disposed;

        public void Report(ProgressInfo value)
        {
            ProgressInfo? updated;
            lock (owner._gate)
            {
                if (_disposed || generation is { } expected && expected != owner._generation) return;
                updated = owner.ReportCore(accountID, value);
            }
            owner.Notify(updated);
            observer?.Report(value);
        }

        public void Dispose()
        {
            ProgressInfo? updated = null;
            lock (owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (completesRefresh && generation == owner._generation && owner._refreshingAccounts.Remove(accountID))
                {
                    var progress = owner._accountProgress[accountID];
                    // The delegate may save completion metadata after its last progress report.
                    updated = owner.ReportCore(accountID, new ProgressInfo(0, progress.NumberOfTasks, progress.NumberOfTasks))
                        ?? owner._combined;
                }
            }
            owner.Notify(updated);
        }
    }
}
