namespace WinNewsWire.Images;

public sealed class ThumbnailTaskQueue
{
    private readonly object _gate = new();
    private readonly int _maximumRunningCount;
    private readonly int _maximumWaitingCount;
    private readonly int _maximumRunningCountPerGroup;
    private readonly Dictionary<string, int> _runningPerGroup = new(StringComparer.Ordinal);
    private readonly LinkedList<Waiter> _waiting = [];
    private int _running;
    public int WaitingCount { get { lock (_gate) return _waiting.Count; } }

    public ThumbnailTaskQueue(int maximumRunningCount, int maximumWaitingCount = 60,
        int maximumRunningCountPerGroup = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRunningCount);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumWaitingCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRunningCountPerGroup);
        _maximumRunningCount = maximumRunningCount;
        _maximumWaitingCount = maximumWaitingCount;
        _maximumRunningCountPerGroup = maximumRunningCountPerGroup;
    }

    public ValueTask<Lease?> AcquireAsync(CancellationToken ct = default, string? key = null, string? group = null)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_running < _maximumRunningCount && HasRoom(group))
            {
                NoteStarted(group);
                return ValueTask.FromResult<Lease?>(new Lease(this, group));
            }
            var waiter = new Waiter(this, key, group);
            _waiting.AddLast(waiter.Node);
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
            NoteStarted(node.Value.Group);
            node.Value.Ready.SetResult(new Lease(this, node.Value.Group, startedNow: true));
        }
    }

    private static async Task<Lease?> WaitAsync(Waiter waiter, CancellationToken ct)
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

    private bool HasRoom(string? group)
        => group is null || _runningPerGroup.GetValueOrDefault(group) < _maximumRunningCountPerGroup;

    private void NoteStarted(string? group)
    {
        _running++;
        if (group is not null) _runningPerGroup[group] = _runningPerGroup.GetValueOrDefault(group) + 1;
    }

    private void Release(string? group)
    {
        lock (_gate)
        {
            _running--;
            if (group is not null)
            {
                int remaining = _runningPerGroup[group] - 1;
                if (remaining == 0) _runningPerGroup.Remove(group);
                else _runningPerGroup[group] = remaining;
            }
            if (_running < _maximumRunningCount)
            {
                var next = _waiting.Last;
                while (next is not null && !HasRoom(next.Value.Group)) next = next.Previous;
                if (next is null) return;
                _waiting.Remove(next);
                NoteStarted(next.Value.Group);
                next.Value.Ready.SetResult(new Lease(this, next.Value.Group));
            }
        }
    }

    private sealed class Waiter
    {
        internal ThumbnailTaskQueue Owner { get; }
        internal string? Key { get; }
        internal string? Group { get; }
        internal TaskCompletionSource<Lease?> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter> Node { get; }
        internal Waiter(ThumbnailTaskQueue owner, string? key, string? group)
        {
            Owner = owner;
            Key = key;
            Group = group;
            Node = new(this);
        }
    }

    public sealed class Lease : IDisposable
    {
        private ThumbnailTaskQueue? _owner;
        private readonly string? _group;
        internal bool StartedNow { get; }
        internal Lease(ThumbnailTaskQueue owner, string? group, bool startedNow = false)
        {
            _owner = owner;
            _group = group;
            StartedNow = startedNow;
        }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_group);
    }
}
