using System;
using System.Collections.Generic;
using Avalonia.Animation.Easings;
using VmixGtPlus.Models;

namespace VmixGtPlus.Services;

/// <summary>per-target visual overrides produced by evaluating a storyboard at one instant; values are neutral by default so an override with nothing applied renders normally</summary>
public sealed class GtAnimOverride
{
    /// <summary>multiplied onto the target's own Opacity</summary>
    public double OpacityMul { get; set; } = 1.0;

    /// <summary>translation applied on top of the target's Location, in its own coordinate space</summary>
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }

    /// <summary>per-axis scale; GT animates Dimensions rather than a scale factor but for a linear lerp the two are identical as long as the box is pinned at the right anchor, which is what <see cref="ScaleAnchorX"/>/<see cref="ScaleAnchorY"/> carry</summary>
    public double ScaleX { get; set; } = 1.0;
    public double ScaleY { get; set; } = 1.0;

    /// <summary>point the scale is taken about, normalised inside the resting box: (0,0) = top-left, (0.5,0.5) = centre (what Zoom uses), (1,1) = bottom-right</summary>
    public double ScaleAnchorX { get; set; } = 0.5;
    public double ScaleAnchorY { get; set; } = 0.5;

    /// <summary>rotation added to the target's own RotateX/Y/Z in radians, same axis convention as the model: X = yaw, Y = pitch, Z = in-plane roll</summary>
    public double RotateX { get; set; }
    public double RotateY { get; set; }
    public double RotateZ { get; set; }

    /// <summary>true when the target is fully suppressed for the whole storyboard</summary>
    public bool Hidden { get; set; }

    /// <summary>true when a Reveal is driving the crop range below</summary>
    public bool HasCrop { get; set; }

    /// <summary>animated crop range, normalised 0-1, replacing the target's own while a Reveal runs</summary>
    public double CropX0 { get; set; }
    public double CropY0 { get; set; }
    public double CropX1 { get; set; } = 1.0;
    public double CropY1 { get; set; } = 1.0;

    /// <summary>scale applied to the target's own crop feather; only a Center Reveal moves it, GT ramps Crop.Feather from zero up to the authored value alongside the range</summary>
    public double FeatherScale { get; set; } = 1.0;

    /// <summary>normalised position (0-1) into an image sequence, when one is driven</summary>
    public double? SequencePosition { get; set; }

    /// <summary>true when this override would not change rendering at all</summary>
    public bool IsNeutral =>
        !Hidden && !HasCrop && SequencePosition is null &&
        OpacityMul >= 1.0 && ScaleX == 1.0 && ScaleY == 1.0 &&
        OffsetX == 0 && OffsetY == 0 &&
        RotateX == 0 && RotateY == 0 && RotateZ == 0;

    /// <summary>true when the box itself is transformed away from its resting rectangle</summary>
    public bool HasBoxTransform =>
        OffsetX != 0 || OffsetY != 0 || ScaleX != 1.0 || ScaleY != 1.0;

    internal void SetCrop(double x0, double y0, double x1, double y1)
    {
        HasCrop = true;
        CropX0 = x0; CropY0 = y0; CropX1 = x1; CropY1 = y1;
    }
}

/// <summary>immutable snapshot of every animated target at a single storyboard time; lookups are by object reference so the canvas can consult it while walking the model</summary>
public sealed class GtAnimationFrame
{
    private readonly Dictionary<GtLayer, GtAnimOverride> _layers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GtElement, GtAnimOverride> _elements = new(ReferenceEqualityComparer.Instance);

    public double Time { get; init; }

    public bool IsEmpty => _layers.Count == 0 && _elements.Count == 0;

    public GtAnimOverride? ForLayer(GtLayer layer) =>
        _layers.TryGetValue(layer, out var o) ? o : null;

    public GtAnimOverride? ForElement(GtElement element) =>
        _elements.TryGetValue(element, out var o) ? o : null;

    internal GtAnimOverride GetOrAddLayer(GtLayer layer)
    {
        if (!_layers.TryGetValue(layer, out var o)) _layers[layer] = o = new GtAnimOverride();
        return o;
    }

    internal GtAnimOverride GetOrAddElement(GtElement element)
    {
        if (!_elements.TryGetValue(element, out var o)) _elements[element] = o = new GtAnimOverride();
        return o;
    }
}

/// <summary>evaluates a <see cref="GtStoryboard"/> at an arbitrary time without mutating the document, drives both timeline scrubbing and playback preview; the per-direction tables below are transcribed from GT one row at a time rather than derived from a generic (dx,dy) vector because several are deliberately asymmetric, Rotate emits nothing for <c>None</c>, Scroll names the edge it travels toward where Fly names the edge it comes from, and the brush-offset pair folds <c>Center</c> in with <c>Left</c>, so a "clever" mapping diverges from vMix output</summary>
public static class GtAnimationEvaluator
{
    /// <summary>size a ZoomFade starts at, as a multiple of the object's own size</summary>
    private const double ZoomFadeStartScale = 2.0;

    /// <summary>one full turn, the amount every Rotate covers</summary>
    private const double FullTurn = 2 * Math.PI;

    /// <summary>fraction of its own height a Bounce overshoots above its resting position</summary>
    private const double BounceOvershoot = 1.0 / 3.0;

    // GT's easing names describe the resulting motion, inverted relative to the conventional in/out naming Avalonia uses: GT "CubicEasingIn" decelerates (its curve is 1-(1-p)^3, a standard ease-out) and "CubicEasingOut" is p^3, a standard ease-in
    private static readonly Easing Linear           = new LinearEasing();
    private static readonly Easing CubicDecelerate  = new CubicEaseOut();
    private static readonly Easing CubicAccelerate  = new CubicEaseIn();
    private static readonly Easing CubicSmooth      = new CubicEaseInOut();
    private static readonly Easing BounceIn         = new BounceEaseIn();
    private static readonly Easing BounceOut        = new BounceEaseOut();

    private static Easing EasingFor(GtInterpolation interpolation) => interpolation switch
    {
        GtInterpolation.CubicEasingIn    => CubicDecelerate,
        GtInterpolation.CubicEasingOut   => CubicAccelerate,
        GtInterpolation.CubicEasingInOut => CubicSmooth,
        GtInterpolation.BounceIn         => BounceIn,
        GtInterpolation.BounceOut        => BounceOut,
        _                                => Linear
    };

    /// <summary>builds the override set for <paramref name="storyboard"/> at <paramref name="time"/> seconds, returns an empty frame when there is nothing to animate</summary>
    public static GtAnimationFrame Evaluate(GtDocument document, GtStoryboard? storyboard, double time)
    {
        var frame = new GtAnimationFrame { Time = time };
        if (storyboard is not null)
            Accumulate(frame, document, storyboard, time, storyboard.PlaysRewound);
        return frame;
    }

    /// <summary>builds one override set from several storyboards laid out on a shared timeline; segments accumulate into the same frame so a TransitionIn/TransitionOut pair reads exactly like stacked animations on one storyboard, the out half takes over from wherever the in half settled</summary>
    public static GtAnimationFrame Evaluate(GtDocument document,
                                            IReadOnlyList<GtTimelineSegment>? segments,
                                            double time)
    {
        var frame = new GtAnimationFrame { Time = time };
        if (segments is null) return frame;

        foreach (var segment in segments)
        {
            double local = time - segment.Offset;

            // a segment contributes nothing before it starts; without this a TransitionOut whose objects are Hidden or driven by an ImageSequence would alter the picture while the in half is still playing instead of picking up from its last frame; its pre-roll counts as part of it, a negative delay is computed ahead of the segment's own zero so the gate opens at the earliest clip rather than at zero
            if (local < segment.Storyboard.EarliestStart) continue;

            Accumulate(frame, document, segment.Storyboard, local, segment.Storyboard.PlaysRewound);
        }

        return frame;
    }

    /// <summary>applies one storyboard's animations at <paramref name="time"/> into <paramref name="frame"/></summary>
    private static void Accumulate(GtAnimationFrame frame, GtDocument document,
                                   GtStoryboard storyboard, double time, bool rewound)
    {
        foreach (var anim in storyboard.Animations)
        {
            if (anim.IsPlaceholder || anim.Muted || string.IsNullOrEmpty(anim.Object)) continue;

            // GT's effective-reverse rule: the storyboard's own direction (TransitionOut and DataChangeIn run backwards) flips the animation's Reverse flag rather than replacing it, so a Reverse="True" fly on a TransitionOut plays forwards
            bool reversed = anim.Reverse != rewound;

            // "progress" runs 0->1 across the animation window, "rest" is the settled state (1 = at the To value); continuous animations have no window at all, they accumulate a Speed-scaled delta for as long as they have been running
            double elapsed  = time - anim.Delay;
            double duration = Math.Max(anim.EffectiveDuration, 1e-6);
            double raw      = anim.IsContinuousType ? 0.0 : Math.Clamp(elapsed / duration, 0.0, 1.0);
            double eased    = EasingFor(anim.Interpolation).Ease(raw);
            double rest     = reversed ? 1.0 - eased : eased;

            foreach (var target in ResolveTargets(document, anim.Object))
                Apply(frame, document, anim, target, raw, rest, reversed, elapsed, time);
        }
    }

    private readonly struct Target
    {
        public Target(GtLayer layer) { Layer = layer; Element = null; OwnerLayer = layer; }
        public Target(GtLayer owner, GtElement element) { Layer = null; Element = element; OwnerLayer = owner; }

        public GtLayer? Layer { get; }
        public GtElement? Element { get; }
        public GtLayer OwnerLayer { get; }
    }

    /// <summary>resolves an animation's Object name to layers and elements; GT names are meant to be unique but a name reused across layers animates every match rather than silently one</summary>
    private static IEnumerable<Target> ResolveTargets(GtDocument document, string name)
    {
        foreach (var layer in document.Layers)
        {
            if (string.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase))
                yield return new Target(layer);

            foreach (var element in layer.Elements)
                if (string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase))
                    yield return new Target(layer, element);
        }
    }

    /// <summary>the target's resting box in absolute document space; GT would union this with the display bounds of every object bound to the target (GetTransitionBounds), but with no binding concept here that union degenerates to the object's own box and the resulting transition display offset to zero, which is the correct degenerate case</summary>
    private static (double Left, double Top, double Width, double Height) BoxOf(Target target, GtDocument document)
    {
        if (target.Layer is not null)
        {
            // layers in the wild carry no Dimensions, so the inner composition (and failing that the canvas) stands in, the same fallback the renderer draws the frame with
            var l = target.Layer;
            double lw = l.Dimensions.Width  > 0 ? l.Dimensions.Width  :
                        l.InnerWidth        > 0 ? l.InnerWidth        : document.Width;
            double lh = l.Dimensions.Height > 0 ? l.Dimensions.Height :
                        l.InnerHeight       > 0 ? l.InnerHeight       : document.Height;
            return (l.Location.X, l.Location.Y, lw, lh);
        }

        var el = target.Element!;
        return (target.OwnerLayer.Location.X + el.Location.X,
                target.OwnerLayer.Location.Y + el.Location.Y,
                el.Dimensions.Width, el.Dimensions.Height);
    }

    private static void Apply(GtAnimationFrame frame, GtDocument document, GtAnimation anim,
                              Target target, double raw, double rest, bool reversed,
                              double elapsed, double time)
    {
        var o = target.Layer is not null
            ? frame.GetOrAddLayer(target.Layer)
            : frame.GetOrAddElement(target.Element!);

        var box  = BoxOf(target, document);
        var dir  = anim.Direction;
        double docW = document.Width, docH = document.Height;

        switch (anim.Type)
        {
            case GtAnimationType.Hidden:
                o.Hidden = true;
                break;

            case GtAnimationType.Fade:
                // stacked fades multiply, so an in/out pair reads as visible only between them
                o.OpacityMul *= rest;
                break;

            case GtAnimationType.Zoom:
                ApplyScale(o, rest, rest, 0.5, 0.5);
                break;

            // zoom and fade in one animation, GT gives it no Direction; unlike Zoom it shrinks into place (the object starts at twice its size and settles at 1.0) so the scale interpolates 2 -> 1 rather than 0 -> 1
            case GtAnimationType.ZoomFade:
            {
                double s = ZoomFadeStartScale - rest * (ZoomFadeStartScale - 1.0);
                ApplyScale(o, s, s, 0.5, 0.5);
                o.OpacityMul *= rest;
                break;
            }

            case GtAnimationType.Fly:
            {
                var (dx, dy) = FlyFromDelta(dir, box, docW, docH);
                // offsets accumulate, an in/out pair is at rest (0,0) in the middle
                o.OffsetX += dx * (1.0 - rest);
                o.OffsetY += dy * (1.0 - rest);
                break;
            }

            case GtAnimationType.Bounce:
            {
                // GT builds a Fly and then adds a second animation on Location.Y applied after it each frame, so the fly supplies X while the overshoot owns Y outright whatever the direction's vertical component would have been
                var (dx, _) = FlyFromDelta(dir, box, docW, docH);
                o.OffsetX += dx * (1.0 - rest);

                // the overshoot forces its own curve, BounceOut forwards, BounceIn reversed
                double bounceEased = (reversed ? BounceIn : BounceOut).Ease(raw);
                double bounceRest  = reversed ? 1.0 - bounceEased : bounceEased;
                o.OffsetY += -box.Height * BounceOvershoot * (1.0 - bounceRest);
                break;
            }

            case GtAnimationType.Expand:
            {
                // GT lerps Dimensions from a collapsed size and Location from the matching corner, together that is exactly a scale pinned at that corner
                var (collapseX, collapseY, ax, ay) = ExpandAnchor(dir);
                ApplyScale(o, collapseX ? rest : 1.0, collapseY ? rest : 1.0, ax, ay);
                break;
            }

            case GtAnimationType.Reveal:
            {
                // GT lerps all four components of Crop.Range independently, towards the object's own authored crop
                var to = RestingCrop(target);
                var (fx0, fy0, fx1, fy1) = RevealFromRange(dir, anim.CenterAxis);

                o.SetCrop(Lerp(fx0, to.X0, rest), Lerp(fy0, to.Y0, rest),
                          Lerp(fx1, to.X1, rest), Lerp(fy1, to.Y1, rest));

                // only a Center reveal animates Crop.Feather, from zero up to the authored value so the soft edge grows in with the opening band
                o.FeatherScale = dir == GtAnimDirection.Center ? rest : 1.0;
                break;
            }

            case GtAnimationType.Rotate:
            {
                // From = static + one full turn, To = static, the object settles onto its own authored rotation from exactly 360 degrees away
                double turn = FullTurn * (1.0 - rest);
                ApplyRotate(o, dir, turn);
                break;
            }

            case GtAnimationType.Scroll:
            {
                // edge to edge across the composition; unlike Fly the direction names where the object is heading, not where it came from
                var (fx, fy, tx, ty) = ScrollPath(dir, box, docW, docH);
                o.OffsetX += Lerp(fx, tx, rest) - box.Left;
                o.OffsetY += Lerp(fy, ty, rest) - box.Top;
                break;
            }

            case GtAnimationType.RotateContinuous:
            {
                // GT adds Speed/FPS turns of rotation every frame, which over t seconds is Speed*t turns however fast the render loop happens to run
                double turn = FullTurn * anim.EffectiveSpeed * Math.Max(0.0, elapsed);
                ApplyContinuousRotate(o, dir, turn);
                break;
            }

            case GtAnimationType.ImageSequence:
                o.SequencePosition = rest;
                break;

            case GtAnimationType.ImageSequenceLoop:
            {
                double loop = Math.Max(anim.EffectiveDuration, 1e-6);
                double phase = (time - anim.Delay) / loop;
                o.SequencePosition = time < anim.Delay ? 0.0 : phase - Math.Floor(phase);
                break;
            }

            // FillOffset / StrokeOffset drift a brush's texture offset rather than the object, and Blink toggles visibility through a change converter; neither has anything to drive in this renderer yet, so exactly as GT does for an object with no such brush they build to nothing rather than throwing or guessing
            case GtAnimationType.FillOffset:
            case GtAnimationType.StrokeOffset:
            case GtAnimationType.Blink:
            default:
                break;
        }
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;

    /// <summary>composes a scale onto the override; anchors do not compose exactly when two animations pull a target from different corners at once, which GT cannot express either (its second animation simply overwrites Location), so the most recent anchor wins</summary>
    private static void ApplyScale(GtAnimOverride o, double sx, double sy, double ax, double ay)
    {
        o.ScaleX *= sx;
        o.ScaleY *= sy;
        o.ScaleAnchorX = ax;
        o.ScaleAnchorY = ay;
    }

    /// <summary>the target's authored crop range, or the full rect when it carries no crop</summary>
    private static (double X0, double Y0, double X1, double Y1) RestingCrop(Target target)
    {
        var crop = target.Element?.Crop;
        return crop is null ? (0.0, 0.0, 1.0, 1.0) : (crop.X0, crop.Y0, crop.X1, crop.Y1);
    }

    /// <summary>Fly's start offset relative to the resting position, far enough that the object sits just outside the composition edge named by Direction; Center and None both start at the resting position so the animation still drives and holds Location but never moves</summary>
    private static (double dx, double dy) FlyFromDelta(
        GtAnimDirection dir, (double Left, double Top, double Width, double Height) box,
        double docW, double docH)
    {
        double offLeft   = -box.Width - box.Left;    // just past the left edge
        double offRight  = docW - box.Left;          // just past the right edge
        double offTop    = -box.Height - box.Top;    // just above the top edge
        double offBottom = docH - box.Top;           // just below the bottom edge

        return dir switch
        {
            GtAnimDirection.Top         => (0,        offTop),
            GtAnimDirection.Bottom      => (0,        offBottom),
            GtAnimDirection.Left        => (offLeft,  0),
            GtAnimDirection.Right       => (offRight, 0),
            GtAnimDirection.TopLeft     => (offLeft,  offTop),
            GtAnimDirection.TopRight    => (offRight, offTop),
            GtAnimDirection.BottomLeft  => (offLeft,  offBottom),
            GtAnimDirection.BottomRight => (offRight, offBottom),
            _                           => (0, 0),   // Center and None, no travel
        };
    }

    /// <summary>which axes an Expand collapses, and the point it grows out of; None collapses both axes from the top-left, the same as TopLeft</summary>
    private static (bool CollapseX, bool CollapseY, double AnchorX, double AnchorY) ExpandAnchor(
        GtAnimDirection dir) => dir switch
    {
        GtAnimDirection.Top         => (false, true,  0.5, 0.0),
        GtAnimDirection.Bottom      => (false, true,  0.5, 1.0),
        GtAnimDirection.Left        => (true,  false, 0.0, 0.5),
        GtAnimDirection.Right       => (true,  false, 1.0, 0.5),
        GtAnimDirection.TopLeft     => (true,  true,  0.0, 0.0),
        GtAnimDirection.TopRight    => (true,  true,  1.0, 0.0),
        GtAnimDirection.BottomLeft  => (true,  true,  0.0, 1.0),
        GtAnimDirection.BottomRight => (true,  true,  1.0, 1.0),
        GtAnimDirection.Center      => (true,  true,  0.5, 0.5),
        _                           => (true,  true,  0.0, 0.0),   // None
    };

    /// <summary>Crop.Range a Reveal starts from; Left is also the fallback for None matching GT's default branch, and Center is the only direction that consults the axis</summary>
    private static (double X0, double Y0, double X1, double Y1) RevealFromRange(
        GtAnimDirection dir, GtCenterAxis axis) => dir switch
    {
        GtAnimDirection.Top         => (0, 0, 1, 0),
        GtAnimDirection.Bottom      => (0, 1, 1, 1),
        GtAnimDirection.Right       => (1, 0, 1, 1),
        GtAnimDirection.TopLeft     => (0, 0, 0, 0),
        GtAnimDirection.TopRight    => (1, 0, 1, 0),
        GtAnimDirection.BottomLeft  => (0, 1, 0, 1),
        GtAnimDirection.BottomRight => (1, 1, 1, 1),
        GtAnimDirection.Center      => axis switch
        {
            GtCenterAxis.X => (0.5, 0.0, 0.5, 1.0),
            GtCenterAxis.Y => (0.0, 0.5, 1.0, 0.5),
            _              => (0.5, 0.5, 0.5, 0.5),   // Both
        },
        _                           => (0, 0, 0, 1),   // Left, and None
    };

    /// <summary>Rotate's axis and sign; corners spin about two axes at once, and None is the one case in the whole engine where a direction builds no animation at all</summary>
    private static void ApplyRotate(GtAnimOverride o, GtAnimDirection dir, double turn)
    {
        switch (dir)
        {
            case GtAnimDirection.Top:         o.RotateY += turn; break;
            case GtAnimDirection.Bottom:      o.RotateY -= turn; break;
            case GtAnimDirection.Left:        o.RotateX += turn; break;
            case GtAnimDirection.Right:       o.RotateX -= turn; break;
            case GtAnimDirection.TopLeft:     o.RotateY += turn; o.RotateX += turn; break;
            case GtAnimDirection.TopRight:    o.RotateY += turn; o.RotateX -= turn; break;
            case GtAnimDirection.BottomLeft:  o.RotateY -= turn; o.RotateX += turn; break;
            case GtAnimDirection.BottomRight: o.RotateY -= turn; o.RotateX -= turn; break;
            case GtAnimDirection.Center:      o.RotateZ += turn; break;
            default:                          break;   // None emits nothing
        }
    }

    /// <summary>RotateContinuous signs, the opposite of the fixed Rotate on both axes (it spins away from the resting pose rather than settling onto it); Left is also the fallback for None here</summary>
    private static void ApplyContinuousRotate(GtAnimOverride o, GtAnimDirection dir, double turn)
    {
        switch (dir)
        {
            case GtAnimDirection.Top:         o.RotateY -= turn; break;
            case GtAnimDirection.Bottom:      o.RotateY += turn; break;
            case GtAnimDirection.Right:       o.RotateX += turn; break;
            case GtAnimDirection.TopLeft:     o.RotateY -= turn; o.RotateX -= turn; break;
            case GtAnimDirection.TopRight:    o.RotateY -= turn; o.RotateX += turn; break;
            case GtAnimDirection.BottomLeft:  o.RotateY += turn; o.RotateX -= turn; break;
            case GtAnimDirection.BottomRight: o.RotateY += turn; o.RotateX += turn; break;
            case GtAnimDirection.Center:      o.RotateZ -= turn; break;
            default:                          o.RotateX -= turn; break;   // Left, and None
        }
    }

    /// <summary>Scroll's absolute start and end positions; Bottom is both the default and the fallback for Center and None, the object enters below the composition and travels up off the top</summary>
    private static (double FromX, double FromY, double ToX, double ToY) ScrollPath(
        GtAnimDirection dir, (double Left, double Top, double Width, double Height) box,
        double docW, double docH)
    {
        double offLeft = -box.Width, offTop = -box.Height;

        return dir switch
        {
            GtAnimDirection.Top         => (box.Left, offTop,   box.Left, docH),
            GtAnimDirection.Left        => (offLeft,  box.Top,  docW,     box.Top),
            GtAnimDirection.Right       => (docW,     box.Top,  offLeft,  box.Top),
            GtAnimDirection.TopLeft     => (offLeft,  offTop,   docW,     docH),
            GtAnimDirection.TopRight    => (docW,     offTop,   offLeft,  docH),
            GtAnimDirection.BottomLeft  => (offLeft,  docH,     docW,     offTop),
            GtAnimDirection.BottomRight => (docW,     docH,     offLeft,  offTop),
            _                           => (box.Left, docH,     box.Left, offTop),   // Bottom, Center, None
        };
    }

    /// <summary>hard-edged clip rectangle for an animated crop, in the same space as <paramref name="bounds"/>; used where the full feathered crop pipeline is not available, masking one element with another</summary>
    public static Avalonia.Rect CropClip(Avalonia.Rect bounds, GtAnimOverride o)
    {
        if (!o.HasCrop) return bounds;

        double x0 = Math.Clamp(Math.Min(o.CropX0, o.CropX1), 0.0, 1.0);
        double x1 = Math.Clamp(Math.Max(o.CropX0, o.CropX1), 0.0, 1.0);
        double y0 = Math.Clamp(Math.Min(o.CropY0, o.CropY1), 0.0, 1.0);
        double y1 = Math.Clamp(Math.Max(o.CropY0, o.CropY1), 0.0, 1.0);

        return new Avalonia.Rect(bounds.X + x0 * bounds.Width,
                                 bounds.Y + y0 * bounds.Height,
                                 (x1 - x0) * bounds.Width,
                                 (y1 - y0) * bounds.Height);
    }

    /// <summary>the crop the renderer should actually use for <paramref name="element"/>: its own, the animated range, or the two combined; returns null when nothing is cropped</summary>
    public static GtCrop? EffectiveCrop(GtElement element, GtAnimOverride? anim)
    {
        if (anim is null || !anim.HasCrop) return element.Crop;

        // the animated range already interpolates towards the element's own so it replaces it outright, the feather is the element's ramped in by a Center reveal
        var source = element.Crop;
        return new GtCrop
        {
            X0 = anim.CropX0,
            Y0 = anim.CropY0,
            X1 = anim.CropX1,
            Y1 = anim.CropY1,
            FeatherLeft   = (source?.FeatherLeft   ?? GtCrop.DefaultFeather) * anim.FeatherScale,
            FeatherTop    = (source?.FeatherTop    ?? GtCrop.DefaultFeather) * anim.FeatherScale,
            FeatherRight  = (source?.FeatherRight  ?? GtCrop.DefaultFeather) * anim.FeatherScale,
            FeatherBottom = (source?.FeatherBottom ?? GtCrop.DefaultFeather) * anim.FeatherScale,
        };
    }
}
