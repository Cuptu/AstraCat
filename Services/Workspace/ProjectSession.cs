namespace AstraCat;

using CaptionProject = MainWindow.CaptionProject;

/// <summary>
/// Encapsulates the undo/redo history and editing session state of the current active project.
/// </summary>
internal sealed class ProjectSession
{
    public sealed record HistoryCommand(Action Undo, Action Redo);

    private readonly Stack<HistoryCommand> _undoStack = new();
    private readonly Stack<HistoryCommand> _redoStack = new();

    public string? ActiveProjectId { get; set; }
    public CaptionProject? ActiveProject { get; set; }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public int UndoCount => _undoStack.Count;
    public int RedoCount => _redoStack.Count;

    public event EventHandler? HistoryChanged;

    public void PushCommand(Action undo, Action redo)
    {
        _undoStack.Push(new HistoryCommand(undo, redo));
        _redoStack.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undoStack.Count == 0) return false;
        var cmd = _undoStack.Pop();
        cmd.Undo();
        _redoStack.Push(cmd);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redoStack.Count == 0) return false;
        var cmd = _redoStack.Pop();
        cmd.Redo();
        _undoStack.Push(cmd);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ClearHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }
}
