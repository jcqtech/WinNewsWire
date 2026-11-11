namespace WinNewsWire.Core;

public class MainThreadOperation : IEquatable<MainThreadOperation>
{
    private static int _nextID = -1;
    private int _cancelled;
    private int _completionRequested;
    private int _finished;
    private WeakReference<MainThreadOperationQueue>? _queue;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal HashSet<MainThreadOperation> Dependencies { get; } = [];
    internal bool WasAdded { get; set; }
    internal bool CompletionRequested => Volatile.Read(ref _completionRequested) != 0;
    public int ID { get; } = Interlocked.Increment(ref _nextID);
    public string? Name { get; }
    public bool IsCanceled { get => Volatile.Read(ref _cancelled) != 0; set => Volatile.Write(ref _cancelled, value ? 1 : 0); }
    public bool IsCompleted => Volatile.Read(ref _finished) != 0;
    public Exception? Error { get; private set; }
    public Action<MainThreadOperation>? CompletionBlock { get; set; }
    public Task Completion => _completion.Task;
    protected CancellationToken CancellationToken => _cancellation.Token;
    public MainThreadOperationQueue? OperationQueue => _queue?.TryGetTarget(out var queue) == true ? queue : null;

    public MainThreadOperation(string? name = null, Action<MainThreadOperation>? completionBlock = null)
    {
        Name = name; CompletionBlock = completionBlock;
    }

    public virtual void Run() => throw new NotSupportedException("Override Run and call DidComplete when finished.");
    public virtual void NoteDidComplete() { }

    public void AddDependency(MainThreadOperation parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (WasAdded) throw new InvalidOperationException("Add dependencies before adding an operation to a queue.");
        var visited = new HashSet<MainThreadOperation>();
        bool DependsOnThis(MainThreadOperation operation)
            => ReferenceEquals(operation, this) || visited.Add(operation) && operation.Dependencies.Any(DependsOnThis);
        if (DependsOnThis(parent)) throw new InvalidOperationException("Operation dependencies cannot form a cycle.");
        if (parent.IsCompleted)
        {
            if (parent.IsCanceled) Cancel();
            return;
        }
        Dependencies.Add(parent);
    }

    public void Cancel()
    {
        IsCanceled = true;
        if (OperationQueue is { } queue)
            queue.Post(() =>
            {
                if (!IsCompleted) _cancellation.Cancel();
                Dependencies.Clear();
                DidComplete();
            });
    }

    public void DidComplete()
    {
        if (Interlocked.Exchange(ref _completionRequested, 1) != 0) return;
        if (OperationQueue is { } queue) queue.Dispatch(() => queue.OperationDidComplete(this));
    }

    protected internal void Fail(Exception error)
    {
        Error = error;
        System.Diagnostics.Trace.TraceError($"Operation {Name ?? ID.ToString()} failed: {error}");
        DidComplete();
    }

    internal void Attach(MainThreadOperationQueue queue) => _queue = new(queue);
    internal bool TryFinish() => Interlocked.Exchange(ref _finished, 1) == 0;
    internal void FinishCompletion(Exception? callbackError)
    {
        Error ??= callbackError;
        if (Error is not null) _completion.TrySetException(Error);
        else if (IsCanceled) _completion.TrySetCanceled();
        else _completion.TrySetResult();
        _cancellation.Dispose();
    }

    public bool Equals(MainThreadOperation? other) => other?.ID == ID;
    public override bool Equals(object? obj) => obj is MainThreadOperation other && Equals(other);
    public override int GetHashCode() => ID;
}
