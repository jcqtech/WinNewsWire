namespace WinNewsWire.Images;

public enum ThumbnailPriority { Visible, Prefetch }

public sealed class ThumbnailTaskQueue
{
    private readonly object _gate = new();
    private readonly int _maximumRunningCount;
    private readonly int _maximumWaitingCount;
    private readonly LinkedList<Waiter> _waiting = [];
    private int _running;
    public int WaitingCount { get { lock (_gate) return _waiting.Count; } }

    public ThumbnailTaskQueue(int maximumRunningCount, int maximumWaitingCount = 60)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRunningCount);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumWaitingCount);
        _maximumRunningCount = maximumRunningCount;
        _maximumWaitingCount = maximumWaitingCount;
    }

    public ValueTask<IDisposable?> AcquireAsync(CancellationToken ct = default, string? key = null,
        ThumbnailPriority priority = ThumbnailPriority.Visible)
    {
        if (priority is not (ThumbnailPriority.Visible or ThumbnailPriority.Prefetch))
            throw new ArgumentOutOfRangeException(nameof(priority));
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_running < _maximumRunningCount)
            {
                _running++;
                return ValueTask.FromResult<IDisposable?>(new Lease(this));
            }
            var waiter = new Waiter(this, key, priority);
            if (priority == ThumbnailPriority.Visible) _waiting.AddLast(waiter.Node);
            else
            {
                var firstVisible = _waiting.First;
                while (firstVisible is not null && firstVisible.Value.Priority == ThumbnailPriority.Prefetch)
                    firstVisible = firstVisible.Next;
                if (firstVisible is null) _waiting.AddLast(waiter.Node);
                else _waiting.AddBefore(firstVisible, waiter.Node);
            }
            if (_waiting.Count > _maximumWaitingCount)
            {
                var oldest = _waiting.First!;
                _waiting.RemoveFirst();
                oldest.Value.Ready.SetResult(null);
            }
            return new(WaitAsync(waiter, ct));
        }
    }

    public void Prioritize(string key)
    {
        lock (_gate)
        {
            var node = _waiting.Last;
            while (node is not null && !string.Equals(node.Value.Key, key, StringComparison.Ordinal)) node = node.Previous;
            if (node is null) return;
            _waiting.Remove(node);
            node.Value.Priority = ThumbnailPriority.Visible;
            _waiting.AddLast(node);
        }
    }

    internal void StartNow(string key)
    {
        lock (_gate)
        {
            var node = _waiting.Last;
            while (node is not null && !string.Equals(node.Value.Key, key, StringComparison.Ordinal)) node = node.Previous;
            if (node is null) return;
            _waiting.Remove(node);
            _running++;
            node.Value.Ready.SetResult(new Lease(this));
        }
    }

    private static async Task<IDisposable?> WaitAsync(Waiter waiter, CancellationToken ct)
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
            _running--;
            if (_running < _maximumRunningCount && _waiting.Last is { } next)
            {
                _waiting.RemoveLast();
                _running++;
                next.Value.Ready.SetResult(new Lease(this));
            }
        }
    }

    private sealed class Waiter
    {
        internal ThumbnailTaskQueue Owner { get; }
        internal string? Key { get; }
        internal ThumbnailPriority Priority { get; set; }
        internal TaskCompletionSource<IDisposable?> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter> Node { get; }
        internal Waiter(ThumbnailTaskQueue owner, string? key, ThumbnailPriority priority)
        {
            Owner = owner;
            Key = key;
            Priority = priority;
            Node = new(this);
        }
    }

    private sealed class Lease(ThumbnailTaskQueue owner) : IDisposable
    {
        private ThumbnailTaskQueue? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
