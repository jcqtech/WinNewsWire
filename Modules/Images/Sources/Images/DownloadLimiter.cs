namespace WinNewsWire.Images;

internal sealed class DownloadLimiter
{
    private readonly object _gate = new();
    private readonly int _maximumConcurrentDownloads;
    private readonly LinkedList<Waiter> _waiting = [];
    private int _running;

    internal DownloadLimiter(int maximumConcurrentDownloads)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrentDownloads);
        _maximumConcurrentDownloads = maximumConcurrentDownloads;
    }

    internal ValueTask<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_running < _maximumConcurrentDownloads)
            {
                _running++;
                return ValueTask.FromResult<IDisposable>(new Lease(this));
            }
            var waiter = new Waiter(this);
            _waiting.AddLast(waiter.Node);
            return new(WaitAsync(waiter, ct));
        }
    }

    private static async Task<IDisposable> WaitAsync(Waiter waiter, CancellationToken ct)
    {
        using var registration = ct.UnsafeRegister(static (state, token) =>
        {
            var waiting = (Waiter)state!;
            lock (waiting.Owner._gate)
            {
                if (waiting.Node.List is null) return;
                waiting.Owner._waiting.Remove(waiting.Node);
                waiting.Ready.TrySetCanceled(token);
            }
        }, waiter);
        return await waiter.Ready.Task.ConfigureAwait(false);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_waiting.Last is { } next)
            {
                _waiting.RemoveLast();
                next.Value.Ready.SetResult(new Lease(this));
            }
            else
                _running--;
        }
    }

    private sealed class Waiter
    {
        internal DownloadLimiter Owner { get; }
        internal TaskCompletionSource<IDisposable> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter> Node { get; }
        internal Waiter(DownloadLimiter owner)
        {
            Owner = owner;
            Node = new(this);
        }
    }

    private sealed class Lease(DownloadLimiter owner) : IDisposable
    {
        private DownloadLimiter? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
