using System.Diagnostics;

namespace WinNewsWire.AppShared.Timer;

internal sealed class AsyncPeriodicTimer(Func<CancellationToken, Task> tick, Action<Exception> reportError) : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task _running = Task.CompletedTask;
    private bool _disposed;

    public void Start(TimeSpan? interval)
    {
        if (interval is { } value && value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelCurrent();
            if (interval is null) return;
            _cancellation = new();
            _running = RunAsync(_running, interval.Value, _cancellation.Token);
        }
    }

    public void Stop() => _ = StopCore(dispose: false);
    public Task StopAsync() => StopCore(dispose: false);
    public void Dispose() => _ = StopCore(dispose: true);
    public ValueTask DisposeAsync() => new(StopCore(dispose: true));

    private Task StopCore(bool dispose)
    {
        lock (_gate)
        {
            _disposed |= dispose;
            CancelCurrent();
            return _running;
        }
    }

    private void CancelCurrent()
    {
        if (_cancellation is not { } cancellation) return;
        _cancellation = null;
        cancellation.Cancel();
        _ = _running.ContinueWith(_ => cancellation.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunAsync(Task previous, TimeSpan interval, CancellationToken ct)
    {
        await previous.ConfigureAwait(false);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await tick(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception error) { reportError(error); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal static void ReportError(Action<Exception>? handler, Exception error)
    {
        if (handler is not null)
        {
            try { handler(error); return; }
            catch (Exception failure) { Trace.TraceError($"The periodic-operation error handler failed: {failure}"); }
        }
        Trace.TraceError($"Periodic account operation failed: {error}");
    }
}
