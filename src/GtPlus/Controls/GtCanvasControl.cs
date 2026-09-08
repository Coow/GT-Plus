using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using GtPlus.Models;
using GtPlus.Services;

namespace GtPlus.Controls;

public enum CanvasTool  { Select, Edit, TextBox, Rectangle, Ticker, Web }
public enum ResizeHandle { None, NW, N, NE, E, SE, S, SW, W }

/// <summary>custom control that renders a GtDocument onto an Avalonia DrawingContext; supports element selection (click, shift+click, ctrl+drag-box) and per-element outside-canvas opacity dimming</summary>
public class GtCanvasControl : Control
{
    public GtCanvasControl()
    {
        // double-clicking a guide is how Photoshop opens it for a precise, typed position
        AddHandler(InputElement.DoubleTappedEvent, OnCanvasDoubleTapped, RoutingStrategies.Bubble);

        // an interactive web element is typed into, and keys only arrive at a control that can hold focus
        Focusable = true;
    }

    private void OnCanvasDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsPreviewing || _document is null) return;
        if (ActiveTool != CanvasTool.Select && ActiveTool != CanvasTool.Edit) return;

        var docPt = ToDocPoint(e.GetPosition(this));
        if (HitTestGuide(docPt) is not { } guide) return;

        GuideEditRequested?.Invoke(this, guide);
        e.Handled = true;
    }

    /// <summary>tools that create an element by dragging a box out on the canvas</summary>
    private static bool IsDrawTool(CanvasTool tool) =>
        tool is CanvasTool.TextBox or CanvasTool.Rectangle or CanvasTool.Ticker or CanvasTool.Web;

    private GtDocument? _document;
    private GtAssetLibrary _assets = new();
    private readonly Dictionary<string, Bitmap?> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _exportMode;

    // sequence frames are cached separately and evicted: a 1000-frame 1080p sequence decodes to gigabytes, so only a scrubbing window is kept alive
    private const int SequenceCacheLimit = 12;
    private readonly Dictionary<string, Bitmap?> _sequenceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _sequenceCacheOrder = new();

    private WebPreviewService? _webPreviews;

    /// <summary>supplies the captured stills for <see cref="GtWebElement"/>s; leave null and web elements draw as an empty placeholder box</summary>
    public WebPreviewService? WebPreviews
    {
        get => _webPreviews;
        set
        {
            if (ReferenceEquals(_webPreviews, value)) return;
            if (_webPreviews is not null) _webPreviews.Changed -= OnWebPreviewChanged;
            _webPreviews = value;
            if (_webPreviews is not null)
            {
                _webPreviews.Changed += OnWebPreviewChanged;
                _webPreviews.Document = _document;
            }
            InvalidateVisual();
        }
    }

    private void OnWebPreviewChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>the interactive web element keystrokes are going to, null when the editor has the keyboard</summary>
    private GtWebElement? _webFocus;
    private bool _webPointerDown;

    /// <summary>true while an interactive page holds the keyboard, so the editor's single-key shortcuts stay out of the way of typing into it</summary>
    public bool WebInputFocused => _webFocus is not null;

    /// <summary>topmost interactive web element under the point, with the layer it lives in</summary>
    private (GtLayer? Layer, GtWebElement? Web) HitTestInteractiveWeb(Point docPoint)
    {
        if (_document is null) return (null, null);

        var layers = _document.Layers;
        for (int li = layers.Count - 1; li >= 0; li--)
        {
            var layer = layers[li];
            if (!layer.Visible) continue;

            var elements = layer.Elements;
            for (int ei = elements.Count - 1; ei >= 0; ei--)
            {
                if (elements[ei] is not GtWebElement web) continue;
                if (!web.Visible || !web.Interactive) continue;
                if (ElementAbsBounds(layer, web).Contains(docPoint)) return (layer, web);
            }
        }
        return (null, null);
    }

    private GtLayer? LayerOf(GtElement element)
    {
        if (_document is null) return null;
        foreach (var layer in _document.Layers)
            if (layer.Elements.Contains(element)) return layer;
        return null;
    }

    /// <summary>document point expressed in the page's own coordinates; the page is laid out at the element's pixel size so the offset into the box is the offset into the page</summary>
    private static Point WebLocal(GtLayer layer, GtWebElement web, Point docPoint)
    {
        var bounds = ElementAbsBounds(layer, web);
        return new Point(docPoint.X - bounds.X, docPoint.Y - bounds.Y);
    }

    private static int CdpModifiers(KeyModifiers modifiers) => WebPreviewService.Modifiers(
        modifiers.HasFlag(KeyModifiers.Alt),
        modifiers.HasFlag(KeyModifiers.Control),
        modifiers.HasFlag(KeyModifiers.Meta),
        modifiers.HasFlag(KeyModifiers.Shift));

    /// <summary>drops the keyboard back to the editor</summary>
    private void ClearWebFocus()
    {
        if (_webFocus is null) return;
        _webFocus = null;
        InvalidateVisual();
    }

    private readonly HashSet<GtElement> _selectedElements = new(ReferenceEqualityComparer.Instance);

    // layer selection is single and exclusive with the element selection: the two use the same handles and the same drag gestures, so only one of them can own a pointer press
    private GtLayer? _selectedLayer;

    // layer move/resize state (Edit tool, while a layer is selected)
    private bool _isMovingLayer;
    private bool _isResizingLayer;
    private GtPoint _layerMoveOrigin = GtPoint.Zero;
    private (GtPoint Location, GtSize Dimensions) _layerResizeOrigin = (GtPoint.Zero, new GtSize(0, 0));

    // layer-local positions of the resized layer's elements when the handle drag started; GT holds them still in absolute space while the frame moves, so each one is re-derived from this snapshot on every drag step rather than nudged incrementally
    private readonly Dictionary<GtElement, GtPoint> _layerResizeElementOrigins =
        new(ReferenceEqualityComparer.Instance);

    // drag-box state (Select tool)
    private bool _isDragBoxing;
    private bool _dragBoxAdditive; // true = shift held, add to existing selection
    private Point _dragStart;    // doc coords
    private Point _dragCurrent;  // doc coords

    // move-drag state (Edit tool)
    private bool _isMoving;
    private Point _moveStart;    // doc coords
    private readonly Dictionary<GtElement, GtPoint> _moveOrigins = new(ReferenceEqualityComparer.Instance);

    // resize-drag state (Edit tool)
    private bool _isResizing;
    private ResizeHandle _activeHandle;
    private Point _resizeStart;  // doc coords
    private readonly Dictionary<GtElement, (GtPoint Location, GtSize Dimensions)> _resizeOrigins =
        new(ReferenceEqualityComparer.Instance);

    // union bounds of the selection when the gesture started, what the snap engine probes
    private Rect _moveOriginBounds;
    private Rect _resizeOriginBounds;

    // draw-box state (TextBox / Rectangle tools)
    private bool _isDrawing;
    private Point _drawStart;    // doc coords
    private Point _drawCurrent;  // doc coords

    /// <summary>fires when user finishes drawing a box with TextBox or Rectangle tool</summary>
    public event Action<Rect>? DrawCompleted;

    public event EventHandler? SelectionChanged;

    /// <summary>fires after the render pass has moved an element on its own (an auto-sizing text box resizing itself, or a bound element following its source) so the toolbar can pick up the new box; raised off the render pass, never during it</summary>
    public event EventHandler? AutoSizeApplied;

    /// <summary>fires on every pointer step of a move or resize drag so the toolbar boxes track the element live instead of only catching up on pointer release</summary>
    public event EventHandler? TransformLive;

    public IReadOnlyCollection<GtElement> SelectedElements => _selectedElements;

    /// <summary>the selected layer, or null when the selection is elements (or empty)</summary>
    public GtLayer? SelectedLayer => _selectedLayer;

    public void ClearSelection()
    {
        CommitNudge();
        _selectedElements.Clear();
        _selectedLayer = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void SetSelection(GtElement element, bool addToSelection = false)
    {
        CommitNudge();
        if (!addToSelection) _selectedElements.Clear();
        _selectedLayer = null;
        _selectedElements.Add(element);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void ToggleSelection(GtElement element)
    {
        CommitNudge();
        _selectedLayer = null;
        if (!_selectedElements.Remove(element))
            _selectedElements.Add(element);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>selects a whole layer, dropping any element selection; with a layer selected the Edit tool acts on the layer frame (handles resize it, a drag inside it moves it) so going back to elements means clicking one with the Select tool or in the layers panel</summary>
    public void SetLayerSelection(GtLayer? layer)
    {
        CommitNudge();
        if (layer is not null) _selectedElements.Clear();
        _selectedLayer = layer;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private readonly Dictionary<GtElement, GtPoint> _nudgeOrigins = new(ReferenceEqualityComparer.Instance);

    /// <summary>moves the unlocked part of the element selection by a doc-space delta; consecutive nudges coalesce, <see cref="CommitNudge"/> closes the run and pushes it as one move so holding an arrow key costs a single undo step instead of one per repeat</summary>
    public bool NudgeSelection(double dx, double dy)
    {
        if (IsPreviewing || _selectedElements.Count == 0) return false;

        bool moved = false;
        foreach (var el in _selectedElements)
        {
            if (el.Locked || IsLayerLocked(el)) continue;
            if (!_nudgeOrigins.ContainsKey(el)) _nudgeOrigins[el] = el.Location;
            el.Location = new GtPoint(el.Location.X + dx, el.Location.Y + dy);
            moved = true;
        }
        if (!moved) return false;

        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>closes an open nudge run, recording it in history as a single move</summary>
    public void CommitNudge()
    {
        if (_nudgeOrigins.Count == 0) return;

        if (History is not null)
        {
            var moves = new List<(GtElement, GtPoint, GtPoint)>();
            bool anyMoved = false;
            foreach (var (el, before) in _nudgeOrigins)
            {
                var after = el.Location;
                moves.Add((el, before, after));
                if (before.X != after.X || before.Y != after.Y) anyMoved = true;
            }
            if (anyMoved) History.Push(new MoveElementsAction(moves));
        }
        _nudgeOrigins.Clear();
    }

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<GtCanvasControl, double>(nameof(Zoom), 1.0);

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public static readonly StyledProperty<double> OutsideCanvasOpacityProperty =
        AvaloniaProperty.Register<GtCanvasControl, double>(nameof(OutsideCanvasOpacity), 0.30);

    /// <summary>opacity multiplier for elements whose bounds do not intersect the canvas area; 1.0 = fully visible, 0.0 = invisible, default 0.30 (= 70% transparent)</summary>
    public double OutsideCanvasOpacity
    {
        get => GetValue(OutsideCanvasOpacityProperty);
        set => SetValue(OutsideCanvasOpacityProperty, value);
    }

    public static readonly StyledProperty<double> OutsideLayerOpacityProperty =
        AvaloniaProperty.Register<GtCanvasControl, double>(nameof(OutsideLayerOpacity), 1.0);

    /// <summary>opacity multiplier for the parts of a layer's elements that fall outside the layer frame; only applied to the selected layer, with nothing selected the whole document draws undimmed; 1.0 = no dimming (default)</summary>
    public double OutsideLayerOpacity
    {
        get => GetValue(OutsideLayerOpacityProperty);
        set => SetValue(OutsideLayerOpacityProperty, value);
    }

    public HistoryService? History { get; set; }

    public static readonly StyledProperty<bool> ShowGuidesProperty =
        AvaloniaProperty.Register<GtCanvasControl, bool>(nameof(ShowGuides), true);

    /// <summary>hides guides without deleting them (Photoshop's View › Show › Guides)</summary>
    public bool ShowGuides
    {
        get => GetValue(ShowGuidesProperty);
        set => SetValue(ShowGuidesProperty, value);
    }

    public static readonly StyledProperty<bool> SnapEnabledProperty =
        AvaloniaProperty.Register<GtCanvasControl, bool>(nameof(SnapEnabled), true);

    /// <summary>master snap switch, Ctrl held during a drag suspends it temporarily</summary>
    public bool SnapEnabled
    {
        get => GetValue(SnapEnabledProperty);
        set => SetValue(SnapEnabledProperty, value);
    }

    public static readonly StyledProperty<bool> SnapToGuidesProperty =
        AvaloniaProperty.Register<GtCanvasControl, bool>(nameof(SnapToGuides), true);

    public bool SnapToGuides
    {
        get => GetValue(SnapToGuidesProperty);
        set => SetValue(SnapToGuidesProperty, value);
    }

    public static readonly StyledProperty<bool> SnapToElementsProperty =
        AvaloniaProperty.Register<GtCanvasControl, bool>(nameof(SnapToElements), true);

    public bool SnapToElements
    {
        get => GetValue(SnapToElementsProperty);
        set => SetValue(SnapToElementsProperty, value);
    }

    public static readonly StyledProperty<bool> SnapToCanvasProperty =
        AvaloniaProperty.Register<GtCanvasControl, bool>(nameof(SnapToCanvas), true);

    /// <summary>snap to the document edges and its centre lines</summary>
    public bool SnapToCanvas
    {
        get => GetValue(SnapToCanvasProperty);
        set => SetValue(SnapToCanvasProperty, value);
    }

    public static readonly StyledProperty<double> SnapDistanceProperty =
        AvaloniaProperty.Register<GtCanvasControl, double>(nameof(SnapDistance), 8.0);

    /// <summary>pull distance in screen pixels, converted to doc units by the current zoom</summary>
    public double SnapDistance
    {
        get => GetValue(SnapDistanceProperty);
        set => SetValue(SnapDistanceProperty, value);
    }

    /// <summary>how close in screen pixels the pointer must be to grab a guide</summary>
    private const double GuideGrabPixels = 5;

    /// <summary>fires when guides are added, moved, removed or cleared</summary>
    public event EventHandler? GuidesChanged;

    /// <summary>fires when a guide is double-clicked, asking the host for a precise edit</summary>
    public event EventHandler<GtGuide>? GuideEditRequested;

    private SnapEngine? _snapEngine;
    private readonly List<SnapLine> _snapLines = new();

    // guide-drag state; a guide dragged out of a ruler is added to the document immediately and removed again if the gesture ends off-canvas, which is what makes drag-to-delete work
    private GtGuide? _draggedGuide;
    private double   _draggedGuideBefore;
    private bool     _draggedGuideIsNew;
    private Point    _guideDragPoint;   // doc-space pointer, anchors the position readout

    /// <summary>true while a guide is being dragged, from either a ruler or the canvas</summary>
    public bool IsDraggingGuide => _draggedGuide is not null;

    public void AddGuide(GtGuideOrientation orientation, double position)
    {
        if (_document is null) return;
        var guide = new GtGuide(orientation, position);
        _document.Guides.Add(guide);
        History?.Push(GuideAction.Add(_document, guide));
        RaiseGuidesChanged();
    }

    public void ClearGuides()
    {
        if (_document is null || _document.Guides.Count == 0) return;
        var action = new ClearGuidesAction(_document);
        action.Redo();
        History?.Push(action);
        RaiseGuidesChanged();
    }

    /// <summary>moves a guide to an exact position, recording one undo entry</summary>
    public void SetGuidePosition(GtGuide guide, double position)
    {
        if (_document is null || guide.Position == position) return;

        var before = guide.Position;
        guide.Position = position;
        History?.Push(GuideAction.Move(_document, guide, before, position));
        RaiseGuidesChanged();
    }

    public void RemoveGuide(GtGuide guide)
    {
        if (_document is null || !_document.Guides.Remove(guide)) return;
        History?.Push(GuideAction.Remove(_document, guide));
        RaiseGuidesChanged();
    }

    /// <summary>replaces the whole guide set, what importing a .gtguides file does</summary>
    public void ReplaceGuides(List<GtGuide> guides, GtPoint rulerOrigin)
    {
        if (_document is null) return;

        var action = new ReplaceGuidesAction(_document, guides, rulerOrigin);
        action.Redo();
        History?.Push(action);
        ShowGuides = true;
        RaiseGuidesChanged();
    }

    private void RaiseGuidesChanged()
    {
        GuidesChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>starts a guide pulled out of a ruler, called by <see cref="RulerControl"/></summary>
    public void BeginGuideCreation(GtGuideOrientation orientation, Point docPoint)
    {
        if (_document is null || _document.GuidesLocked) return;

        var position = orientation == GtGuideOrientation.Vertical ? docPoint.X : docPoint.Y;
        var guide = new GtGuide(orientation, position);
        _document.Guides.Add(guide);

        _draggedGuide       = guide;
        _draggedGuideBefore = position;
        _draggedGuideIsNew  = true;
        ShowGuides = true;
        InvalidateVisual();
    }

    /// <summary>moves the guide under the pointer; <paramref name="suspendSnap"/> = Ctrl held</summary>
    public void UpdateGuideDrag(Point docPoint, bool suspendSnap)
    {
        if (_draggedGuide is null || _document is null) return;

        _guideDragPoint = docPoint;

        var vertical = _draggedGuide.Orientation == GtGuideOrientation.Vertical;
        var position = vertical ? docPoint.X : docPoint.Y;

        _snapLines.Clear();
        if (SnapEnabled && !suspendSnap)
        {
            // guides snap to content and canvas, never to other guides
            var engine = SnapEngine.Build(_document, null,
                includeGuides:   false,
                includeElements: SnapToElements,
                includeCanvas:   SnapToCanvas,
                threshold:       SnapDistance / Zoom);
            position = engine.SolveSingle(position, vertical, out var line);
            if (line is { } l) _snapLines.Add(l);
        }

        if (SnapToPixelGrid) position = Math.Round(position);

        _draggedGuide.Position = position;
        InvalidateVisual();
    }

    /// <summary>ends a guide drag, deleting the guide when it was dropped outside the canvas</summary>
    public void EndGuideDrag()
    {
        var guide = _draggedGuide;
        _draggedGuide = null;
        _snapLines.Clear();

        if (guide is null || _document is null)
        {
            InvalidateVisual();
            return;
        }

        var limit   = guide.Orientation == GtGuideOrientation.Vertical ? _document.Width : _document.Height;
        var dropped = guide.Position < 0 || guide.Position > limit;

        if (dropped)
        {
            _document.Guides.Remove(guide);
            // a new guide dragged straight back off-canvas never existed as far as undo cares
            if (!_draggedGuideIsNew)
                History?.Push(GuideAction.Remove(_document, guide));
        }
        else if (_draggedGuideIsNew)
        {
            History?.Push(GuideAction.Add(_document, guide));
        }
        else if (guide.Position != _draggedGuideBefore)
        {
            History?.Push(GuideAction.Move(_document, guide, _draggedGuideBefore, guide.Position));
        }

        _draggedGuideIsNew = false;
        RaiseGuidesChanged();
    }

    /// <summary>guide within grab distance of the point, or null</summary>
    private GtGuide? HitTestGuide(Point docPoint)
    {
        if (_document is null || !ShowGuides || _document.GuidesLocked) return null;

        var tolerance = GuideGrabPixels / Zoom;
        GtGuide? best = null;
        double bestDist = double.MaxValue;

        foreach (var guide in _document.Guides)
        {
            var dist = guide.Orientation == GtGuideOrientation.Vertical
                ? Math.Abs(docPoint.X - guide.Position)
                : Math.Abs(docPoint.Y - guide.Position);
            if (dist > tolerance || dist >= bestDist) continue;
            best     = guide;
            bestDist = dist;
        }

        return best;
    }

    /// <summary>grabs an existing guide under the pointer, returns false when there is none</summary>
    private bool TryBeginGuideDrag(Point docPoint)
    {
        var guide = HitTestGuide(docPoint);
        if (guide is null) return false;

        _draggedGuide       = guide;
        _draggedGuideBefore = guide.Position;
        _draggedGuideIsNew  = false;
        return true;
    }

    private SnapEngine? BuildSnapEngine() =>
        BuildSnapEngineExcluding(_selectedElements, OwnerLayerFrames(_selectedElements));

    /// <param name="exclude">elements that must not act as snap targets, the ones being dragged or, for a layer drag, everything the layer carries with it</param>
    /// <param name="frames">layer frames the drag should snap to; null for a layer gesture since a layer must not snap to its own frame</param>
    private SnapEngine? BuildSnapEngineExcluding(IReadOnlyCollection<GtElement> exclude,
                                                 IReadOnlyList<Rect>? frames = null)
    {
        if (_document is null || !SnapEnabled) return null;
        return SnapEngine.Build(_document, exclude,
            includeGuides:   SnapToGuides,
            includeElements: SnapToElements,
            includeCanvas:   SnapToCanvas,
            frames:          frames,
            threshold:       SnapDistance / Zoom);
    }

    /// <summary>frames of the layers owning <paramref name="elements"/>; the layer crops its contents so its edges and centre lines are snap targets for the elements inside it, the same way the canvas is for the document; follows the canvas-snapping preference</summary>
    private IReadOnlyList<Rect>? OwnerLayerFrames(IReadOnlyCollection<GtElement> elements)
    {
        if (_document is null || !SnapToCanvas || elements.Count == 0) return null;

        var frames = new List<Rect>();
        foreach (var layer in _document.Layers)
        {
            foreach (var el in layer.Elements)
                if (Contains(elements, el)) { frames.Add(LayerBounds(layer)); break; }
        }
        return frames.Count > 0 ? frames : null;
    }

    private static bool Contains(IReadOnlyCollection<GtElement> set, GtElement el)
    {
        foreach (var e in set)
            if (ReferenceEquals(e, el)) return true;
        return false;
    }

    /// <summary>the union box a resize handle produces, used to snap the dragged edges</summary>
    private static Rect ResizedBounds(Rect bounds, ResizeHandle handle, double dx, double dy)
    {
        double x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height;
        switch (handle)
        {
            case ResizeHandle.NW: x += dx; w -= dx; y += dy; h -= dy; break;
            case ResizeHandle.N:                     y += dy; h -= dy; break;
            case ResizeHandle.NE: w += dx;           y += dy; h -= dy; break;
            case ResizeHandle.E:  w += dx;                             break;
            case ResizeHandle.SE: w += dx;           h += dy;          break;
            case ResizeHandle.S:                     h += dy;          break;
            case ResizeHandle.SW: x += dx; w -= dx;  h += dy;          break;
            case ResizeHandle.W:  x += dx; w -= dx;                    break;
        }
        return new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    public static readonly StyledProperty<bool> SnapToPixelGridProperty =
        AvaloniaProperty.Register<GtCanvasControl, bool>(nameof(SnapToPixelGrid), false);

    /// <summary>when true, move and resize operations snap to integer pixel positions</summary>
    public bool SnapToPixelGrid
    {
        get => GetValue(SnapToPixelGridProperty);
        set => SetValue(SnapToPixelGridProperty, value);
    }

    public static readonly StyledProperty<CanvasTool> ActiveToolProperty =
        AvaloniaProperty.Register<GtCanvasControl, CanvasTool>(nameof(ActiveTool), CanvasTool.Select);

    public CanvasTool ActiveTool
    {
        get => GetValue(ActiveToolProperty);
        set => SetValue(ActiveToolProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ZoomProperty)
            ApplyZoom((double)change.NewValue!);
        else if (change.Property == OutsideCanvasOpacityProperty ||
                 change.Property == OutsideLayerOpacityProperty ||
                 change.Property == ShowGuidesProperty)
            InvalidateVisual();
        else if (change.Property == ActiveToolProperty)
        {
            _isMoving     = false;
            _isResizing   = false;
            _isDragBoxing  = false;
            _isDrawing    = false;
            _activeHandle  = ResizeHandle.None;
            var t = (CanvasTool)change.NewValue!;
            Cursor = IsDrawTool(t) ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
            InvalidateVisual();
        }
    }

    private void ApplyZoom(double zoom)
    {
        var doc = _document;
        Width  = (doc?.Width  ?? 1920) * zoom;
        Height = (doc?.Height ?? 1080) * zoom;
        InvalidateVisual();
    }

    public GtDocument? Document
    {
        get => _document;
        set
        {
            _document = value;
            _bitmapCache.Clear();
            ClearSequenceCache();
            ClearCropMasks();
            if (_webPreviews is not null) _webPreviews.Document = value;
            _animationFrame = null;
            _selectedElements.Clear();
            _selectedLayer = null;
            ApplyZoom(Zoom);
            InvalidateVisual();
        }
    }

    public void SetAssets(GtAssetLibrary assets)
    {
        _assets = assets;
        _bitmapCache.Clear();
        ClearSequenceCache();
        InvalidateVisual();
    }

    public GtAssetLibrary Assets => _assets;

    private GtAnimationFrame? _animationFrame;

    /// <summary>storyboard overrides applied on top of the model while rendering; null (the default) renders the document at rest, and the document itself is never mutated so scrubbing leaves no history entries and cannot dirty the file</summary>
    public GtAnimationFrame? AnimationFrame
    {
        get => _animationFrame;
        set
        {
            bool was = IsPreviewing;
            _animationFrame = value;

            // a gesture started at rest would keep running against geometry that has since moved, so anything in flight is abandoned when the preview comes on
            if (IsPreviewing && !was) CancelGestures();

            InvalidateVisual();
        }
    }

    /// <summary>true while a storyboard frame is applied; objects are then drawn away from their stored geometry so the editing gestures (move, resize, draw) are suspended since dragging would shift an object by an offset the user cannot see, and selection still works</summary>
    public bool IsPreviewing => _animationFrame is not null;

    private DispatcherTimer? _tickerTimer;
    private int _tickerFrame;

    /// <summary>frames the ticker transport has advanced; GT moves a ticker by Speed pixels per rendered frame so the walk is counted in frames not seconds, and zero means "stopped" which draws the tickers at rest</summary>
    public int TickerFrame
    {
        get => _tickerFrame;
        set
        {
            var frame = Math.Max(0, value);
            if (frame == _tickerFrame) return;
            _tickerFrame = frame;
            InvalidateVisual();
        }
    }

    public bool TickerPlaying => _tickerTimer?.IsEnabled == true;

    /// <summary>runs the ticker clock at <see cref="TickerLayout.Fps"/>, the rate the walk is defined against, so the editor shows the same pixels-per-frame motion as vMix</summary>
    public void PlayTicker()
    {
        _tickerTimer ??= CreateTickerTimer();
        _tickerTimer.Start();
    }

    public void PauseTicker() => _tickerTimer?.Stop();

    /// <summary>stops the clock and rewinds, so the tickers go back to their rest layout</summary>
    public void StopTicker()
    {
        PauseTicker();
        TickerFrame = 0;
    }

    public void ToggleTickerPlayback()
    {
        if (TickerPlaying) PauseTicker(); else PlayTicker();
    }

    private DispatcherTimer CreateTickerTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / TickerLayout.Fps),
        };
        timer.Tick += (_, _) =>
        {
            _tickerFrame++;
            InvalidateVisual();
        };
        return timer;
    }

    private void CancelGestures()
    {
        // a nudge run is a finished edit not a half-drag, so close it instead of rolling it back
        CommitNudge();

        // half-finished drags are rolled back rather than left behind: they never reached the history stack, so keeping them would be an untracked edit
        foreach (var (el, origin) in _moveOrigins)
            el.Location = origin;
        foreach (var (el, origin) in _resizeOrigins)
        {
            el.Location   = origin.Location;
            el.Dimensions = origin.Dimensions;
        }

        // a half-finished layer resize took its elements with it, both halves roll back
        if (_isResizingLayer && _selectedLayer is { } resizedLayer)
        {
            resizedLayer.Location   = _layerResizeOrigin.Location;
            resizedLayer.Dimensions = _layerResizeOrigin.Dimensions;
            foreach (var (el, origin) in _layerResizeElementOrigins)
                el.Location = origin;
        }
        if (_isMovingLayer && _selectedLayer is { } movedLayer)
            movedLayer.Location = _layerMoveOrigin;

        _isDrawing       = false;
        _isDragBoxing    = false;
        _isMoving        = false;
        _isResizing      = false;
        _isMovingLayer   = false;
        _isResizingLayer = false;
        _activeHandle = ResizeHandle.None;
        _moveOrigins.Clear();
        _resizeOrigins.Clear();
        _layerResizeElementOrigins.Clear();
        Cursor = Cursor.Default;
    }

    /// <summary>renders the document to a <see cref="RenderTargetBitmap"/> at 1:1 document resolution; background is transparent (alpha preserved), returns null when no document is loaded</summary>
    public RenderTargetBitmap? ExportToBitmap()
    {
        var doc = _document;
        if (doc is null) return null;

        var w = (int)Math.Round(doc.Width);
        var h = (int)Math.Round(doc.Height);
        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));

        _exportMode = true;
        try
        {
            using var ctx = rtb.CreateDrawingContext();
            var docBounds = new Rect(0, 0, doc.Width, doc.Height);
            foreach (var layer in doc.Layers)
            {
                if (!layer.Visible) continue;
                RenderLayer(ctx, layer, docBounds);
            }
        }
        finally
        {
            _exportMode = false;
        }

        return rtb;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        CommitNudge();

        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;

        var docPt = ToDocPoint(e.GetPosition(this));
        var ctrl  = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt   = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (!IsPreviewing && IsDrawTool(ActiveTool))
        {
            _isDrawing   = true;
            _drawStart   = docPt;
            _drawCurrent = docPt;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // guides float above the artwork, so grabbing one beats selecting through it
        if (!IsPreviewing && (ActiveTool == CanvasTool.Select || ActiveTool == CanvasTool.Edit) &&
            TryBeginGuideDrag(docPt))
        {
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // an interactive page takes the click itself; Alt is the way through to selecting and moving the box, as it is for the Edit tool.
        // a resize handle still wins, since handles sit half inside the box they belong to and would otherwise be unusable while the page is live
        if (!IsPreviewing && !alt && _webPreviews is not null &&
            HitTestHandle(docPt) == ResizeHandle.None &&
            HitTestLayerHandle(docPt) == ResizeHandle.None &&
            HitTestInteractiveWeb(docPt) is ({ } webLayer, { } web))
        {
            _webFocus       = web;
            _webPointerDown = true;
            Focus();
            _webPreviews.SendMouse(web, "mousePressed", WebLocal(webLayer, web, docPt),
                                   "left", (int)e.ClickCount, CdpModifiers(e.KeyModifiers));
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        // a click anywhere else is the page losing the keyboard
        ClearWebFocus();

        // previewing falls back to selection for every tool: the model geometry the handles and drags work against is not where the objects are being drawn
        bool useSelect = IsPreviewing || ActiveTool == CanvasTool.Select ||
                         (ActiveTool == CanvasTool.Edit && alt);

        if (useSelect)
        {
            if (ctrl)
            {
                _isDragBoxing     = true;
                _dragBoxAdditive  = shift;
                _dragStart        = docPt;
                _dragCurrent      = docPt;
                e.Pointer.Capture(this);
            }
            else if (shift)
            {
                var (_, el) = HitTest(docPt);
                if (el is not null) ToggleSelection(el);
            }
            else
            {
                var (_, el) = HitTest(docPt);
                if (el is not null) SetSelection(el, false);
                else ClearSelection();
            }
        }
        else // Edit tool, no Alt: resize handles or move
        {
            // a selected layer owns the gesture inside its own frame: its handles resize it and a drag anywhere inside moves it, and a press outside falls through to the elements
            if (TryBeginLayerGesture(docPt))
            {
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            // handles take priority over element hit test
            var handle = HitTestHandle(docPt);
            if (handle != ResizeHandle.None)
            {
                _activeHandle = handle;
                _resizeStart  = docPt;
                _resizeOrigins.Clear();
                foreach (var sel in _selectedElements)
                    if (!sel.Locked && !IsLayerLocked(sel))
                        _resizeOrigins[sel] = (sel.Location, sel.Dimensions);
                if (_resizeOrigins.Count > 0)
                {
                    _isResizing         = true;
                    _resizeOriginBounds = GetSelectionBounds();
                    _snapEngine         = BuildSnapEngine();
                    e.Pointer.Capture(this);
                }
            }
            else
            {
                var (_, el) = HitTest(docPt);
                if (el is not null)
                {
                    if (shift) ToggleSelection(el);
                    else if (!_selectedElements.Contains(el)) SetSelection(el, false);

                    // start move for unlocked selected elements only
                    _moveOrigins.Clear();
                    foreach (var sel in _selectedElements)
                        if (!sel.Locked && !IsLayerLocked(sel))
                            _moveOrigins[sel] = sel.Location;

                    if (_moveOrigins.Count > 0)
                    {
                        _isMoving         = true;
                        _moveStart        = docPt;
                        _moveOriginBounds = GetSelectionBounds();
                        _snapEngine       = BuildSnapEngine();
                        e.Pointer.Capture(this);
                    }
                }
                else
                {
                    ClearSelection();
                }
            }
        }

        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // Ctrl+wheel is the editor's zoom whatever is under the pointer, everything else scrolls the page it is over
        if (IsPreviewing || _webPreviews is null || e.Handled) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;

        var docPt = ToDocPoint(e.GetPosition(this));
        if (HitTestInteractiveWeb(docPt) is not ({ } layer, { } web)) return;

        // one wheel notch is 120 units to a browser, and the axes are inverted against Avalonia's
        _webPreviews.SendWheel(web, WebLocal(layer, web, docPt),
                               -e.Delta.X * WheelNotch, -e.Delta.Y * WheelNotch,
                               CdpModifiers(e.KeyModifiers));
        e.Handled = true;
    }

    /// <summary>scroll distance a browser expects for one wheel notch</summary>
    private const double WheelNotch = 120;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_webFocus is not { } web || _webPreviews is null) return;

        // Escape is how the keyboard is handed back to the editor, so it is never forwarded
        if (e.Key == Key.Escape)
        {
            ClearWebFocus();
            e.Handled = true;
            return;
        }

        var (key, code, vk) = MapKey(e.Key);
        // a printable key arrives again as text input, which is what actually types it; this pass is what page shortcuts and editing keys listen for
        _webPreviews.SendKey(web, "rawKeyDown", null, key, code, vk, CdpModifiers(e.KeyModifiers));
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (_webFocus is not { } web || _webPreviews is null) return;

        var (key, code, vk) = MapKey(e.Key);
        _webPreviews.SendKey(web, "keyUp", null, key, code, vk, CdpModifiers(e.KeyModifiers));
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);

        if (_webFocus is not { } web || _webPreviews is null) return;
        if (string.IsNullOrEmpty(e.Text)) return;

        _webPreviews.SendKey(web, "char", e.Text, e.Text, "", 0, 0);
        e.Handled = true;
    }

    /// <summary>DOM key name, physical code and Windows virtual key code for the keys a page cares about beyond plain typing, which arrives as text input instead</summary>
    private static (string Key, string Code, int Vk) MapKey(Key key) => key switch
    {
        Key.Back      => ("Backspace", "Backspace", 8),
        Key.Tab       => ("Tab",       "Tab",       9),
        Key.Enter     => ("Enter",     "Enter",     13),
        Key.Escape    => ("Escape",    "Escape",    27),
        Key.Space     => (" ",         "Space",     32),
        Key.PageUp    => ("PageUp",    "PageUp",    33),
        Key.PageDown  => ("PageDown",  "PageDown",  34),
        Key.End       => ("End",       "End",       35),
        Key.Home      => ("Home",      "Home",      36),
        Key.Left      => ("ArrowLeft", "ArrowLeft", 37),
        Key.Up        => ("ArrowUp",   "ArrowUp",   38),
        Key.Right     => ("ArrowRight","ArrowRight",39),
        Key.Down      => ("ArrowDown", "ArrowDown", 40),
        Key.Delete    => ("Delete",    "Delete",    46),
        _             => (key.ToString(), "", 0),
    };

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var docPt = ToDocPoint(e.GetPosition(this));

        if (!IsPreviewing && _webPreviews is not null)
        {
            // a drag that started in a page stays in that page, wherever the pointer wanders
            if (_webPointerDown && _webFocus is { } dragged && LayerOf(dragged) is { } draggedLayer)
            {
                _webPreviews.SendMouse(dragged, "mouseMoved", WebLocal(draggedLayer, dragged, docPt),
                                       "left", 1, CdpModifiers(e.KeyModifiers));
                e.Handled = true;
                return;
            }

            // plain hover still goes down so the page's own rollovers work
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt) &&
                HitTestInteractiveWeb(docPt) is ({ } hoverLayer, { } hovered))
                _webPreviews.SendMouse(hovered, "mouseMoved", WebLocal(hoverLayer, hovered, docPt),
                                       "none", 0, CdpModifiers(e.KeyModifiers));
        }

        if (_draggedGuide is not null)
        {
            UpdateGuideDrag(docPt, e.KeyModifiers.HasFlag(KeyModifiers.Control));
            e.Handled = true;
            return;
        }

        if (_isDrawing)
        {
            _drawCurrent = docPt;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isDragBoxing)
        {
            _dragCurrent = docPt;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isResizingLayer)
        {
            var shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var target    = docPt;

            _snapLines.Clear();
            if (_snapEngine is not null && !shiftHeld && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var dx = docPt.X - _resizeStart.X;
                var dy = docPt.Y - _resizeStart.Y;
                var predicted = ResizedBounds(_resizeOriginBounds, _activeHandle, dx, dy);

                bool left   = _activeHandle is ResizeHandle.NW or ResizeHandle.W or ResizeHandle.SW;
                bool right  = _activeHandle is ResizeHandle.NE or ResizeHandle.E or ResizeHandle.SE;
                bool top    = _activeHandle is ResizeHandle.NW or ResizeHandle.N or ResizeHandle.NE;
                bool bottom = _activeHandle is ResizeHandle.SW or ResizeHandle.S or ResizeHandle.SE;

                var solution = _snapEngine.SolveEdges(predicted, left, right, top, bottom);
                target = new Point(docPt.X + solution.DeltaX, docPt.Y + solution.DeltaY);
                _snapLines.AddRange(solution.Lines);
            }

            ApplyLayerResizeDelta(target, shiftHeld);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isMovingLayer && _selectedLayer is not null)
        {
            var dx = docPt.X - _moveStart.X;
            var dy = docPt.Y - _moveStart.Y;

            _snapLines.Clear();
            bool snappedX = false, snappedY = false;
            if (_snapEngine is not null && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var moved = new Rect(_moveOriginBounds.X + dx, _moveOriginBounds.Y + dy,
                                     _moveOriginBounds.Width, _moveOriginBounds.Height);
                var solution = _snapEngine.SolveMove(moved);
                dx += solution.DeltaX;
                dy += solution.DeltaY;
                snappedX = solution.SnappedX;
                snappedY = solution.SnappedY;
                _snapLines.AddRange(solution.Lines);
            }

            double nx = _layerMoveOrigin.X + dx;
            double ny = _layerMoveOrigin.Y + dy;
            if (SnapToPixelGrid && !snappedX) nx = Math.Round(nx);
            if (SnapToPixelGrid && !snappedY) ny = Math.Round(ny);
            _selectedLayer.Location = new GtPoint(nx, ny);

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isResizing)
        {
            var shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var target    = docPt;

            _snapLines.Clear();
            // an aspect-constrained resize derives one axis from the other, so a snap on either axis would fight the constraint; Ctrl suspends snapping the same way it does moves
            if (_snapEngine is not null && !shiftHeld && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var dx = docPt.X - _resizeStart.X;
                var dy = docPt.Y - _resizeStart.Y;
                var predicted = ResizedBounds(_resizeOriginBounds, _activeHandle, dx, dy);

                bool left   = _activeHandle is ResizeHandle.NW or ResizeHandle.W or ResizeHandle.SW;
                bool right  = _activeHandle is ResizeHandle.NE or ResizeHandle.E or ResizeHandle.SE;
                bool top    = _activeHandle is ResizeHandle.NW or ResizeHandle.N or ResizeHandle.NE;
                bool bottom = _activeHandle is ResizeHandle.SW or ResizeHandle.S or ResizeHandle.SE;

                var solution = _snapEngine.SolveEdges(predicted, left, right, top, bottom);
                target = new Point(docPt.X + solution.DeltaX, docPt.Y + solution.DeltaY);
                _snapLines.AddRange(solution.Lines);
            }

            ApplyResizeDelta(target, shiftHeld);
            InvalidateVisual();
            TransformLive?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (_isMoving)
        {
            var dx = docPt.X - _moveStart.X;
            var dy = docPt.Y - _moveStart.Y;

            _snapLines.Clear();
            bool snappedX = false, snappedY = false;
            if (_snapEngine is not null && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var moved = new Rect(_moveOriginBounds.X + dx, _moveOriginBounds.Y + dy,
                                     _moveOriginBounds.Width, _moveOriginBounds.Height);
                var solution = _snapEngine.SolveMove(moved);
                dx += solution.DeltaX;
                dy += solution.DeltaY;
                snappedX = solution.SnappedX;
                snappedY = solution.SnappedY;
                _snapLines.AddRange(solution.Lines);
            }

            foreach (var (sel, origin) in _moveOrigins)
            {
                double nx = origin.X + dx;
                double ny = origin.Y + dy;
                // rounding a snapped axis would nudge the element back off its target
                if (SnapToPixelGrid && !snappedX) nx = Math.Round(nx);
                if (SnapToPixelGrid && !snappedY) ny = Math.Round(ny);
                sel.Location = new GtPoint(nx, ny);
            }
            InvalidateVisual();
            TransformLive?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        // update cursor hover feedback: resize handles first, then guides, then elements
        if (!IsPreviewing && (ActiveTool == CanvasTool.Edit || ActiveTool == CanvasTool.Select))
        {
            var handle = ActiveTool == CanvasTool.Edit ? HitTestHandle(docPt) : ResizeHandle.None;
            if (handle == ResizeHandle.None) handle = HitTestLayerHandle(docPt);
            if (handle != ResizeHandle.None)
            {
                Cursor = HandleCursor(handle);
            }
            else if (ActiveTool == CanvasTool.Edit && _selectedLayer is { Locked: false } layer
                     && LayerBounds(layer).Contains(docPt)
                     && HitTest(docPt) is var (hitLayer, hitElement)
                     && (hitElement is null || ReferenceEquals(hitLayer, layer)))
            {
                Cursor = new Cursor(StandardCursorType.SizeAll);
            }
            else if (HitTestGuide(docPt) is { } hoverGuide)
            {
                Cursor = new Cursor(hoverGuide.Orientation == GtGuideOrientation.Vertical
                    ? StandardCursorType.SizeWestEast
                    : StandardCursorType.SizeNorthSouth);
            }
            else if (ActiveTool == CanvasTool.Edit)
            {
                var (_, el) = HitTest(docPt);
                Cursor = (el is not null && _selectedElements.Contains(el))
                    ? new Cursor(StandardCursorType.SizeAll)
                    : Cursor.Default;
            }
            else
            {
                Cursor = Cursor.Default;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_webPointerDown && e.InitialPressMouseButton == MouseButton.Left)
        {
            _webPointerDown = false;
            e.Pointer.Capture(null);

            if (_webFocus is { } web && LayerOf(web) is { } layer)
                _webPreviews?.SendMouse(web, "mouseReleased",
                                        WebLocal(layer, web, ToDocPoint(e.GetPosition(this))),
                                        "left", 1, CdpModifiers(e.KeyModifiers));
            e.Handled = true;
            return;
        }

        if (_draggedGuide is not null && e.InitialPressMouseButton == MouseButton.Left)
        {
            e.Pointer.Capture(null);
            EndGuideDrag();
            e.Handled = true;
            return;
        }

        if (_isDrawing && e.InitialPressMouseButton == MouseButton.Left)
        {
            _isDrawing = false;
            e.Pointer.Capture(null);
            var rect = MakeRect(_drawStart, _drawCurrent);

            // a web page is nearly always wanted at full canvas size, so a plain click places one there; dragging a box still sizes it by hand
            if (ActiveTool == CanvasTool.Web && _document is not null &&
                (rect.Width < 2 || rect.Height < 2))
                rect = new Rect(0, 0, _document.Width, _document.Height);

            if (rect.Width >= 2 && rect.Height >= 2)
                DrawCompleted?.Invoke(rect);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isDragBoxing && e.InitialPressMouseButton == MouseButton.Left)
        {
            var dragRect = MakeRect(_dragStart, _dragCurrent);
            if (!_dragBoxAdditive)
                _selectedElements.Clear();
            foreach (var el in HitTestRect(dragRect))
                _selectedElements.Add(el);

            _isDragBoxing = false;
            e.Pointer.Capture(null);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isResizingLayer && e.InitialPressMouseButton == MouseButton.Left)
        {
            _isResizingLayer = false;
            _activeHandle    = ResizeHandle.None;
            _snapEngine      = null;
            _snapLines.Clear();
            e.Pointer.Capture(null);

            var layer = _selectedLayer;
            var (origLoc, origDim) = _layerResizeOrigin;
            if (History is not null && layer is not null &&
                (layer.Location.X != origLoc.X || layer.Location.Y != origLoc.Y ||
                 layer.Dimensions.Width != origDim.Width || layer.Dimensions.Height != origDim.Height))
            {
                var elementMoves = new List<(GtElement, GtPoint, GtPoint)>();
                foreach (var (el, before) in _layerResizeElementOrigins)
                    if (before.X != el.Location.X || before.Y != el.Location.Y)
                        elementMoves.Add((el, before, el.Location));

                History.Push(new ResizeLayerAction(layer, origLoc, origDim,
                                                   layer.Location, layer.Dimensions, elementMoves));
            }
            _layerResizeElementOrigins.Clear();

            SelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (_isMovingLayer && e.InitialPressMouseButton == MouseButton.Left)
        {
            _isMovingLayer = false;
            _snapEngine    = null;
            _snapLines.Clear();
            e.Pointer.Capture(null);

            var layer = _selectedLayer;
            if (History is not null && layer is not null &&
                (layer.Location.X != _layerMoveOrigin.X || layer.Location.Y != _layerMoveOrigin.Y))
                History.Push(new MoveLayerAction(layer, _layerMoveOrigin, layer.Location));

            SelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (_isResizing && e.InitialPressMouseButton == MouseButton.Left)
        {
            _isResizing   = false;
            _activeHandle  = ResizeHandle.None;
            _snapEngine   = null;
            _snapLines.Clear();
            e.Pointer.Capture(null);

            if (History is not null && _resizeOrigins.Count > 0)
            {
                var changes = new List<(GtElement, GtPoint, GtSize, GtPoint, GtSize)>();
                bool anyChanged = false;
                foreach (var (el, (origLoc, origDim)) in _resizeOrigins)
                {
                    var newLoc = el.Location;
                    var newDim = el.Dimensions;
                    if (origLoc.X != newLoc.X || origLoc.Y != newLoc.Y ||
                        origDim.Width != newDim.Width || origDim.Height != newDim.Height)
                        anyChanged = true;
                    changes.Add((el, origLoc, origDim, newLoc, newDim));
                }
                if (anyChanged)
                    History.Push(new ResizeElementsAction(changes));
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (_isMoving && e.InitialPressMouseButton == MouseButton.Left)
        {
            _isMoving   = false;
            _snapEngine = null;
            _snapLines.Clear();
            e.Pointer.Capture(null);

            // record move in history if any element actually moved
            if (History is not null)
            {
                var moves = new List<(GtElement, GtPoint, GtPoint)>();
                bool anyMoved = false;
                foreach (var (el, before) in _moveOrigins)
                {
                    var after = el.Location;
                    moves.Add((el, before, after));
                    if (before.X != after.X || before.Y != after.Y)
                        anyMoved = true;
                }
                if (anyMoved)
                    History.Push(new MoveElementsAction(moves));
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private Point ToDocPoint(Point screenPoint) =>
        new(screenPoint.X / Zoom, screenPoint.Y / Zoom);

    private static Rect MakeRect(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    /// <summary>single-point hit test, returns topmost visible element, ignores lock</summary>
    private (GtLayer? layer, GtElement? element) HitTest(Point docPoint)
    {
        if (_document is null) return (null, null);
        var layers = _document.Layers;
        for (int li = layers.Count - 1; li >= 0; li--)
        {
            var layer = layers[li];
            if (!layer.Visible) continue;
            var elements = layer.Elements;
            for (int ei = elements.Count - 1; ei >= 0; ei--)
            {
                var el = elements[ei];
                if (!el.Visible) continue;
                var bounds = ElementAbsBounds(layer, el);
                if (bounds.Contains(docPoint))
                    return (layer, el);
            }
        }
        return (null, null);
    }

    /// <summary>rect intersection test, skips locked layers/elements (drag-box rule)</summary>
    private List<GtElement> HitTestRect(Rect docRect)
    {
        var result = new List<GtElement>();
        if (_document is null) return result;
        foreach (var layer in _document.Layers)
        {
            if (!layer.Visible || layer.Locked) continue;
            foreach (var el in layer.Elements)
            {
                if (!el.Visible || el.Locked) continue;
                if (docRect.Intersects(ElementAbsBounds(layer, el)))
                    result.Add(el);
            }
        }
        return result;
    }

    private bool IsLayerLocked(GtElement el)
    {
        if (_document is null) return false;
        foreach (var layer in _document.Layers)
            if (layer.Elements.Contains(el))
                return layer.Locked;
        return false;
    }

    private static Rect ElementAbsBounds(GtLayer layer, GtElement el) => new(
        layer.Location.X + el.Location.X,
        layer.Location.Y + el.Location.Y,
        el.Dimensions.Width,
        el.Dimensions.Height);

    /// <summary>doc-space frame of a layer; files in the wild carry layers with no Dimensions so the inner composition size (and failing that the canvas) stands in, giving the selection chrome something to draw and something to grab</summary>
    private Rect LayerBounds(GtLayer layer)
    {
        double w = layer.Dimensions.Width  > 0 ? layer.Dimensions.Width
                 : layer.InnerWidth        > 0 ? layer.InnerWidth
                 : _document?.Width ?? 0;
        double h = layer.Dimensions.Height > 0 ? layer.Dimensions.Height
                 : layer.InnerHeight       > 0 ? layer.InnerHeight
                 : _document?.Height ?? 0;
        return new Rect(layer.Location.X, layer.Location.Y, w, h);
    }

    /// <summary>handle under the pointer on the selected layer's frame, if any</summary>
    private ResizeHandle HitTestLayerHandle(Point docPt)
    {
        if (ActiveTool != CanvasTool.Edit || IsPreviewing || _selectedLayer is null) return ResizeHandle.None;

        double r2 = (7.0 / Zoom) * (7.0 / Zoom);
        foreach (var (pt, handle) in GetHandlePoints(LayerBounds(_selectedLayer)))
        {
            var dx = docPt.X - pt.X;
            var dy = docPt.Y - pt.Y;
            if (dx * dx + dy * dy <= r2) return handle;
        }
        return ResizeHandle.None;
    }

    /// <summary>starts a layer resize or move when the press lands on the selected layer; returns false when there is no layer selected, it is locked, or the press is outside its frame, and the caller then handles the press as an element gesture</summary>
    private bool TryBeginLayerGesture(Point docPt)
    {
        var layer = _selectedLayer;
        if (layer is null || layer.Locked) return false;

        var handle = HitTestLayerHandle(docPt);
        if (handle != ResizeHandle.None)
        {
            _isResizingLayer   = true;
            _activeHandle      = handle;
            _resizeStart       = docPt;
            _layerResizeOrigin = (layer.Location, new GtSize(LayerBounds(layer).Width, LayerBounds(layer).Height));
            _resizeOriginBounds = LayerBounds(layer);
            _snapEngine        = BuildSnapEngineExcluding(layer.Elements);
            _layerResizeElementOrigins.Clear();
            foreach (var el in layer.Elements)
                _layerResizeElementOrigins[el] = el.Location;
            return true;
        }

        if (!LayerBounds(layer).Contains(docPt)) return false;

        // content of another layer showing through this one keeps its own click: the press falls through and selects that element, as it would with no layer selected
        var (hitLayer, hitElement) = HitTest(docPt);
        if (hitElement is not null && !ReferenceEquals(hitLayer, layer)) return false;

        _isMovingLayer    = true;
        _moveStart        = docPt;
        _layerMoveOrigin  = layer.Location;
        _moveOriginBounds = LayerBounds(layer);
        _snapEngine       = BuildSnapEngineExcluding(layer.Elements);
        return true;
    }

    /// <summary>applies a handle drag to the selected layer's frame; the frame is not a scale, the inner composition keeps its own size and the elements keep their absolute position so a moved origin counter-shifts their layer-local coordinates, negative included</summary>
    private void ApplyLayerResizeDelta(Point docPt, bool constrainAspect)
    {
        var layer = _selectedLayer;
        if (layer is null) return;

        var (origLoc, origDim) = _layerResizeOrigin;
        var box = ResizedBounds(new Rect(origLoc.X, origLoc.Y, origDim.Width, origDim.Height),
                                _activeHandle, docPt.X - _resizeStart.X, docPt.Y - _resizeStart.Y);

        bool leftEdge = _activeHandle is ResizeHandle.NW or ResizeHandle.W or ResizeHandle.SW;
        bool topEdge  = _activeHandle is ResizeHandle.NW or ResizeHandle.N or ResizeHandle.NE;

        double x = box.X, y = box.Y, w = box.Width, h = box.Height;

        if (constrainAspect && origDim.Width > 0 && origDim.Height > 0 && w > 0 && h > 0)
        {
            double ratio = origDim.Width / origDim.Height;
            if (w / h > ratio)
            {
                h = w / ratio;
                if (topEdge) y = origLoc.Y + origDim.Height - h;
            }
            else
            {
                w = h * ratio;
                if (leftEdge) x = origLoc.X + origDim.Width - w;
            }
        }

        if (w < 1) { w = 1; if (leftEdge) x = origLoc.X + origDim.Width  - 1; }
        if (h < 1) { h = 1; if (topEdge)  y = origLoc.Y + origDim.Height - 1; }

        if (SnapToPixelGrid) { x = Math.Round(x); y = Math.Round(y); w = Math.Round(w); h = Math.Round(h); }

        layer.Location   = new GtPoint(x, y);
        layer.Dimensions = new GtSize(w, h);

        // absolute position of the contents is what GT preserves, so whatever the origin moved by comes straight back off the element coordinates
        double shiftX = origLoc.X - x;
        double shiftY = origLoc.Y - y;
        foreach (var (el, origin) in _layerResizeElementOrigins)
            el.Location = new GtPoint(origin.X + shiftX, origin.Y + shiftY);
    }

    /// <summary>absolute doc-space union of the selection, empty when nothing is selected</summary>
    public Rect SelectionBounds => GetSelectionBounds();

    private Rect GetSelectionBounds()
    {
        if (_document is null || _selectedElements.Count == 0) return default;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var layer in _document.Layers)
        foreach (var el in layer.Elements)
        {
            if (!_selectedElements.Contains(el)) continue;
            var b = ElementAbsBounds(layer, el);
            if (b.Left   < minX) minX = b.Left;
            if (b.Top    < minY) minY = b.Top;
            if (b.Right  > maxX) maxX = b.Right;
            if (b.Bottom > maxY) maxY = b.Bottom;
        }
        return minX == double.MaxValue ? default : new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static (Point Pt, ResizeHandle Handle)[] GetHandlePoints(Rect b) => new[]
    {
        (new Point(b.Left,    b.Top),      ResizeHandle.NW),
        (new Point(b.Center.X, b.Top),     ResizeHandle.N),
        (new Point(b.Right,   b.Top),      ResizeHandle.NE),
        (new Point(b.Right,   b.Center.Y), ResizeHandle.E),
        (new Point(b.Right,   b.Bottom),   ResizeHandle.SE),
        (new Point(b.Center.X, b.Bottom),  ResizeHandle.S),
        (new Point(b.Left,    b.Bottom),   ResizeHandle.SW),
        (new Point(b.Left,    b.Center.Y), ResizeHandle.W),
    };

    private ResizeHandle HitTestHandle(Point docPt)
    {
        if (ActiveTool != CanvasTool.Edit || _document is null || _selectedElements.Count == 0)
            return ResizeHandle.None;

        double r2 = (7.0 / Zoom) * (7.0 / Zoom);
        foreach (var layer in _document.Layers)
        foreach (var el in layer.Elements)
        {
            if (!_selectedElements.Contains(el)) continue;
            foreach (var (pt, handle) in GetHandlePoints(ElementAbsBounds(layer, el)))
            {
                var dx = docPt.X - pt.X;
                var dy = docPt.Y - pt.Y;
                if (dx * dx + dy * dy <= r2)
                    return handle;
            }
        }
        return ResizeHandle.None;
    }

    private static Cursor HandleCursor(ResizeHandle h) => h switch
    {
        ResizeHandle.N  or ResizeHandle.S  => new Cursor(StandardCursorType.SizeNorthSouth),
        ResizeHandle.E  or ResizeHandle.W  => new Cursor(StandardCursorType.SizeWestEast),
        ResizeHandle.NW or ResizeHandle.NE
        or ResizeHandle.SW or ResizeHandle.SE => new Cursor(StandardCursorType.SizeAll),
        _                                     => Cursor.Default
    };

    private void ApplyResizeDelta(Point docPt, bool constrainAspect = false)
    {
        double dx = docPt.X - _resizeStart.X;
        double dy = docPt.Y - _resizeStart.Y;

        bool leftEdge = _activeHandle is ResizeHandle.NW or ResizeHandle.W  or ResizeHandle.SW;
        bool topEdge  = _activeHandle is ResizeHandle.NW or ResizeHandle.N  or ResizeHandle.NE;

        foreach (var (el, (origLoc, origDim)) in _resizeOrigins)
        {
            double x = origLoc.X, y = origLoc.Y;
            double w = origDim.Width, h = origDim.Height;

            switch (_activeHandle)
            {
                case ResizeHandle.NW: x += dx; w -= dx; y += dy; h -= dy; break;
                case ResizeHandle.N:                     y += dy; h -= dy; break;
                case ResizeHandle.NE: w += dx;           y += dy; h -= dy; break;
                case ResizeHandle.E:  w += dx;                             break;
                case ResizeHandle.SE: w += dx;           h += dy;          break;
                case ResizeHandle.S:                     h += dy;          break;
                case ResizeHandle.SW: x += dx; w -= dx;  h += dy;          break;
                case ResizeHandle.W:  x += dx; w -= dx;                    break;
            }

            if (constrainAspect && origDim.Width > 0 && origDim.Height > 0 && w > 0 && h > 0)
            {
                double ratio = origDim.Width / origDim.Height;
                bool isCorner = _activeHandle is ResizeHandle.NW or ResizeHandle.NE
                                             or ResizeHandle.SE or ResizeHandle.SW;
                if (isCorner)
                {
                    if (w / h > ratio)
                    {
                        // too wide: keep w, shrink h to match
                        h = w / ratio;
                        if (topEdge) y = origLoc.Y + origDim.Height - h;
                    }
                    else
                    {
                        // too tall: keep h, shrink w to match
                        w = h * ratio;
                        if (leftEdge) x = origLoc.X + origDim.Width - w;
                    }
                }
                else
                {
                    // edge handles: adjust perpendicular dimension, center on original axis
                    switch (_activeHandle)
                    {
                        case ResizeHandle.N:
                        case ResizeHandle.S:
                            w = h * ratio;
                            x = origLoc.X + (origDim.Width - w) / 2;
                            break;
                        case ResizeHandle.E:
                        case ResizeHandle.W:
                            h = w / ratio;
                            y = origLoc.Y + (origDim.Height - h) / 2;
                            break;
                    }
                }
            }

            // clamp to 1×1 minimum, correct position for edge-moving handles
            if (w < 1) { w = 1; if (leftEdge) x = origLoc.X + origDim.Width  - 1; }
            if (h < 1) { h = 1; if (topEdge)  y = origLoc.Y + origDim.Height - 1; }

            if (SnapToPixelGrid) { x = Math.Round(x); y = Math.Round(y); w = Math.Round(w); h = Math.Round(h); }

            el.Location   = new GtPoint(x, y);
            el.Dimensions = new GtSize(w, h);
        }
    }

    private long _renderCount;
    private double _renderMsTotal;

    /// <summary>frames actually drawn since startup; the compositor coalesces invalidations so this counts real paints rather than <see cref="InvalidateVisual"/> calls, which is what the timeline's FPS readout samples to report true playback frame rate</summary>
    public long RenderCount => _renderCount;

    /// <summary>cumulative milliseconds spent inside <see cref="Render"/></summary>
    public double RenderMsTotal => _renderMsTotal;

    public override void Render(DrawingContext ctx)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            RenderDocument(ctx);
        }
        finally
        {
            _renderCount++;
            _renderMsTotal += (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        }
    }

    private void RenderDocument(DrawingContext ctx)
    {
        // GT resizes auto-sized text from OnRender before anything is drawn, so the boxes the artwork and the selection chrome use are already the measured ones; only model objects are touched here and the notification is posted so no control is re-measured from inside the render pass
        // bounding follows: an auto-sized text box has its measured box by now, so the background bound to it lands on the same frame rather than a frame behind
        bool geometryMoved = ApplyAutoSize();
        geometryMoved |= GtBoundingResolver.Apply(_document);
        if (geometryMoved)
            Dispatcher.UIThread.Post(() => AutoSizeApplied?.Invoke(this, EventArgs.Empty));

        var doc  = _document;
        var zoom = Zoom;
        var docW = doc?.Width  ?? 1920;
        var docH = doc?.Height ?? 1080;

        using (ctx.PushTransform(Matrix.CreateScale(zoom, zoom)))
        {
            var docBounds = new Rect(0, 0, docW, docH);

            ctx.DrawRectangle(Brushes.Black, null, docBounds);

            if (doc is null) return;

            foreach (var layer in doc.Layers)
            {
                if (!layer.Visible) continue;
                RenderLayer(ctx, layer, docBounds);
            }

            // guides sit above the artwork but below the selection chrome
            RenderGuides(ctx, doc);

            // selection overlays (drawn above all content, in doc-space)
            RenderSelectionOverlays(ctx, doc);

            // snap feedback is the topmost thing on the canvas while a drag is live
            RenderSnapLines(ctx);

            // drag-box rectangle
            if (_isDragBoxing)
            {
                var dragRect = MakeRect(_dragStart, _dragCurrent);
                ctx.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(40, 80, 160, 255)),
                    new Pen(new SolidColorBrush(Color.FromArgb(210, 80, 160, 255)), 1.0 / zoom),
                    dragRect);
            }

            // Draw preview (TextBox / Rectangle tools)
            if (_isDrawing)
            {
                var drawRect = MakeRect(_drawStart, _drawCurrent);
                if (ActiveTool == CanvasTool.Rectangle)
                {
                    ctx.DrawRectangle(
                        new SolidColorBrush(Color.FromArgb(80, 220, 0, 0)),
                        new Pen(new SolidColorBrush(Colors.Red), 1.5 / zoom),
                        drawRect);
                }
                else
                {
                    ctx.DrawRectangle(
                        new SolidColorBrush(Color.FromArgb(20, 100, 160, 255)),
                        new Pen(new SolidColorBrush(Color.FromArgb(200, 80, 160, 255)), 1.5 / zoom),
                        drawRect);
                }
            }
        }
    }

    private void RenderLayer(DrawingContext ctx, GtLayer layer, Rect docBounds)
    {
        var anim = _animationFrame?.ForLayer(layer);
        if (anim is null || anim.IsNeutral)
        {
            RenderLayerContent(ctx, layer, docBounds);
            return;
        }

        if (anim.Hidden || anim.OpacityMul <= 0 || anim.ScaleX <= 0 || anim.ScaleY <= 0) return;

        // a layer animates as one sub-composition: the whole frame is moved, scaled, turned and cropped and its contents come along; everything is taken about the layer's resting frame, which is also what a Reveal's crop range is measured against
        var box = LayerBounds(layer);

        if ((anim.RotateX != 0 || anim.RotateY != 0) && box.Width >= 1 && box.Height >= 1)
        {
            RenderLayerWithPerspective(ctx, layer, docBounds, anim, box);
            return;
        }

        RenderLayerAffine(ctx, layer, docBounds, anim, box);
    }

    /// <summary>layer animation without 3D rotation: offset, scale about its anchor, in-plane spin, Reveal crop and fade, all applied to the sub-composition as a whole</summary>
    private void RenderLayerAffine(DrawingContext ctx, GtLayer layer, Rect docBounds,
                                   GtAnimOverride anim, Rect box)
    {
        var scopes = new List<IDisposable>(3);
        try
        {
            var matrix = LayerAnimMatrix(anim, box);
            if (matrix != Matrix.Identity) scopes.Add(ctx.PushTransform(matrix));

            // pushed inside the transform: the crop is a window on the layer's own surface, so it travels and scales with it rather than staying put in doc space
            if (anim.HasCrop)
            {
                var clip = GtAnimationEvaluator.CropClip(box, anim);
                if (clip.Width <= 0 || clip.Height <= 0) return;
                scopes.Add(ctx.PushClip(new RoundedRect(clip)));
            }

            if (anim.OpacityMul < 1.0)
                scopes.Add(ctx.PushOpacity(Math.Clamp(anim.OpacityMul, 0.0, 1.0)));

            RenderLayerContent(ctx, layer, docBounds);
        }
        finally
        {
            for (int i = scopes.Count - 1; i >= 0; i--) scopes[i].Dispose();
        }
    }

    /// <summary>doc-space transform for a layer override: scale about its anchor, then the in-plane rotation about the frame centre, then the animated offset</summary>
    private static Matrix LayerAnimMatrix(GtAnimOverride anim, Rect box)
    {
        var matrix = Matrix.Identity;

        if (anim.ScaleX != 1.0 || anim.ScaleY != 1.0)
        {
            double px = box.X + anim.ScaleAnchorX * box.Width;
            double py = box.Y + anim.ScaleAnchorY * box.Height;
            matrix *= Matrix.CreateTranslation(-px, -py)
                    * Matrix.CreateScale(anim.ScaleX, anim.ScaleY)
                    * Matrix.CreateTranslation(px, py);
        }

        if (anim.RotateZ != 0)
        {
            var c = box.Center;
            matrix *= Matrix.CreateTranslation(-c.X, -c.Y)
                    * Matrix.CreateRotation(anim.RotateZ)
                    * Matrix.CreateTranslation(c.X, c.Y);
        }

        if (anim.OffsetX != 0 || anim.OffsetY != 0)
            matrix *= Matrix.CreateTranslation(anim.OffsetX, anim.OffsetY);

        return matrix;
    }

    /// <summary>layer Rotate about a horizontal or vertical axis, same trick as the element path: the sub-composition is rendered flat into a bitmap then drawn through the homography of its 3D-projected corners; scale and offset stay on the drawing context so a Rotate stacked with a Zoom or a Fly still composes</summary>
    private void RenderLayerWithPerspective(DrawingContext ctx, GtLayer layer, Rect docBounds,
                                            GtAnimOverride anim, Rect box)
    {
        int pw = Math.Max(1, (int)Math.Round(box.Width));
        int ph = Math.Max(1, (int)Math.Round(box.Height));

        byte[] pixels;
        int rowBytes;
        try
        {
            using var rtb = new RenderTargetBitmap(new PixelSize(pw, ph), new Vector(96, 96));
            using (var tmpCtx = rtb.CreateDrawingContext())
            using (tmpCtx.PushTransform(Matrix.CreateTranslation(-box.X, -box.Y)))
            {
                IDisposable? cropScope = null;
                if (anim.HasCrop)
                {
                    var clip = GtAnimationEvaluator.CropClip(box, anim);
                    if (clip.Width <= 0 || clip.Height <= 0) return;
                    cropScope = tmpCtx.PushClip(new RoundedRect(clip));
                }

                try { RenderLayerContent(tmpCtx, layer, docBounds); }
                finally { cropScope?.Dispose(); }
            }

            using var wb = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96, 96),
                                               PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var fb = wb.Lock();
            rtb.CopyPixels(fb);
            rowBytes = fb.RowBytes;
            pixels   = new byte[ph * rowBytes];
            Marshal.Copy(fb.Address, pixels, 0, pixels.Length);
        }
        catch
        {
            // fallback: everything but the 3D turn
            RenderLayerAffine(ctx, layer, docBounds, anim, box);
            return;
        }

        var info  = new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul);
        var skImg = SKImage.FromPixelCopy(info, pixels, rowBytes);
        if (skImg is null) return;

        // the bitmap already carries the crop and the layer's own contents, so only the outer transforms (scale about the anchor, offset) are left on the context
        var outer = LayerAnimMatrix(new GtAnimOverride
        {
            ScaleX = anim.ScaleX, ScaleY = anim.ScaleY,
            ScaleAnchorX = anim.ScaleAnchorX, ScaleAnchorY = anim.ScaleAnchorY,
            OffsetX = anim.OffsetX, OffsetY = anim.OffsetY,
        }, box);

        var quad = ComputePerspectiveQuad(box, anim.RotateX, anim.RotateY, anim.RotateZ);

        IDisposable? outerScope = outer != Matrix.Identity ? ctx.PushTransform(outer) : null;
        try
        {
            ctx.Custom(new PerspectiveBitmapOp(skImg, pw, ph, quad,
                                               (float)Math.Clamp(anim.OpacityMul, 0.0, 1.0)));
        }
        finally
        {
            outerScope?.Dispose();
        }
    }

    private void RenderLayerContent(DrawingContext ctx, GtLayer layer, Rect docBounds)
    {
        using (ctx.PushTransform(Matrix.CreateTranslation(layer.Location.X, layer.Location.Y)))
        {
            // canvas bounds shifted into layer-local coordinate space; docBounds is always (0,0,docW,docH) so this is just (-layer.X, -layer.Y, docW, docH)
            var localCanvas = new Rect(
                -layer.Location.X, -layer.Location.Y,
                docBounds.Width, docBounds.Height);

            // the layer frame is a mask: whatever hangs outside it is not part of the picture; the one exception is the selected layer, where the overflow shows through at the preference's opacity so it can still be seen and grabbed while editing
            var lb = LayerBounds(layer);
            var layerRect = new Rect(0, 0, lb.Width, lb.Height);

            bool showOverflow = !_exportMode && OutsideLayerOpacity > 0 &&
                                (ReferenceEquals(layer, _selectedLayer) || HasSelectedElement(layer));
            Rect? localLayer = showOverflow && OutsideLayerOpacity < 1.0 ? layerRect : null;

            IDisposable? maskScope = showOverflow ? null : ctx.PushClip(new RoundedRect(layerRect));
            try
            {
                // name to element map for mask resolution within this layer
                var nameMap = new Dictionary<string, GtElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var el in layer.Elements)
                    if (!string.IsNullOrEmpty(el.Name))
                        nameMap[el.Name] = el;

                foreach (var element in layer.Elements)
                {
                    if (!element.Visible) continue;
                    RenderElement(ctx, element, localCanvas, localLayer, nameMap);
                }
            }
            finally
            {
                maskScope?.Dispose();
            }
        }
    }

    /// <summary>true when the selection sits inside this layer; working on the contents is as much a reason to see what the frame crops as selecting the layer itself</summary>
    private bool HasSelectedElement(GtLayer layer)
    {
        if (_selectedElements.Count == 0) return false;
        foreach (var el in layer.Elements)
            if (_selectedElements.Contains(el)) return true;
        return false;
    }

    private void RenderElement(DrawingContext ctx, GtElement element, Rect localCanvas, Rect? localLayer,
                               IReadOnlyDictionary<string, GtElement> layerElements)
    {
        if (element.Opacity <= 0) return;

        var anim = _animationFrame?.ForElement(element);
        if (anim is not null && (anim.Hidden || anim.OpacityMul <= 0 ||
                                 anim.ScaleX <= 0 || anim.ScaleY <= 0)) return;

        // resolve mask: clip masked element to mask element's geometry (text glyphs or rect); the mask is a live object so its own animation drives the clip too, Fly/Zoom move and scale the geometry, Reveal wipes it, and Fade dims what shows through; a geometry clip is binary so the mask's animated alpha is folded in as an opacity push instead
        Geometry? maskGeom   = null;
        Rect?     maskReveal = null;
        double    maskAlpha  = 1.0;
        if (element.MaskObject is not null &&
            layerElements.TryGetValue(element.MaskObject, out var maskEl) &&
            !ReferenceEquals(maskEl, element))
        {
            var maskAnim = _animationFrame?.ForElement(maskEl);
            if (maskAnim is not null)
            {
                // a mask that has animated itself out of existence masks everything away
                if (maskAnim.Hidden || maskAnim.ScaleX <= 0 || maskAnim.ScaleY <= 0) return;

                maskAlpha = Math.Clamp(maskAnim.OpacityMul, 0.0, 1.0);
                if (maskAlpha <= 0) return;

                // a geometry clip cannot carry the mask's feathered crop, so a Reveal on the mask contributes its hard-edged rectangle instead
                if (maskAnim.HasCrop)
                {
                    var mr = GtAnimationEvaluator.CropClip(AnimatedBounds(maskEl, maskAnim), maskAnim);
                    if (mr.Width <= 0 || mr.Height <= 0) return;
                    maskReveal = mr;
                }
            }

            maskGeom = BuildMaskGeometry(maskEl, maskAnim);
        }

        // a Reveal on the element itself needs no scope here: it drives the element's crop range, so it is applied by the feathered crop pipeline in RenderCore
        if (maskGeom is null && maskAlpha >= 1.0)
        {
            RenderElementBody(ctx, element, localCanvas, localLayer);
            return;
        }

        // clips and opacity nest, so they are pushed in order and unwound in reverse
        var scopes = new List<IDisposable>(3);
        try
        {
            if (maskAlpha < 1.0)      scopes.Add(ctx.PushOpacity(maskAlpha));
            if (maskGeom is not null) scopes.Add(ctx.PushGeometryClip(maskGeom));
            if (maskReveal is { } mrc) scopes.Add(ctx.PushClip(new RoundedRect(mrc)));

            RenderElementBody(ctx, element, localCanvas, localLayer);
        }
        finally
        {
            for (int i = scopes.Count - 1; i >= 0; i--)
                scopes[i].Dispose();
        }
    }

    /// <summary>the element's layer-local box after animated translation and scaling; returns the resting box when there is no animation override</summary>
    private static Rect AnimatedBounds(GtElement element, GtAnimOverride? anim)
    {
        var bounds = new Rect(element.Location.X, element.Location.Y,
                              element.Dimensions.Width, element.Dimensions.Height);
        if (anim is null) return bounds;

        if (anim.OffsetX != 0 || anim.OffsetY != 0)
            bounds = bounds.Translate(new Vector(anim.OffsetX, anim.OffsetY));

        if (anim.ScaleX != 1.0 || anim.ScaleY != 1.0)
        {
            // scale about the anchor the animation named: Zoom takes the centre, an Expand pins the edge or corner the box grows out of
            double ax = bounds.X + bounds.Width  * anim.ScaleAnchorX;
            double ay = bounds.Y + bounds.Height * anim.ScaleAnchorY;
            double w  = bounds.Width  * anim.ScaleX;
            double h  = bounds.Height * anim.ScaleY;
            bounds = new Rect(ax - w * anim.ScaleAnchorX, ay - h * anim.ScaleAnchorY, w, h);
        }

        return bounds;
    }

    /// <summary>builds a clip geometry from a mask element, carried along by the mask's own Fly/Zoom; TextBlock masks use glyph geometry and everything else falls back to the bounding rect; the mask's resting opacity is intentionally ignored (a mask helper is often dimmed or hidden in the designer) and its animated alpha is applied by the caller instead</summary>
    private static Geometry BuildMaskGeometry(GtElement maskEl, GtAnimOverride? maskAnim)
    {
        var geom = BuildRestMaskGeometry(maskEl);

        if (maskAnim is null || !maskAnim.HasBoxTransform) return geom;

        // same transform AnimatedBounds applies to a box: scale about the animation's anchor then translate, applied to the geometry so glyph outlines move with the mask too
        var rest = new Rect(maskEl.Location.X, maskEl.Location.Y,
                            maskEl.Dimensions.Width, maskEl.Dimensions.Height);
        double ax = rest.X + rest.Width  * maskAnim.ScaleAnchorX;
        double ay = rest.Y + rest.Height * maskAnim.ScaleAnchorY;
        geom.Transform = new MatrixTransform(
            Matrix.CreateTranslation(-ax, -ay)
          * Matrix.CreateScale(maskAnim.ScaleX, maskAnim.ScaleY)
          * Matrix.CreateTranslation(ax + maskAnim.OffsetX, ay + maskAnim.OffsetY));
        return geom;
    }

    private static Geometry BuildRestMaskGeometry(GtElement maskEl)
    {
        // a ticker draws clones of its text not the text itself, and what is on screen depends on the frame, so it masks by its box like a rectangle does
        if (maskEl is GtTextBlock tb && maskEl is not GtTickerElement && !string.IsNullOrEmpty(tb.Text))
        {
            var bounds = new Rect(tb.Location.X, tb.Location.Y, tb.Dimensions.Width, tb.Dimensions.Height);
            if (BuildTextMaskGeometry(tb, bounds) is { } glyphs) return glyphs;
        }

        // ellipse mask: use ellipse geometry
        if (maskEl is GtEllipseElement)
        {
            return new EllipseGeometry(new Rect(
                maskEl.Location.X, maskEl.Location.Y,
                maskEl.Dimensions.Width, maskEl.Dimensions.Height));
        }

        // fallback: bounding rect (used for Rectangle/Image masks, or empty TextBlock)
        return new RectangleGeometry(new Rect(
            maskEl.Location.X, maskEl.Location.Y,
            maskEl.Dimensions.Width, maskEl.Dimensions.Height));
    }

    /// <summary>glyph outlines for a text mask, laid out through the same pipeline <see cref="RenderTextBlock"/> uses, so line spacing, wrapping, uppercase, the auto-size coercions, the overhang nudge and the stroke all put the mask exactly where the text draws; a one-shot FormattedText cannot do that since GT's LineSpacing divorces the line box from the font's and moves every line including the first (see <see cref="LineBox"/>). Returns null when the block lays out to nothing</summary>
    private static Geometry? BuildTextMaskGeometry(GtTextBlock tb, Rect bounds)
    {
        var text     = tb.Uppercase ? tb.Text.ToUpper() : tb.Text;
        var typeface = new Typeface(new FontFamily(tb.FontFamily), tb.FontStyle, tb.FontWeight);

        var plan = ResolveTextPlan(tb, text, typeface, bounds);
        if (LayOutText(text, typeface, tb, plan, bounds, Brushes.White) is not { } laid) return null;

        var glyphs = BuildGlyphGeometry(text, typeface, plan, laid, laid.Offset + StrokeOriginShift(tb));
        if (glyphs is null) return null;

        // the stroke straddles the outline so half of it paints outside the glyph; without growing by that half a stroked text masks a shape smaller than the one it draws
        if (tb.Stroke is null || tb.StrokeThickness <= 0) return glyphs;

        var widened = glyphs.GetWidenedGeometry(new Pen(Brushes.White, tb.StrokeThickness));
        return widened is null
            ? glyphs
            : new CombinedGeometry(GeometryCombineMode.Union, glyphs, widened);
    }

    /// <summary>glyph outlines of an already laid-out block, one geometry per line placed on that line's own baseline so the layout's line spacing and alignment carry over; the fill rule is non-zero because glyphs from neighbouring lines overlap once the spacing pulls them together and an even-odd fill would punch holes where they cross</summary>
    private static Geometry? BuildGlyphGeometry(string text, Typeface typeface,
                                                in TextPlan plan, in LaidOutText laid, Vector offset)
    {
        var group = new GeometryGroup { FillRule = FillRule.NonZero };

        int count = Math.Min(laid.Lines.Count, laid.Origins.Length);
        for (int i = 0; i < count; i++)
        {
            var line  = laid.Lines[i];
            var slice = text.Substring(line.FirstTextSourceIndex, line.Length).TrimEnd('\r', '\n');
            if (slice.Length == 0) continue;

            var ft = new FormattedText(slice, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                       typeface, plan.FontSize, Brushes.White);

            // TextLine draws its glyphs line.Baseline below the origin and FormattedText ft.Baseline below its own, so the two are lined up on the baseline rather than on the top edge
            var origin = laid.Origins[i] + offset + new Vector(0, line.Baseline - ft.Baseline);
            if (ft.BuildGeometry(origin) is { } geom) group.Children.Add(geom);
        }

        return group.Children.Count > 0 ? group : null;
    }

    /// <summary>the half-stroke nudge GT applies to the glyph origin before drawing (DrawGlyphRun moves the baseline origin by strokeWidth/2 on both axes): D2D has no outer-stroke mode so a stroke straddles the outline, and without the shift the left and top halves of it fall outside the object's surface and are clipped away</summary>
    private static Vector StrokeOriginShift(GtTextBlock tb)
        => tb.StrokeThickness > 0
            ? new Vector(tb.StrokeThickness / 2, tb.StrokeThickness / 2)
            : default;

    /// <summary>pushes an in-plane (Z-axis) rotation transform centred on the element bounds (affine, exact); returns null when RotateZ is zero</summary>
    private static IDisposable? PushZRotation(DrawingContext ctx, double rotateZ, Rect bounds)
    {
        if (rotateZ == 0) return null;
        var center = bounds.Center;
        var mat = Matrix.CreateTranslation(-center.X, -center.Y)
                * Matrix.CreateRotation(rotateZ)
                * Matrix.CreateTranslation(center.X, center.Y);
        return ctx.PushTransform(mat);
    }

    /// <summary>the element's rotation with any animated spin folded in; Rotate settles onto the authored angle from a full turn away, RotateContinuous keeps adding to it</summary>
    private static (double X, double Y, double Z) AnimatedRotation(GtElement element, GtAnimOverride? anim) =>
        anim is null
            ? (element.RotateX, element.RotateY, element.RotateZ)
            : (element.RotateX + anim.RotateX,
               element.RotateY + anim.RotateY,
               element.RotateZ + anim.RotateZ);

    private void RenderElementBody(DrawingContext ctx, GtElement element, Rect localCanvas, Rect? localLayer)
    {
        var anim   = _animationFrame?.ForElement(element);
        var bounds = AnimatedBounds(element, anim);
        var opacity = anim is null ? element.Opacity
                                   : element.Opacity * Math.Clamp(anim.OpacityMul, 0.0, 1.0);

        // a Reveal replaces the element's crop range for the duration of the wipe
        var crop = GtAnimationEvaluator.EffectiveCrop(element, anim);
        var (rx, ry, rz) = AnimatedRotation(element, anim);

        // X/Y rotation requires perspective projection, use Skia homography path; Z rotation (including combined X/Y/Z) is handled inside ComputePerspectiveQuad
        if (rx != 0 || ry != 0)
        {
            RenderElementWithPerspective(ctx, element, bounds, localCanvas, opacity, crop, rx, ry, rz);
            return;
        }

        // Z-only (or no) rotation: exact affine path; the canvas-edge clips live in unrotated layer space so they must be pushed before the rotation transform, otherwise the dimming boundary spins with the element and the fade follows its unrotated bounding box instead of the canvas edge; the rotated footprint (AABB of the turned rect) drives the inside/outside tests for the same reason
        var footprint = RotatedFootprint(bounds, rz);

        // frames that dim what escapes them: the canvas, and the selected layer's own box; each one the footprint leaves costs it a multiplier so the corner outside both fades by the product
        var frames = new List<(Rect Rect, double Outside)>(2);
        var outsideOp = _exportMode ? 1.0 : OutsideCanvasOpacity;
        if (outsideOp < 1.0) frames.Add((localCanvas, outsideOp));
        if (localLayer is { } layerRect && OutsideLayerOpacity < 1.0)
            frames.Add((layerRect, OutsideLayerOpacity));

        bool fullyInside = true;
        foreach (var (rect, _) in frames)
            if (!ContainsRect(rect, footprint)) { fullyInside = false; break; }

        if (fullyInside)
        {
            RenderRotated(ctx, element, bounds, crop, rz, opacity);
            return;
        }

        // one pass per inside/outside combination of the frames, each clipped to its own region; the all-inside pass clips to a plain rect since a geometry clip there would leave an antialiased seam along the shared edge; the clips bound the dimming not the drawing, shadows and glows reach past the element box so the outside passes start from a footprint with room around it
        var outerBase = footprint.Inflate(ClipOverflowMargin);

        for (int mask = 0; mask < (1 << frames.Count); mask++)
        {
            double factor = 1.0;
            bool possible  = true;
            for (int i = 0; i < frames.Count; i++)
            {
                bool outside = (mask & (1 << i)) != 0;
                if (outside)
                {
                    factor *= frames[i].Outside;
                    if (ContainsRect(frames[i].Rect, footprint)) { possible = false; break; }
                }
                else if (!frames[i].Rect.Intersects(footprint)) { possible = false; break; }
            }
            if (!possible || factor <= 0) continue;

            if (mask == 0)
            {
                var inner = frames[0].Rect;
                for (int i = 1; i < frames.Count; i++) inner = inner.Intersect(frames[i].Rect);
                if (inner.Width <= 0 || inner.Height <= 0) continue;

                using (ctx.PushClip(new RoundedRect(inner)))
                    RenderRotated(ctx, element, bounds, crop, rz, opacity);
                continue;
            }

            Geometry region = new RectangleGeometry(outerBase);
            for (int i = 0; i < frames.Count; i++)
            {
                // a frame the element misses entirely dims all of it, cutting the frame out would only chop off overflow that has nothing else to belong to
                if ((mask & (1 << i)) != 0 && !frames[i].Rect.Intersects(footprint)) continue;

                region = new CombinedGeometry
                {
                    Geometry1           = region,
                    Geometry2           = new RectangleGeometry(frames[i].Rect),
                    GeometryCombineMode = (mask & (1 << i)) != 0
                        ? GeometryCombineMode.Exclude
                        : GeometryCombineMode.Intersect,
                };
            }

            using (ctx.PushGeometryClip(region))
                RenderRotated(ctx, element, bounds, crop, rz, opacity * factor);
        }
    }

    /// <summary>slack around an element's box for effects that draw past it (shadow, glow)</summary>
    private const double ClipOverflowMargin = 2000;

    private static bool ContainsRect(Rect outer, Rect inner) =>
        inner.Left  >= outer.Left  && inner.Top    >= outer.Top &&
        inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    /// <summary>axis-aligned box actually covered by <paramref name="bounds"/> once turned by <paramref name="rotateZ"/></summary>
    private static Rect RotatedFootprint(Rect bounds, double rotateZ)
    {
        if (rotateZ == 0) return bounds;
        var center = bounds.Center;
        var mat = Matrix.CreateTranslation(-center.X, -center.Y)
                * Matrix.CreateRotation(rotateZ)
                * Matrix.CreateTranslation(center.X, center.Y);
        return bounds.TransformToAABB(mat);
    }

    /// <summary>renders the element body with its Z rotation applied inside any clip already pushed</summary>
    private void RenderRotated(DrawingContext ctx, GtElement element, Rect bounds,
                               GtCrop? crop, double rz, double opacity)
    {
        using var rotScope = PushZRotation(ctx, rz, bounds);
        WithOpacity(ctx, opacity, () => RenderCore(ctx, element, bounds, crop));
    }

    private static void WithOpacity(DrawingContext ctx, double opacity, Action render)
    {
        if (opacity < 1.0)
            using (ctx.PushOpacity(opacity))
                render();
        else
            render();
    }

    /// <summary>computes the 4 screen-space corners of the element after 3D rotation and perspective projection; vMix convention is RotateX (model) acts as Y-axis rotation (compresses width), RotateY (model) acts as X-axis rotation (compresses height), RotateZ is standard 2D in-plane rotation; application order matches WPF PlaneProjection, X-axis (RotateY_model) then Y-axis (RotateX_model) then Z-axis; returns corners TL, TR, BR, BL in layer-local coordinate space</summary>
    private static SKPoint[] ComputePerspectiveQuad(Rect bounds, double rx, double ry, double rz,
                                                    double viewerDist = 2000.0)
    {
        // rx is applied as Y-axis 3D rotation, ry as X-axis, rz in-plane.
        double w  = bounds.Width;
        double h  = bounds.Height;
        double cx = bounds.X + w / 2.0;
        double cy = bounds.Y + h / 2.0;

        double cosRy = Math.Cos(ry), sinRy = Math.Sin(ry); // X-axis rotation (RotateY_model)
        double cosRx = Math.Cos(rx), sinRx = Math.Sin(rx); // Y-axis rotation (RotateX_model)
        double cosRz = Math.Cos(rz), sinRz = Math.Sin(rz);

        // 4 corners in element-local space (centred at origin): TL, TR, BR, BL
        double[] xs = new[] { -w / 2,  w / 2,  w / 2, -w / 2 };
        double[] ys = new[] { -h / 2, -h / 2,  h / 2,  h / 2 };

        var result = new SKPoint[4];
        for (int i = 0; i < 4; i++)
        {
            double px = xs[i], py = ys[i], pz = 0.0;

            // step 1: rotate around X axis (WPF RotationX = vMix RotateY_model), negated for vMix sign convention
            double ny = py * cosRy + pz * sinRy;
            double nz = -py * sinRy + pz * cosRy;
            py = ny; pz = nz;

            // step 2: rotate around Y axis (WPF RotationY = vMix RotateX_model)
            double nx = px * cosRx + pz * sinRx;
            nz = -px * sinRx + pz * cosRx;
            px = nx; pz = nz;

            // step 3: rotate around Z axis
            nx = px * cosRz - py * sinRz;
            ny = px * sinRz + py * cosRz;
            px = nx; py = ny;

            // perspective projection: viewer at (0,0,-viewerDist) looking in +Z
            double scale = viewerDist / (viewerDist + pz);
            result[i] = new SKPoint((float)(px * scale + cx), (float)(py * scale + cy));
        }
        return result;
    }

    /// <summary>renders an element using a Skia perspective homography for X/Y rotation; the element is first rendered flat to an off-screen bitmap then the bitmap is drawn with a homography matrix derived from the 3D-projected corners</summary>
    private void RenderElementWithPerspective(DrawingContext ctx, GtElement element, Rect bounds,
                                              Rect localCanvas, double opacity, GtCrop? crop,
                                              double rx, double ry, double rz)
    {
        int pw = Math.Max(1, (int)Math.Round(bounds.Width));
        int ph = Math.Max(1, (int)Math.Round(bounds.Height));

        byte[] pixels;
        int rowBytes;
        try
        {
            // render element content flat (no rotation) to a temp RenderTargetBitmap
            using var rtb = new RenderTargetBitmap(new PixelSize(pw, ph), new Vector(96, 96));
            using (var tmpCtx = rtb.CreateDrawingContext())
            using (tmpCtx.PushTransform(Matrix.CreateTranslation(-bounds.X, -bounds.Y)))
            {
                // pass bounds as localCanvas so the element is always "fully inside" (no outside-dimming)
                RenderCore(tmpCtx, element, bounds, crop);
            }

            // copy pixel data via a WriteableBitmap lock
            using var wb = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var fb = wb.Lock();
            rtb.CopyPixels(fb);
            rowBytes = fb.RowBytes;
            pixels = new byte[ph * rowBytes];
            Marshal.Copy(fb.Address, pixels, 0, pixels.Length);
        }
        catch
        {
            // fallback: affine Z-rotation only
            using var zRot = PushZRotation(ctx, rz, bounds);
            WithOpacity(ctx, opacity, () => RenderCore(ctx, element, localCanvas, crop));
            return;
        }

        var info = new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul);
        var skImg = SKImage.FromPixelCopy(info, pixels, rowBytes);
        if (skImg is null) return;

        var quad = ComputePerspectiveQuad(bounds, rx, ry, rz);
        ctx.Custom(new PerspectiveBitmapOp(skImg, pw, ph, quad, (float)opacity));
    }

    /// <summary>draws selection boxes and resize handles; these intentionally stay at the element's resting geometry even while a storyboard frame is previewed since the handles edit the authored Location/Dimensions, so following the animated position would offer a drag that does not correspond to what would change</summary>
    private static readonly Color GuideColor       = Color.FromRgb(0x00, 0xa8, 0xff);
    private static readonly Color LockedGuideColor = Color.FromRgb(0x66, 0x88, 0x99);
    private static readonly Color SnapLineColor    = Color.FromRgb(0xff, 0x30, 0xc0);

    private void RenderGuides(DrawingContext ctx, GtDocument doc)
    {
        if (!ShowGuides || doc.Guides.Count == 0) return;

        // locked guides read as inert: muted colour, dashed stroke
        var pen = doc.GuidesLocked
            ? new Pen(new SolidColorBrush(LockedGuideColor), 1.0 / Zoom,
                      new DashStyle(new double[] { 4, 3 }, 0))
            : new Pen(new SolidColorBrush(GuideColor), 1.0 / Zoom);

        foreach (var guide in doc.Guides)
        {
            if (guide.Orientation == GtGuideOrientation.Vertical)
                ctx.DrawLine(pen, new Point(guide.Position, 0), new Point(guide.Position, doc.Height));
            else
                ctx.DrawLine(pen, new Point(0, guide.Position), new Point(doc.Width, guide.Position));
        }

        if (_draggedGuide is not null)
            RenderGuideReadout(ctx, _draggedGuide);
    }

    /// <summary>pixel readout that follows a guide while it is being dragged</summary>
    private void RenderGuideReadout(DrawingContext ctx, GtGuide guide)
    {
        var vertical = guide.Orientation == GtGuideOrientation.Vertical;
        var label = (vertical ? "X: " : "Y: ") +
                    guide.Position.ToString("0.##", CultureInfo.InvariantCulture) + " px";

        // text is laid out in screen pixels then scaled back down so zoom never distorts it
        var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                     new Typeface("Segoe UI"), 11,
                                     new SolidColorBrush(Colors.White));

        var scale  = 1.0 / Zoom;
        var origin = vertical
            ? new Point(guide.Position + 6 * scale, _guideDragPoint.Y + 6 * scale)
            : new Point(_guideDragPoint.X + 6 * scale, guide.Position + 6 * scale);

        var box = new Rect(origin,
                           new Size((text.Width + 8) * scale, (text.Height + 4) * scale));

        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)),
                          new Pen(new SolidColorBrush(GuideColor), 1.0 * scale),
                          new RoundedRect(box, 2 * scale));

        using (ctx.PushTransform(Matrix.CreateScale(scale, scale) *
                                 Matrix.CreateTranslation(origin.X + 4 * scale, origin.Y + 2 * scale)))
            ctx.DrawText(text, new Point(0, 0));
    }

    private void RenderSnapLines(DrawingContext ctx)
    {
        if (_snapLines.Count == 0) return;

        var pen = new Pen(new SolidColorBrush(SnapLineColor), 1.0 / Zoom);
        foreach (var line in _snapLines)
        {
            if (line.Vertical)
                ctx.DrawLine(pen, new Point(line.Position, line.Start), new Point(line.Position, line.End));
            else
                ctx.DrawLine(pen, new Point(line.Start, line.Position), new Point(line.End, line.Position));
        }
    }

    private void RenderSelectionOverlays(DrawingContext ctx, GtDocument doc)
    {
        if (_selectedLayer is not null) RenderLayerSelection(ctx, _selectedLayer);
        if (_selectedElements.Count == 0) return;

        var frame = _animationFrame;
        var stroke = new SolidColorBrush(Color.FromArgb(220, 80, 160, 255));

        // dashed while previewing, to read as "shown, not editable"
        var fill = new SolidColorBrush(Color.FromArgb(frame is null ? (byte)45 : (byte)20, 80, 160, 255));
        var pen  = frame is null
            ? new Pen(stroke, 1.5 / Zoom)
            : new Pen(stroke, 1.5 / Zoom, new DashStyle(new double[] { 4, 3 }, 0));

        foreach (var layer in doc.Layers)
        foreach (var el in layer.Elements)
        {
            if (!_selectedElements.Contains(el)) continue;

            var bounds = ElementAbsBounds(layer, el);

            if (frame is not null)
            {
                var layerAnim   = frame.ForLayer(layer);
                var elementAnim = frame.ForElement(el);

                // nothing to outline where the object is not being drawn at all
                if (layerAnim?.Hidden == true || elementAnim?.Hidden == true) continue;

                // follows the animated position so the marquee stays on the object; scale and reveal are left out, the outline marks what is selected not its exact pixels
                bounds = bounds.Translate(new Vector(
                    (layerAnim?.OffsetX ?? 0) + (elementAnim?.OffsetX ?? 0),
                    (layerAnim?.OffsetY ?? 0) + (elementAnim?.OffsetY ?? 0)));
            }

            ctx.DrawRectangle(fill, pen, bounds);
        }

        // handles work against the model geometry, so they are hidden while it is overridden
        if (ActiveTool == CanvasTool.Edit && !IsPreviewing)
        {
            foreach (var layer in doc.Layers)
            foreach (var el in layer.Elements)
            {
                if (!_selectedElements.Contains(el)) continue;
                RenderResizeHandles(ctx, ElementAbsBounds(layer, el));
            }
        }
    }

    /// <summary>frame of the selected layer, amber rather than the element blue so a layer selection never reads as an element one; the frame is drawn empty because the layer's own content is whatever its elements paint</summary>
    private void RenderLayerSelection(DrawingContext ctx, GtLayer layer)
    {
        var frame  = _animationFrame;
        var anim   = frame?.ForLayer(layer);
        if (anim?.Hidden == true) return;

        var bounds = LayerBounds(layer);
        if (anim is not null)
            bounds = bounds.Translate(new Vector(anim.OffsetX, anim.OffsetY));

        var stroke = new SolidColorBrush(Color.FromArgb(230, 255, 176, 32));
        var pen    = frame is null
            ? new Pen(stroke, 1.5 / Zoom, new DashStyle(new double[] { 6, 3 }, 0))
            : new Pen(stroke, 1.5 / Zoom, new DashStyle(new double[] { 4, 3 }, 0));

        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(18, 255, 176, 32)), pen, bounds);

        // handles act on the model geometry, so they are hidden while a preview overrides it
        if (ActiveTool == CanvasTool.Edit && !IsPreviewing && !layer.Locked)
            RenderResizeHandles(ctx, bounds, stroke);
    }

    private void RenderResizeHandles(DrawingContext ctx, Rect selBounds, IBrush? outline = null)
    {
        double size = 7.0 / Zoom;
        double half = size / 2;
        var fill = new SolidColorBrush(Colors.White);
        var pen  = new Pen(outline ?? new SolidColorBrush(Color.FromArgb(220, 80, 160, 255)), 1.0 / Zoom);

        foreach (var (pt, _) in GetHandlePoints(selBounds))
            ctx.DrawRectangle(fill, pen, new Rect(pt.X - half, pt.Y - half, size, size));
    }

    /// <summary>draws the element's own content through the crop pipeline; <paramref name="crop"/> is the element's crop with any animated Reveal range already folded in</summary>
    private void RenderCore(DrawingContext ctx, GtElement element, Rect bounds, GtCrop? crop)
    {
        // GT's crop shader skips the whole block for an untouched Range, so feather alone never shows: an uncropped element renders flat whatever its Feather values say
        if (crop is null || crop.RangeIsFull)
        {
            RenderShape(ctx, element, bounds);
            return;
        }

        // feather is stored in pixels of the element's own (unscaled) box, so it is normalised against Dimensions rather than the drawn bounds; a scaled or rotated element stretches its feather on screen exactly as GT does
        var f = NormalizedFeather(crop, element.Dimensions);

        // support of the alpha ramp: the visible band reaches feather-far past each crop line, back out towards the element edge
        var (x0, _) = FeatherBand(crop.X0, f.L, invert: true);
        var (_, x1) = FeatherBand(crop.X1, f.R, invert: false);
        var (y0, _) = FeatherBand(crop.Y0, f.T, invert: true);
        var (_, y1) = FeatherBand(crop.Y1, f.B, invert: false);
        if (x1 <= x0 || y1 <= y0) return;

        var clip = new Rect(bounds.X + x0 * bounds.Width,
                            bounds.Y + y0 * bounds.Height,
                            (x1 - x0) * bounds.Width,
                            (y1 - y0) * bounds.Height);
        if (clip.Width <= 0 || clip.Height <= 0) return;

        var scopes = new List<IDisposable>(2);
        try
        {
            // the clip gives every hard edge exactly, the mask only carries the ramps
            scopes.Add(ctx.PushClip(new RoundedRect(clip)));

            if (f.Any)
            {
                var mask = GetCropMask(element, crop, f, x0, x1, y0, y1, clip);
                if (mask is not null)
                    scopes.Add(ctx.PushOpacityMask(
                        new ImageBrush(mask) { Stretch = Stretch.Fill }, clip));
            }

            RenderShape(ctx, element, bounds);
        }
        finally
        {
            for (int i = scopes.Count - 1; i >= 0; i--)
                scopes[i].Dispose();
        }
    }

    /// <summary>per-edge feather in element-normalised units, with GT's right/bottom clamp</summary>
    private static (double L, double T, double R, double B, bool Any) NormalizedFeather(
        GtCrop crop, GtSize dimensions)
    {
        double w = dimensions.Width  > 0 ? dimensions.Width  : 1;
        double h = dimensions.Height > 0 ? dimensions.Height : 1;

        double l = crop.FeatherLeft   / w;
        double t = crop.FeatherTop    / h;
        double r = crop.FeatherRight  / w;
        double b = crop.FeatherBottom / h;

        // GT clamps only these two, and against the crop coordinate itself rather than the distance left to the edge; kept verbatim, it is visible behaviour not a bug fix
        if (crop.X1 - r < 0) r = crop.X1;
        if (crop.Y1 - b < 0) b = crop.Y1;

        return (l, t, r, b, l > 0 || t > 0 || r > 0 || b > 0);
    }

    /// <summary>the ramp band for one edge as GT's shader computes it: alpha is 0 outside the band on the cut side, ramps linearly across it and is 1 on the kept side; for left/top (<paramref name="invert"/>) the band runs <c>cut - feather</c> to <c>cut</c>, for right/bottom it runs <c>cut</c> to <c>cut + feather</c></summary>
    private static (double Start, double End) FeatherBand(double cut, double feather, bool invert)
    {
        double start = cut, end = cut;
        if (invert)
        {
            start -= feather;
            // GT's near-edge hack: measured against endPoint which is still cut here, so a band that would overrun the far edge is pulled in to 2*cut - 1
            if (end + feather > 1) start -= 1 - (end + feather);
        }
        else
        {
            end += feather;
        }
        return (Math.Clamp(start, 0, 1), Math.Clamp(end, 0, 1));
    }

    /// <summary>alpha multiplier for one axis pass, transliterated from GT's <c>crop()</c> HLSL; <paramref name="position"/> is a 0-1 coordinate across the element box</summary>
    private static double CropAlpha(double cut, double feather, double position, bool invert)
    {
        var (start, end) = FeatherBand(cut, feather, invert);

        if (invert ? position <= start : position >= end) return 0;

        if (end > position && start < position)
        {
            double v = (end - position) / (end - start);
            return invert ? 1 - v : v;
        }
        return 1;
    }

    // the ramp is separable (alpha = vx · vy) but Avalonia has no multiply-composited brush, so the product is baked into a small alpha bitmap; corners then get the true quadratic falloff instead of the single-axis fade nested gradient masks would give
    private const int CropMaskMaxSide    = 512;
    private const int CropMaskCacheLimit = 64;

    private readonly Dictionary<GtElement, (CropMaskKey Key, WriteableBitmap Bitmap)> _cropMasks =
        new(ReferenceEqualityComparer.Instance);

    private readonly struct CropMaskKey : IEquatable<CropMaskKey>
    {
        private readonly double _x0, _y0, _x1, _y1, _fl, _ft, _fr, _fb;
        private readonly int _w, _h;

        public CropMaskKey(GtCrop crop, (double L, double T, double R, double B, bool Any) f,
                           int w, int h)
        {
            _x0 = crop.X0; _y0 = crop.Y0; _x1 = crop.X1; _y1 = crop.Y1;
            _fl = f.L; _ft = f.T; _fr = f.R; _fb = f.B;
            _w = w; _h = h;
        }

        public bool Equals(CropMaskKey o) =>
            _x0 == o._x0 && _y0 == o._y0 && _x1 == o._x1 && _y1 == o._y1 &&
            _fl == o._fl && _ft == o._ft && _fr == o._fr && _fb == o._fb &&
            _w == o._w && _h == o._h;

        public override bool Equals(object? obj) => obj is CropMaskKey o && Equals(o);

        public override int GetHashCode() =>
            HashCode.Combine(_x0, _y0, _x1, _y1, _fl, _ft, HashCode.Combine(_fr, _fb, _w, _h));
    }

    private WriteableBitmap? GetCropMask(GtElement element, GtCrop crop,
        (double L, double T, double R, double B, bool Any) f,
        double x0, double x1, double y0, double y1, Rect clip)
    {
        int w = Math.Clamp((int)Math.Round(clip.Width),  1, CropMaskMaxSide);
        int h = Math.Clamp((int)Math.Round(clip.Height), 1, CropMaskMaxSide);

        var key = new CropMaskKey(crop, f, w, h);
        if (_cropMasks.TryGetValue(element, out var cached))
        {
            if (cached.Key.Equals(key)) return cached.Bitmap;
            cached.Bitmap.Dispose();
            _cropMasks.Remove(element);
        }

        var bitmap = BuildCropMask(crop, f, x0, x1, y0, y1, w, h);
        if (bitmap is null) return null;

        if (_cropMasks.Count >= CropMaskCacheLimit) ClearCropMasks();
        _cropMasks[element] = (key, bitmap);
        return bitmap;
    }

    private static WriteableBitmap? BuildCropMask(GtCrop crop,
        (double L, double T, double R, double B, bool Any) f,
        double x0, double x1, double y0, double y1, int w, int h)
    {
        // both axis profiles are 1D, so each is evaluated once and multiplied per pixel
        var vx = new double[w];
        for (int i = 0; i < w; i++)
        {
            double u = x0 + (i + 0.5) / w * (x1 - x0);
            vx[i] = CropAlpha(crop.X0, f.L, u, invert: true)
                  * CropAlpha(crop.X1, f.R, u, invert: false);
        }

        var vy = new double[h];
        for (int j = 0; j < h; j++)
        {
            double v = y0 + (j + 0.5) / h * (y1 - y0);
            vy[j] = CropAlpha(crop.Y0, f.T, v, invert: true)
                  * CropAlpha(crop.Y1, f.B, v, invert: false);
        }

        try
        {
            var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                             PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = bitmap.Lock())
            {
                var row = new byte[fb.RowBytes];
                for (int j = 0; j < h; j++)
                {
                    for (int i = 0; i < w; i++)
                    {
                        // premultiplied white: every channel carries the alpha
                        byte a = (byte)Math.Round(Math.Clamp(vx[i] * vy[j], 0, 1) * 255);
                        int o = i * 4;
                        row[o] = row[o + 1] = row[o + 2] = row[o + 3] = a;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(fb.Address, j * fb.RowBytes), fb.RowBytes);
                }
            }
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void ClearCropMasks()
    {
        foreach (var (_, bitmap) in _cropMasks.Values) bitmap.Dispose();
        _cropMasks.Clear();
    }

    private void RenderShape(DrawingContext ctx, GtElement element, Rect bounds)
    {
        switch (element)
        {
            case GtTickerElement    ticker:  RenderTicker(ctx, ticker, bounds);     break;
            case GtRectangleElement rect:    RenderRectangle(ctx, rect, bounds);    break;
            case GtEllipseElement   ellipse: RenderEllipse(ctx, ellipse, bounds);   break;
            case GtImageElement     img:     RenderImage(ctx, img, bounds);         break;
            case GtWebElement       web:     RenderWeb(ctx, web, bounds);           break;
            case GtTextBlock        tb:      RenderTextBlock(ctx, tb, bounds);      break;
        }
    }

    private void RenderRectangle(DrawingContext ctx, GtRectangleElement rect, Rect bounds)
    {
        var pen = rect.StrokeThickness > 0 ? MakePen(rect.Stroke, rect.StrokeThickness) : null;

        if (rect.Fill?.Type == GtBrushType.RadialGradient)
        {
            switch (rect.Fill.WrapMode)
            {
                case GtRadialWrap.Mirror:
                {
                    // Gradient reflects backwards through stops into the corners
                    ctx.DrawRectangle(MakeRadialGradient(rect.Fill, GradientSpreadMethod.Reflect), pen, bounds);
                    return;
                }
                case GtRadialWrap.Clamp:
                {
                    // Corners filled with last stop color; inscribed ellipse filled with gradient
                    var stops = rect.Fill.Stops;
                    var cornerColor = stops.Count > 0 ? stops[stops.Count - 1].Color : rect.Fill.Color;
                    ctx.DrawRectangle(new SolidColorBrush(cornerColor), null, bounds);
                    ctx.DrawEllipse(MakeRadialGradient(rect.Fill), null,
                        bounds.Center, bounds.Width / 2, bounds.Height / 2);
                    if (pen is not null) ctx.DrawRectangle(null, pen, bounds);
                    return;
                }
                case GtRadialWrap.Wrap:
                {
                    // Corners filled with brush.Color; inscribed ellipse filled with gradient
                    ctx.DrawRectangle(new SolidColorBrush(rect.Fill.Color), null, bounds);
                    ctx.DrawEllipse(MakeRadialGradient(rect.Fill), null,
                        bounds.Center, bounds.Width / 2, bounds.Height / 2);
                    if (pen is not null) ctx.DrawRectangle(null, pen, bounds);
                    return;
                }
            }
        }

        ctx.DrawRectangle(MakeBrush(rect.Fill), pen, bounds);
    }

    private void RenderEllipse(DrawingContext ctx, GtEllipseElement ellipse, Rect bounds)
    {
        var center  = bounds.Center;
        var radiusX = bounds.Width  / 2;
        var radiusY = bounds.Height / 2;
        var pen = ellipse.StrokeThickness > 0 ? MakePen(ellipse.Stroke, ellipse.StrokeThickness) : null;
        ctx.DrawEllipse(MakeBrush(ellipse.Fill), pen, center, radiusX, radiusY);
    }

    private void RenderImage(DrawingContext ctx, GtImageElement img, Rect bounds)
    {
        if (img.BitmapSource is null) return;

        // an ImageSequence animation (or the designer's saved scrub Position) selects which frame of the sequence this image resolves to
        var position = _animationFrame?.ForElement(img)?.SequencePosition ?? img.SequencePosition;
        var source   = position is { } p ? _assets.FrameAt(img.BitmapSource, p) : img.BitmapSource;
        if (source is null) return;

        var bmp = GetBitmap(source);
        if (bmp is null) return;

        var srcRect = new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height);
        var dest    = SizeModeDestination(img.SizeMode, bounds, srcRect.Size);
        if (dest.Width <= 0 || dest.Height <= 0) return;

        // Normal / TopRight draw at native size, so a bitmap larger than the box overflows it; GT clips to the object surface so clip here too
        using (ctx.PushClip(bounds))
            ctx.DrawImage(bmp, srcRect, dest);
    }

    /// <summary>destination rect for the whole bitmap inside <paramref name="box"/>, mirroring GT's ImageObject layout: Normal and TopRight draw 1:1 anchored left/right, Centered scales uniformly to fit (letterbox/pillarbox), Stretch fills the box and is also the fallback</summary>
    private static Rect SizeModeDestination(GtImageSizeMode mode, Rect box, Size image)
    {
        double w = box.Width, h = box.Height;
        double bw = image.Width, bh = image.Height;
        if (w <= 0 || h <= 0 || bw <= 0 || bh <= 0) return default;

        switch (mode)
        {
            case GtImageSizeMode.Normal:
                return new Rect(box.X, box.Y, bw, bh);

            case GtImageSizeMode.TopRight:
                return new Rect(box.X + w - bw, box.Y, bw, bh);

            case GtImageSizeMode.Centered:
            {
                double scale = Math.Min(w / bw, h / bh);
                double dw = bw * scale, dh = bh * scale;
                return new Rect(box.X + (w - dw) / 2, box.Y + (h - dh) / 2, dw, dh);
            }

            default:  // Stretch, and any unknown value
                return box;
        }
    }

    /// <summary>draws the newest frame the element's live page has sent, stretched to its box. Nothing is drawn while exporting: an export has to match what vMix will play, and vMix only sees the empty carrier rectangle the page is saved as</summary>
    private void RenderWeb(DrawingContext ctx, GtWebElement web, Rect bounds)
    {
        if (_exportMode) return;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var view = _webPreviews?.Ensure(web, new PixelSize(
            Math.Clamp((int)Math.Round(bounds.Width),  16, 4096),
            Math.Clamp((int)Math.Round(bounds.Height), 16, 4096)));

        if (view?.Frame is { } bmp)
        {
            var srcRect = new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height);
            using (ctx.PushClip(bounds))
                ctx.DrawImage(bmp, srcRect, bounds);

            // the page is taking input, so it gets a frame saying so - clicks over it are not going to select or move the box
            if (web.Interactive && !IsPreviewing)
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(
                    ReferenceEquals(_webFocus, web) ? Color.FromArgb(230, 120, 200, 120)
                                                    : Color.FromArgb(120, 120, 200, 120)),
                    1.0 / Math.Max(Zoom, 0.0001)), bounds);
            return;
        }

        RenderWebPlaceholder(ctx, web, bounds, view);
    }

    /// <summary>the box a web element occupies before its page has sent a picture: a dashed frame plus whatever it is waiting on</summary>
    private void RenderWebPlaceholder(DrawingContext ctx, GtWebElement web, Rect bounds,
                                      WebPreviewService.WebView? view)
    {
        double scale = 1.0 / Math.Max(Zoom, 0.0001);

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 110, 150, 200)), 1.0 * scale)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
        };
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, 110, 150, 200)), pen, bounds);

        string label =
            view?.Error is { } err                ? err
            : string.IsNullOrWhiteSpace(web.Url)  ? "Web page - set a URL in the properties bar"
            : view?.Starting == true              ? "Starting browser…"
            : web.Url;

        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                     new Typeface("Segoe UI"), 11,
                                     new SolidColorBrush(Color.FromArgb(230, 210, 225, 245)));
        text.MaxTextWidth = Math.Max(20, bounds.Width / scale - 12);
        text.MaxLineCount = 2;

        var origin = new Point(bounds.X + (bounds.Width  - text.Width  * scale) / 2,
                               bounds.Y + (bounds.Height - text.Height * scale) / 2);

        using (ctx.PushClip(bounds))
        using (ctx.PushTransform(Matrix.CreateScale(scale, scale) *
                                 Matrix.CreateTranslation(origin.X, origin.Y)))
            ctx.DrawText(text, new Point(0, 0));
    }

    /// <summary>line box height and baseline offset for one laid-out line, mirroring the single <c>LineSpacing</c> float GT stores which carries three different meanings depending on its magnitude (D2D1Text.CreateTextFormat calls SetLineSpacing with the same value for both the height and the baseline argument): &lt;= 0 is the sentinel where SetLineSpacing is never called so DWrite uses the font metrics (ascent + descent + lineGap) untouched; 0 &lt; v &lt;= 2 is proportional, a multiplier of the font-computed line height and of the baseline so the extra leading lands above every line including the first, which is why raising line spacing also pushes the block down; v &gt; 2 is uniform, an absolute line height in DIPs with baseline at the bottom of the box, so 2.001 collapses every line onto the previous one, and the cliff is in the original with no clamping and is reproduced here so saved titles match; <paramref name="fontHeight"/>/<paramref name="fontBaseline"/> are the font's own line box as DirectWrite computes it (see FontMetricsService), and scaling both keeps LineSpacing 1.0 pixel-identical to the 0 sentinel</summary>
    private static (double Height, double Baseline) LineBox(double lineSpacing,
                                                            double fontHeight, double fontBaseline)
    {
        if (lineSpacing <= 0.0) return (fontHeight, fontBaseline);
        if (lineSpacing <= 2.0) return (lineSpacing * fontHeight, lineSpacing * fontBaseline);
        return (lineSpacing, lineSpacing);
    }

    /// <summary>the text properties actually handed to the layout engine for one render; GT coerces alignment and wrapping from <see cref="GtAutoSize"/> on the per-render snapshot only (D2D1Text.CreateTextFormat) never on the stored object, so the toolbar keeps showing what the user set and switching back to Fixed restores it with no data loss</summary>
    private readonly struct TextPlan
    {
        public readonly double          FontSize;
        public readonly GtTextAlign     TextAlign;
        public readonly GtVerticalAlign VerticalAlign;
        public readonly bool            NoWrap;
        /// <summary>maxWidth for the layout engine, infinite in the auto-width modes</summary>
        public readonly double          LayoutWidth;

        public TextPlan(double fontSize, GtTextAlign align, GtVerticalAlign valign,
                        bool noWrap, double layoutWidth)
        {
            FontSize      = fontSize;
            TextAlign     = align;
            VerticalAlign = valign;
            NoWrap        = noWrap;
            LayoutWidth   = layoutWidth;
        }
    }

    /// <summary>applies the format-level coercions the auto-size mode forces at a given font size; vertical alignment is meaningless in a box defined as exactly the text height and horizontal alignment likewise at exactly the text width, so those modes pin the alignment to the edge the box grows from and WidthAndHeight also kills wrapping; GT picks Right rather than Left for a right-to-left reading direction, which is not modelled here</summary>
    private static TextPlan CoerceTextPlan(GtTextBlock tb, Rect bounds, double fontSize)
    {
        var mode   = tb.AutoSize;
        var align  = tb.TextAlign;
        var valign = tb.VerticalAlign;
        var noWrap = tb.NoWrap;

        if (mode is GtAutoSize.Height or GtAutoSize.WidthAndHeight) valign = GtVerticalAlign.Top;
        if (mode is GtAutoSize.Width  or GtAutoSize.WidthAndHeight) align  = GtTextAlign.Left;
        if (mode is GtAutoSize.WidthAndHeight)                      noWrap = true;

        bool autoWidth = mode is GtAutoSize.Width or GtAutoSize.WidthAndHeight;
        double layoutWidth = noWrap || autoWidth
            ? double.PositiveInfinity
            : Math.Max(0, bounds.Width);

        return new TextPlan(fontSize, align, valign, noWrap, layoutWidth);
    }

    /// <summary>the plan for one render: the coercions above plus the Shrink walk; Shrink always restarts from the user's font size since the reduced size is a transient of this layout and is never written back, so deleting text springs the font straight back up</summary>
    private static TextPlan ResolveTextPlan(GtTextBlock tb, string text, Typeface typeface,
                                            Rect bounds)
        => CoerceTextPlan(tb, bounds,
                          tb.AutoSize == GtAutoSize.Shrink
                              ? ShrinkFontSize(tb, text, typeface, bounds)
                              : tb.FontSize);

    /// <summary>GT's Shrink loop: step the font size down in whole points until the ink fits the box, with a floor of 2pt; width overflow only counts when wrapping is off since with wrapping on a long word is allowed to stick out sideways and only the height drives the shrink; no binary search, the 1pt walk is what GT does and a bisection can land on a different size where the fit test flips non-monotonically across a wrap or ligature boundary</summary>
    private static double ShrinkFontSize(GtTextBlock tb, string text, Typeface typeface, Rect bounds)
    {
        // up to FontSize-2 full re-layouts, so the converged size is memoised against every input that can change the fit
        var key = string.Format(CultureInfo.InvariantCulture,
            "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}|{11}",
            tb.FontSize, tb.FontFamily, (int)tb.FontWeight, (int)tb.FontStyle,
            tb.NoWrap, tb.LineSpacing, tb.StrokeThickness, (int)tb.TextAlign,
            (int)tb.VerticalAlign, bounds.Width, bounds.Height, text);

        if (_shrinkCache.TryGetValue(tb, out var cached) && cached.Key == key)
            return cached.Size;

        double size = tb.FontSize;
        while (true)
        {
            var plan = CoerceTextPlan(tb, bounds, size);
            if (LayOutText(text, typeface, tb, plan, bounds, Brushes.White) is not { } laid) break;

            var ink = MeasureInk(laid, tb, bounds, textAreaOnly: true);
            var pad = ShrinkPadding(tb, plan, laid, bounds);

            bool tooWide = plan.NoWrap && ink.Width + pad.X > bounds.Width;
            bool tooTall = ink.Height + pad.Y > bounds.Height;

            if (!(tooWide || tooTall) || size <= 2.0) break;
            size -= 1.0;
        }

        _shrinkCache.Remove(tb);
        _shrinkCache.Add(tb, new ShrinkResult(key, size));
        return size;
    }

    private sealed class ShrinkResult
    {
        public readonly string Key;
        public readonly double Size;
        public ShrinkResult(string key, double size) { Key = key; Size = size; }
    }

    private static readonly ConditionalWeakTable<GtTextBlock, ShrinkResult> _shrinkCache = new();

    /// <summary>one laid-out text block: the lines, where each of them is drawn, and the block nudge that pulls overhanging ink back inside the box</summary>
    private readonly struct LaidOutText
    {
        public readonly IReadOnlyList<TextLine> Lines;
        /// <summary>per-line draw origin in absolute space, before <see cref="Offset"/></summary>
        public readonly Point[]                 Origins;
        public readonly Vector                  Offset;
        /// <summary>resolved line box height, which LineSpacing can divorce from the font's</summary>
        public readonly double                  LineHeight;
        public readonly double                  BlockTop;

        public LaidOutText(IReadOnlyList<TextLine> lines, Point[] origins,
                           Vector offset, double lineHeight, double blockTop)
        {
            Lines      = lines;
            Origins    = origins;
            Offset     = offset;
            LineHeight = lineHeight;
            BlockTop   = blockTop;
        }
    }

    /// <summary>lays the text out with Avalonia doing the line breaking only; alignment is left inside the layout so every line reports Start = 0, horizontal placement is done per line here and vertical placement per line box by <see cref="LineBox"/>; returns null when the text produces no lines at all</summary>
    private static LaidOutText? LayOutText(string text, Typeface typeface, GtTextBlock tb,
                                           in TextPlan plan, Rect bounds, IBrush brush)
    {
        var layout = new TextLayout(text, typeface, plan.FontSize, brush,
                                    textWrapping: plan.NoWrap ? TextWrapping.NoWrap : TextWrapping.Wrap,
                                    maxWidth: plan.LayoutWidth);
        var lines = layout.TextLines;
        if (lines.Count == 0) return null;

        // Avalonia derives its line box from hhea, DirectWrite (so GT) from the OS/2 win metrics with the line gap above the baseline; on fonts where the two disagree the block ends up vertically off, so prefer the DWrite-shaped metrics and fall back to Avalonia's when the font tables cannot be read
        var font            = FontMetricsService.ForTypeface(typeface, plan.FontSize);
        double fontHeight   = font?.Height   ?? lines[0].Height;
        double fontBaseline = font?.Baseline ?? lines[0].Baseline;

        var box = LineBox(tb.LineSpacing, fontHeight, fontBaseline);

        // total height first, vertical alignment works on the line boxes LineSpacing asks for not on the natural text height
        double totalHeight = box.Height * lines.Count;

        double blockTop = plan.VerticalAlign switch
        {
            GtVerticalAlign.Center => bounds.Y + (bounds.Height - totalHeight) / 2,
            GtVerticalAlign.Bottom => bounds.Y + bounds.Height - totalHeight,
            _                      => bounds.Y  // top
        };

        // TextLine.Draw takes the top-left of the line and puts the glyph baseline line.Baseline below it, so shifting the origin by (wanted - natural) baseline is what moves the glyphs inside their line box
        var origins = new Point[lines.Count];
        double boxTop = blockTop;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            double x = plan.TextAlign switch
            {
                GtTextAlign.Center => bounds.X + (bounds.Width - line.Width) / 2,
                GtTextAlign.Right  => bounds.X + bounds.Width - line.Width,
                _                  => bounds.X
            };
            origins[i] = new Point(x, boxTop + box.Baseline - line.Baseline);
            boxTop += box.Height;
        }

        var laid = new LaidOutText(lines, origins, default, box.Height, blockTop);
        return new LaidOutText(lines, origins, OverhangOffset(tb, plan, laid, bounds),
                               box.Height, blockTop);
    }

    private void RenderTextBlock(DrawingContext ctx, GtTextBlock tb, Rect bounds)
    {
        if (string.IsNullOrEmpty(tb.Text)) return;

        var text      = tb.Uppercase ? tb.Text.ToUpper() : tb.Text;
        var typeface  = new Typeface(new FontFamily(tb.FontFamily), tb.FontStyle, tb.FontWeight);
        var fillBrush = MakeBrush(tb.Fill) ?? new SolidColorBrush(Colors.White);

        var plan = ResolveTextPlan(tb, text, typeface, bounds);
        if (LayOutText(text, typeface, tb, plan, bounds, fillBrush) is not { } laid) return;

        // the block is nudged by half the stroke width, as GT's DrawGlyphRun does, so a stroked glyph keeps its outer half instead of losing it off the top-left of the surface
        var shift = laid.Offset + StrokeOriginShift(tb);

        // Fixed does not clip to the layout box; GT clips to the object's own surface which is the element box, so overflowing text is cut there with no ellipsis
        using (ctx.PushClip(new RoundedRect(bounds)))
        {
            // fill first, then the stroke over the same outline, the order GT draws them in: the inner half of the stroke covers the edge of the fill
            DrawLines(ctx, laid.Lines, laid.Origins, shift);

            if (tb.Stroke is not null && tb.StrokeThickness > 0 &&
                MakePen(tb.Stroke, tb.StrokeThickness) is { } pen &&
                BuildGlyphGeometry(text, typeface, plan, laid, shift) is { } glyphs)
            {
                ctx.DrawGeometry(null, pen, glyphs);
            }
        }
    }

    /// <summary>draws the ticker's scrolling clones; the ticker never renders its own text, the template text is split into chunks, each chunk is laid out as its own text object with the ticker's font properties, and <see cref="TickerLayout"/> says where each one currently sits; the ticker's box clips the run, which is what GT gets from rendering its child composition into a render target its own size</summary>
    private void RenderTicker(DrawingContext ctx, GtTickerElement ticker, Rect bounds)
    {
        bool   vertical     = ticker.IsVertical;
        double screenLength = vertical ? bounds.Height : bounds.Width;
        if (screenLength <= 0) return;

        var chunks  = TickerLayout.Split(ticker.Text, vertical);
        var lengths = new double[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
            lengths[i] = MeasureChunk(ticker, chunks[i], bounds, vertical);

        // Speed is pixels per frame; a storyboard preview owns the clock while it is applied (which is also what makes the video export come out right) otherwise the frame comes from the ticker transport; frame 0 draws the rest layout so a stopped ticker can be positioned instead of sitting empty offscreen
        int frame = _animationFrame is not null
            ? (int)Math.Round(_animationFrame.Time * TickerLayout.Fps)
            : _tickerFrame;

        var placements = frame <= 0
            ? TickerLayout.Rest(lengths, screenLength)
            : TickerLayout.Simulate(lengths, screenLength, ticker.Speed, ticker.Direction,
                                    ticker.TickerType, frame);

        using (ctx.PushClip(new RoundedRect(bounds)))
        {
            foreach (var placement in placements)
            {
                double length = lengths[placement.Index];
                var chunkBounds = vertical
                    ? new Rect(bounds.X, bounds.Y + placement.Position, bounds.Width, length)
                    : new Rect(bounds.X + placement.Position, bounds.Y, length, bounds.Height);
                RenderTextBlock(ctx, ticker.CreateChunk(chunks[placement.Index]), chunkBounds);
            }
        }
    }

    /// <summary>size of one chunk along the scroll axis: the autosized dimension of GT's FormatTextObject, width for a horizontal ticker and height for a vertical one</summary>
    private static double MeasureChunk(GtTickerElement ticker, string text, Rect bounds, bool vertical)
    {
        var chunk    = ticker.CreateChunk(text);
        var body     = chunk.Uppercase ? chunk.Text.ToUpper() : chunk.Text;
        var typeface = new Typeface(new FontFamily(chunk.FontFamily), chunk.FontStyle, chunk.FontWeight);
        var plan     = ResolveTextPlan(chunk, body, typeface, bounds);
        if (LayOutText(body, typeface, chunk, plan, bounds, Brushes.White) is not { } laid)
            return 0;

        // the separator whitespace is the only thing holding consecutive chunks apart, so the trailing space has to count towards the length
        if (!vertical)
        {
            double width = 0;
            foreach (var line in laid.Lines)
                width = Math.Max(width, line.WidthIncludingTrailingWhitespace);
            return width;
        }

        return laid.LineHeight * laid.Lines.Count;
    }

    private static void DrawLines(DrawingContext ctx, IReadOnlyList<TextLine> lines,
                                  Point[] origins, Vector offset)
    {
        int count = Math.Min(lines.Count, origins.Length);
        for (int i = 0; i < count; i++)
            lines[i].Draw(ctx, origins[i] + offset);
    }

    /// <summary>absolute bounding box of the painted glyphs unioned over every line, GT's ink box which it gets by running the draw through a test renderer that turns each glyph run into geometry instead of pixels; false when no line reports usable ink metrics</summary>
    private static bool InkBounds(in LaidOutText laid, out Rect ink)
    {
        double top = double.MaxValue, bottom = double.MinValue;
        double left = double.MaxValue, right = double.MinValue;

        int count = Math.Min(laid.Lines.Count, laid.Origins.Length);
        for (int i = 0; i < count; i++)
        {
            var line = laid.Lines[i];
            // ink metrics are reported relative to the line box: OverhangAfter is how far the ink reaches past the bottom edge, Extent is the ink height
            if (double.IsNaN(line.Extent) || double.IsInfinity(line.Extent)) { ink = default; return false; }
            double lineBottom = laid.Origins[i].Y + line.Height + line.OverhangAfter;
            bottom = Math.Max(bottom, lineBottom);
            top    = Math.Min(top,    lineBottom - line.Extent);
            left   = Math.Min(left,   laid.Origins[i].X - line.OverhangLeading);
            right  = Math.Max(right,  laid.Origins[i].X + line.Width + line.OverhangTrailing);
        }

        if (top > bottom || left > right) { ink = default; return false; }
        ink = new Rect(left, top, right - left, bottom - top);
        return true;
    }

    /// <summary>how far the ink escapes the box on each edge, positive outwards; only the edge the text is aligned to is meaningful, and GT reads the same four numbers for both the draw nudge and the Shrink loop's padding which are mirror images of each other</summary>
    private static (double Top, double Bottom, double Left, double Right)
        Overhangs(in Rect ink, Rect bounds)
        => (bounds.Y - ink.Y, ink.Bottom - bounds.Bottom,
            bounds.X - ink.X, ink.Right - bounds.Right);

    /// <summary>the block nudge GT applies after layout (D2D1Text.CalculateOverhang, added to the draw origin): top-aligned text moves down by however far the ink escapes above the box, bottom-aligned text moves up by the ink below it, and the same pattern applies horizontally for left/right alignment; IgnoreOverhang applies the correction unconditionally including the negative direction, which pulls the ink flush with the box edge instead of leaving it inside; line spacing feeds straight into this since it moves the glyphs inside their line boxes which changes the overhang which moves the block again, so the property does not look linear on screen; the alignment read here is the auto-size-coerced one, matching the format GT hands the layout</summary>
    private static Vector OverhangOffset(GtTextBlock tb, in TextPlan plan,
                                         in LaidOutText laid, Rect bounds)
    {
        if (!InkBounds(laid, out var ink)) return default;
        var over = Overhangs(ink, bounds);

        double dx = 0, dy = 0;

        switch (plan.VerticalAlign)
        {
            case GtVerticalAlign.Top:
                if (tb.IgnoreOverhang || over.Top > 0) dy = over.Top;
                break;
            case GtVerticalAlign.Bottom:
                if (tb.IgnoreOverhang || over.Bottom > 0) dy = -over.Bottom;
                break;
        }

        switch (plan.TextAlign)
        {
            case GtTextAlign.Left:
                if (tb.IgnoreOverhang || over.Left > 0) dx = over.Left;
                break;
            case GtTextAlign.Right:
                if (tb.IgnoreOverhang || over.Right > 0) dx = -over.Right;
                break;
        }

        return new Vector(dx, dy);
    }

    /// <summary>GT's GetPadding, the mirror of the overhang nudge and used only by the Shrink loop: on the aligned edge when the ink sits inside the box, the gap counts as part of the measured size; that is what makes the loop compare like for like against the box, since it measures the ink extent which knows nothing about where the block starts</summary>
    private static Vector ShrinkPadding(GtTextBlock tb, in TextPlan plan,
                                        in LaidOutText laid, Rect bounds)
    {
        if (!InkBounds(laid, out var ink)) return default;
        var over = Overhangs(ink, bounds);

        double px = plan.TextAlign switch
        {
            GtTextAlign.Left  => Math.Max(0, -over.Left),
            GtTextAlign.Right => Math.Max(0, -over.Right),
            _                 => 0
        };
        double py = plan.VerticalAlign switch
        {
            GtVerticalAlign.Top    => Math.Max(0, -over.Top),
            GtVerticalAlign.Bottom => Math.Max(0, -over.Bottom),
            _                      => 0
        };

        return new Vector(px, py);
    }

    /// <summary>GT's ink measurement (DWriteTextRenderer.EndTest); <paramref name="textAreaOnly"/> true gives the origin-independent ink extent, false gives the distance from the box origin to the far ink edge so leading whitespace and indent count towards the size; negatives clamp to zero, the full stroke width is added back (the ink box is of the unstroked outline), and the result is truncated and bumped by one, the slack that keeps antialiased edges and stroke tips off the surface edge</summary>
    private static Size MeasureInk(in LaidOutText laid, GtTextBlock tb, Rect bounds,
                                   bool textAreaOnly)
    {
        double w = 0, h = 0;
        if (InkBounds(laid, out var ink))
        {
            if (textAreaOnly)
            {
                w = ink.Width;
                h = ink.Height;
            }
            else
            {
                // the measure pass draws through the same overhang offset as the real render
                var drawn = ink.Translate(laid.Offset);
                w = drawn.Right  - bounds.X;
                h = drawn.Bottom - bounds.Y;
            }
        }

        if (w < 0) w = 0;
        if (h < 0) h = 0;

        if (tb.StrokeThickness > 0)
        {
            w += tb.StrokeThickness;
            h += tb.StrokeThickness;
        }

        return new Size(Math.Truncate(w) + 1, Math.Truncate(h) + 1);
    }

    /// <summary>the layout's own metrics box; trailing whitespace counts towards the width which is why typing "Name " into an auto-width box makes it wider than "Name", and blank lines and trailing spaces carry no ink at all so this is the half of the measurement that keeps them alive</summary>
    private static Size MeasureMetrics(in LaidOutText laid, Rect bounds, bool textAreaOnly)
    {
        double w = 0;
        int count = Math.Min(laid.Lines.Count, laid.Origins.Length);
        for (int i = 0; i < count; i++)
        {
            double lead = textAreaOnly ? 0 : laid.Origins[i].X - bounds.X;
            w = Math.Max(w, laid.Lines[i].WidthIncludingTrailingWhitespace + lead);
        }

        double h = laid.LineHeight * laid.Lines.Count
                 + (textAreaOnly ? 0 : laid.BlockTop - bounds.Y);

        return new Size(Math.Max(0, w), Math.Max(0, h));
    }

    /// <summary>GT's CalculateDimensions: the component-wise max of the ink box and the metrics box; both are needed since ink alone collapses trailing whitespace and empty lines while metrics alone clips stroked, italic and swash glyphs that paint outside their advance box</summary>
    private static Size MeasureText(in LaidOutText laid, GtTextBlock tb, Rect bounds,
                                    bool textAreaOnly)
    {
        var ink     = MeasureInk(laid, tb, bounds, textAreaOnly);
        var metrics = MeasureMetrics(laid, bounds, textAreaOnly);
        return new Size(Math.Max(ink.Width, metrics.Width),
                        Math.Max(ink.Height, metrics.Height));
    }

    /// <summary>the box an auto-sized text object wants after GT's LimitAndRoundDimensions: truncate towards zero, add one, clamp to the 8192 max texture size; this is the second truncate and +1 on the ink path, and the couple of pixels of slack it leaves is the padding that stops the surface clipping the glyphs, not a rounding bug to tidy up</summary>
    private static Size MeasureAutoDimensions(GtTextBlock tb)
    {
        var bounds   = new Rect(0, 0, tb.Dimensions.Width, tb.Dimensions.Height);
        var text     = tb.Uppercase ? tb.Text.ToUpper() : tb.Text;
        var typeface = new Typeface(new FontFamily(tb.FontFamily), tb.FontStyle, tb.FontWeight);
        var plan     = ResolveTextPlan(tb, text, typeface, bounds);

        if (LayOutText(text, typeface, tb, plan, bounds, Brushes.White) is not { } laid)
            return new Size(tb.Dimensions.Width, tb.Dimensions.Height);

        var measured = MeasureText(laid, tb, bounds, textAreaOnly: false);
        return new Size(Math.Min(8192, Math.Truncate(measured.Width)  + 1),
                        Math.Min(8192, Math.Truncate(measured.Height) + 1));
    }

    /// <summary>runs GT's UpdateTextDimensions over the document: the auto-size modes that grow the box write the measured size back onto the element, Fixed and Shrink never touch it; the box grows away from the element's anchor point which the file's Location names, so on the default top-left anchor that is right and down only and a centred auto-width box keeps its left edge rather than its centre, which is what GT does and is user-visible; the inequality tests are the recursion guard since Dimensions feeds the layout that produced the measurement so writing it unconditionally never settles, but because the value is quantised by truncate-and-one it reaches a fixed point immediately; returns true when anything moved so the caller can refresh whatever was showing the old numbers</summary>
    public bool ApplyAutoSize()
    {
        var doc = _document;
        if (doc is null) return false;

        bool changed = false;
        foreach (var layer in doc.Layers)
        foreach (var element in layer.Elements)
        {
            // a Ticker is a TextObject base in GT, which is where auto-size does not live: it forces its own mode on the clones it scrolls and never resizes itself
            if (element is not GtTextBlock tb || element is GtTickerElement) continue;
            if (tb.AutoSize is not (GtAutoSize.Width or GtAutoSize.Height or GtAutoSize.WidthAndHeight))
                continue;

            var measured = MeasureAutoDimensions(tb);
            var current  = tb.Dimensions;

            var wanted = tb.AutoSize switch
            {
                GtAutoSize.Width  => new GtSize(measured.Width, current.Height),
                GtAutoSize.Height => new GtSize(current.Width,  measured.Height),
                _                 => new GtSize(measured.Width, measured.Height)
            };

            if (wanted.Width == current.Width && wanted.Height == current.Height) continue;

            // Depth is a separate property so it survives untouched; Location moves only for a non-default anchor, keeping the anchor point where the file put it
            tb.SetWidthAboutAnchor(wanted.Width);
            tb.SetHeightAboutAnchor(wanted.Height);
            changed = true;
        }

        return changed;
    }

    private IBrush? MakeBrush(GtBrush? brush)
    {
        if (brush is null) return null;
        return brush.Type switch
        {
            GtBrushType.LinearGradient => MakeLinearGradient(brush),
            GtBrushType.RadialGradient => MakeRadialGradient(brush),
            GtBrushType.Bitmap => (IBrush?)MakeBitmapBrush(brush) ?? new SolidColorBrush(brush.Color),
            _ => new SolidColorBrush(brush.Color)
        };
    }

    private IPen? MakePen(GtBrush? brush, double thickness = 1.0)
    {
        var b = MakeBrush(brush);
        return b is null ? null : new Pen(b, thickness);
    }

    private static LinearGradientBrush MakeLinearGradient(GtBrush brush)
    {
        var lgb = new LinearGradientBrush
        {
            StartPoint   = new RelativePoint(brush.StartPoint.X, brush.StartPoint.Y, RelativeUnit.Relative),
            EndPoint     = new RelativePoint(brush.EndPoint.X,   brush.EndPoint.Y,   RelativeUnit.Relative),
            SpreadMethod = BrushPreview.ToSpread(brush.WrapMode),
        };
        foreach (var stop in brush.Stops)
            lgb.GradientStops.Add(new GradientStop(stop.Color, stop.Position));
        return lgb;
    }

    private static RadialGradientBrush MakeRadialGradient(GtBrush brush,
        GradientSpreadMethod spread = GradientSpreadMethod.Pad)
    {
        // stop 0 radiates from element center outward, StartPoint/EndPoint not used
        var center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        var rgb = new RadialGradientBrush
        {
            Center         = center,
            GradientOrigin = center,
            RadiusX        = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY        = new RelativeScalar(0.5, RelativeUnit.Relative),
            SpreadMethod   = spread,
        };
        foreach (var stop in brush.Stops)
            rgb.GradientStops.Add(new GradientStop(stop.Color, stop.Position));
        return rgb;
    }

    private ImageBrush? MakeBitmapBrush(GtBrush brush)
    {
        if (brush.BitmapSource is null) return null;
        var bmp = GetBitmap(brush.BitmapSource);
        return bmp is null ? null : new ImageBrush(bmp) { Stretch = Stretch.Fill };
    }

    private Bitmap? GetBitmap(string logicalPath)
    {
        // sequence frames other than the anchor go in the evictable cache; anchors and ordinary images stay cached for the lifetime of the document
        bool evictable = !_assets.Sequences.ContainsKey(logicalPath) && _assets.IsSequenceFrame(logicalPath);
        var cache = evictable ? _sequenceCache : _bitmapCache;

        if (cache.TryGetValue(logicalPath, out var cached)) return cached;

        Bitmap? result = null;
        if (_assets.TryGetValue(logicalPath, out var bytes))
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                result = new Bitmap(ms);
            }
            catch { }
        }

        cache[logicalPath] = result;
        if (evictable) TrimSequenceCache(logicalPath);
        return result;
    }

    private void TrimSequenceCache(string justAdded)
    {
        _sequenceCacheOrder.Enqueue(justAdded);
        while (_sequenceCacheOrder.Count > SequenceCacheLimit)
        {
            var oldest = _sequenceCacheOrder.Dequeue();
            if (_sequenceCache.Remove(oldest, out var bmp)) bmp?.Dispose();
        }
    }

    private void ClearSequenceCache()
    {
        foreach (var bmp in _sequenceCache.Values) bmp?.Dispose();
        _sequenceCache.Clear();
        _sequenceCacheOrder.Clear();
    }

    private static Color GetPrimaryColor(GtBrush? brush)
    {
        if (brush is null) return Colors.White;
        if (brush.Type == GtBrushType.Solid) return brush.Color;
        return brush.Stops.Count > 0 ? brush.Stops[0].Color : brush.Color;
    }

    /// <summary>custom Skia draw operation that renders a pre-rasterised element bitmap into a perspective-projected quadrilateral using a homography matrix; the SKImage is owned by this op and disposed when Avalonia disposes the op</summary>
    private sealed class PerspectiveBitmapOp : ICustomDrawOperation
    {
        private readonly SKImage _img;
        private readonly int _pw, _ph;
        private readonly SKPoint[] _quad; // TL, TR, BR, BL in layer-local doc coords
        private readonly float _opacity;

        public PerspectiveBitmapOp(SKImage img, int pw, int ph, SKPoint[] quad, float opacity)
        {
            _img = img;
            _pw = pw; _ph = ph;
            _quad = quad;
            _opacity = opacity;
        }

        public Rect Bounds
        {
            get
            {
                float minX = Math.Min(Math.Min(_quad[0].X, _quad[1].X), Math.Min(_quad[2].X, _quad[3].X));
                float minY = Math.Min(Math.Min(_quad[0].Y, _quad[1].Y), Math.Min(_quad[2].Y, _quad[3].Y));
                float maxX = Math.Max(Math.Max(_quad[0].X, _quad[1].X), Math.Max(_quad[2].X, _quad[3].X));
                float maxY = Math.Max(Math.Max(_quad[0].Y, _quad[1].Y), Math.Max(_quad[2].Y, _quad[3].Y));
                return new Rect(minX, minY, maxX - minX, maxY - minY);
            }
        }

        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Dispose() => _img.Dispose();

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } leaseFeature)
                return;
            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            // compute homography: image pixel (0..pw, 0..ph) to layer-local _quad corners
            if (!TryComputeHomography(out var homography))
                return;

            // compose with the current canvas transform (layer-local to screen)
            var finalMatrix = SKMatrix.Concat(canvas.TotalMatrix, homography);

            canvas.Save();
            try
            {
                canvas.SetMatrix(finalMatrix);
                using var paint = new SKPaint
                {
                    Color         = SKColors.White.WithAlpha((byte)Math.Clamp((int)(_opacity * 255f), 0, 255)),
                    FilterQuality = SKFilterQuality.High,
                    IsAntialias   = true,
                };
                canvas.DrawImage(_img, 0f, 0f, paint);
            }
            finally
            {
                canvas.Restore();
            }
        }

        /// <summary>solves the 8-parameter homography H that maps the source image rect (0,0) to (_pw,_ph) onto the destination quad _quad[0..3] (TL,TR,BR,BL)</summary>
        private bool TryComputeHomography(out SKMatrix matrix)
        {
            // source corners: TL, TR, BR, BL
            float[] srcX = { 0f,   _pw, _pw, 0f   };
            float[] srcY = { 0f,   0f,  _ph, _ph  };
            float[] dstX = { _quad[0].X, _quad[1].X, _quad[2].X, _quad[3].X };
            float[] dstY = { _quad[0].Y, _quad[1].Y, _quad[2].Y, _quad[3].Y };

            // build 8×8 linear system with unknowns h = [h00,h01,h02, h10,h11,h12, h20,h21]; for each point pair (xi,yi) to (xi',yi'): xi' = (h00*xi + h01*yi + h02) / (h20*xi + h21*yi + 1), yi' = (h10*xi + h11*yi + h12) / (h20*xi + h21*yi + 1); rearranging gives two linear rows per point
            double[,] A = new double[8, 8];
            double[]  b = new double[8];
            for (int i = 0; i < 4; i++)
            {
                double xi = srcX[i], yi = srcY[i], xp = dstX[i], yp = dstY[i];
                int r1 = i * 2, r2 = r1 + 1;
                // x' equation
                A[r1, 0] = xi;  A[r1, 1] = yi;  A[r1, 2] = 1;
                A[r1, 3] = 0;   A[r1, 4] = 0;   A[r1, 5] = 0;
                A[r1, 6] = -xi * xp; A[r1, 7] = -yi * xp;
                b[r1] = xp;
                // y' equation
                A[r2, 0] = 0;   A[r2, 1] = 0;   A[r2, 2] = 0;
                A[r2, 3] = xi;  A[r2, 4] = yi;  A[r2, 5] = 1;
                A[r2, 6] = -xi * yp; A[r2, 7] = -yi * yp;
                b[r2] = yp;
            }

            if (!GaussianElim(A, b, out var h))
            {
                matrix = SKMatrix.Identity;
                return false;
            }

            // h = [h00,h01,h02, h10,h11,h12, h20,h21], h22 = 1
            matrix = new SKMatrix
            {
                ScaleX = (float)h[0], SkewX  = (float)h[1], TransX = (float)h[2],
                SkewY  = (float)h[3], ScaleY = (float)h[4], TransY = (float)h[5],
                Persp0 = (float)h[6], Persp1 = (float)h[7], Persp2 = 1f,
            };
            return true;
        }

        private static bool GaussianElim(double[,] A, double[] b, out double[] x)
        {
            int n = b.Length;
            x = new double[n];
            // build augmented matrix [A|b]
            double[,] M = new double[n, n + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) M[i, j] = A[i, j];
                M[i, n] = b[i];
            }

            for (int col = 0; col < n; col++)
            {
                // partial pivot
                int pivot = col;
                for (int row = col + 1; row < n; row++)
                    if (Math.Abs(M[row, col]) > Math.Abs(M[pivot, col])) pivot = row;
                if (Math.Abs(M[pivot, col]) < 1e-10) return false;
                // swap rows
                for (int j = 0; j <= n; j++) { double t = M[col, j]; M[col, j] = M[pivot, j]; M[pivot, j] = t; }
                // eliminate below
                for (int row = col + 1; row < n; row++)
                {
                    double f = M[row, col] / M[col, col];
                    for (int j = col; j <= n; j++) M[row, j] -= f * M[col, j];
                }
            }

            // back-substitution
            for (int i = n - 1; i >= 0; i--)
            {
                x[i] = M[i, n];
                for (int j = i + 1; j < n; j++) x[i] -= M[i, j] * x[j];
                x[i] /= M[i, i];
            }
            return true;
        }
    }
}
