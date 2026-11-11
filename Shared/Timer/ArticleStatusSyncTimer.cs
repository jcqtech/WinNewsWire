using WinNewsWire.Account;

namespace WinNewsWire.AppShared.Timer;

public sealed class ArticleStatusSyncTimer : IDisposable, IAsyncDisposable
{
    public static readonly TimeSpan NormalInterval = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(30);
    public static Action<Exception>? ErrorHandler { get; set; }
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Func<CancellationToken, Task<bool>> _synchronize;
    private readonly ArticleStatusSyncSchedule _schedule;
    private readonly AccountManager? _manager;
    private CancellationTokenSource? _cancellation;
    private Task _running = Task.CompletedTask;
    private bool _disposed;
    internal bool IsIdle { get { lock (_gate) return _schedule.LastRunWasIdle; } }

    public ArticleStatusSyncTimer(AccountManager manager, TimeSpan interval)
        : this(manager.SyncArticleStatusAllAsync, interval, IdleInterval)
    {
        _manager = manager;
        manager.ArticleStatusesQueued += AccountStatusesQueued;
    }

    internal ArticleStatusSyncTimer(Func<CancellationToken, Task<bool>> synchronize, TimeSpan normal, TimeSpan idle, TimeProvider? clock = null)
    {
        _synchronize = synchronize;
        _schedule = new(normal, idle, clock ?? TimeProvider.System);
    }

    public TimeSpan Interval
    {
        get { lock (_gate) return _schedule.NormalInterval; }
        set
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _schedule.NormalInterval = value;
                Wake();
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cancellation is not null) return;
            _cancellation = new();
            _running = RunAsync(_running, _cancellation.Token);
        }
    }

    private async Task RunAsync(Task previous, CancellationToken ct)
    {
        await previous.ConfigureAwait(false);
        try
        {
            while (true)
            {
                TimeSpan delay;
                lock (_gate) delay = _schedule.NextDelay;
                if (await _wake.WaitAsync(delay, ct).ConfigureAwait(false)) continue;
                ct.ThrowIfCancellationRequested();
                long version;
                lock (_gate) version = _schedule.Started();
                bool didWork = false;
                try { didWork = await _synchronize(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception error) { ReportError(error); }
                lock (_gate) _schedule.Completed(didWork, version);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void AccountStatusesQueued(object? sender, EventArgs args) => NotifyStatusesQueued();

    internal void NotifyStatusesQueued()
    {
        lock (_gate)
        {
            if (_disposed || _cancellation is null) return;
            if (_schedule.StatusesQueued()) Wake();
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public void Stop() => _ = StopCore(dispose: false);
    public Task StopAsync() => StopCore(dispose: false);
    public void Dispose() => _ = StopCore(dispose: true);
    public ValueTask DisposeAsync() => new(StopCore(dispose: true));

    private Task StopCore(bool dispose)
    {
        lock (_gate)
        {
            if (_cancellation is { } cancellation)
            {
                _cancellation = null;
                cancellation.Cancel();
                _ = _running.ContinueWith(_ => cancellation.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            if (dispose && !_disposed)
            {
                _disposed = true;
                if (_manager is not null) _manager.ArticleStatusesQueued -= AccountStatusesQueued;
                _ = _running.ContinueWith(_ => _wake.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return _running;
        }
    }

    internal static void ReportError(Exception error) => AsyncPeriodicTimer.ReportError(ErrorHandler, error);
}

internal sealed class ArticleStatusSyncSchedule
{
    private readonly TimeProvider _clock;
    private readonly DateTimeOffset _launchTime;
    private readonly TimeSpan _idleInterval;
    private DateTimeOffset? _lastRefresh;
    private TimeSpan _normalInterval;
    private long _queuedVersion;
    internal bool LastRunWasIdle { get; private set; }
    internal TimeSpan NormalInterval
    {
        get => _normalInterval;
        set
        {
            if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value));
            _normalInterval = value;
        }
    }

    internal ArticleStatusSyncSchedule(TimeSpan normal, TimeSpan idle, TimeProvider clock)
    {
        if (idle <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idle));
        NormalInterval = normal;
        _idleInterval = idle;
        _clock = clock;
        _launchTime = clock.GetUtcNow();
    }

    internal TimeSpan NextDelay
    {
        get
        {
            var interval = LastRunWasIdle ? _idleInterval : NormalInterval;
            var now = _clock.GetUtcNow();
            var next = (_lastRefresh ?? _launchTime) + interval;
            return next < now ? interval : next - now;
        }
    }

    internal long Started()
    {
        _lastRefresh = _clock.GetUtcNow();
        return _queuedVersion;
    }

    internal void Completed(bool didWork, long version) => LastRunWasIdle = !didWork && version == _queuedVersion;

    internal bool StatusesQueued()
    {
        _queuedVersion++;
        var wasIdle = LastRunWasIdle;
        LastRunWasIdle = false;
        return wasIdle;
    }
}
