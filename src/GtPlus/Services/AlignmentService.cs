using System;
using System.Collections.Generic;
using Avalonia;
using GtPlus.Models;

namespace GtPlus.Services;

public enum GtAlign
{
    Left,
    CenterHorizontal,
    Right,
    Top,
    Middle,
    Bottom,
    /// <summary>centres on both axes at once</summary>
    CenterBoth
}

/// <summary>what the elements are aligned against</summary>
public enum GtAlignTarget
{
    /// <summary>canvas for a single element, the selection's own bounds for several</summary>
    Auto,
    /// <summary>always the document rectangle, however many elements are selected</summary>
    Canvas,
    /// <summary>always the selection's bounding box</summary>
    Selection
}

/// <summary>aligns elements against the canvas (single selection) or the selection's own bounding box (two or more selected), like Photoshop's align buttons</summary>
public static class AlignmentService
{
    /// <summary>applies the alignment and returns an undoable action, or null when nothing moved (all locked or already in position)</summary>
    public static IHistoryAction? Align(GtDocument document,
                                        IReadOnlyCollection<GtElement> selection,
                                        GtAlign align,
                                        GtAlignTarget target = GtAlignTarget.Auto)
    {
        if (document is null || selection.Count == 0) return null;

        // element locations are layer-local, so every candidate carries its layer offset
        var targets = new List<(GtElement Element, GtLayer Layer, Rect Bounds)>();
        foreach (var layer in document.Layers)
        {
            if (layer.Locked) continue;
            foreach (var el in layer.Elements)
            {
                if (el.Locked || !ContainsRef(selection, el)) continue;
                targets.Add((el, layer, new Rect(
                    layer.Location.X + el.Location.X,
                    layer.Location.Y + el.Location.Y,
                    el.Dimensions.Width,
                    el.Dimensions.Height)));
            }
        }

        if (targets.Count == 0) return null;

        bool useSelectionBounds = target switch
        {
            GtAlignTarget.Canvas    => false,
            GtAlignTarget.Selection => true,
            _                       => targets.Count >= 2
        };

        var frame = useSelectionBounds
            ? Union(targets)
            : new Rect(0, 0, document.Width, document.Height);

        var moves = new List<(GtElement, GtPoint, GtPoint)>();
        foreach (var (el, layer, bounds) in targets)
        {
            double absX = bounds.X, absY = bounds.Y;

            switch (align)
            {
                case GtAlign.Left:             absX = frame.Left; break;
                case GtAlign.CenterHorizontal: absX = frame.Center.X - bounds.Width / 2; break;
                case GtAlign.Right:            absX = frame.Right - bounds.Width; break;
                case GtAlign.Top:              absY = frame.Top; break;
                case GtAlign.Middle:           absY = frame.Center.Y - bounds.Height / 2; break;
                case GtAlign.Bottom:           absY = frame.Bottom - bounds.Height; break;
                case GtAlign.CenterBoth:
                    absX = frame.Center.X - bounds.Width  / 2;
                    absY = frame.Center.Y - bounds.Height / 2;
                    break;
            }

            var before = el.Location;
            var after  = new GtPoint(absX - layer.Location.X, absY - layer.Location.Y);
            if (before.X == after.X && before.Y == after.Y) continue;

            el.Location = after;
            moves.Add((el, before, after));
        }

        if (moves.Count == 0) return null;

        return new MoveElementsAction(moves, $"Align {Label(align)}");
    }

    private static Rect Union(List<(GtElement Element, GtLayer Layer, Rect Bounds)> targets)
    {
        var rect = targets[0].Bounds;
        for (int i = 1; i < targets.Count; i++)
            rect = rect.Union(targets[i].Bounds);
        return rect;
    }

    private static bool ContainsRef(IReadOnlyCollection<GtElement> set, GtElement el)
    {
        foreach (var e in set)
            if (ReferenceEquals(e, el)) return true;
        return false;
    }

    private static string Label(GtAlign align) => align switch
    {
        GtAlign.Left             => "left",
        GtAlign.CenterHorizontal => "horizontal centres",
        GtAlign.Right            => "right",
        GtAlign.Top              => "top",
        GtAlign.Middle           => "vertical centres",
        GtAlign.Bottom           => "bottom",
        GtAlign.CenterBoth       => "centre",
        _                        => "elements"
    };
}
