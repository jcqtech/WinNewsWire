namespace WinNewsWire.Account;

/// <summary>
/// Port of NNW <c>CombinedRefreshProgress</c>. Aggregates refresh progress from multiple
/// accounts into a single combined value for use by the UI refresh-status indicator.
/// </summary>
public sealed class CombinedRefreshProgress
{
    public static CombinedRefreshProgress Shared { get; } = new();

    private readonly Dictionary<string, ProgressInfo> _accountProgress = new();
    private ProgressInfo _combined = new(0, 0, 0);
    private bool _isStarted;

    public int NumberOfTasks => _combined.NumberOfTasks;
    public int NumberCompleted => _combined.NumberCompleted;
    public int NumberRemaining => _combined.NumberRemaining;
    public bool IsComplete => !_isStarted || NumberRemaining < 1;

    public event EventHandler<ProgressInfo>? ProgressChanged;

    /// <summary>Begin tracking progress for <paramref name="account"/>.</summary>
    public void AddAccount(Account account)
    {
        _accountProgress[account.AccountID] = new ProgressInfo(0, 0, 0);
    }

    /// <summary>Stop tracking progress for <paramref name="account"/> and recalculate.</summary>
    public void RemoveAccount(Account account)
    {
        if (_accountProgress.Remove(account.AccountID))
            Recalculate();
    }

    /// <summary>Get an <see cref="IProgress{ProgressInfo}"/> reporter that feeds progress for
    /// <paramref name="account"/> into this aggregator.</summary>
    public IProgress<ProgressInfo> GetProgressReporter(Account account)
    {
        return new AccountProgressReporter(this, account.AccountID);
    }

    /// <summary>Clear all per-account progress and reset the combined total.</summary>
    public void Reset()
    {
        foreach (var key in _accountProgress.Keys.ToList())
            _accountProgress[key] = new ProgressInfo(0, 0, 0);
        _combined = new ProgressInfo(0, 0, 0);
        _isStarted = false;
    }

    internal void ReportAccountProgress(string accountID, ProgressInfo info)
    {
        if (!_accountProgress.ContainsKey(accountID)) return;
        _accountProgress[accountID] = info;
        _isStarted = true;
        Recalculate();
    }

    private void Recalculate()
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
        if (updated != _combined)
        {
            _combined = updated;
            ProgressChanged?.Invoke(this, _combined);
        }
    }

    /// <summary>Lightweight <see cref="IProgress{T}"/> that avoids
    /// <see cref="Progress{T}"/>'s SynchronizationContext capture.</summary>
    private sealed class AccountProgressReporter(CombinedRefreshProgress owner, string accountID)
        : IProgress<ProgressInfo>
    {
        public void Report(ProgressInfo value) => owner.ReportAccountProgress(accountID, value);
    }
}
