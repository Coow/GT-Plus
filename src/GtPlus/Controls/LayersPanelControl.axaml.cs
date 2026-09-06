using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GtPlus.Models;
using GtPlus.Services;

namespace GtPlus.Controls;

public partial class LayersPanelControl : UserControl
{
    private GtCanvasControl? _canvas;
    private GtDocument?      _doc;

    public HistoryService? History { get; set; }

    private enum RowKind { Layer, Element }
    private record RowMeta(RowKind Kind, GtLayer Layer, GtElement? Element, Border Border, TextBlock NameBlock);
    private readonly List<RowMeta> _allRows = new List<RowMeta>();

    // drag state
    private RowMeta?        _dragging;           // primary pressed row
    private List<RowMeta>   _dragSet = new List<RowMeta>(); // logical rows moving (for element multi-select)
    private List<Control>   _dragGroupControls = new List<Control>(); // all Controls moving (layer includes its elements)
    private bool            _isDragging;
    private Point           _dragStartPt;
    private Border?         _dropIndicator;
    private GtElement?      _pendingSelectionApply;

    // icon paint-drag state (click an eye/lock icon and sweep to toggle several rows)
    private enum IconKind { Visibility, Lock }
    private record IconBtn(IconKind Kind, Button Button, GtLayer Layer, GtElement? Element);
    private readonly List<IconBtn> _iconButtons = new List<IconBtn>();

    private IconKind? _paintKind;                                        // null = no gesture running
    private bool      _paintValue;                                       // value being painted onto rows
    private readonly HashSet<object> _paintApplied = new HashSet<object>(); // layers/elements already hit
    private readonly List<IconBtn>   _paintChanged = new List<IconBtn>();   // rows actually flipped (for undo)

    public LayersPanelControl()
    {
        InitializeComponent();

        // press is taken on the tunnel pass, Button's own class handler marks the bubbling event handled so a plain `btn.PointerPressed +=` would never fire; tunnelling also keeps the press away from the row drag-reorder handlers
        LayersStack.AddHandler(PointerPressedEvent, OnPanelPointerPressed,
                               RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        LayersStack.AddHandler(PointerMovedEvent,       OnPanelPointerMoved,    handledEventsToo: true);
        LayersStack.AddHandler(PointerReleasedEvent,    OnPanelPointerReleased, handledEventsToo: true);
        LayersStack.AddHandler(PointerCaptureLostEvent, OnPanelCaptureLost,     handledEventsToo: true);
    }

    public GtCanvasControl? Canvas
    {
        get => _canvas;
        set
        {
            if (_canvas is not null)
                _canvas.SelectionChanged -= OnCanvasSelectionChanged;
            _canvas = value;
            if (_canvas is not null)
                _canvas.SelectionChanged += OnCanvasSelectionChanged;
        }
    }

    public void Populate(GtDocument? doc)
    {
        _doc = doc;
        LayersStack.Children.Clear();
        _allRows.Clear();
        _iconButtons.Clear();
        CancelPaint();
        CancelRename();
        _dropIndicator         = null;
        _isDragging            = false;
        _dragging              = null;
        _dragSet               = new List<RowMeta>();
        _dragGroupControls     = new List<Control>();
        _pendingSelectionApply = null;

        if (doc is null) return;

        // show top layer first (reverse render order)
        for (int li = doc.Layers.Count - 1; li >= 0; li--)
        {
            var layer       = doc.Layers[li];
            var layerBorder = BuildLayerHeader(layer, out var layerNameBlock);
            _allRows.Add(new RowMeta(RowKind.Layer, layer, null, layerBorder, layerNameBlock));
            LayersStack.Children.Add(layerBorder);
            AttachDragHandlers(layerBorder);

            // elements in reverse render order
            for (int ei = layer.Elements.Count - 1; ei >= 0; ei--)
            {
                var el  = layer.Elements[ei];
                var row = BuildElementRow(layer, el, out var elNameBlock);
                _allRows.Add(new RowMeta(RowKind.Element, layer, el, row, elNameBlock));
                LayersStack.Children.Add(row);
                AttachDragHandlers(row);
            }
        }

        // sync selection highlight after rebuild
        OnCanvasSelectionChanged(null, EventArgs.Empty);
    }

    private void AttachDragHandlers(Border border)
    {
        border.PointerPressed  += OnRowPointerPressed;
        border.PointerMoved    += OnRowPointerMoved;
        border.PointerReleased += OnRowPointerReleased;
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border) return;
        if (e.Source is Button or TextBox) return;
        if (_renameBox is not null) return;

        var meta = _allRows.FirstOrDefault(r => r.Border == border);
        if (meta is null) return;

        SetFocusRow(meta.Layer, meta.Element);

        // right-click opens the context menu, never starts a reorder drag
        if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;

        _dragging    = meta;
        _dragStartPt = e.GetPosition(LayersStack);
        _isDragging  = false;

        if (meta.Kind == RowKind.Element)
        {
            var selected    = _canvas?.SelectedElements;
            bool inMultiSel = selected is not null
                && selected.Contains(meta.Element)
                && selected.Count > 1;

            _dragSet = inMultiSel
                ? _allRows.Where(r => r.Kind == RowKind.Element && selected!.Contains(r.Element)).ToList()
                : new List<RowMeta> { meta };

            _dragGroupControls = _dragSet.Select(m => (Control)m.Border).ToList();
        }
        else // layer, move header and all of its element rows as one group
        {
            _dragSet = new List<RowMeta> { meta };
            var dragLayer = meta.Layer;
            _dragGroupControls = _allRows
                .Where(r => (r.Kind == RowKind.Layer  && r.Layer == dragLayer) ||
                            (r.Kind == RowKind.Element && r.Layer == dragLayer))
                .Select(r => (Control)r.Border)
                .ToList();
        }

        e.Pointer.Capture(border);
    }

    private void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is null) return;

        var pt = e.GetPosition(LayersStack);
        if (!_isDragging && Math.Abs(pt.Y - _dragStartPt.Y) < 6) return;

        if (!_isDragging)
        {
            _isDragging = true;
            foreach (var c in _dragGroupControls) c.Opacity = 0.4;
            _dropIndicator = new Border
            {
                Height     = 2,
                Background = new SolidColorBrush(Color.Parse("#4a90e2")),
                Margin     = new Thickness(4, 0, 4, 0),
            };
        }

        UpdateDropIndicator(pt.Y);
        e.Handled = true;
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging is null) return;
        e.Pointer.Capture(null);

        foreach (var c in _dragGroupControls) c.Opacity = 1.0;

        if (_isDragging)
        {
            CommitDrop();
        }
        else if (_pendingSelectionApply is not null && _canvas is not null)
        {
            // deferred single-click on multi-selected element
            _canvas.SetSelection(_pendingSelectionApply, false);
        }

        _pendingSelectionApply = null;
        HideDropIndicator();
        _dragging          = null;
        _isDragging        = false;
        _dragSet           = new List<RowMeta>();
        _dragGroupControls = new List<Control>();
    }

    private void UpdateDropIndicator(double pointerY)
    {
        if (_dropIndicator is null || _dragging is null) return;

        var children = LayersStack.Children.Where(c => c != _dropIndicator).ToList();

        double accY     = 0;
        int    insertIdx = children.Count;

        for (int i = 0; i < children.Count; i++)
        {
            var h = children[i].Bounds.Height;
            if (h <= 0) h = 28;
            if (pointerY < accY + h / 2) { insertIdx = i; break; }
            accY += h;
        }

        insertIdx = _dragging.Kind == RowKind.Layer
            ? ClampLayerDropIndex(insertIdx, children)
            : ClampElementDropIndex(insertIdx, children);

        if (LayersStack.Children.Contains(_dropIndicator))
            LayersStack.Children.Remove(_dropIndicator);
        LayersStack.Children.Insert(insertIdx, _dropIndicator);
    }

    // layers may only drop before another layer header or at the very end
    private int ClampLayerDropIndex(int proposed, IList<Control> children)
    {
        int n = children.Count;
        for (int delta = 0; delta <= n; delta++)
        {
            int hi = proposed + delta;
            int lo = proposed - delta;
            if (hi <= n && IsValidLayerPos(hi, children)) return hi;
            if (lo >= 0 && lo != hi && IsValidLayerPos(lo, children)) return lo;
        }
        return 0;
    }

    private bool IsValidLayerPos(int pos, IList<Control> children)
    {
        if (pos == 0 || pos == children.Count) return true;
        var meta = _allRows.FirstOrDefault(r => r.Border == children[pos]);
        return meta?.Kind == RowKind.Layer;
    }

    // elements must sit under a layer header, cannot float above position 1
    private int ClampElementDropIndex(int proposed, IList<Control> children) =>
        Math.Max(1, Math.Min(proposed, children.Count));

    private void HideDropIndicator()
    {
        if (_dropIndicator is not null)
            LayersStack.Children.Remove(_dropIndicator);
    }

    private void CommitDrop()
    {
        if (_doc is null || _dragging is null || _dropIndicator is null) return;
        if (_dragSet.Count == 0) return;

        var allChildren  = LayersStack.Children.ToList();
        int indicatorIdx = allChildren.IndexOf(_dropIndicator);
        if (indicatorIdx < 0) return;

        var draggedControls = new HashSet<Control>(_dragGroupControls);

        // how many dragged controls sit above the indicator in the current visual list
        int draggedAboveIndicator = allChildren.Take(indicatorIdx).Count(c => draggedControls.Contains(c));

        // build final order: remove indicator and all dragged controls
        var final = allChildren.Where(c => c != _dropIndicator && !draggedControls.Contains(c)).ToList();

        // insert position in the stripped list
        int insertAt = Math.Clamp(indicatorIdx - draggedAboveIndicator, 0, final.Count);

        // re-insert dragged controls in their original visual order (header before its elements)
        var orderedDrag = _dragGroupControls
            .OrderBy(c => allChildren.IndexOf(c))
            .ToList();
        for (int i = orderedDrag.Count - 1; i >= 0; i--)
            final.Insert(insertAt, orderedDrag[i]);

        // snapshot before state for undo
        var doc          = _doc;
        var layersBefore = doc.Layers.ToList();
        var elemsBefore  = doc.Layers.ToDictionary(l => l, l => l.Elements.ToList());

        ApplyNewOrder(final);

        var layersAfter = doc.Layers.ToList();
        var elemsAfter  = doc.Layers.ToDictionary(l => l, l => l.Elements.ToList());

        var desc = _dragging.Kind == RowKind.Layer
            ? "Reorder Layers"
            : (_dragSet.Count > 1 ? $"Reorder {_dragSet.Count} Elements" : "Reorder Element");

        History?.Push(new PropertyChangeAction(
            desc,
            undo: () =>
            {
                doc.Layers.Clear();
                foreach (var layer in layersBefore)
                {
                    layer.Elements.Clear();
                    foreach (var el in elemsBefore[layer]) layer.Elements.Add(el);
                    doc.Layers.Add(layer);
                }
                _canvas?.ClearSelection();
                Populate(doc);
                _canvas?.InvalidateVisual();
            },
            redo: () =>
            {
                doc.Layers.Clear();
                foreach (var layer in layersAfter)
                {
                    layer.Elements.Clear();
                    foreach (var el in elemsAfter[layer]) layer.Elements.Add(el);
                    doc.Layers.Add(layer);
                }
                _canvas?.ClearSelection();
                Populate(doc);
                _canvas?.InvalidateVisual();
            }
        ));

        Populate(doc);
        _canvas?.InvalidateVisual();
    }

    // walk the new visual order and rebuild doc.Layers and each layer's Elements
    private void ApplyNewOrder(IList<Control> finalChildren)
    {
        if (_doc is null) return;

        var visualLayerOrder = new List<GtLayer>();
        var layerElements    = new Dictionary<GtLayer, List<GtElement>>();
        GtLayer? currentLayer = null;

        foreach (var child in finalChildren)
        {
            var meta = _allRows.FirstOrDefault(r => r.Border == child);
            if (meta is null) continue;

            if (meta.Kind == RowKind.Layer)
            {
                currentLayer = meta.Layer;
                visualLayerOrder.Add(currentLayer);
                layerElements[currentLayer] = new List<GtElement>();
            }
            else if (meta.Kind == RowKind.Element && currentLayer is not null)
            {
                layerElements[currentLayer].Add(meta.Element!);
            }
        }

        // visual order is top-to-bottom, model/render order is bottom-to-top
        _doc.Layers.Clear();
        for (int i = visualLayerOrder.Count - 1; i >= 0; i--)
        {
            var layer = visualLayerOrder[i];
            var elems = layerElements.GetValueOrDefault(layer) ?? new List<GtElement>();
            elems.Reverse(); // element visual order is also reversed vs render order
            layer.Elements.Clear();
            foreach (var el in elems) layer.Elements.Add(el);
            _doc.Layers.Add(layer);
        }
    }

    private Border BuildLayerHeader(GtLayer layer, out TextBlock nameBlock)
    {
        var visBtn  = MakeIconButton(IconKind.Visibility, layer, null);
        var lockBtn = MakeIconButton(IconKind.Lock,       layer, null);

        nameBlock = new TextBlock
        {
            Text              = string.IsNullOrEmpty(layer.Name) ? "(unnamed layer)" : layer.Name,
            FontSize          = 12,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = new SolidColorBrush(Color.Parse("#cccccc")),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Margin            = new Thickness(4, 0, 0, 0),
        };

        var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        inner.Children.Add(visBtn);
        inner.Children.Add(lockBtn);
        inner.Children.Add(nameBlock);

        var header = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1e1e1e")),
            Padding    = new Thickness(6, 4, 6, 4),
            Cursor     = new Cursor(StandardCursorType.SizeNorthSouth),
            Child      = inner,
        };

        header.PointerPressed += (_, e) =>
        {
            if (e.Source is Button or TextBox) return;
            if (_renameBox is not null) return;
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            _canvas?.SetLayerSelection(layer);
        };

        AttachRowContextMenu(header, layer, null);
        return header;
    }

    private Border BuildElementRow(GtLayer layer, GtElement element, out TextBlock nameBlock)
    {
        var visBtn  = MakeIconButton(IconKind.Visibility, layer, element);
        var lockBtn = MakeIconButton(IconKind.Lock,       layer, element);

        var typeTag = element switch
        {
            GtTickerElement    => "K",
            GtTextBlock        => "T",
            GtImageElement     => "I",
            GtRectangleElement => "R",
            GtEllipseElement   => "E",
            _                  => "?"
        };

        nameBlock = new TextBlock
        {
            Text              = $"[{typeTag}] {(string.IsNullOrEmpty(element.Name) ? "(unnamed)" : element.Name)}",
            FontSize          = 11,
            Foreground        = new SolidColorBrush(Color.Parse("#aaaaaa")),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Margin            = new Thickness(4, 0, 0, 0),
        };

        var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        inner.Children.Add(visBtn);
        inner.Children.Add(lockBtn);
        inner.Children.Add(nameBlock);

        var rowBorder = new Border
        {
            Background = Brushes.Transparent,
            Padding    = new Thickness(22, 2, 6, 2),
            Cursor     = new Cursor(StandardCursorType.SizeNorthSouth),
            Child      = inner,
        };

        rowBorder.PointerPressed += (_, e) =>
        {
            if (e.Source is Button or TextBox) return;
            if (_renameBox is not null) return;
            if (_canvas is null) return;
            if (!e.GetCurrentPoint(rowBorder).Properties.IsLeftButtonPressed) return;

            var shift      = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var selected   = _canvas.SelectedElements;
            bool inMultiSel = !shift && selected.Contains(element) && selected.Count > 1;

            if (shift)
            {
                _canvas.ToggleSelection(element);
                _pendingSelectionApply = null;
            }
            else if (inMultiSel)
            {
                // Defer: could be start of multi-element drag
                _pendingSelectionApply = element;
            }
            else
            {
                _canvas.SetSelection(element, false);
                _pendingSelectionApply = null;
            }
        };

        AttachRowContextMenu(rowBorder, layer, element);
        return rowBorder;
    }

    private Button MakeIconButton(IconKind kind, GtLayer layer, GtElement? element)
    {
        var btn = new Button
        {
            Width                      = 22,
            Height                     = 22,
            Padding                    = new Thickness(0),
            FontSize                   = 10,
            Background                 = Brushes.Transparent,
            BorderThickness            = new Thickness(0),
            VerticalContentAlignment   = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        var entry = new IconBtn(kind, btn, layer, element);
        _iconButtons.Add(entry);
        RefreshIconBtn(entry);

        ToolTip.SetTip(btn, kind == IconKind.Visibility ? "Toggle Visibility" : "Toggle Lock");

        // no Click handler, presses are picked up by OnPanelPointerPressed (tunnel)
        return btn;
    }

    private static bool IconValue(IconBtn ib) => ib.Kind == IconKind.Visibility
        ? (ib.Element?.Visible ?? ib.Layer.Visible)
        : (ib.Element?.Locked  ?? ib.Layer.Locked);

    private static object PaintKey(IconBtn ib) => ib.Element ?? (object)ib.Layer;

    private IconBtn? HitIconButton(PointerEventArgs e, IconKind? kind)
    {
        foreach (var ib in _iconButtons)
        {
            if (kind is not null && ib.Kind != kind) continue;   // eye drag never touches lock, and vice versa
            var p = e.GetPosition(ib.Button);
            if (p.X < 0 || p.Y < 0 || p.X > ib.Button.Bounds.Width || p.Y > ib.Button.Bounds.Height) continue;
            return ib;
        }
        return null;
    }

    private void BeginPaint(IconBtn ib)
    {
        _paintKind  = ib.Kind;
        _paintValue = !IconValue(ib);
        _paintApplied.Clear();
        _paintChanged.Clear();
        ApplyPaint(ib);
    }

    private void ApplyPaint(IconBtn ib)
    {
        if (!_paintApplied.Add(PaintKey(ib))) return;   // row already painted this gesture
        if (IconValue(ib) == _paintValue) return;

        if (ib.Kind == IconKind.Visibility)
        {
            if (ib.Element is not null) ib.Element.Visible = _paintValue;
            else                        ib.Layer.Visible   = _paintValue;
            _canvas?.InvalidateVisual();
        }
        else
        {
            if (ib.Element is not null) ib.Element.Locked = _paintValue;
            else                        ib.Layer.Locked   = _paintValue;
        }

        _paintChanged.Add(ib);
        RefreshIconBtn(ib);
    }

    /// <summary>drops the gesture without recording it (used when the rows are rebuilt)</summary>
    private void CancelPaint()
    {
        _paintKind = null;
        _paintApplied.Clear();
        _paintChanged.Clear();
    }

    /// <summary>ends the gesture and pushes one undo entry covering every row it flipped</summary>
    private void EndPaint()
    {
        var kind    = _paintKind;
        var value   = _paintValue;
        var changed = _paintChanged.ToList();

        _paintKind = null;
        _paintApplied.Clear();
        _paintChanged.Clear();

        if (kind is null || changed.Count == 0) return;

        var targets = changed
            .Select(ib => (Layer: ib.Layer, Element: ib.Element))
            .ToList();

        void Set(bool v)
        {
            foreach (var (layer, element) in targets)
            {
                if (kind == IconKind.Visibility)
                {
                    if (element is not null) element.Visible = v;
                    else                     layer.Visible   = v;
                }
                else
                {
                    if (element is not null) element.Locked = v;
                    else                     layer.Locked   = v;
                }
            }
            RefreshAllIconBtns();
            if (kind == IconKind.Visibility) _canvas?.InvalidateVisual();
        }

        string what = changed.Count == 1
            ? DescribeTarget(changed[0])
            : $"{changed.Count} items";

        string verb = kind == IconKind.Visibility
            ? (value ? "Show" : "Hide")
            : (value ? "Lock" : "Unlock");

        History?.Push(new PropertyChangeAction($"{verb} {what}", undo: () => Set(!value), redo: () => Set(value)));
    }

    private static string DescribeTarget(IconBtn ib)
    {
        var name = ib.Element?.Name ?? ib.Layer.Name;
        if (!string.IsNullOrEmpty(name)) return name;
        return ib.Element is not null ? "element" : "layer";
    }

    private void RefreshAllIconBtns()
    {
        foreach (var ib in _iconButtons) RefreshIconBtn(ib);
    }

    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_paintKind is not null) return;   // tunnel pass already started this gesture
        if (!e.GetCurrentPoint(LayersStack).Properties.IsLeftButtonPressed) return;

        var ib = HitIconButton(e, null);
        if (ib is null) return;

        BeginPaint(ib);
        e.Pointer.Capture(LayersStack);   // keep moves/release coming to us for the whole sweep
        e.Handled = true;                 // stops row selection and drag-reorder
    }

    private void OnPanelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_paintKind is null) return;
        var ib = HitIconButton(e, _paintKind);
        if (ib is not null) ApplyPaint(ib);
    }

    private void OnPanelPointerReleased(object? sender, PointerReleasedEventArgs e) => EndPaint();

    private void OnPanelCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndPaint();

    private static Bitmap? _iconLocked;
    private static Bitmap? _iconUnlocked;
    private static Bitmap? _iconEye;
    private static Bitmap? _iconEyeOff;
    private static bool    _iconsLoaded;

    private static void EnsureIcons()
    {
        if (_iconsLoaded) return;
        _iconsLoaded  = true;
        _iconLocked   = LoadIcon("lock.png");
        _iconUnlocked = LoadIcon("lock-open.png");
        _iconEye      = LoadIcon("eye.png");
        _iconEyeOff   = LoadIcon("eye-off.png");
    }

    private static Bitmap LoadIcon(string filename)
    {
        var uri = new Uri($"avares://GtPlus/Assets/Icons/{filename}");
        using var stream = AssetLoader.Open(uri);
        return new Bitmap(stream);
    }

    private static object LockContent(bool locked)
    {
        EnsureIcons();
        return new Image
        {
            Source            = locked ? _iconLocked : _iconUnlocked,
            Width             = 14,
            Height            = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static object VisContent(bool visible)
    {
        EnsureIcons();
        return new Image
        {
            Source            = visible ? _iconEye : _iconEyeOff,
            Width             = 14,
            Height            = 14,
            Opacity           = visible ? 1.0 : 0.45,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static void RefreshIconBtn(IconBtn ib)
    {
        bool on = IconValue(ib);
        if (ib.Kind == IconKind.Visibility)
        {
            ib.Button.Content    = VisContent(on);
            ib.Button.Foreground = on ? Brushes.White : new SolidColorBrush(Color.Parse("#555555"));
        }
        else
        {
            ib.Button.Content    = LockContent(on);
            ib.Button.Foreground = Brushes.White;
        }
    }

    private void OnCanvasSelectionChanged(object? sender, EventArgs e)
    {
        var selected      = _canvas?.SelectedElements;
        var selectedLayer = _canvas?.SelectedLayer;
        var selBrush      = new SolidColorBrush(Color.FromArgb(60, 80, 160, 255));
        // amber, matching the layer frame the canvas draws
        var layerSelBrush = new SolidColorBrush(Color.FromArgb(70, 255, 176, 32));
        var layerBrush    = new SolidColorBrush(Color.Parse("#1e1e1e"));

        foreach (var meta in _allRows)
        {
            if (meta.Kind == RowKind.Element)
                meta.Border.Background = selected?.Contains(meta.Element) == true ? selBrush : Brushes.Transparent;
            else
                meta.Border.Background = ReferenceEquals(meta.Layer, selectedLayer) ? layerSelBrush : layerBrush;
        }
    }

    /// <summary>fired after a rename is applied, undone or redone</summary>
    public event EventHandler? DocumentChanged;

    // row the keyboard acts on (F2) and the row a context menu was opened over; held as model references not as a RowMeta so it survives a Populate() rebuild
    private GtLayer?   _focusLayer;
    private GtElement? _focusElement;

    private TextBox?  _renameBox;
    private RowMeta?  _renameRow;
    private bool      _renameClosing;

    private void SetFocusRow(GtLayer layer, GtElement? element)
    {
        _focusLayer   = layer;
        _focusElement = element;
    }

    private void AttachRowContextMenu(Border border, GtLayer layer, GtElement? element)
    {
        border.ContextRequested += (_, _) =>
        {
            SetFocusRow(layer, element);

            // right-clicking outside the current selection moves the selection to that row, so "Rename" always acts on what the user is pointing at
            if (element is not null && _canvas is not null && !_canvas.SelectedElements.Contains(element))
                _canvas.SetSelection(element, false);
            else if (element is null && !ReferenceEquals(_canvas?.SelectedLayer, layer))
                _canvas?.SetLayerSelection(layer);
        };

        border.ContextMenu = element is null ? BuildLayerMenu(layer) : BuildElementMenu(layer, element);
    }

    /// <summary>layer row menu, layer-wide commands</summary>
    private ContextMenu BuildLayerMenu(GtLayer layer)
    {
        var rename = new MenuItem { Header = "Rename" };
        rename.Click += (_, _) => BeginRename(layer, null);

        var newLayer = new MenuItem { Header = "New Layer" };
        newLayer.Click += (_, _) => AddLayer();

        var duplicateLayer = new MenuItem { Header = "Duplicate Layer" };
        duplicateLayer.Click += (_, _) => DuplicateLayer();

        var deleteLayer = new MenuItem { Header = "Delete Layer" };
        deleteLayer.Click += (_, _) => DeleteLayer();

        var menu = new ContextMenu();
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(newLayer);
        menu.Items.Add(duplicateLayer);
        menu.Items.Add(deleteLayer);

        // the delete is refused on the last layer, so the item says so before it is clicked
        menu.Opened += (_, _) => deleteLayer.IsEnabled = (_doc?.Layers.Count ?? 0) > 1;

        return menu;
    }

    /// <summary>element row menu, acts on the selection which ContextRequested has already moved onto the clicked row; carries no layer commands since "Delete Layer" sitting a pixel away from "Delete" made it far too easy to lose a whole layer while aiming at one element</summary>
    private ContextMenu BuildElementMenu(GtLayer layer, GtElement element)
    {
        var rename = new MenuItem { Header = "Rename" };
        rename.Click += (_, _) => BeginRename(layer, element);

        var duplicate = new MenuItem { Header = "Duplicate" };
        duplicate.Click += (_, _) => DuplicateElements();

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => DeleteElements();

        // only an image sitting on a multi-frame sequence has a sequence clip to author
        var sequenceSeparator = new Separator { IsVisible = false };
        var addSequence = new MenuItem { Header = "Add Image Sequence Animation...", IsVisible = false };
        addSequence.Click += (_, _) => AddSequenceAnimationRequested?.Invoke(element);

        var menu = new ContextMenu();
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(duplicate);
        menu.Items.Add(delete);
        menu.Items.Add(sequenceSeparator);
        menu.Items.Add(addSequence);

        menu.Opened += (_, _) =>
        {
            int count  = _canvas?.SelectedElements.Count ?? 0;
            var suffix = count > 1 ? $" {count} Elements" : " Element";

            duplicate.Header    = "Duplicate" + suffix;
            delete.Header       = "Delete" + suffix;
            duplicate.IsEnabled = count > 0;
            // a locked element is immovable everywhere else, so it is not deletable either
            delete.IsEnabled    = DeletableSelection().Count > 0;

            bool isSequence = SequenceFrameCount(element) > 1;
            sequenceSeparator.IsVisible = isSequence;
            addSequence.IsVisible       = isSequence;
            // greyed rather than hidden while the timeline shows no storyboard: the command exists, it just has nowhere to put the clip yet
            addSequence.IsEnabled       = CanAddSequenceAnimation?.Invoke(element) == true;
        };

        return menu;
    }

    /// <summary>frames behind the sequence an element draws, 1 for an ordinary still</summary>
    private int SequenceFrameCount(GtElement element) =>
        element is GtImageElement img && img.BitmapSource is not null && _canvas is not null
            ? _canvas.Assets.FrameCount(img.BitmapSource)
            : 1;

    /// <summary>host hook: true when an ImageSequence clip can be added for this element right now, which needs a storyboard on the timeline to hold it</summary>
    public Func<GtElement, bool>? CanAddSequenceAnimation { get; set; }

    /// <summary>host hook: add an ImageSequence clip for this element and open the length helper</summary>
    public Action<GtElement>? AddSequenceAnimationRequested { get; set; }

    /// <summary>F2 entry point, returns false when there is nothing to rename</summary>
    public bool BeginRenameFocused()
    {
        if (_renameBox is not null) return true;   // already editing

        // no row clicked yet, fall back to the canvas selection
        if (_focusLayer is null)
        {
            var sel = _canvas?.SelectedElements;
            if (sel is null || sel.Count == 0) return false;
            var row = _allRows.FirstOrDefault(r => r.Kind == RowKind.Element && sel.Contains(r.Element));
            if (row is null) return false;
            SetFocusRow(row.Layer, row.Element);
        }

        return BeginRename(_focusLayer!, _focusElement);
    }

    private bool BeginRename(GtLayer layer, GtElement? element)
    {
        if (_renameBox is not null) return true;

        var meta = _allRows.FirstOrDefault(r => r.Layer == layer && r.Element == element);
        if (meta is null) return false;
        if (meta.Border.Child is not StackPanel host) return false;

        int idx = host.Children.IndexOf(meta.NameBlock);
        if (idx < 0) return false;

        var box = new TextBox
        {
            Text                     = element is not null ? element.Name : layer.Name,
            FontSize                 = meta.Kind == RowKind.Layer ? 12 : 11,
            MinWidth                 = 80,
            MinHeight                = 0,
            Padding                  = new Thickness(3, 0, 3, 0),
            Margin                   = new Thickness(4, 0, 0, 0),
            Background               = new SolidColorBrush(Color.Parse("#2a2a2a")),
            Foreground               = Brushes.White,
            BorderBrush              = new SolidColorBrush(Color.Parse("#4a90e2")),
            BorderThickness          = new Thickness(1),
            VerticalAlignment        = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)       { EndRename(commit: true);  e.Handled = true; }
            else if (e.Key == Key.Escape) { EndRename(commit: false); e.Handled = true; }
        };
        box.LostFocus += (_, _) => EndRename(commit: true);
        box.AttachedToVisualTree += (_, _) => { box.Focus(); box.SelectAll(); };

        _renameBox     = box;
        _renameRow     = meta;
        _renameClosing = false;

        host.Children.RemoveAt(idx);
        host.Children.Insert(idx, box);
        return true;
    }

    private void EndRename(bool commit)
    {
        if (_renameBox is null || _renameRow is null || _renameClosing) return;
        _renameClosing = true;

        var box  = _renameBox;
        var meta = _renameRow;
        _renameBox = null;
        _renameRow = null;

        if (meta.Border.Child is StackPanel host)
        {
            int idx = host.Children.IndexOf(box);
            if (idx >= 0)
            {
                host.Children.RemoveAt(idx);
                host.Children.Insert(idx, meta.NameBlock);
            }
        }

        _renameClosing = false;

        if (commit) CommitRename(meta, box.Text ?? string.Empty);
    }

    /// <summary>drops an in-progress edit without applying it (rows are about to be rebuilt)</summary>
    private void CancelRename()
    {
        _renameBox     = null;
        _renameRow     = null;
        _renameClosing = false;
    }

    private void CommitRename(RowMeta meta, string input)
    {
        var doc = _doc;
        if (doc is null) return;

        input = input.Trim();
        if (input.Length == 0) return;

        // a layer row renames only itself; an element row renames the whole selection when it is part of a multi-selection, otherwise just itself
        var targets = new List<RowMeta>();
        if (meta.Kind == RowKind.Element)
        {
            var sel = _canvas?.SelectedElements;
            if (sel is not null && sel.Count > 1 && sel.Contains(meta.Element))
            {
                // document order, so a multi-rename numbers the same way a multi-paste stacks
                foreach (var layer in doc.Layers)
                    foreach (var el in layer.Elements)
                    {
                        if (!sel.Contains(el)) continue;
                        var row = _allRows.FirstOrDefault(r => r.Kind == RowKind.Element && r.Element == el);
                        if (row is not null) targets.Add(row);
                    }
            }
        }
        if (targets.Count == 0) targets.Add(meta);

        // names being replaced are free again, renaming Box1..Box3 to "Box" must not skip to Box4
        var taken = NameService.CollectAllNames(doc);
        foreach (var t in targets)
            taken.Remove(t.Element?.Name ?? t.Layer.Name);

        var renames = new List<(GtLayer Layer, GtElement? Element, string Old, string New)>();
        foreach (var t in targets)
        {
            string oldName = t.Element?.Name ?? t.Layer.Name;
            string newName = targets.Count == 1 && !taken.Contains(input)
                ? input                                        // single rename keeps the typed name
                : NameService.UniqueName(taken, input, 1);     // otherwise suffix an incrementing number
            taken.Add(newName);

            if (!string.Equals(oldName, newName, StringComparison.Ordinal))
                renames.Add((t.Layer, t.Element, oldName, newName));
        }

        if (renames.Count == 0) return;

        // animations and masks reference objects by name, so collect the references while the old names are still in place then retarget them alongside the rename
        var animRefs = new List<(GtAnimation Anim, string Old, string New)>();
        var maskRefs = new List<(GtElement Owner, string Old, string New)>();
        var boundRefs = new List<(GtBounding Bounding, string Old, string New)>();

        // a DataChange storyboard scoped to "Old.Text" must follow the object to "New.Text", or it would be pruned as a dangling scope on the next save
        var scopeRefs = new List<(GtStoryboard Storyboard, string Old, string New)>();

        foreach (var (_, _, oldName, newName) in renames)
        {
            if (string.IsNullOrEmpty(oldName)) continue;

            foreach (var sb in doc.Storyboards)
            {
                foreach (var anim in sb.Animations)
                    if (string.Equals(anim.Object, oldName, StringComparison.OrdinalIgnoreCase))
                        animRefs.Add((anim, oldName, newName));

                if (sb.IsScoped &&
                    !string.Equals(sb.DataName,
                                   GtDataFieldService.RenameOwner(sb.DataName, oldName, newName),
                                   StringComparison.Ordinal))
                    scopeRefs.Add((sb, oldName, newName));
            }

            foreach (var layer in doc.Layers)
                foreach (var el in layer.Elements)
                    if (el.MaskObject is not null &&
                        string.Equals(el.MaskObject, oldName, StringComparison.OrdinalIgnoreCase))
                        maskRefs.Add((el, oldName, newName));

            foreach (var layer in doc.Layers)
                foreach (var el in layer.Elements)
                    if (el.Bounding is { } bounding && bounding.HasSource &&
                        string.Equals(bounding.Object, oldName, StringComparison.OrdinalIgnoreCase))
                        boundRefs.Add((bounding, oldName, newName));
        }

        void Set(bool forward)
        {
            foreach (var (layer, element, oldName, newName) in renames)
            {
                var value = forward ? newName : oldName;
                if (element is not null) element.Name = value;
                else                     layer.Name   = value;
            }
            foreach (var (anim, oldName, newName) in animRefs)
                anim.Object = forward ? newName : oldName;
            foreach (var (owner, oldName, newName) in maskRefs)
                owner.MaskObject = forward ? newName : oldName;
            foreach (var (bounding, oldName, newName) in boundRefs)
                bounding.Object = forward ? newName : oldName;
            foreach (var (storyboard, oldName, newName) in scopeRefs)
                storyboard.DataName = forward
                    ? GtDataFieldService.RenameOwner(storyboard.DataName, oldName, newName)
                    : GtDataFieldService.RenameOwner(storyboard.DataName, newName, oldName);

            Populate(doc);
            _canvas?.InvalidateVisual();
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }

        Set(true);

        string desc = renames.Count == 1
            ? $"Rename {renames[0].Old} to {renames[0].New}"
            : $"Rename {renames.Count} elements";

        History?.Push(new PropertyChangeAction(desc, undo: () => Set(false), redo: () => Set(true)));
    }

    private void NewLayerButton_Click(object? sender, RoutedEventArgs e)       => AddLayer();
    private void DuplicateLayerButton_Click(object? sender, RoutedEventArgs e) => DuplicateLayer();
    private void DeleteLayerButton_Click(object? sender, RoutedEventArgs e)    => DeleteLayer();

    /// <summary>layer the New/Duplicate/Delete commands act on: the row last clicked, else the canvas selection (layer, or the layer owning a selected element), else the topmost layer</summary>
    private GtLayer? CommandLayer
    {
        get
        {
            if (_doc is null) return null;
            if (_focusLayer is not null && _doc.Layers.Contains(_focusLayer)) return _focusLayer;
            if (_canvas?.SelectedLayer is { } selected && _doc.Layers.Contains(selected)) return selected;

            var el = _canvas?.SelectedElements.FirstOrDefault();
            if (el is not null)
                foreach (var layer in _doc.Layers)
                    if (layer.Elements.Contains(el)) return layer;

            return _doc.Layers.Count > 0 ? _doc.Layers[_doc.Layers.Count - 1] : null;
        }
    }

    /// <summary>adds an empty layer above the current one and selects it, returns false when there is no document to add it to</summary>
    public bool AddLayer()
    {
        var doc = _doc;
        if (doc is null) return false;

        var above = CommandLayer;
        int index = above is null ? doc.Layers.Count : doc.Layers.IndexOf(above) + 1;

        var layer = new GtLayer
        {
            Name        = NameService.UniqueName(NameService.CollectAllNames(doc), "Layer", 1),
            Location    = GtPoint.Zero,
            Dimensions  = new GtSize(doc.Width, doc.Height),
            InnerWidth  = doc.Width,
            InnerHeight = doc.Height,
        };

        InsertLayer(doc, layer, index, $"Add layer {layer.Name}");
        return true;
    }

    /// <summary>copies the current layer (elements, and the animations targeting them) onto a new layer directly above it</summary>
    public bool DuplicateLayer()
    {
        var doc    = _doc;
        var source = CommandLayer;
        if (doc is null || source is null) return false;

        var copy  = source.Clone();
        var taken = NameService.CollectAllNames(doc);

        // animations resolve their target by name, so every copied name is renamed before the copied animations are retargeted onto it
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        copy.Name = NameService.NextCopyName(taken, string.IsNullOrEmpty(source.Name) ? "Layer" : source.Name);
        taken.Add(copy.Name);
        if (!string.IsNullOrEmpty(source.Name)) renamed[source.Name] = copy.Name;

        for (int i = 0; i < copy.Elements.Count; i++)
        {
            var el      = copy.Elements[i];
            var oldName = el.Name;
            el.Name = NameService.NextCopyName(taken, string.IsNullOrEmpty(oldName) ? "Element" : oldName);
            taken.Add(el.Name);
            if (!string.IsNullOrEmpty(oldName)) renamed[oldName] = el.Name;
        }

        // masks point at a sibling in the same layer, so they follow the copy
        foreach (var el in copy.Elements)
            if (el.MaskObject is not null && renamed.TryGetValue(el.MaskObject, out var newMask))
                el.MaskObject = newMask;

        // a bounding names a sibling too, so it follows the copy the same way
        foreach (var el in copy.Elements)
            if (el.Bounding is { } bounding && bounding.Object is { } boundSource &&
                renamed.TryGetValue(boundSource, out var newSource))
                bounding.Object = newSource;

        var copiedAnimations = new List<(GtStoryboard Storyboard, GtAnimation Animation)>();
        foreach (var sb in doc.Storyboards)
            foreach (var anim in sb.Animations)
                if (!string.IsNullOrEmpty(anim.Object) && renamed.TryGetValue(anim.Object, out var newTarget))
                {
                    var clone = anim.Clone();
                    clone.Object = newTarget;
                    copiedAnimations.Add((sb, clone));
                }

        int index = doc.Layers.IndexOf(source) + 1;
        InsertLayer(doc, copy, index, $"Duplicate layer {source.Name}", copiedAnimations);
        return true;
    }

    /// <summary>removes the current layer with everything on it, including the animations that targeted it or its elements; refuses to empty the document</summary>
    public bool DeleteLayer()
    {
        var doc   = _doc;
        var layer = CommandLayer;
        if (doc is null || layer is null) return false;

        // GT has no concept of a layerless composition, so the last one stays
        if (doc.Layers.Count <= 1) return false;

        int index = doc.Layers.IndexOf(layer);
        if (index < 0) return false;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(layer.Name)) names.Add(layer.Name);
        foreach (var el in layer.Elements)
            if (!string.IsNullOrEmpty(el.Name)) names.Add(el.Name);

        // orphaned animations would keep a dead Object name in the file, so they go with it, recorded with their index so undo puts them back where they were
        var orphans = new List<(GtStoryboard Storyboard, int Index, GtAnimation Animation)>();
        foreach (var sb in doc.Storyboards)
            for (int i = 0; i < sb.Animations.Count; i++)
                if (names.Contains(sb.Animations[i].Object))
                    orphans.Add((sb, i, sb.Animations[i]));

        void Remove()
        {
            for (int i = orphans.Count - 1; i >= 0; i--)
                orphans[i].Storyboard.Animations.Remove(orphans[i].Animation);
            doc.Layers.Remove(layer);
            if (ReferenceEquals(_canvas?.SelectedLayer, layer)) _canvas?.SetLayerSelection(null);
            AfterStructureChange(doc);
        }

        void Restore()
        {
            doc.Layers.Insert(Math.Min(index, doc.Layers.Count), layer);
            foreach (var (sb, at, anim) in orphans)
                sb.Animations.Insert(Math.Min(at, sb.Animations.Count), anim);
            _canvas?.SetLayerSelection(layer);
            AfterStructureChange(doc);
        }

        Remove();
        History?.Push(new PropertyChangeAction(
            $"Delete layer {(string.IsNullOrEmpty(layer.Name) ? "(unnamed)" : layer.Name)}",
            undo: Restore, redo: Remove));
        return true;
    }

    private void InsertLayer(GtDocument doc, GtLayer layer, int index, string description,
                             List<(GtStoryboard Storyboard, GtAnimation Animation)>? animations = null)
    {
        void Add()
        {
            doc.Layers.Insert(Math.Min(index, doc.Layers.Count), layer);
            if (animations is not null)
                foreach (var (sb, anim) in animations)
                    sb.Animations.Add(anim);
            _canvas?.SetLayerSelection(layer);
            AfterStructureChange(doc);
        }

        void Remove()
        {
            if (animations is not null)
                foreach (var (sb, anim) in animations)
                    sb.Animations.Remove(anim);
            doc.Layers.Remove(layer);
            if (ReferenceEquals(_canvas?.SelectedLayer, layer)) _canvas?.SetLayerSelection(null);
            AfterStructureChange(doc);
        }

        Add();
        History?.Push(new PropertyChangeAction(description, undo: Remove, redo: Add));
    }

    /// <summary>the canvas selection paired with its owning layer, in document order</summary>
    private List<(GtLayer Layer, GtElement Element)> SelectedElementsInOrder()
    {
        var result = new List<(GtLayer Layer, GtElement Element)>();

        var doc = _doc;
        var sel = _canvas?.SelectedElements;
        if (doc is null || sel is null || sel.Count == 0) return result;

        foreach (var layer in doc.Layers)
            foreach (var el in layer.Elements)
                if (sel.Contains(el)) result.Add((layer, el));

        return result;
    }

    /// <summary>selected elements a delete may touch, a lock on the element or its layer wins</summary>
    private List<(GtLayer Layer, GtElement Element)> DeletableSelection() =>
        SelectedElementsInOrder().Where(t => !t.Element.Locked && !t.Layer.Locked).ToList();

    /// <summary>copies the selected elements (and the animations targeting them) directly above each original then selects the copies; returns false when nothing is selected</summary>
    public bool DuplicateElements()
    {
        var doc = _doc;
        if (doc is null) return false;

        var targets = SelectedElementsInOrder();
        if (targets.Count == 0) return false;

        var taken   = NameService.CollectAllNames(doc);
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var copies  = new List<(GtLayer Layer, GtElement Copy, GtElement Source)>();

        // animations resolve their target by name, so every copy is named before the copied animations are retargeted onto it
        foreach (var (layer, source) in targets)
        {
            var copy = source.Clone();
            copy.Name = NameService.NextCopyName(taken, string.IsNullOrEmpty(source.Name) ? "Element" : source.Name);
            taken.Add(copy.Name);
            if (!string.IsNullOrEmpty(source.Name)) renamed[source.Name] = copy.Name;
            copies.Add((layer, copy, source));
        }

        // a mask pointing at another element in the same duplicate follows the copy
        foreach (var (_, copy, _) in copies)
            if (copy.MaskObject is not null && renamed.TryGetValue(copy.MaskObject, out var masked))
                copy.MaskObject = masked;

        // so does a bounding pointing at another element in the same duplicate
        foreach (var (_, copy, _) in copies)
            if (copy.Bounding is { } bounding && bounding.Object is { } source &&
                renamed.TryGetValue(source, out var bound))
                bounding.Object = bound;

        var animations = new List<(GtStoryboard Storyboard, GtAnimation Animation)>();
        foreach (var sb in doc.Storyboards)
            foreach (var anim in sb.Animations)
                if (!string.IsNullOrEmpty(anim.Object) && renamed.TryGetValue(anim.Object, out var target))
                {
                    var clone = anim.Clone();
                    clone.Object = target;
                    animations.Add((sb, clone));
                }

        void Apply()
        {
            // the index is resolved per copy, so duplicating several elements out of one layer still lands each copy directly above its own original
            foreach (var (layer, copy, source) in copies)
            {
                int at = layer.Elements.IndexOf(source);
                at = at < 0 ? layer.Elements.Count : at + 1;
                layer.Elements.Insert(Math.Min(at, layer.Elements.Count), copy);
            }
            foreach (var (sb, anim) in animations)
                sb.Animations.Add(anim);

            SelectOnly(copies.Select(c => c.Copy));
            AfterStructureChange(doc);
        }

        void Revert()
        {
            foreach (var (sb, anim) in animations)
                sb.Animations.Remove(anim);
            foreach (var (layer, copy, _) in copies)
                layer.Elements.Remove(copy);

            SelectOnly(copies.Select(c => c.Source));
            AfterStructureChange(doc);
        }

        Apply();

        History?.Push(new PropertyChangeAction(
            copies.Count == 1 ? $"Duplicate {copies[0].Copy.Name}" : $"Duplicate {copies.Count} elements",
            undo: Revert, redo: Apply));
        return true;
    }

    /// <summary>removes the selected elements together with the animations that targeted them and any mask that pointed at them; returns false when the selection is empty or entirely locked</summary>
    public bool DeleteElements()
    {
        var doc = _doc;
        if (doc is null) return false;

        var targets = DeletableSelection();
        if (targets.Count == 0) return false;

        var removed = new List<(GtLayer Layer, GtElement Element, int Index)>();
        foreach (var (layer, el) in targets)
            removed.Add((layer, el, layer.Elements.IndexOf(el)));

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, el, _) in removed)
            if (!string.IsNullOrEmpty(el.Name)) names.Add(el.Name);

        // orphaned animations would keep a dead Object name in the file, so they go with the element, recorded with their index so undo puts them back where they were
        var orphans = new List<(GtStoryboard Storyboard, int Index, GtAnimation Animation)>();
        foreach (var sb in doc.Storyboards)
            for (int i = 0; i < sb.Animations.Count; i++)
                if (names.Contains(sb.Animations[i].Object))
                    orphans.Add((sb, i, sb.Animations[i]));

        // a mask naming a deleted element would clip its owner away to nothing, so it is cleared
        var masks = new List<(GtElement Owner, string Value)>();
        foreach (var layer in doc.Layers)
            foreach (var el in layer.Elements)
                if (el.MaskObject is not null && names.Contains(el.MaskObject) &&
                    !targets.Any(t => ReferenceEquals(t.Element, el)))
                    masks.Add((el, el.MaskObject));

        // a bounding naming a deleted element would freeze its owner at whatever box it last copied, so it is cleared too; GT leaves the dead name behind, which is a bug
        var boundings = new List<(GtBounding Bounding, string Value)>();
        foreach (var layer in doc.Layers)
            foreach (var el in layer.Elements)
                if (el.Bounding is { } bounding && bounding.Object is { } source &&
                    names.Contains(source) && !targets.Any(t => ReferenceEquals(t.Element, el)))
                    boundings.Add((bounding, source));

        void Remove()
        {
            for (int i = orphans.Count - 1; i >= 0; i--)
                orphans[i].Storyboard.Animations.Remove(orphans[i].Animation);
            foreach (var (owner, _) in masks)
                owner.MaskObject = null;
            foreach (var (bounding, _) in boundings)
                bounding.Object = null;
            foreach (var (layer, el, _) in removed)
                layer.Elements.Remove(el);

            _canvas?.ClearSelection();
            AfterStructureChange(doc);
        }

        void Restore()
        {
            // ascending order, so each element lands back on its original index
            foreach (var (layer, el, at) in removed)
                layer.Elements.Insert(Math.Min(Math.Max(at, 0), layer.Elements.Count), el);
            foreach (var (sb, at, anim) in orphans)
                sb.Animations.Insert(Math.Min(at, sb.Animations.Count), anim);
            foreach (var (owner, value) in masks)
                owner.MaskObject = value;
            foreach (var (bounding, value) in boundings)
                bounding.Object = value;

            SelectOnly(removed.Select(r => r.Element));
            AfterStructureChange(doc);
        }

        Remove();

        History?.Push(new PropertyChangeAction(
            removed.Count == 1
                ? $"Delete {(string.IsNullOrEmpty(removed[0].Element.Name) ? "(unnamed)" : removed[0].Element.Name)}"
                : $"Delete {removed.Count} elements",
            undo: Restore, redo: Remove));
        return true;
    }

    private void SelectOnly(IEnumerable<GtElement> elements)
    {
        if (_canvas is null) return;
        _canvas.ClearSelection();
        foreach (var el in elements)
            _canvas.SetSelection(el, addToSelection: true);
    }

    private void AfterStructureChange(GtDocument doc)
    {
        _focusLayer   = null;
        _focusElement = null;
        Populate(doc);
        _canvas?.InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }
}
