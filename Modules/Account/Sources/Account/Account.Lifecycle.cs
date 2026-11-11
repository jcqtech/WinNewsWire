using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace WinNewsWire.Account;

public sealed partial class Account
{
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource? _disposal;
    private bool _disposed;
    private readonly object _statusSendGate = new();
    private Task? _backgroundStatusSend;
    internal bool IsStopping { get { lock (_lifecycleGate) return _disposal is not null; } }

    private CancellationTokenSource OperationCancellation(CancellationToken ct)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            return CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        }
    }

    private async Task<T> RunOperationAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var cancellation = OperationCancellation(ct);
        await _operationGate.WaitAsync(cancellation.Token);
        try { return await work(cancellation.Token); }
        finally { _operationGate.Release(); }
    }

    private Task RunOperationAsync(Func<CancellationToken, Task> work, CancellationToken ct)
        => RunOperationAsync(async token => { await work(token); return true; }, ct);

    public Task SendArticleStatusAsync(CancellationToken ct = default)
        => RunOperationAsync(token => Delegate.SendArticleStatusAsync(this, token), ct);

    public Task RefreshArticleStatusAsync(CancellationToken ct = default)
        => RunOperationAsync(async token =>
        {
            await Delegate.RefreshArticleStatusAsync(this, token);
            await RecalculateUnreadCountsAsync();
        }, ct);

    public Task<bool> SyncArticleStatusAsync(CancellationToken ct = default)
        => RunOperationAsync(async token =>
        {
            var didWork = await Delegate.SyncArticleStatusAsync(this, token);
            if (didWork) await RecalculateUnreadCountsAsync();
            return didWork;
        }, ct);

    private void ScheduleStatusSend()
    {
        lock (_statusSendGate)
            if (_backgroundStatusSend is null || _backgroundStatusSend.IsCompleted)
                _backgroundStatusSend = SendQueuedStatusesInBackgroundAsync();
    }

    private async Task SendQueuedStatusesInBackgroundAsync()
    {
        try
        {
            await RunOperationAsync(async token =>
            {
                var pending = await SyncDatabase.SelectPendingCountAsync();
                while (pending > 100)
                {
                    await Delegate.SendArticleStatusAsync(this, token);
                    var remaining = await SyncDatabase.SelectPendingCountAsync();
                    if (remaining >= pending)
                    {
                        Trace.TraceWarning($"Account {AccountID} retained its status queue after a send; the timer will retry it.");
                        break;
                    }
                    pending = remaining;
                }
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (IsStopping) { }
        catch (ObjectDisposedException) when (IsStopping) { }
        catch (Exception error) { Trace.TraceError($"Background status send failed for {AccountID}: {error}"); }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource disposal;
        lock (_lifecycleGate)
        {
            if (_disposal is not null) return new(_disposal.Task);
            disposal = _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = FinishDisposalAsync(disposal);
        return new(disposal.Task);
    }

    private async Task FinishDisposalAsync(TaskCompletionSource disposal)
    {
        var errors = CancelLifetime();
        try
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StatusMutationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    try { await Delegate.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { errors.Add(error); }
                    if (_localRefresher is not null)
                    {
                        try { await _localRefresher.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception error) { errors.Add(error); }
                    }
                    DisposeResources(errors);
                }
                finally { StatusMutationGate.Release(); }
            }
            finally { _operationGate.Release(); }
            ThrowDisposalErrors(errors);
            disposal.TrySetResult();
        }
        catch (Exception error)
        {
            Trace.TraceError($"Could not close account {AccountID}: {error}");
            disposal.TrySetException(error);
        }
    }

    public void Dispose()
    {
        TaskCompletionSource disposal;
        lock (_lifecycleGate)
        {
            if (_disposal is not null)
            {
                if (!_disposal.Task.IsCompleted)
                    throw new InvalidOperationException("Account work is still stopping. Await DisposeAsync before closing its storage.");
                _disposal.Task.GetAwaiter().GetResult();
                return;
            }
            if (!_operationGate.Wait(0))
                throw new InvalidOperationException("This account is busy. Use DisposeAsync to cancel and finish its pending work.");
            if (!StatusMutationGate.Wait(0))
            {
                _operationGate.Release();
                throw new InvalidOperationException("This account is updating article status. Use DisposeAsync before closing its storage.");
            }
            if (!Delegate.CanDisposeSynchronously || _localRefresher?.CanDisposeSynchronously == false)
            {
                StatusMutationGate.Release();
                _operationGate.Release();
                throw new InvalidOperationException("This account has background work that requires DisposeAsync before closing its storage.");
            }
            disposal = _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            var errors = CancelLifetime();
            try { Delegate.Dispose(); }
            catch (Exception error) { errors.Add(error); }
            DisposeResources(errors);
            ThrowDisposalErrors(errors);
            disposal.TrySetResult();
        }
        catch (Exception error)
        {
            Trace.TraceError($"Could not close account {AccountID}: {error}");
            disposal.TrySetException(error);
            throw;
        }
        finally
        {
            StatusMutationGate.Release();
            _operationGate.Release();
        }
    }

    private List<Exception> CancelLifetime()
    {
        var errors = new List<Exception>();
        try { _lifetime.Cancel(); }
        catch (AggregateException error) { errors.Add(error); }
        return errors;
    }

    private void DisposeResources(List<Exception> errors)
    {
        IDisposable?[] resources = [_localRefresher, OpmlFile, Database, _syncDatabase, FeedSettingsDatabase, _lifetime];
        foreach (var resource in resources)
        {
            try { resource?.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        }
        lock (_lifecycleGate) _disposed = true;
    }

    private static void ThrowDisposalErrors(IReadOnlyList<Exception> errors)
    {
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("Some account resources could not be closed.", errors);
    }
}
