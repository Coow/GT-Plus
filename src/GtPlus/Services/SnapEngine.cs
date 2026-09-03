using System;
using System.Collections.Generic;
using Avalonia;
using GtPlus.Models;

namespace GtPlus.Services;

public enum SnapKind
{
    /// <summary>a ruler guide</summary>
    Guide,
    /// <summary>a canvas edge or the canvas centre line</summary>
    Canvas,
    /// <summary>an edge or centre line of the layer frame that crops the dragged element</summary>
    Layer,
    /// <summary>another element's edge or centre line (Photoshop's "smart guide")</summary>
    Element
}

/// <summary>one feedback line to draw while a snap is active, in doc space</summary>
public readonly struct SnapLine
{
    public SnapLine(bool vertical, double position, double start, double end, SnapKind kind)
    {
        Vertical = vertical;
        Position = position;
        Start    = start;
        End      = end;
        Kind     = kind;
    }

    /// <summary>true when the line runs top-to-bottom, i.e. <see cref="Position"/> is an X value</summary>
    public bool     Vertical { get; }
    public double   Position { get; }
    /// <summary>extent along the other axis, guides and canvas lines span the whole document</summary>
    public double   Start    { get; }
    public double   End      { get; }
    public SnapKind Kind     { get; }
}

/// <summary>snap offsets to apply plus the lines that produced them</summary>
public sealed class SnapSolution
{
    public double DeltaX { get; set; }
    public double DeltaY { get; set; }
    public List<SnapLine> Lines { get; } = new();
    public bool SnappedX { get; set; }
    public bool SnappedY { get; set; }
}

/// <summary>Photoshop-style snapping; built once per drag gesture from the document's guides, canvas edges/centre and the bounds of every element not being dragged, then queried per pointer move for the offset that pulls the dragged geometry onto the nearest target</summary>
public sealed class SnapEngine
{
    private readonly struct Candidate
    {
        public Candidate(double pos, SnapKind kind, double start, double end)
        {
            Pos = pos; Kind = kind; Start = start; End = end;
        }

        public double   Pos  { get; }
        public SnapKind Kind { get; }
        public double   Start { get; }
        public double   End   { get; }
    }

    private readonly List<Candidate> _x = new();
    private readonly List<Candidate> _y = new();
    private readonly double _docW;
    private readonly double _docH;

    /// <summary>pull distance in doc units, callers pass screen-px / zoom so it feels constant</summary>
    public double Threshold { get; set; } = 8;

    public bool IsEmpty => _x.Count == 0 && _y.Count == 0;

    private SnapEngine(double docW, double docH)
    {
        _docW = docW;
        _docH = docH;
    }

    /// <summary>collects snap targets; <paramref name="exclude"/> elements contribute nothing so a dragged element never snaps to itself</summary>
    /// <param name="frames">doc-space layer frames that crop what is being dragged; their edges and centres snap like canvas lines since a cropping boundary is exactly where the contents want to line up</param>
    public static SnapEngine Build(GtDocument doc,
                                   IReadOnlyCollection<GtElement>? exclude,
                                   bool includeGuides   = true,
                                   bool includeElements = true,
                                   bool includeCanvas   = true,
                                   IReadOnlyList<Rect>? frames = null,
                                   double threshold     = 8)
    {
        var engine = new SnapEngine(doc.Width, doc.Height) { Threshold = threshold };

        if (includeCanvas)
        {
            engine._x.Add(new Candidate(0,            SnapKind.Canvas, 0, doc.Height));
            engine._x.Add(new Candidate(doc.Width / 2, SnapKind.Canvas, 0, doc.Height));
            engine._x.Add(new Candidate(doc.Width,     SnapKind.Canvas, 0, doc.Height));
            engine._y.Add(new Candidate(0,             SnapKind.Canvas, 0, doc.Width));
            engine._y.Add(new Candidate(doc.Height / 2, SnapKind.Canvas, 0, doc.Width));
            engine._y.Add(new Candidate(doc.Height,     SnapKind.Canvas, 0, doc.Width));
        }

        if (frames is not null)
        {
            foreach (var f in frames)
            {
                engine._x.Add(new Candidate(f.Left,     SnapKind.Layer, f.Top,  f.Bottom));
                engine._x.Add(new Candidate(f.Center.X, SnapKind.Layer, f.Top,  f.Bottom));
                engine._x.Add(new Candidate(f.Right,    SnapKind.Layer, f.Top,  f.Bottom));
                engine._y.Add(new Candidate(f.Top,      SnapKind.Layer, f.Left, f.Right));
                engine._y.Add(new Candidate(f.Center.Y, SnapKind.Layer, f.Left, f.Right));
                engine._y.Add(new Candidate(f.Bottom,   SnapKind.Layer, f.Left, f.Right));
            }
        }

        if (includeGuides)
        {
            foreach (var guide in doc.Guides)
            {
                if (guide.Orientation == GtGuideOrientation.Vertical)
                    engine._x.Add(new Candidate(guide.Position, SnapKind.Guide, 0, doc.Height));
                else
                    engine._y.Add(new Candidate(guide.Position, SnapKind.Guide, 0, doc.Width));
            }
        }

        if (includeElements)
        {
            foreach (var layer in doc.Layers)
            {
                if (!layer.Visible) continue;
                foreach (var el in layer.Elements)
                {
                    if (!el.Visible) continue;
                    if (exclude is not null && Contains(exclude, el)) continue;

                    var b = new Rect(
                        layer.Location.X + el.Location.X,
                        layer.Location.Y + el.Location.Y,
                        el.Dimensions.Width,
                        el.Dimensions.Height);

                    engine._x.Add(new Candidate(b.Left,     SnapKind.Element, b.Top,  b.Bottom));
                    engine._x.Add(new Candidate(b.Center.X, SnapKind.Element, b.Top,  b.Bottom));
                    engine._x.Add(new Candidate(b.Right,    SnapKind.Element, b.Top,  b.Bottom));
                    engine._y.Add(new Candidate(b.Top,      SnapKind.Element, b.Left, b.Right));
                    engine._y.Add(new Candidate(b.Center.Y, SnapKind.Element, b.Left, b.Right));
                    engine._y.Add(new Candidate(b.Bottom,   SnapKind.Element, b.Left, b.Right));
                }
            }
        }

        return engine;
    }

    private static bool Contains(IReadOnlyCollection<GtElement> set, GtElement el)
    {
        foreach (var e in set)
            if (ReferenceEquals(e, el)) return true;
        return false;
    }

    /// <summary>snap a moving box, probes its left/centre/right and top/centre/bottom</summary>
    public SnapSolution SolveMove(Rect bounds)
    {
        var solution = new SnapSolution();
        SolveAxis(solution, vertical: true,
                  new[] { bounds.Left, bounds.Center.X, bounds.Right }, bounds.Top, bounds.Bottom);
        SolveAxis(solution, vertical: false,
                  new[] { bounds.Top, bounds.Center.Y, bounds.Bottom }, bounds.Left, bounds.Right);
        return solution;
    }

    /// <summary>snap only the edges a resize handle is dragging</summary>
    public SnapSolution SolveEdges(Rect bounds, bool left, bool right, bool top, bool bottom)
    {
        var solution = new SnapSolution();

        var xProbes = new List<double>(2);
        if (left)  xProbes.Add(bounds.Left);
        if (right) xProbes.Add(bounds.Right);
        if (xProbes.Count > 0)
            SolveAxis(solution, vertical: true, xProbes, bounds.Top, bounds.Bottom);

        var yProbes = new List<double>(2);
        if (top)    yProbes.Add(bounds.Top);
        if (bottom) yProbes.Add(bounds.Bottom);
        if (yProbes.Count > 0)
            SolveAxis(solution, vertical: false, yProbes, bounds.Left, bounds.Right);

        return solution;
    }

    /// <summary>snap a single coordinate, a guide being dragged; guides do not snap to other guides so build the engine with <c>includeGuides: false</c> for this</summary>
    public double SolveSingle(double position, bool vertical, out SnapLine? line)
    {
        line = null;
        var list = vertical ? _x : _y;
        double best = double.MaxValue;
        Candidate bestCand = default;
        bool found = false;

        foreach (var c in list)
        {
            var delta = c.Pos - position;
            var abs   = Math.Abs(delta);
            if (abs > Threshold || abs >= Math.Abs(best)) continue;
            best     = delta;
            bestCand = c;
            found    = true;
        }

        if (!found) return position;

        var snapped = position + best;
        bool spanned = bestCand.Kind is SnapKind.Element or SnapKind.Layer;
        line = new SnapLine(vertical, snapped,
                            spanned ? bestCand.Start : 0,
                            spanned ? bestCand.End   : (vertical ? _docH : _docW),
                            bestCand.Kind);
        return snapped;
    }

    private void SolveAxis(SnapSolution solution, bool vertical,
                           IReadOnlyList<double> probes, double probeSpanStart, double probeSpanEnd)
    {
        var list = vertical ? _x : _y;
        if (list.Count == 0 || probes.Count == 0) return;

        double bestDelta = double.MaxValue;
        double bestProbe = 0;
        Candidate bestCand = default;
        bool found = false;

        foreach (var probe in probes)
        foreach (var c in list)
        {
            var delta = c.Pos - probe;
            var abs   = Math.Abs(delta);
            if (abs > Threshold) continue;

            // ties go to the earlier candidate, which orders canvas before guides before elements, the same priority Photoshop shows when targets coincide
            if (found && abs >= Math.Abs(bestDelta)) continue;

            bestDelta = delta;
            bestProbe = probe;
            bestCand  = c;
            found     = true;
        }

        if (!found) return;

        if (vertical) { solution.DeltaX = bestDelta; solution.SnappedX = true; }
        else          { solution.DeltaY = bestDelta; solution.SnappedY = true; }

        var pos = bestProbe + bestDelta;
        double start, end;
        if (bestCand.Kind is SnapKind.Element or SnapKind.Layer)
        {
            // smart guide spans both boxes, so the alignment it reports is visible at a glance
            start = Math.Min(bestCand.Start, probeSpanStart);
            end   = Math.Max(bestCand.End,   probeSpanEnd);
        }
        else
        {
            start = 0;
            end   = vertical ? _docH : _docW;
        }

        solution.Lines.Add(new SnapLine(vertical, pos, start, end, bestCand.Kind));
    }
}
