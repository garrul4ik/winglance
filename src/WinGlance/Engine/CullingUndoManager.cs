using System.Collections.Generic;
using WinGlance.Model;

namespace WinGlance.Engine;

public abstract record CullingAction;

public sealed record RatingAction(string ItemId, int Previous, int New) : CullingAction;

public sealed record PickAction(string ItemId, PickStatus Previous, PickStatus New) : CullingAction;

public sealed record ColorAction(string ItemId, ColorLabel Previous, ColorLabel New) : CullingAction;

public sealed record RotateAction(SlideItem Item, RotationDirection Direction, int PreviousOrientation, int NewOrientation) : CullingAction;

public sealed record CameraProfileAction(string ItemId, CameraProfile? Previous, CameraProfile? New) : CullingAction;

public sealed record DeleteAction(
    SlideItem Item,
    int OriginalAllItemsIndex,
    CullingMetadata? PreviousMeta,
    IReadOnlyList<TrashedFileRecord> TrashedRecords
) : CullingAction
{
    public IReadOnlyList<TrashedFileRecord> TrashedRecords { get; set; } = TrashedRecords;
}

public sealed record CompoundAction(IReadOnlyList<CullingAction> Actions, string Description) : CullingAction;

public sealed class CullingUndoManager
{
    public const int MaxHistoryCount = 100;

    private readonly LinkedList<CullingAction> _undoStack = new();
    private readonly Stack<CullingAction> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public int UndoCount => _undoStack.Count;
    public int RedoCount => _redoStack.Count;

    public void Record(CullingAction action)
    {
        _undoStack.AddLast(action);
        while (_undoStack.Count > MaxHistoryCount)
        {
            _undoStack.RemoveFirst();
        }
        _redoStack.Clear();
    }

    public CullingAction? PopUndo()
    {
        if (_undoStack.Last == null) return null;
        var action = _undoStack.Last.Value;
        _undoStack.RemoveLast();
        _redoStack.Push(action);
        return action;
    }

    public CullingAction? PopRedo()
    {
        if (_redoStack.Count == 0) return null;
        var action = _redoStack.Pop();
        _undoStack.AddLast(action);
        return action;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
