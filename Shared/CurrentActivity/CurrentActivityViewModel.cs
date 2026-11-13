using System.Diagnostics;
using WinNewsWire.ActivityLog;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.AppShared.CurrentActivity;

public sealed record ActivityDisplayText(string Title, string? Detail);

public sealed class CurrentActivityViewModel : IDisposable
{
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan LingerDuration = TimeSpan.FromSeconds(2);
    private readonly ActivityLogger _log;
    private readonly TimeProvider _clock;
    private readonly Action<Action> _dispatch;
    private readonly object _gate = new();
    private ITimer? _timer;
    private bool _observing, _disposed, _queued;
    private int _generation;
    private IReadOnlyList<ActivitySnapshot> _displayed = [];
    public IReadOnlyList<ActivitySnapshot> DisplayedActivities { get { lock (_gate) return _displayed; } }
    public event EventHandler? DisplayedActivitiesDidChange;

    public CurrentActivityViewModel(ActivityLogger log, Action<Action> dispatch, TimeProvider? clock = null)
    {
        _log = log;
        _dispatch = dispatch;
        _clock = clock ?? TimeProvider.System;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_observing)
            {
                _log.ActivityDidChange += ActivityDidChange;
                _observing = true;
            }
        }
        ScheduleUpdate();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_observing) _log.ActivityDidChange -= ActivityDidChange;
            _observing = false;
            StopTimer();
        }
    }

    public void Dispose()
    {
        lock (_gate) { Stop(); _disposed = true; }
    }

    private void ActivityDidChange(object? sender, EventArgs args) => ScheduleUpdate();

    private void ScheduleUpdate()
    {
        int generation;
        lock (_gate)
        {
            if (!_observing || _timer is not null) return;
            generation = ++_generation;
            _timer = _clock.CreateTimer(_ => QueueUpdate(generation), null, UpdateInterval, UpdateInterval);
        }
        QueueUpdate(generation);
    }

    private void QueueUpdate(int generation)
    {
        lock (_gate)
        {
            if (!_observing || generation != _generation || _queued) return;
            _queued = true;
        }
        try { _dispatch(() => Update(generation)); }
        catch (Exception error)
        {
            Trace.TraceError($"Current Activity could not schedule its update: {error}");
            Stop();
        }
    }

    private void Update(int generation)
    {
        bool changed;
        lock (_gate)
        {
            if (!_observing || generation != _generation) return;
            _queued = false;
            var snapshot = _log.Snapshot();
            var now = _clock.GetUtcNow().UtcDateTime;
            var activities = snapshot.Running.Concat(snapshot.Pending)
                .Concat(snapshot.Completed.Where(activity => activity.EndDate is { } end && now - end < LingerDuration)).ToArray();
            changed = !_displayed.SequenceEqual(activities);
            _displayed = activities;
            if (activities.Length == 0) StopTimer();
        }
        if (changed) DisplayedActivitiesDidChange?.Invoke(this, EventArgs.Empty);
    }

    private void StopTimer()
    {
        _generation++;
        _queued = false;
        _timer?.Dispose();
        _timer = null;
    }

    public static ActivityDisplayText DisplayText(ActivitySnapshot activity) => activity.Kind.Identifier switch
    {
        "refreshFeedContent" => activity.Detail is { } name
            ? new(name, activity.Kind.Url) : new(activity.Kind.Url ?? "", null),
        "findFeed" => new(AppStrings.Get("FindingFeed"), activity.Kind.Url),
        "fetchFeedCandidate" => new(AppStrings.Get("Fetching"), activity.Kind.Url),
        "downloadFeedImage" => new(AppStrings.Get("DownloadingImage"), activity.Kind.Url),
        "downloadFavicon" => new(AppStrings.Get("DownloadingFavicon"), activity.Kind.Url),
        "downloadAvatar" => new(AppStrings.Get("DownloadingAvatar"), activity.Kind.Url),
        "downloadHTMLMetadata" => new(AppStrings.Get("DownloadingMetadata"), activity.Kind.Url),
        "downloadThumbnail" => new(AppStrings.Get("DownloadingThumbnail"), activity.Kind.Url),
        _ => new(activity.Kind.SimpleDisplayName ?? "", activity.Detail)
    };

    public static string AccessibilityLabel(ActivityState state) => AppStrings.Get(state switch
    {
        ActivityState.Pending => "Pending",
        ActivityState.Running => "Running",
        ActivityState.Completed => "Completed",
        ActivityState.Failed => "Failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    });
}
