using System.Diagnostics;
using WinNewsWire.Account;

namespace WinNewsWire.AppShared.Timer;

/// <summary>Port of <c>AccountRefreshTimer</c>.</summary>
public sealed class AccountRefreshTimer : IDisposable
{
    private readonly AccountManager _manager;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private RefreshInterval _interval;

    /// <summary>
    /// Optional sink for exceptions thrown by the periodic refresh delegate.
    /// When null, errors are written to <see cref="Debug"/>.
    /// </summary>
    public static Action<Exception>? ErrorHandler { get; set; }

    public AccountRefreshTimer(AccountManager manager, RefreshInterval interval)
    {
        _manager = manager;
        _interval = interval;
    }

    public RefreshInterval Interval
    {
        get => _interval;
        set { _interval = value; Restart(); }
    }

    public void Start() => Restart();

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Restart()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            var dt = _interval.Interval();
            if (dt is null) return;
            _timer = new System.Threading.Timer(async _ =>
            {
                try { await _manager.RefreshAllAsync(); }
                catch (Exception ex) { ReportError(ex); }
            }, null, dt.Value, dt.Value);
        }
    }

    internal static void ReportError(Exception ex)
    {
        var handler = ErrorHandler;
        if (handler is not null)
        {
            try { handler(ex); }
            catch { /* never let the tick crash the AppDomain */ }
        }
        else
        {
            Debug.WriteLine($"[AccountRefreshTimer] {ex}");
        }
    }

    public void Dispose() => Stop();
}

/// <summary>Port of <c>ArticleStatusSyncTimer</c>. Periodically drives remote sync on all accounts.</summary>
public sealed class ArticleStatusSyncTimer : IDisposable
{
    private readonly AccountManager _manager;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private TimeSpan _interval;

    /// <summary>
    /// Optional sink for exceptions thrown inside the periodic sync delegate.
    /// When null, errors are written to <see cref="Debug"/>.
    /// </summary>
    public static Action<Exception>? ErrorHandler { get; set; }

    public ArticleStatusSyncTimer(AccountManager manager, TimeSpan interval)
    {
        _manager = manager; _interval = interval;
    }

    public TimeSpan Interval { get => _interval; set { _interval = value; Restart(); } }

    public void Start() => Restart();

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Restart()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _timer = new System.Threading.Timer(async _ =>
            {
                foreach (var a in _manager.ActiveAccounts)
                {
                    try { await a.Delegate.SendArticleStatusAsync(a, default); }
                    catch (Exception ex) { ReportError(ex); }
                    try { await a.Delegate.RefreshArticleStatusAsync(a, default); }
                    catch (Exception ex) { ReportError(ex); }
                }
            }, null, _interval, _interval);
        }
    }

    internal static void ReportError(Exception ex)
    {
        var handler = ErrorHandler;
        if (handler is not null)
        {
            try { handler(ex); }
            catch { /* never let the tick crash the AppDomain */ }
        }
        else
        {
            Debug.WriteLine($"[ArticleStatusSyncTimer] {ex}");
        }
    }

    public void Dispose() => Stop();
}
