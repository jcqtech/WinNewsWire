using WinNewsWire.Account;

namespace WinNewsWire.AppShared.Timer;

/// <summary>Port of <c>AccountRefreshTimer</c>.</summary>
public sealed class AccountRefreshTimer : IDisposable, IAsyncDisposable
{
    private readonly AsyncPeriodicTimer _timer;
    private RefreshInterval _interval;
    public static Action<Exception>? ErrorHandler { get; set; }

    public AccountRefreshTimer(AccountManager manager, RefreshInterval interval)
    {
        _interval = interval;
        _timer = new(ct => manager.RefreshAllAsync(ct: ct), ReportError);
    }

    public RefreshInterval Interval
    {
        get => _interval;
        set { _interval = value; Start(); }
    }

    public void Start() => _timer.Start(_interval.Interval());
    public void Stop() => _timer.Stop();
    public Task StopAsync() => _timer.StopAsync();
    public void Dispose() => _timer.Dispose();
    public ValueTask DisposeAsync() => _timer.DisposeAsync();
    internal static void ReportError(Exception error) => AsyncPeriodicTimer.ReportError(ErrorHandler, error);
}
