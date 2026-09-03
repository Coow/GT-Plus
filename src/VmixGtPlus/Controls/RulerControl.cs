using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VmixGtPlus.Models;

namespace VmixGtPlus.Controls;

public enum RulerOrientation { Horizontal, Vertical }

/// <summary>Photoshop-style ruler strip drawn along the top or left edge of the canvas viewport; the ruler knows nothing about scrolling, <see cref="ContentOffset"/> is the position in ruler-local pixels along its own axis of the canvas's 0 coordinate and the host recomputes it whenever the viewport scrolls, zooms or relayouts; dragging out of the ruler creates a guide exactly as in Photoshop, the top ruler yields a horizontal guide and the left ruler a vertical one with Alt inverting that</summary>
public class RulerControl : Control
{
    /// <summary>thickness of the strip, also the width of the corner box between two rulers</summary>
    public const double Thickness = 18;

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x23));
    private static readonly IBrush TickBrush       = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
    private static readonly IBrush LabelBrush      = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa));
    private static readonly IBrush CanvasSpanBrush = new SolidColorBrush(Color.FromRgb(0x2f, 0x2f, 0x2f));
    private static readonly IBrush MarkerBrush     = new SolidColorBrush(Color.FromArgb(0xff, 0x50, 0xa0, 0xff));
    private static readonly IBrush SelectionBrush  = new SolidColorBrush(Color.FromArgb(0x55, 0x50, 0xa0, 0xff));
    private static readonly IBrush BorderBrush     = new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x3a));

    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private bool _draggingGuide;

    public RulerControl()
    {
        ClipToBounds = true;
    }

    public static readonly StyledProperty<RulerOrientation> OrientationProperty =
        AvaloniaProperty.Register<RulerControl, RulerOrientation>(nameof(Orientation));

    public RulerOrientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<RulerControl, double>(nameof(Zoom), 1.0);

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public static readonly StyledProperty<double> ContentOffsetProperty =
        AvaloniaProperty.Register<RulerControl, double>(nameof(ContentOffset));

    /// <summary>ruler-local pixel position of the canvas origin along this ruler's axis</summary>
    public double ContentOffset
    {
        get => GetValue(ContentOffsetProperty);
        set => SetValue(ContentOffsetProperty, value);
    }

    public static readonly StyledProperty<double> DocLengthProperty =
        AvaloniaProperty.Register<RulerControl, double>(nameof(DocLength), 1920);

    /// <summary>document extent along this ruler's axis, in doc units</summary>
    public double DocLength
    {
        get => GetValue(DocLengthProperty);
        set => SetValue(DocLengthProperty, value);
    }

    public static readonly StyledProperty<double> RulerOriginProperty =
        AvaloniaProperty.Register<RulerControl, double>(nameof(RulerOrigin));

    /// <summary>doc coordinate the ruler labels as 0 (draggable, like Photoshop's origin)</summary>
    public double RulerOrigin
    {
        get => GetValue(RulerOriginProperty);
        set => SetValue(RulerOriginProperty, value);
    }

    public static readonly StyledProperty<double?> PointerPositionProperty =
        AvaloniaProperty.Register<RulerControl, double?>(nameof(PointerPosition));

    /// <summary>doc coordinate of the pointer, drawn as a tracking marker, null hides it</summary>
    public double? PointerPosition
    {
        get => GetValue(PointerPositionProperty);
        set => SetValue(PointerPositionProperty, value);
    }

    public static readonly StyledProperty<double?> SelectionStartProperty =
        AvaloniaProperty.Register<RulerControl, double?>(nameof(SelectionStart));

    /// <summary>start of the selection extent shading, in doc units, null hides it</summary>
    public double? SelectionStart
    {
        get => GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public static readonly StyledProperty<double?> SelectionEndProperty =
        AvaloniaProperty.Register<RulerControl, double?>(nameof(SelectionEnd));

    public double? SelectionEnd
    {
        get => GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    /// <summary>canvas this ruler drags guides onto, also supplies the doc-space mapping</summary>
    public GtCanvasControl? Canvas { get; set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OrientationProperty ||
            change.Property == ZoomProperty ||
            change.Property == ContentOffsetProperty ||
            change.Property == DocLengthProperty ||
            change.Property == RulerOriginProperty ||
            change.Property == PointerPositionProperty ||
            change.Property == SelectionStartProperty ||
            change.Property == SelectionEndProperty)
            InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var canvas = Canvas;
        if (canvas?.Document is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (canvas.Document.GuidesLocked) return;

        // top ruler pulls out a horizontal guide, Alt swaps the axis as Photoshop does
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var orientation = Orientation == RulerOrientation.Horizontal
            ? (alt ? GtGuideOrientation.Vertical : GtGuideOrientation.Horizontal)
            : (alt ? GtGuideOrientation.Horizontal : GtGuideOrientation.Vertical);

        canvas.BeginGuideCreation(orientation, DocPointFor(e));
        _draggingGuide = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_draggingGuide)
        {
            Canvas?.UpdateGuideDrag(DocPointFor(e), e.KeyModifiers.HasFlag(KeyModifiers.Control));
            e.Handled = true;
            return;
        }

        // track the pointer over the ruler itself so the marker keeps up outside the canvas
        PointerPosition = ToDocCoordinate(AxisPosition(e.GetPosition(this)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_draggingGuide) return;

        _draggingGuide = false;
        e.Pointer.Capture(null);
        Canvas?.EndGuideDrag();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_draggingGuide) PointerPosition = null;
    }

    /// <summary>pointer position in doc space, measured against the canvas rather than this strip</summary>
    private Point DocPointFor(PointerEventArgs e)
    {
        var canvas = Canvas;
        if (canvas is null) return default;
        var p = e.GetPosition(canvas);
        var zoom = Zoom <= 0 ? 1 : Zoom;
        return new Point(p.X / zoom, p.Y / zoom);
    }

    private double AxisPosition(Point p) => Orientation == RulerOrientation.Horizontal ? p.X : p.Y;

    /// <summary>ruler-local pixel to doc coordinate</summary>
    private double ToDocCoordinate(double pixel)
    {
        var zoom = Zoom <= 0 ? 1 : Zoom;
        return (pixel - ContentOffset) / zoom;
    }

    /// <summary>doc coordinate to ruler-local pixel</summary>
    private double ToPixel(double doc)
    {
        var zoom = Zoom <= 0 ? 1 : Zoom;
        return doc * zoom + ContentOffset;
    }

    private static readonly double[] StepLadder =
    {
        1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500,
        1000, 2000, 2500, 5000, 10000, 20000, 50000
    };

    /// <summary>smallest ladder step whose labels stay at least 64px apart on screen</summary>
    private static double ChooseStep(double zoom)
    {
        const double minLabelSpacing = 64;
        foreach (var step in StepLadder)
            if (step * zoom >= minLabelSpacing) return step;
        return StepLadder[StepLadder.Length - 1];
    }

    public override void Render(DrawingContext ctx)
    {
        var horizontal = Orientation == RulerOrientation.Horizontal;
        var bounds = Bounds;
        var length = horizontal ? bounds.Width : bounds.Height;
        if (length <= 0) return;

        var zoom = Zoom <= 0 ? 1 : Zoom;

        ctx.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, bounds.Width, bounds.Height));

        // lighter band over the document extent, so the page is easy to find when zoomed in
        var docStart = ToPixel(0);
        var docEnd   = ToPixel(DocLength);
        var spanRect = horizontal
            ? new Rect(docStart, 0, Math.Max(0, docEnd - docStart), bounds.Height)
            : new Rect(0, docStart, bounds.Width, Math.Max(0, docEnd - docStart));
        ctx.DrawRectangle(CanvasSpanBrush, null, spanRect);

        // selection extent shading
        if (SelectionStart is { } selStart && SelectionEnd is { } selEnd && selEnd > selStart)
        {
            var a = ToPixel(selStart);
            var b = ToPixel(selEnd);
            var selRect = horizontal
                ? new Rect(a, 0, Math.Max(1, b - a), bounds.Height)
                : new Rect(0, a, bounds.Width, Math.Max(1, b - a));
            ctx.DrawRectangle(SelectionBrush, null, selRect);
        }

        var step  = ChooseStep(zoom);
        // whole number of minor ticks per labelled step, so major/mid tests are exact integers
        var divisions = step % 4 == 0 ? 4 : 5;
        var minor     = step / divisions;

        // visible doc range in origin-relative units so ticks land on labelled values
        var originOffset = RulerOrigin;
        var firstDoc = ToDocCoordinate(0) - originOffset;
        var lastDoc  = ToDocCoordinate(length) - originOffset;

        var pen       = new Pen(TickBrush, 1);
        var thickness = horizontal ? bounds.Height : bounds.Width;

        for (long i = (long)Math.Floor(firstDoc / minor); i * minor <= lastDoc; i++)
        {
            var value = i * minor;
            var pixel = Math.Round(ToPixel(value + originOffset)) + 0.5;

            var mod     = ((i % divisions) + divisions) % divisions;
            var isMajor = mod == 0;
            var isMid   = !isMajor && divisions % 2 == 0 && mod == divisions / 2;

            var tickLen = isMajor ? thickness : isMid ? thickness * 0.5 : thickness * 0.3;

            if (horizontal)
                ctx.DrawLine(pen, new Point(pixel, thickness - tickLen), new Point(pixel, thickness));
            else
                ctx.DrawLine(pen, new Point(thickness - tickLen, pixel), new Point(thickness, pixel));

            if (!isMajor) continue;

            var text = new FormattedText(
                value.ToString("0.##", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface, 9, LabelBrush);

            if (horizontal)
            {
                ctx.DrawText(text, new Point(pixel + 2, 1));
            }
            else
            {
                // vertical labels read bottom-to-top, hugging the tick like Photoshop's
                using (ctx.PushTransform(
                           Matrix.CreateRotation(-Math.PI / 2) *
                           Matrix.CreateTranslation(1, pixel - 2)))
                    ctx.DrawText(text, new Point(0, 0));
            }
        }

        // pointer tracking marker
        if (PointerPosition is { } pointerDoc)
        {
            var p = Math.Round(ToPixel(pointerDoc)) + 0.5;
            var markerPen = new Pen(MarkerBrush, 1);
            if (horizontal)
                ctx.DrawLine(markerPen, new Point(p, 0), new Point(p, bounds.Height));
            else
                ctx.DrawLine(markerPen, new Point(0, p), new Point(bounds.Width, p));
        }

        // edge separating the strip from the viewport
        var edgePen = new Pen(BorderBrush, 1);
        if (horizontal)
            ctx.DrawLine(edgePen, new Point(0, bounds.Height - 0.5), new Point(bounds.Width, bounds.Height - 0.5));
        else
            ctx.DrawLine(edgePen, new Point(bounds.Width - 0.5, 0), new Point(bounds.Width - 0.5, bounds.Height));
    }
}
