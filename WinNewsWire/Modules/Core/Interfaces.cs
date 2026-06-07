namespace WinNewsWire.Core;

public interface IUndoableCommand
{
    string UndoActionName { get; }
    string RedoActionName { get; }
    void Perform();
    void Undo();
    void Redo();
}

public sealed class UndoManager
{
    private readonly Stack<IUndoableCommand> _undo = new();
    private readonly Stack<IUndoableCommand> _redo = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event EventHandler? StackChanged;
    public void Register(IUndoableCommand c) { _undo.Push(c); _redo.Clear(); StackChanged?.Invoke(this, EventArgs.Empty); }
    public void Undo() { if (_undo.Count == 0) return; var c = _undo.Pop(); c.Undo(); _redo.Push(c); StackChanged?.Invoke(this, EventArgs.Empty); }
    public void Redo() { if (_redo.Count == 0) return; var c = _redo.Pop(); c.Redo(); _undo.Push(c); StackChanged?.Invoke(this, EventArgs.Empty); }
    public void RemoveAll() { _undo.Clear(); _redo.Clear(); StackChanged?.Invoke(this, EventArgs.Empty); }
}

public interface IDisplayNameProvider { string NameForDisplay { get; } }
public interface IRenamable { Task RenameAsync(string newName, CancellationToken ct = default); }
public interface IOpmlRepresentable { string OpmlString(int indentLevel); }

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
