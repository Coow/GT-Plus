using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VmixGtPlus.Models;

namespace VmixGtPlus.Controls;

/// <summary>custom-drawn storyboard track area: a time ruler, one row per animation, a draggable playhead, and drag handles on each bar for delay (move) and duration (edges); purely a view over <see cref="GtStoryboard"/>, it never touches the document model outside the drag gestures the user performs</summary>
public class TimelineTrackControl : Control
{
    public const double GutterWidth = 158;
    public const double RulerHeight = 20;
    public const double RowHeight   = 21;
    private const double BarInset   = 3;
    private const double EdgeGrab   = 5;

    /// <summary>mute eye at the right of each gutter row, and the gap it keeps from the edge</summary>
    private const double MuteIconSize  = 13;
    private const double MuteIconInset = 6;

    /// <summary>direction arrow drawn at the right end of a bar, and the room it needs</summary>
    private const double IconSize        = 13;
    private const double IconMargin      = 3;
    private const double IconMinBarWidth = 24;

    private static readonly IBrush RowBrushEven  = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16));
    private static readonly IBrush RowBrushOdd   = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a));
    private static readonly IBrush GutterBrush   = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12));
    private static readonly IBrush RulerBrush    = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
    private static readonly IBrush LabelBrush    = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc));
    private static readonly IBrush DimLabelBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Color  SelectedColor = Color.FromRgb(0x4d, 0x9a, 0xe0);
    private static readonly IBrush SelectedBrush = new SolidColorBrush(SelectedColor);
    private static readonly IPen   GridPen       = new Pen(new SolidColorBrush(Color.FromRgb(0x2e, 0x2e, 0x2e)), 1);
    private static readonly IPen   PlayheadPen   = new Pen(new SolidColorBrush(Color.FromRgb(0xff, 0x60, 0x60)), 1.5);
    private static readonly IPen   BorderPen     = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1);

    // time zero: where the clip actually starts playing; only drawn once the track shows pre-roll, since without negative delays it coincides with the left edge anyway
    private static readonly IPen   ZeroPen       = new Pen(new SolidColorBrush(Color.FromRgb(0xe0, 0xa2, 0x4d)), 1);
    private static readonly IBrush ZeroLabelBrush = new SolidColorBrush(Color.FromRgb(0xe0, 0xa2, 0x4d));

    /// <summary>wash over everything left of zero, so pre-roll reads as "already running"</summary>
    private static readonly IBrush PreRollBrush  = new SolidColorBrush(Colors.Black, 0.30);

    // the marquee is drawn over everything, so it stays thin and mostly transparent
    private static readonly IPen   MarqueePen  = new Pen(SelectedBrush, 1, new DashStyle(new double[] { 3, 3 }, 0));
    private static readonly IBrush MarqueeFill = new SolidColorBrush(SelectedColor, 0.12);

    // cursors are shared: OnPointerMoved fires constantly and allocating one per move would churn native cursor handles
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor HandCursor   = new(StandardCursorType.Hand);
    private static readonly Cursor CrossCursor  = new(StandardCursorType.Cross);

    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x2b));

    private IReadOnlyList<GtTimelineSegment> _segments = Array.Empty<GtTimelineSegment>();

    /// <summary>everything selected, the last entry is the primary whose values fill the strip</summary>
    private readonly List<GtAnimation> _selection = new();

    /// <summary>flattened draw order: one row per animation, preceded by a header row per segment when more than one storyboard shares the timeline; rebuilt whenever the model changes so rendering and hit-testing always agree on what sits in a given row</summary>
    private readonly List<Row> _rows = new();

    private readonly struct Row
    {
        public Row(GtTimelineSegment segment, GtAnimation? animation)
        {
            Segment = segment;
            Animation = animation;
        }

        public GtTimelineSegment Segment { get; }

        /// <summary>null for a segment header row</summary>
        public GtAnimation? Animation { get; }

        public bool IsHeader => Animation is null;
    }

    /// <summary>an animation's timing before a drag started, for the history entry</summary>
    public readonly struct AnimationTiming
    {
        public AnimationTiming(GtAnimation animation, double delay, double? duration)
        {
            Animation = animation;
            Delay     = delay;
            Duration  = duration;
        }

        public GtAnimation Animation { get; }
        public double Delay { get; }
        public double? Duration { get; }
    }

    private enum DragMode { None, Playhead, MoveBar, ResizeStart, ResizeEnd, Marquee }

    private DragMode _drag;
    private GtAnimation? _dragAnim;
    private GtTimelineSegment? _dragSegment;
    private double _dragGrabOffset;       // seconds between pointer and bar start
    private double _dragOrigDelay;
    private double? _dragOrigDuration;

    /// <summary>pre-drag timing of every selected bar, so a group drag moves as one</summary>
    private readonly List<AnimationTiming> _dragEntries = new();

    /// <summary>start of the visible range held still for the duration of a bar drag; dragging a clip into the negative grows the track leftwards, which would otherwise re-map every X coordinate mid-gesture and make the bar fight the pointer</summary>
    private double? _frozenStart;

    private Point _marqueeAnchor;
    private Point _marqueeCurrent;
    private bool _marqueeAdditive;

    public static readonly StyledProperty<double> PixelsPerSecondProperty =
        AvaloniaProperty.Register<TimelineTrackControl, double>(nameof(PixelsPerSecond), 70.0);

    public double PixelsPerSecond
    {
        get => GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }

    public static readonly StyledProperty<double> CurrentTimeProperty =
        AvaloniaProperty.Register<TimelineTrackControl, double>(nameof(CurrentTime));

    public double CurrentTime
    {
        get => GetValue(CurrentTimeProperty);
        set => SetValue(CurrentTimeProperty, value);
    }

    /// <summary>fires while the user scrubs the playhead</summary>
    public event Action<double>? TimeScrubbed;

    /// <summary>fires when the selection changes, with the primary animation and its owning storyboard (null = cleared); the whole selection is read back from <see cref="SelectedAnimations"/></summary>
    public event Action<GtAnimation?, GtStoryboard?>? AnimationSelected;

    /// <summary>fires when a row's mute eye is clicked, after the flag has been flipped</summary>
    public event Action<GtAnimation>? MuteToggled;

    /// <summary>fires once per completed drag, with every dragged bar's pre-drag timing</summary>
    public event Action<IReadOnlyList<AnimationTiming>>? AnimationsTimingChanged;

    /// <summary>fires when the track grows or shrinks at its left edge, with the pixels the existing content moved right by; the host adds it to the scroll offset so the picture stays put</summary>
    public event Action<double>? ContentShifted;

    /// <summary>storyboards shown on the timeline, each at its own offset</summary>
    public IReadOnlyList<GtTimelineSegment> Segments
    {
        get => _segments;
        set
        {
            _segments = value ?? Array.Empty<GtTimelineSegment>();
            _selection.Clear();
            Refresh();
        }
    }

    /// <summary>primary selection: the bar whose values the property strip shows</summary>
    public GtAnimation? SelectedAnimation
    {
        get => _selection.Count == 0 ? null : _selection[_selection.Count - 1];
        set
        {
            _selection.Clear();
            if (value is not null) _selection.Add(value);
            InvalidateVisual();
        }
    }

    /// <summary>every selected bar, in the order it was added</summary>
    public IReadOnlyList<GtAnimation> SelectedAnimations => _selection;

    /// <summary>replaces the selection outright, the last item becomes the primary</summary>
    public void SetSelection(IEnumerable<GtAnimation> animations)
    {
        _selection.Clear();
        foreach (var anim in animations)
            if (!IsSelected(anim)) _selection.Add(anim);
        InvalidateVisual();
    }

    /// <summary>total seconds the track spans: the last segment's end plus a little headroom</summary>
    public double TimelineLength
    {
        get
        {
            double end = 0;
            foreach (var segment in _segments)
                if (segment.EndTime > end) end = segment.EndTime;
            return Math.Max(end, 1.0) + 1.0;
        }
    }

    /// <summary>leftmost second the track draws; zero unless a clip carries a negative delay (vMix computes those before the title goes live) in which case the track is extended leftwards far enough to show the whole clip; frozen while a bar is being dragged</summary>
    public double TimelineStart => _frozenStart ?? ComputeStart();

    /// <summary>earliest clip start across the track, never above zero</summary>
    private double ComputeStart()
    {
        double start = 0;
        foreach (var row in _rows)
        {
            if (row.Animation is null) continue;
            double t = row.Animation.Delay + row.Segment.Offset;
            if (t < start) start = t;
        }
        return Math.Round(start, 3);
    }

    /// <summary>seconds from the left edge of the track to the right, pre-roll included</summary>
    public double TimelineSpan => TimelineLength - TimelineStart;

    public void Refresh()
    {
        RebuildRows();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void RebuildRows()
    {
        _rows.Clear();

        // headers only earn their row when there is more than one storyboard to tell apart
        bool headers = _segments.Count > 1;
        foreach (var segment in _segments)
        {
            if (headers) _rows.Add(new Row(segment, null));
            foreach (var anim in segment.Storyboard.Animations)
            {
                // GT's <None> placeholders animate nothing, so they get no row
                if (anim.IsPlaceholder) continue;
                _rows.Add(new Row(segment, anim));
            }
        }

        // an animation deleted out from under the selection must not linger in it
        _selection.RemoveAll(a => !_rows.Any(r => ReferenceEquals(r.Animation, a)));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CurrentTimeProperty) InvalidateVisual();
        else if (change.Property == PixelsPerSecondProperty) { InvalidateMeasure(); InvalidateVisual(); }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(GutterWidth + TimelineSpan * PixelsPerSecond,
                        RulerHeight + Math.Max(_rows.Count, 1) * RowHeight);
    }

    private double TimeToX(double seconds) => GutterWidth + (seconds - TimelineStart) * PixelsPerSecond;

    private double XToTime(double x) => TimelineStart + (x - GutterWidth) / PixelsPerSecond;

    private int RowAt(double y)
    {
        if (y < RulerHeight) return -1;
        return (int)((y - RulerHeight) / RowHeight);
    }

    /// <summary>the mute eye's box in row <paramref name="index"/>, at the gutter's right edge</summary>
    private static Rect MuteRect(int index) =>
        new(GutterWidth - MuteIconSize - MuteIconInset,
            RulerHeight + index * RowHeight + (RowHeight - MuteIconSize) / 2,
            MuteIconSize, MuteIconSize);

    private Rect BarRect(Row row, int index)
    {
        var anim = row.Animation!;
        double offset = row.Segment.Offset;
        double x0 = TimeToX(anim.Delay + offset);
        double x1 = TimeToX(anim.EndTime + offset);
        return new Rect(x0, RulerHeight + index * RowHeight + BarInset,
                        Math.Max(x1 - x0, 3), RowHeight - BarInset * 2);
    }

    private static readonly Dictionary<string, Bitmap?> IconCache = new();

    /// <summary>arrow for an animation's direction, pointing the way it travels, the same arrows the direction pad shows so a bar reads as the pad button that set it; types that carry no direction and GT's Center fall back to the no-direction dot</summary>
    public static string IconFileFor(GtAnimDirection direction, GtAnimationType type)
    {
        if (!GtAnimation.SupportsDirection(type)) return "no-direction.png";
        return IconFileFor(direction);
    }

    /// <summary>arrow file for a direction on its own, also drives the direction pad's buttons</summary>
    public static string IconFileFor(GtAnimDirection direction) => direction switch
    {
        GtAnimDirection.TopLeft     => "arrow-down-right.png",
        GtAnimDirection.Top         => "arrow-down.png",
        GtAnimDirection.TopRight    => "arrow-down-left.png",
        GtAnimDirection.Left        => "arrow-right.png",
        GtAnimDirection.Right       => "arrow-left.png",
        GtAnimDirection.BottomLeft  => "arrow-up-right.png",
        GtAnimDirection.Bottom      => "arrow-up.png",
        GtAnimDirection.BottomRight => "arrow-up-left.png",
        _                           => "no-direction.png",
    };

    /// <summary>loads an icon once per name, a missing file caches as null and draws nothing</summary>
    public static Bitmap? Icon(string filename)
    {
        if (IconCache.TryGetValue(filename, out var cached)) return cached;

        Bitmap? bitmap = null;
        try
        {
            var uri = new Uri($"avares://VmixGtPlus/Assets/Icons/{filename}");
            using var stream = AssetLoader.Open(uri);
            bitmap = new Bitmap(stream);
        }
        catch
        {
            // icons are decoration, a missing file must not take the timeline down with it
        }

        IconCache[filename] = bitmap;
        return bitmap;
    }

    public override void Render(DrawingContext ctx)
    {
        double width  = Bounds.Width;
        double height = Bounds.Height;

        // row striping across the full width
        for (int i = 0; i < Math.Max(_rows.Count, 1); i++)
        {
            var y = RulerHeight + i * RowHeight;
            var brush = i < _rows.Count && _rows[i].IsHeader
                ? HeaderBrush
                : i % 2 == 0 ? RowBrushEven : RowBrushOdd;
            ctx.DrawRectangle(brush, null, new Rect(0, y, width, RowHeight));
        }

        DrawRuler(ctx, width);

        if (_rows.Count == 0)
        {
            DrawText(ctx, "No animations in this storyboard", GutterWidth + 8, RulerHeight + 4, DimLabelBrush, 11);
            return;
        }

        for (int i = 0; i < _rows.Count; i++)
            DrawRow(ctx, _rows[i], i, width);

        DrawPreRoll(ctx, height);

        // gutter sits above the bars so long bars scroll under it cleanly
        ctx.DrawRectangle(GutterBrush, null, new Rect(0, RulerHeight, GutterWidth, height - RulerHeight));
        ctx.DrawLine(BorderPen, new Point(GutterWidth, 0), new Point(GutterWidth, height));

        for (int i = 0; i < _rows.Count; i++)
            DrawGutterLabel(ctx, _rows[i], i);

        DrawZeroMarker(ctx, height);
        DrawPlayhead(ctx, height);
        DrawMarquee(ctx);
    }

    /// <summary>dims everything before time zero: those frames are computed, never shown live</summary>
    private void DrawPreRoll(DrawingContext ctx, double height)
    {
        if (TimelineStart >= 0) return;

        double zero = TimeToX(0);
        if (zero <= GutterWidth) return;
        ctx.DrawRectangle(PreRollBrush, null,
                          new Rect(GutterWidth, RulerHeight, zero - GutterWidth, height - RulerHeight));
    }

    /// <summary>marks where the clip actually starts, once pre-roll pushes it off the left edge</summary>
    private void DrawZeroMarker(DrawingContext ctx, double height)
    {
        if (TimelineStart >= 0) return;

        double x = TimeToX(0);
        if (x < GutterWidth) return;
        ctx.DrawLine(ZeroPen, new Point(x, RulerHeight), new Point(x, height));
        DrawText(ctx, "0", x + 3, RulerHeight + 1, ZeroLabelBrush, 9);
    }

    private void DrawRuler(DrawingContext ctx, double width)
    {
        ctx.DrawRectangle(RulerBrush, null, new Rect(0, 0, width, RulerHeight));
        ctx.DrawLine(BorderPen, new Point(0, RulerHeight), new Point(width, RulerHeight));

        // choose a tick spacing that stays readable at the current zoom
        double step = PixelsPerSecond switch
        {
            >= 400 => 0.05,
            >= 160 => 0.1,
            >= 60  => 0.5,
            >= 25  => 1.0,
            _      => 5.0
        };

        // counted in whole ticks rather than accumulating a double, so "every Nth tick is major" stays exact however long the timeline is
        int majorEvery = step >= 1 ? 1 : step < 0.1 ? 10 : 5;
        int first = (int)Math.Floor(TimelineStart / step);
        int ticks = (int)Math.Floor(TimelineLength / step);
        for (int i = first; i <= ticks; i++)
        {
            double t = i * step;
            double x = TimeToX(t);
            if (x > width) break;
            if (x < GutterWidth) continue;

            bool major = ((i % majorEvery) + majorEvery) % majorEvery == 0;
            ctx.DrawLine(GridPen, new Point(x, major ? 4 : 12), new Point(x, RulerHeight));
            if (major)
                DrawText(ctx, FormatTime(t), x + 3, 2, DimLabelBrush, 9);
        }
    }

    private void DrawRow(DrawingContext ctx, Row row, int index, double width)
    {
        double y = RulerHeight + index * RowHeight;
        ctx.DrawLine(GridPen, new Point(GutterWidth, y), new Point(width, y));

        if (row.IsHeader)
        {
            // the header's span shows where its storyboard sits on the shared timeline
            double x0 = TimeToX(row.Segment.Offset);
            double x1 = TimeToX(row.Segment.EndTime);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4d, 0x9a, 0xe0), 0.20), null,
                              new Rect(x0, y + 6, Math.Max(x1 - x0, 2), RowHeight - 12));
            return;
        }

        var anim     = row.Animation!;
        var rect     = BarRect(row, index);
        var accent   = AccentFor(anim.Type);
        double alpha = anim.Reverse ? 0.45 : 0.80;
        var fill     = new SolidColorBrush(accent, anim.Muted ? alpha * 0.22 : alpha);
        var outline  = IsSelected(anim)
            ? new Pen(SelectedBrush, 1.5)
            : new Pen(new SolidColorBrush(accent, anim.Muted ? 0.45 : 1.0), 1);

        ctx.DrawRectangle(fill, outline, new RoundedRect(rect, 2));

        // the direction arrow claims the right end of the bar, the label gets what is left
        double labelRight = rect.Right;
        if (rect.Width >= IconMinBarWidth)
        {
            var icon = Icon(IconFileFor(anim.Direction, anim.Type));
            if (icon is not null)
            {
                var iconRect = new Rect(rect.Right - IconSize - IconMargin,
                                        rect.Y + (rect.Height - IconSize) / 2,
                                        IconSize, IconSize);
                using (ctx.PushClip(rect))
                    ctx.DrawImage(icon, iconRect);
                labelRight = iconRect.X - 2;
            }
        }

        // reverse animations run "backwards", the caret marks which end settles
        if (labelRight - rect.X > 22)
        {
            var label = anim.Reverse ? "◀ " + anim.TypeName : anim.TypeName;
            using (ctx.PushClip(new Rect(rect.X, rect.Y, labelRight - rect.X, rect.Height)))
                DrawText(ctx, label, rect.X + 4, rect.Y + 1,
                         anim.Muted ? DimLabelBrush : LabelBrush, 10);
        }
    }

    private void DrawGutterLabel(DrawingContext ctx, Row row, int index)
    {
        double y = RulerHeight + index * RowHeight;

        if (row.IsHeader)
        {
            ctx.DrawRectangle(HeaderBrush, null, new Rect(0, y, GutterWidth, RowHeight));
            var title = row.Segment.Offset > 0
                ? $"{row.Segment.Storyboard.DisplayName}  +{row.Segment.Offset:0.##}s"
                : row.Segment.Storyboard.DisplayName;
            using (ctx.PushClip(new Rect(0, y, GutterWidth - 4, RowHeight)))
                DrawText(ctx, title, 6, y + 3, SelectedBrush, 11);
            return;
        }

        var anim = row.Animation!;
        if (IsSelected(anim))
            ctx.DrawRectangle(new SolidColorBrush(SelectedColor, 0.18), null,
                              new Rect(0, y, GutterWidth, RowHeight));

        // animations sit indented under their segment header when the timeline is combined
        double indent = _segments.Count > 1 ? 10 : 0;
        ctx.DrawRectangle(new SolidColorBrush(AccentFor(anim.Type)), null,
                          new Rect(indent, y + 3, 3, RowHeight - 6));

        // the eye claims the right of the gutter, the object name gets what is left of it
        var eye = MuteRect(index);
        var eyeIcon = Icon(anim.Muted ? "eye-off.png" : "eye.png");
        if (eyeIcon is not null)
        {
            using (ctx.PushOpacity(anim.Muted ? 0.9 : 0.5))
                ctx.DrawImage(eyeIcon, eye);
        }

        var name = string.IsNullOrEmpty(anim.Object) ? "(no object)" : anim.Object;
        using (ctx.PushClip(new Rect(0, y, eye.X - 4, RowHeight)))
            DrawText(ctx, name, indent + 9, y + 3, anim.Muted ? DimLabelBrush : LabelBrush, 11);
    }

    private void DrawPlayhead(DrawingContext ctx, double height)
    {
        double x = TimeToX(CurrentTime);
        if (x < GutterWidth) return;
        ctx.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, height));
        ctx.DrawGeometry(PlayheadPen.Brush, null, new PolylineGeometry(
            new[] { new Point(x - 4, 0), new Point(x + 4, 0), new Point(x, 6) }, true));
    }

    private void DrawMarquee(DrawingContext ctx)
    {
        if (_drag != DragMode.Marquee) return;
        ctx.DrawRectangle(MarqueeFill, MarqueePen, MarqueeRect());
    }

    private Rect MarqueeRect() => new(_marqueeAnchor, _marqueeCurrent);

    private bool IsSelected(GtAnimation anim)
    {
        foreach (var a in _selection)
            if (ReferenceEquals(a, anim)) return true;
        return false;
    }

    private static Color AccentFor(GtAnimationType type) => type switch
    {
        GtAnimationType.Fade              => Color.FromRgb(0x4d, 0x9a, 0xe0),
        GtAnimationType.Fly               => Color.FromRgb(0x5c, 0xb8, 0x5c),
        GtAnimationType.Bounce            => Color.FromRgb(0x7c, 0xc8, 0x4c),
        GtAnimationType.Expand            => Color.FromRgb(0x9c, 0xb8, 0x3c),
        GtAnimationType.Reveal            => Color.FromRgb(0xd0, 0x8b, 0x3c),
        GtAnimationType.Rotate            => Color.FromRgb(0xc8, 0x6c, 0x6c),
        GtAnimationType.RotateContinuous  => Color.FromRgb(0xc8, 0x6c, 0x6c),
        GtAnimationType.Blink             => Color.FromRgb(0x8a, 0x8a, 0xb0),
        GtAnimationType.Zoom              => Color.FromRgb(0xa8, 0x77, 0xd0),
        GtAnimationType.ZoomFade          => Color.FromRgb(0x7d, 0x88, 0xdc),
        GtAnimationType.Scroll            => Color.FromRgb(0x3c, 0x8b, 0xd0),
        GtAnimationType.Hidden            => Color.FromRgb(0x70, 0x70, 0x70),
        GtAnimationType.ImageSequence     => Color.FromRgb(0xd0, 0x5c, 0x8b),
        GtAnimationType.ImageSequenceLoop => Color.FromRgb(0xd0, 0x5c, 0x8b),
        GtAnimationType.FillOffset        => Color.FromRgb(0x3c, 0xa8, 0xa8),
        GtAnimationType.StrokeOffset      => Color.FromRgb(0x3c, 0xa8, 0xa8),
        _                                 => Color.FromRgb(0x88, 0x88, 0x88)
    };

    private static string FormatTime(double t) =>
        t >= 10 ? t.ToString("0") + "s" : t.ToString("0.##") + "s";

    private static void DrawText(DrawingContext ctx, string text, double x, double y, IBrush brush, double size)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, size, brush);
        ctx.DrawText(ft, new Point(x, y));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;

        bool ctrl  = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        e.Pointer.Capture(this);

        // ruler or gutter-left click scrubs
        if (p.Y < RulerHeight)
        {
            _drag = DragMode.Playhead;
            ScrubTo(p.X);
            return;
        }

        // the mute eye sits in the gutter, so it is tested before anything else down here
        if (p.X < GutterWidth && MuteHitTest(p) is { } muted)
        {
            muted.Muted = !muted.Muted;
            InvalidateVisual();
            MuteToggled?.Invoke(muted);
            return;
        }

        var hit = HitTestBar(p);
        if (hit is null)
        {
            // Ctrl+drag rubber-bands a selection, the same gesture the canvas uses; Shift keeps what is already selected, without it the band replaces the selection
            if (ctrl)
            {
                _drag            = DragMode.Marquee;
                _marqueeAnchor   = p;
                _marqueeCurrent  = p;
                _marqueeAdditive = shift;
                if (!shift) _selection.Clear();
                InvalidateVisual();
                return;
            }

            // clicking empty track space still scrubs, matches NLE behaviour
            if (p.X > GutterWidth)
            {
                _drag = DragMode.Playhead;
                ScrubTo(p.X);
            }
            else
            {
                _selection.Clear();
                InvalidateVisual();
                AnimationSelected?.Invoke(null, null);
            }
            return;
        }

        var (row, rect) = hit.Value;
        var anim = row.Animation!;

        if (ctrl || shift)
        {
            // toggling is a selection gesture only, it never starts a drag
            if (IsSelected(anim)) _selection.RemoveAll(a => ReferenceEquals(a, anim));
            else                  _selection.Add(anim);

            _drag = DragMode.None;
            InvalidateVisual();
            RaiseSelectionChanged();
            return;
        }

        // pressing inside the selection keeps it, so a whole group can be dragged as one
        if (!IsSelected(anim))
        {
            _selection.Clear();
            _selection.Add(anim);
        }
        else if (!ReferenceEquals(SelectedAnimation, anim))
        {
            // the pressed bar becomes the primary, so the strip shows what was clicked
            _selection.RemoveAll(a => ReferenceEquals(a, anim));
            _selection.Add(anim);
        }

        InvalidateVisual();
        RaiseSelectionChanged();

        _dragAnim         = anim;
        _dragSegment      = row.Segment;
        _dragOrigDelay    = anim.Delay;
        _dragOrigDuration = anim.Duration;
        _frozenStart      = ComputeStart();
        CaptureDragEntries();

        if (p.X - rect.X <= EdgeGrab && rect.Width > EdgeGrab * 2) _drag = DragMode.ResizeStart;
        else if (rect.Right - p.X <= EdgeGrab)                     _drag = DragMode.ResizeEnd;
        else
        {
            _drag = DragMode.MoveBar;
            // delays stay storyboard-relative, so the segment offset comes back out here
            _dragGrabOffset = XToTime(p.X) - row.Segment.Offset - anim.Delay;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);

        if (_drag == DragMode.None)
        {
            Cursor = CursorFor(p, e.KeyModifiers.HasFlag(KeyModifiers.Control));
            return;
        }

        if (_drag == DragMode.Playhead) { ScrubTo(p.X); return; }

        if (_drag == DragMode.Marquee)
        {
            _marqueeCurrent = p;
            InvalidateVisual();
            return;
        }

        if (_dragAnim is null) return;

        // work in the dragged animation's own storyboard time, not timeline time
        double t = Snap(XToTime(p.X) - (_dragSegment?.Offset ?? 0));

        // vMix accepts a negative delay, the animation is computed before the title goes live so it is already part-way through at time zero; it is deliberate rather than a slip so it takes Shift, without it every bar snaps to the zero mark instead
        bool preRoll = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (_drag)
        {
            case DragMode.MoveBar:
            {
                // the whole selection shifts by the grabbed bar's delta, clamped so nothing in the group is pushed past zero
                double grabbed = Snap(t - _dragGrabOffset);
                double delta   = (preRoll ? grabbed : Math.Max(0, grabbed)) - _dragOrigDelay;
                if (!preRoll)
                    foreach (var entry in _dragEntries) delta = Math.Max(delta, -entry.Delay);

                foreach (var entry in _dragEntries)
                    entry.Animation.Delay = Snap(entry.Delay + delta);
                break;
            }

            case DragMode.ResizeStart:
            {
                // dragging the left edge keeps the end fixed, so duration absorbs the change
                double end   = _dragOrigDelay + (_dragOrigDuration ?? GtAnimation.DefaultDuration);
                double start = Math.Min(t, end - 0.05);
                if (!preRoll) start = Math.Max(start, 0);
                double delta = start - _dragOrigDelay;
                foreach (var entry in _dragEntries)
                {
                    if (!preRoll) delta = Math.Max(delta, -entry.Delay);
                    delta = Math.Min(delta, (entry.Duration ?? GtAnimation.DefaultDuration) - 0.05);
                }

                foreach (var entry in _dragEntries)
                {
                    entry.Animation.Delay    = Snap(entry.Delay + delta);
                    entry.Animation.Duration = Math.Round((entry.Duration ?? GtAnimation.DefaultDuration) - delta, 3);
                }
                break;
            }

            case DragMode.ResizeEnd:
            {
                double delta = Math.Max(0.05, Math.Round(t - _dragOrigDelay, 3))
                               - (_dragOrigDuration ?? GtAnimation.DefaultDuration);
                foreach (var entry in _dragEntries)
                    delta = Math.Max(delta, 0.05 - (entry.Duration ?? GtAnimation.DefaultDuration));

                foreach (var entry in _dragEntries)
                    entry.Animation.Duration =
                        Math.Round((entry.Duration ?? GtAnimation.DefaultDuration) + delta, 3);
                break;
            }
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);

        if (_drag == DragMode.Marquee)
        {
            ApplyMarquee();
            _drag = DragMode.None;
            InvalidateVisual();
            RaiseSelectionChanged();
            return;
        }

        if (_drag is DragMode.MoveBar or DragMode.ResizeStart or DragMode.ResizeEnd)
        {
            var changed = _dragEntries
                .Where(entry => entry.Animation.Delay != entry.Delay ||
                                entry.Animation.Duration != entry.Duration)
                .ToList();

            ReleaseStartFreeze();
            if (changed.Count > 0) AnimationsTimingChanged?.Invoke(changed);
        }

        _drag = DragMode.None;
        _dragAnim = null;
        _dragSegment = null;
        _dragEntries.Clear();
    }

    /// <summary>snapshots the timing of every selected bar so a group drag can move as one</summary>
    private void CaptureDragEntries()
    {
        _dragEntries.Clear();
        foreach (var anim in _selection)
            _dragEntries.Add(new AnimationTiming(anim, anim.Delay, anim.Duration));
    }

    /// <summary>selects every bar the rubber band touches</summary>
    private void ApplyMarquee()
    {
        var box = MarqueeRect();
        if (!_marqueeAdditive) _selection.Clear();

        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row.IsHeader) continue;

            if (!box.Intersects(BarRect(row, i))) continue;
            if (!IsSelected(row.Animation!)) _selection.Add(row.Animation!);
        }
    }

    private void RaiseSelectionChanged()
    {
        var primary = SelectedAnimation;
        GtStoryboard? storyboard = null;
        if (primary is not null)
            foreach (var row in _rows)
                if (ReferenceEquals(row.Animation, primary)) { storyboard = row.Segment.Storyboard; break; }

        AnimationSelected?.Invoke(primary, storyboard);
    }

    /// <summary>lets the track go back to sizing itself and reports how far the picture moved: growing pre-roll pushes everything right, and the host cancels that out with the scroll offset</summary>
    private void ReleaseStartFreeze()
    {
        if (_frozenStart is not { } frozen) return;

        _frozenStart = null;
        double shift = (frozen - ComputeStart()) * PixelsPerSecond;

        InvalidateMeasure();
        InvalidateVisual();
        if (Math.Abs(shift) > 0.5) ContentShifted?.Invoke(shift);
    }

    /// <summary>the playhead reaches back over the pre-roll so the frames vMix computes before the title goes live can be inspected; playback itself still starts at zero</summary>
    private void ScrubTo(double x)
    {
        var t = Math.Clamp(XToTime(x), TimelineStart, TimelineLength);
        CurrentTime = t;
        TimeScrubbed?.Invoke(t);
    }

    /// <summary>rounds to 10ms, holding no modifier is precise enough for storyboard work</summary>
    private static double Snap(double seconds) => Math.Round(seconds, 2);

    /// <summary>the animation whose mute eye is under <paramref name="p"/>, if any</summary>
    private GtAnimation? MuteHitTest(Point p)
    {
        int index = RowAt(p.Y);
        if (index < 0 || index >= _rows.Count) return null;

        var row = _rows[index];
        if (row.IsHeader) return null;

        // grown a little: the eye is 13px, and a pixel-exact target is fussy to hit
        return MuteRect(index).Inflate(3).Contains(p) ? row.Animation : null;
    }

    private (Row Row, Rect Rect)? HitTestBar(Point p)
    {
        int index = RowAt(p.Y);
        if (index < 0 || index >= _rows.Count) return null;

        var row = _rows[index];
        if (row.IsHeader) return null;

        var rect = BarRect(row, index);
        // widen the grab area slightly so thin bars stay clickable
        return rect.Inflate(new Thickness(EdgeGrab, 0, EdgeGrab, 0)).Contains(p) ? (row, rect) : null;
    }

    private Cursor CursorFor(Point p, bool ctrl)
    {
        if (p.Y < RulerHeight) return ResizeCursor;
        if (p.X < GutterWidth) return MuteHitTest(p) is null ? Cursor.Default : HandCursor;

        var hit = HitTestBar(p);
        if (hit is null) return ctrl ? CrossCursor : Cursor.Default;

        var rect = hit.Value.Rect;
        if (p.X - rect.X <= EdgeGrab || rect.Right - p.X <= EdgeGrab)
            return ResizeCursor;
        return HandCursor;
    }
}
