namespace WinNewsWire.Core;

public sealed class MainThreadOperationQueue
{
    private static readonly Lazy<MainThreadOperationQueue> SharedQueue = new(() => new());
    public static MainThreadOperationQueue Shared => SharedQueue.Value;
    private readonly SynchronizationContext _context;
    private readonly int _threadID;
    private readonly List<MainThreadOperation> _pending = [];
    private readonly HashSet<int> _completedIDs = [];
    private MainThreadOperation? _current;
    private bool _suspended;
    private bool _runScheduled;
    private bool _trackingProgress;

    public MainThreadOperationQueue(SynchronizationContext? context = null)
    {
        _context = context ?? SynchronizationContext.Current
            ?? throw new InvalidOperationException("Create the operation queue on a UI synchronization context.");
        _threadID = Environment.CurrentManagedThreadId;
    }

    public int PendingOperationsCount { get { VerifyAccess(); return _pending.Count(operation => !operation.IsCanceled); } }
    public ProgressInfo ProgressInfo { get; private set; }
    public event EventHandler<ProgressInfo>? ProgressChanged;
    public bool IsTrackingProgress
    {
        get => _trackingProgress;
        set
        {
            VerifyAccess();
            if (_trackingProgress == value) return;
            _trackingProgress = value;
            _completedIDs.Clear();
            UpdateProgress();
        }
    }

    public void Add(MainThreadOperation operation)
    {
        VerifyAccess();
        if (operation.WasAdded) throw new InvalidOperationException("An operation can only be added to a queue once.");
        foreach (var dependency in operation.Dependencies.Where(dependency => dependency.IsCompleted).ToArray())
        {
            operation.Dependencies.Remove(dependency);
            if (dependency.IsCanceled) operation.IsCanceled = true;
        }
        operation.WasAdded = true;
        operation.Attach(this);
        _pending.Add(operation);
        if (operation.IsCanceled || operation.CompletionRequested)
            Post(() => OperationDidComplete(operation));
        RunNextIfNeeded();
    }

    public void Add(IEnumerable<MainThreadOperation> operations)
    {
        VerifyAccess();
        foreach (var operation in operations) Add(operation);
    }

    public void CancelAll()
    {
        VerifyAccess();
        Cancel(_pending.Concat(_current is null ? [] : new[] { _current }).Distinct().ToArray());
    }

    public void Cancel(IEnumerable<MainThreadOperation> operations)
    {
        VerifyAccess();
        foreach (var operation in operations)
        {
            operation.Cancel();
            if (IsTrackingProgress) _completedIDs.Add(operation.ID);
        }
        RunNextIfNeeded();
    }

    public void Cancel(string name)
    {
        VerifyAccess();
        Cancel(_pending.Concat(_current is null ? [] : new[] { _current }).Where(operation => operation.Name == name).Distinct().ToArray());
    }

    public void Suspend() { VerifyAccess(); _suspended = true; }
    public void Resume() { VerifyAccess(); _suspended = false; RunNextIfNeeded(); }

    public void OperationDidComplete(MainThreadOperation operation)
    {
        VerifyAccess();
        if (!ReferenceEquals(operation.OperationQueue, this)) throw new ArgumentException("The operation belongs to a different queue.", nameof(operation));
        if (!operation.TryFinish()) return;
        Exception? callbackError = null;
        try
        {
            var completion = operation.CompletionBlock;
            operation.CompletionBlock = null;
            completion?.Invoke(operation);
            operation.NoteDidComplete();
        }
        catch (Exception error)
        {
            callbackError = error;
            System.Diagnostics.Trace.TraceError($"Operation completion failed: {error}");
        }
        finally
        {
            if (IsTrackingProgress) _completedIDs.Add(operation.ID);
            _pending.Remove(operation);
            if (ReferenceEquals(_current, operation)) _current = null;
            foreach (var dependent in _pending.Where(dependent => dependent.Dependencies.Contains(operation)).ToArray())
            {
                dependent.Dependencies.Remove(operation);
                if (operation.IsCanceled) dependent.Cancel();
            }
            operation.FinishCompletion(callbackError);
            RunNextIfNeeded();
        }
    }

    internal void Post(Action action) => _context.Post(_ => action(), null);
    internal void Dispatch(Action action)
    {
        if (Environment.CurrentManagedThreadId == _threadID) action();
        else Post(action);
    }

    private void RunNextIfNeeded()
    {
        UpdateProgress();
        if (_suspended || _runScheduled || _current is not null) return;
        _runScheduled = true;
        Post(() =>
        {
            _runScheduled = false;
            if (_suspended || _current is not null) return;
            var index = _pending.FindIndex(operation => !operation.IsCanceled
                && !operation.CompletionRequested && operation.Dependencies.Count == 0);
            if (index < 0) return;
            var operation = _pending[index];
            _pending.RemoveAt(index);
            _current = operation;
            UpdateProgress();
            try { operation.Run(); }
            catch (Exception error) { operation.Fail(error); }
        });
    }

    private void UpdateProgress()
    {
        if (!IsTrackingProgress) return;
        var remaining = _pending.Select(operation => operation.ID)
            .Concat(_current is null ? [] : new[] { _current.ID }).Distinct().Count(id => !_completedIDs.Contains(id));
        var value = new ProgressInfo(remaining, _completedIDs.Count, remaining + _completedIDs.Count);
        if (value == ProgressInfo) return;
        ProgressInfo = value;
        ProgressChanged?.Invoke(this, value);
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadID)
            throw new InvalidOperationException("Use the operation queue from its owning UI thread.");
    }
}
