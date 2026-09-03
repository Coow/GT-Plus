using System;
using System.Collections.Generic;
using VmixGtPlus.Models;

namespace VmixGtPlus.Services;

/// <summary>applies <see cref="GtBounding"/> across a document, every element naming a source copies that source's box grown by its own padding onto its own Location and Dimensions</summary>
/// <remarks>GT resolves bounding in its compose pass after render has sized the surfaces, so a bound object's resize lands one frame late while a move is immediate; this runs it as a single pass over the model instead (the canvas calls it right after auto-size, before anything is drawn) so a text box that grows and the background bound to it settle on the same frame; everything else follows GT: padding grows the destination outward in composition pixels, a source that is itself bound aborts the whole binding (GT's only cycle guard, dependencies are at most one edge deep and A→B→C silently drops A→B), a destination that would collapse to nothing keeps a 1px floor per axis, a hidden or fully transparent destination stops tracking because GT skips it in compose and snaps back once visible again; the source is looked up among siblings in the element's own layer which is what the properties panel offers, GT resolves the name globally but copies the layer-local coordinates raw so a cross-layer binding lands off by the layer's own location, keeping the lookup layer-local avoids the question entirely</remarks>
public static class GtBoundingResolver
{
    /// <summary>resolves every binding in the document; returns true when any element moved or resized so the caller can refresh whatever was showing the old numbers</summary>
    public static bool Apply(GtDocument? document)
    {
        if (document is null) return false;

        bool changed = false;
        foreach (var layer in document.Layers)
            changed |= Apply(layer);
        return changed;
    }

    /// <summary>resolves the bindings owned by one layer's elements</summary>
    public static bool Apply(GtLayer layer)
    {
        Dictionary<string, GtElement>? names = null;

        bool changed = false;
        foreach (var element in layer.Elements)
        {
            var bounding = element.Bounding;
            if (bounding is null || !bounding.HasSource) continue;

            // GT composes neither a hidden nor a fully transparent object, and bounding is resolved from compose, so such a destination freezes where it last was
            if (!element.Visible || element.Opacity <= 0) continue;

            names ??= BuildNameMap(layer);
            if (!names.TryGetValue(bounding.Object!, out var source)) continue;
            if (ReferenceEquals(source, element)) continue;        // self-reference is a no-op

            // one edge only, a bound source makes this binding do nothing at all
            if (source.Bounding is { HasSource: true }) continue;

            var location = new GtPoint(
                source.Location.X - bounding.PaddingLeft,
                source.Location.Y - bounding.PaddingTop);

            var dimensions = new GtSize(
                Math.Max(1, source.Dimensions.Width  + bounding.PaddingLeft + bounding.PaddingRight),
                Math.Max(1, source.Dimensions.Height + bounding.PaddingTop  + bounding.PaddingBottom));

            if (element.Location.X != location.X || element.Location.Y != location.Y)
            {
                element.Location = location;
                changed = true;
            }

            if (element.Dimensions.Width  != dimensions.Width ||
                element.Dimensions.Height != dimensions.Height)
            {
                element.Dimensions = dimensions;   // Depth is separate, so it survives untouched
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>the box a destination would take from <paramref name="source"/> without writing it; used by the properties panel to show what a binding is about to do</summary>
    public static (GtPoint Location, GtSize Dimensions) Resolve(GtElement source, GtBounding bounding) =>
        (new GtPoint(source.Location.X - bounding.PaddingLeft,
                     source.Location.Y - bounding.PaddingTop),
         new GtSize(Math.Max(1, source.Dimensions.Width  + bounding.PaddingLeft + bounding.PaddingRight),
                    Math.Max(1, source.Dimensions.Height + bounding.PaddingTop  + bounding.PaddingBottom)));

    /// <summary>true when <paramref name="element"/>'s box is being written by a live binding, i.e. the user cannot move or resize it by hand and have it stay put</summary>
    public static bool IsBound(GtLayer? layer, GtElement element)
    {
        if (layer is null) return false;
        var bounding = element.Bounding;
        if (bounding is null || !bounding.HasSource) return false;

        foreach (var candidate in layer.Elements)
        {
            if (ReferenceEquals(candidate, element)) continue;
            if (!string.Equals(candidate.Name, bounding.Object, StringComparison.OrdinalIgnoreCase))
                continue;
            return candidate.Bounding is not { HasSource: true };
        }
        return false;
    }

    private static Dictionary<string, GtElement> BuildNameMap(GtLayer layer)
    {
        var map = new Dictionary<string, GtElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in layer.Elements)
            if (!string.IsNullOrEmpty(element.Name) && !map.ContainsKey(element.Name))
                map[element.Name] = element;
        return map;
    }
}
