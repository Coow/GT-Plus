using System;
using System.Collections.Generic;
using GtPlus.Models;

namespace GtPlus.Services;

public interface IHistoryAction
{
    string Description { get; }
    void Undo();
    void Redo();
}

public class MoveElementsAction : IHistoryAction
{
    private readonly List<(GtElement Element, GtPoint Before, GtPoint After)> _moves;
    private readonly string? _description;

    /// <param name="description">overrides the default "Move ..." label, e.g. for alignment</param>
    public MoveElementsAction(List<(GtElement Element, GtPoint Before, GtPoint After)> moves,
                              string? description = null)
    {
        _moves       = moves;
        _description = description;
    }

    public string Description
    {
        get
        {
            if (_description is not null) return _description;
            if (_moves.Count == 1)
            {
                var name = _moves[0].Element.Name;
                return $"Move {(string.IsNullOrEmpty(name) ? "element" : name)}";
            }
            return $"Move {_moves.Count} elements";
        }
    }

    public void Undo()
    {
        foreach (var (el, before, _) in _moves)
            el.Location = before;
    }

    public void Redo()
    {
        foreach (var (el, _, after) in _moves)
            el.Location = after;
    }
}

public class ResizeElementsAction : IHistoryAction
{
    private readonly List<(GtElement Element, GtPoint OrigLoc, GtSize OrigDim, GtPoint NewLoc, GtSize NewDim)> _changes;

    public ResizeElementsAction(List<(GtElement, GtPoint, GtSize, GtPoint, GtSize)> changes)
        => _changes = changes;

    public string Description
    {
        get
        {
            if (_changes.Count == 1)
            {
                var name = _changes[0].Element.Name;
                return $"Resize {(string.IsNullOrEmpty(name) ? "element" : name)}";
            }
            return $"Resize {_changes.Count} elements";
        }
    }

    public void Undo()
    {
        foreach (var (el, origLoc, origDim, _, _) in _changes)
        {
            el.Location   = origLoc;
            el.Dimensions = origDim;
        }
    }

    public void Redo()
    {
        foreach (var (el, _, _, newLoc, newDim) in _changes)
        {
            el.Location   = newLoc;
            el.Dimensions = newDim;
        }
    }
}

/// <summary>adds, moves or removes a single ruler guide</summary>
/// <summary>layer drag on the canvas, only the layer origin moves; its elements are layer-local so they travel with it untouched</summary>
public class MoveLayerAction : IHistoryAction
{
    private readonly GtLayer _layer;
    private readonly GtPoint _before;
    private readonly GtPoint _after;

    public MoveLayerAction(GtLayer layer, GtPoint before, GtPoint after)
    {
        _layer  = layer;
        _before = before;
        _after  = after;
    }

    public string Description =>
        $"Move layer {(string.IsNullOrEmpty(_layer.Name) ? "(unnamed)" : _layer.Name)}";

    public void Undo() => _layer.Location = _before;
    public void Redo() => _layer.Location = _after;
}

/// <summary>layer handle drag, resizes the layer frame; the inner composition space keeps its size and elements hold absolute position, so dragging a top or left edge counter-shifts their layer-local coords (into the negative if needed) and that rides along here so undo restores both halves in one step</summary>
public class ResizeLayerAction : IHistoryAction
{
    private readonly GtLayer _layer;
    private readonly GtPoint _origLoc;
    private readonly GtSize  _origDim;
    private readonly GtPoint _newLoc;
    private readonly GtSize  _newDim;
    private readonly List<(GtElement El, GtPoint Before, GtPoint After)> _elementMoves;

    public ResizeLayerAction(GtLayer layer, GtPoint origLoc, GtSize origDim, GtPoint newLoc, GtSize newDim,
                             IEnumerable<(GtElement El, GtPoint Before, GtPoint After)>? elementMoves = null)
    {
        _layer   = layer;
        _origLoc = origLoc;
        _origDim = origDim;
        _newLoc  = newLoc;
        _newDim  = newDim;
        _elementMoves = elementMoves is null
            ? new List<(GtElement, GtPoint, GtPoint)>()
            : new List<(GtElement, GtPoint, GtPoint)>(elementMoves);
    }

    public string Description =>
        $"Resize layer {(string.IsNullOrEmpty(_layer.Name) ? "(unnamed)" : _layer.Name)}";

    public void Undo()
    {
        _layer.Location   = _origLoc;
        _layer.Dimensions = _origDim;
        foreach (var (el, before, _) in _elementMoves) el.Location = before;
    }

    public void Redo()
    {
        _layer.Location   = _newLoc;
        _layer.Dimensions = _newDim;
        foreach (var (el, _, after) in _elementMoves) el.Location = after;
    }
}

public class GuideAction : IHistoryAction
{
    private readonly GtDocument _document;
    private readonly GtGuide _guide;
    private readonly double _before;
    private readonly double _after;
    private readonly bool _added;
    private readonly bool _removed;

    private GuideAction(GtDocument document, GtGuide guide, double before, double after,
                        bool added, bool removed, string description)
    {
        _document   = document;
        _guide      = guide;
        _before     = before;
        _after      = after;
        _added      = added;
        _removed    = removed;
        Description = description;
    }

    public static GuideAction Add(GtDocument doc, GtGuide guide) =>
        new(doc, guide, guide.Position, guide.Position, added: true, removed: false,
            $"Add {Axis(guide)} guide");

    public static GuideAction Remove(GtDocument doc, GtGuide guide) =>
        new(doc, guide, guide.Position, guide.Position, added: false, removed: true,
            $"Delete {Axis(guide)} guide");

    public static GuideAction Move(GtDocument doc, GtGuide guide, double before, double after) =>
        new(doc, guide, before, after, added: false, removed: false, $"Move {Axis(guide)} guide");

    private static string Axis(GtGuide guide) =>
        guide.Orientation == GtGuideOrientation.Vertical ? "vertical" : "horizontal";

    public string Description { get; }

    public void Undo()
    {
        if (_added)        _document.Guides.Remove(_guide);
        else if (_removed) { if (!_document.Guides.Contains(_guide)) _document.Guides.Add(_guide); }
        else               _guide.Position = _before;
    }

    public void Redo()
    {
        if (_added)        { if (!_document.Guides.Contains(_guide)) _document.Guides.Add(_guide); }
        else if (_removed) _document.Guides.Remove(_guide);
        else               _guide.Position = _after;
    }
}

/// <summary>swaps the document's whole guide set, as a guides-file import does</summary>
public class ReplaceGuidesAction : IHistoryAction
{
    private readonly GtDocument _document;
    private readonly List<GtGuide> _before;
    private readonly List<GtGuide> _after;
    private readonly GtPoint _originBefore;
    private readonly GtPoint _originAfter;

    public ReplaceGuidesAction(GtDocument document, List<GtGuide> after, GtPoint originAfter)
    {
        _document     = document;
        _before       = new List<GtGuide>(document.Guides);
        _after        = after;
        _originBefore = document.RulerOrigin;
        _originAfter  = originAfter;
    }

    public string Description => $"Import {_after.Count} guides";

    public void Undo() => Apply(_before, _originBefore);
    public void Redo() => Apply(_after,  _originAfter);

    private void Apply(List<GtGuide> guides, GtPoint origin)
    {
        _document.Guides.Clear();
        _document.Guides.AddRange(guides);
        _document.RulerOrigin = origin;
    }
}

/// <summary>removes every guide at once (View › Clear Guides)</summary>
public class ClearGuidesAction : IHistoryAction
{
    private readonly GtDocument _document;
    private readonly List<GtGuide> _removed;

    public ClearGuidesAction(GtDocument document)
    {
        _document = document;
        _removed  = new List<GtGuide>(document.Guides);
    }

    public string Description => $"Clear {_removed.Count} guides";

    public void Undo()
    {
        _document.Guides.Clear();
        _document.Guides.AddRange(_removed);
    }

    public void Redo() => _document.Guides.Clear();
}

/// <summary>general-purpose undo/redo action backed by two lambdas</summary>
public class PropertyChangeAction : IHistoryAction
{
    private readonly Action _undo;
    private readonly Action _redo;

    public string Description { get; }

    public PropertyChangeAction(string description, Action undo, Action redo)
    {
        Description = description;
        _undo = undo;
        _redo = redo;
    }

    public void Undo() => _undo();
    public void Redo() => _redo();
}

public class HistoryService
{
    private readonly List<IHistoryAction> _actions = new();
    private int _cursor = -1;

    public bool CanUndo => _cursor >= 0;
    public bool CanRedo => _cursor < _actions.Count - 1;

    public IReadOnlyList<IHistoryAction> Actions => _actions;
    public int Cursor => _cursor;

    public event EventHandler? Changed;

    public void Push(IHistoryAction action)
    {
        if (_cursor < _actions.Count - 1)
            _actions.RemoveRange(_cursor + 1, _actions.Count - _cursor - 1);
        _actions.Add(action);
        _cursor++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _actions[_cursor--].Undo();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _actions[++_cursor].Redo();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _actions.Clear();
        _cursor = -1;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
