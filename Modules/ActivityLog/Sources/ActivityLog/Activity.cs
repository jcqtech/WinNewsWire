using System.Globalization;

namespace WinNewsWire.ActivityLog;

public sealed record ActivitySnapshot(int ID, ActivityOwner Owner, ActivityKind Kind, string? Detail,
    ActivityState State, DateTime? StartDate, DateTime? EndDate, Exception? Error, string? CompletionMessage,
    bool ReturnedFromCache, bool DurationIsSignificant)
{
    public string? FormattedDuration => DurationIsSignificant && StartDate is { } start && EndDate is { } end
        ? Activity.FormatDuration((end - start).TotalSeconds) : null;
}

public sealed class Activity
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private ActivityState _state;
    private DateTime? _startDate, _endDate;
    private Exception? _error;
    private string? _completionMessage;
    private bool _returnedFromCache;
    private bool _durationIsSignificant = true;
    public int ID { get; }
    public ActivityOwner Owner { get; }
    public ActivityKind Kind { get; }
    public string? Detail { get; }
    public ActivityState State { get { lock (_gate) return _state; } }
    public DateTime? StartDate { get { lock (_gate) return _startDate; } }
    public DateTime? EndDate { get { lock (_gate) return _endDate; } }
    public Exception? Error { get { lock (_gate) return _error; } }
    public string? CompletionMessage { get { lock (_gate) return _completionMessage; } }
    public bool ReturnedFromCache { get { lock (_gate) return _returnedFromCache; } }
    public bool DurationIsSignificant { get { lock (_gate) return _durationIsSignificant; } set { lock (_gate) _durationIsSignificant = value; } }

    public Activity(int id, ActivityOwner owner, ActivityKind kind, string? detail = null, TimeProvider? clock = null)
    {
        ID = id; Owner = owner; Kind = kind; Detail = detail; _clock = clock ?? TimeProvider.System;
    }

    public ActivitySnapshot Snapshot()
    {
        lock (_gate)
            return new(ID, Owner, Kind, Detail, _state, _startDate, _endDate, _error, _completionMessage,
                _returnedFromCache, _durationIsSignificant);
    }

    internal void DidStart(bool withTimestamp)
    {
        lock (_gate)
        {
            _state = ActivityState.Running;
            if (withTimestamp) _startDate = _clock.GetUtcNow().UtcDateTime;
        }
    }

    internal void DidComplete(string? message, bool returnedFromCache)
    {
        lock (_gate)
        {
            _state = ActivityState.Completed;
            _endDate = _clock.GetUtcNow().UtcDateTime;
            _completionMessage = message;
            _returnedFromCache = returnedFromCache;
        }
    }

    internal void DidFail(Exception error)
    {
        lock (_gate)
        {
            _state = ActivityState.Failed;
            _endDate = _clock.GetUtcNow().UtcDateTime;
            _error = error;
        }
    }

    public string? FormattedDuration
    {
        get
        {
            lock (_gate)
                return _durationIsSignificant && _startDate is { } start && _endDate is { } end
                    ? FormatDuration((end - start).TotalSeconds) : null;
        }
    }

    public static string FormatDuration(double seconds)
        => seconds < 10 ? seconds.ToString("F2", CultureInfo.InvariantCulture) + "s"
            : seconds < 60 ? seconds.ToString("F1", CultureInfo.InvariantCulture) + "s"
            : $"{(long)seconds / 60}m {(long)seconds % 60}s";
}
