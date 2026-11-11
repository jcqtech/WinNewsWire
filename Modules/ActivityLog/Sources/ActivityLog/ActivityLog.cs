using System.Diagnostics;

namespace WinNewsWire.ActivityLog;

public sealed record ActivityLogSnapshot(IReadOnlyList<ActivitySnapshot> Pending, IReadOnlyList<ActivitySnapshot> Running,
    IReadOnlyList<ActivitySnapshot> Completed);

public sealed partial class ActivityLog
{
    public static ActivityLog Shared { get; } = new();
    public const int CompletedActivitiesLimit = 500;
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly List<Activity> _pending = [], _running = [], _completed = [];
    private int _nextID;
    private int _nextTaskNumber = 1;
    public event EventHandler? ActivityDidChange;
    public IReadOnlyList<Activity> PendingActivities { get { lock (_gate) return _pending.ToArray(); } }
    public IReadOnlyList<Activity> RunningActivities { get { lock (_gate) return _running.ToArray(); } }
    public IReadOnlyList<Activity> CompletedActivities { get { lock (_gate) return _completed.ToArray(); } }

    public ActivityLog(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;
    public ActivityLogSnapshot Snapshot()
    {
        lock (_gate)
            return new(_pending.Select(activity => activity.Snapshot()).ToArray(),
                _running.Select(activity => activity.Snapshot()).ToArray(),
                _completed.Select(activity => activity.Snapshot()).ToArray());
    }

    public string NextTaskNumberString() { lock (_gate) return "#" + _nextTaskNumber++; }

    public int CreateActivity(ActivityOwner owner, ActivityKind kind, string? detail = null)
    {
        int id;
        lock (_gate)
        {
            id = _nextID++;
            _pending.Add(new(id, owner, kind, detail, _clock));
        }
        ActivityDidChange?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public void LogCompletedActivity(ActivityOwner owner, ActivityKind kind, string? detail = null, string? message = null)
        => DidComplete(CreateActivity(owner, kind, detail), message, durationIsSignificant: false);

    public async Task<T> LogActivityAsync<T>(ActivityOwner owner, ActivityKind kind, Func<Task<T>> work, string? detail = null,
        Func<T, string?>? successMessage = null, Func<T, bool>? durationIsSignificant = null)
    {
        var id = CreateActivity(owner, kind, detail);
        DidStart(id);
        try
        {
            var result = await work();
            DidComplete(id, successMessage?.Invoke(result), durationIsSignificant?.Invoke(result) ?? true);
            return result;
        }
        catch (Exception error) { DidFail(id, error); throw; }
    }

    public Task LogActivityAsync(ActivityOwner owner, ActivityKind kind, Func<Task> work, string? detail = null,
        Func<string?>? successMessage = null, Func<bool>? durationIsSignificant = null)
        => LogActivityAsync(owner, kind, async () => { await work(); return true; }, detail,
            _ => successMessage?.Invoke(), _ => durationIsSignificant?.Invoke() ?? true);

    public T LogActivity<T>(ActivityOwner owner, ActivityKind kind, Func<T> work, string? detail = null,
        Func<T, string?>? successMessage = null, Func<T, bool>? durationIsSignificant = null)
    {
        var id = CreateActivity(owner, kind, detail);
        DidStart(id);
        try
        {
            var result = work();
            DidComplete(id, successMessage?.Invoke(result), durationIsSignificant?.Invoke(result) ?? true);
            return result;
        }
        catch (Exception error) { DidFail(id, error); throw; }
    }

    public void DidStart(ActivityOwner owner, ActivityKind kind) => Change(() =>
    {
        var activity = Find(_pending, owner, kind);
        if (activity is null) return Missing(owner, kind);
        Start(activity, withTimestamp: true);
        return true;
    });

    public void StartIfNeeded(ActivityOwner owner, ActivityKind kind) => Change(() =>
    {
        if (Find(_running, owner, kind) is not null) return false;
        var activity = Find(_pending, owner, kind);
        if (activity is null) return Missing(owner, kind);
        Start(activity, withTimestamp: false);
        return true;
    });

    public void DidComplete(ActivityOwner owner, ActivityKind kind, string? message = null, bool durationIsSignificant = true, bool returnedFromCache = false)
        => Change(() =>
        {
            var activity = Find(_running, owner, kind) ?? Find(_pending, owner, kind);
            if (activity is null) return Missing(owner, kind);
            Complete(EnsureRunning(activity), message, durationIsSignificant, returnedFromCache);
            return true;
        });

    public void DidFail(ActivityOwner owner, ActivityKind kind, Exception error) => Change(() =>
    {
        var activity = Find(_running, owner, kind) ?? Find(_pending, owner, kind);
        if (activity is null) return Missing(owner, kind);
        Fail(EnsureRunning(activity), error);
        return true;
    });

    public void DidStart(int id) => Change(() =>
    {
        var activity = _pending.FirstOrDefault(activity => activity.ID == id) ?? throw new InvalidOperationException($"No pending activity has ID {id}.");
        Start(activity, withTimestamp: true);
        return true;
    });

    public void DidComplete(int id, string? message = null, bool durationIsSignificant = true, bool returnedFromCache = false) => Change(() =>
    {
        Complete(EnsureRunning(Find(id)), message, durationIsSignificant, returnedFromCache);
        return true;
    });

    public void DidFail(int id, Exception error) => Change(() =>
    {
        Fail(EnsureRunning(Find(id)), error);
        return true;
    });

    public IReadOnlyList<Activity> PendingActivitiesFor(ActivityOwner owner) => PendingActivities.Where(activity => activity.Owner.Equals(owner)).ToArray();
    public IReadOnlyList<Activity> RunningActivitiesFor(ActivityOwner owner) => RunningActivities.Where(activity => activity.Owner.Equals(owner)).ToArray();
    public IReadOnlyList<Activity> CompletedActivitiesFor(ActivityOwner owner) => CompletedActivities.Where(activity => activity.Owner.Equals(owner)).ToArray();

    private void Change(Func<bool> change)
    {
        bool changed;
        lock (_gate) changed = change();
        if (changed) ActivityDidChange?.Invoke(this, EventArgs.Empty);
    }

    private static Activity? Find(IEnumerable<Activity> source, ActivityOwner owner, ActivityKind kind)
    {
        var matches = source.Where(activity => activity.Owner.Equals(owner) && activity.Kind == kind).Take(2).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Use activity IDs for concurrent work with the same owner and kind.");
        return matches.FirstOrDefault();
    }

    private Activity Find(int id) => _running.Concat(_pending).FirstOrDefault(activity => activity.ID == id)
        ?? throw new InvalidOperationException($"No active activity has ID {id}.");

    private void Start(Activity activity, bool withTimestamp)
    {
        activity.DidStart(withTimestamp);
        _pending.Remove(activity);
        _running.Add(activity);
    }

    private Activity EnsureRunning(Activity activity)
    {
        if (activity.State == ActivityState.Pending) Start(activity, withTimestamp: false);
        return activity;
    }

    private void Complete(Activity activity, string? message, bool significant, bool cached)
    {
        activity.DurationIsSignificant = significant;
        activity.DidComplete(message, cached);
        MoveToCompleted(activity);
    }

    private void Fail(Activity activity, Exception error)
    {
        activity.DurationIsSignificant = false;
        activity.DidFail(error);
        MoveToCompleted(activity);
    }

    private void MoveToCompleted(Activity activity)
    {
        _running.Remove(activity);
        _completed.Add(activity);
        if (_completed.Count > CompletedActivitiesLimit) _completed.RemoveRange(0, _completed.Count - CompletedActivitiesLimit);
    }

    private static bool Missing(ActivityOwner owner, ActivityKind kind)
    {
        Trace.TraceInformation($"No active {kind.DisplayName()} activity exists for {owner.DisplayName}.");
        return false;
    }
}
