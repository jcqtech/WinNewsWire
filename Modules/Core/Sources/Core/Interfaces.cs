namespace WinNewsWire.Core;

public interface IUndoableCommand
{
    string UndoActionName { get; }
    string RedoActionName { get; }
    Task PerformAsync();
    Task UndoAsync();
    Task RedoAsync();
}

public sealed class UndoManager
{
    private readonly Stack<IUndoableCommand> _undo = new();
    private readonly Stack<IUndoableCommand> _redo = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool IsExecuting { get; private set; }
    public bool CanUndo => !IsExecuting && _undo.Count > 0;
    public bool CanRedo => !IsExecuting && _redo.Count > 0;
    public string? UndoActionName => _undo.TryPeek(out var command) ? command.UndoActionName : null;
    public string? RedoActionName => _redo.TryPeek(out var command) ? command.RedoActionName : null;
    public event EventHandler? StackChanged;

    public Task ExecuteAsync(IUndoableCommand command) => RunAsync(async () =>
    {
        await command.PerformAsync();
        _undo.Push(command);
        _redo.Clear();
    });

    public Task UndoAsync() => RunAsync(async () =>
    {
        if (!_undo.TryPeek(out var command)) return;
        await command.UndoAsync();
        _undo.Pop();
        _redo.Push(command);
    });

    public Task RedoAsync() => RunAsync(async () =>
    {
        if (!_redo.TryPeek(out var command)) return;
        await command.RedoAsync();
        _redo.Pop();
        _undo.Push(command);
    });

    private async Task RunAsync(Func<Task> work)
    {
        await _gate.WaitAsync();
        IsExecuting = true;
        try
        {
            StackChanged?.Invoke(this, EventArgs.Empty);
            await work();
        }
        finally
        {
            IsExecuting = false;
            _gate.Release();
            StackChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RemoveAll()
    {
        if (!_gate.Wait(0)) throw new InvalidOperationException("Cannot clear undo history while a command is running.");
        try { _undo.Clear(); _redo.Clear(); }
        finally { _gate.Release(); }
        StackChanged?.Invoke(this, EventArgs.Empty);
    }
}

public interface IDisplayNameProvider { string NameForDisplay { get; } }
public interface IRenamable { Task RenameAsync(string newName, CancellationToken ct = default); }
public interface IOpmlRepresentable { string OpmlString(int indentLevel, bool allowCustomAttributes = false); }

public sealed class BatchUpdate
{
    public static BatchUpdate Shared { get; } = new();
    private int _depth;
    public event EventHandler? WillPerformBatchUpdate;
    public event EventHandler? DidPerformBatchUpdate;
    public bool IsPerforming => _depth > 0;
    public void Perform(Action update)
    {
        if (Interlocked.Increment(ref _depth) == 1) WillPerformBatchUpdate?.Invoke(this, EventArgs.Empty);
        try { update(); }
        finally { if (Interlocked.Decrement(ref _depth) == 0) DidPerformBatchUpdate?.Invoke(this, EventArgs.Empty); }
    }
}
