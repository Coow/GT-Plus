using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using GtPlus.Controls;
using GtPlus.Models;

namespace GtPlus.Views;

/// <summary>gradient editor dialog mirroring the vMix GT Title Designer gradient menu: type, angle, wrap mode, a draggable stop strip, per-stop colour and position; edits a working copy and <see cref="Applied"/> fires live so the caller can preview</summary>
public partial class GradientEditorWindow : Window
{
    private const double StripMargin = 6;   // matches the strip Border margin in AXAML

    private readonly GtBrush _brush;
    private GtGradientStop? _selected;
    private bool _updating;
    private bool _dragging;

    private Popup? _colorPopup;
    private ColorPickerControl? _colorPicker;

    /// <summary>fires whenever the working brush changes (live preview), argument is the working copy</summary>
    public event Action<GtBrush>? Applied;

    /// <summary>the edited brush, only meaningful when the dialog returned true</summary>
    public GtBrush Brush => _brush;

    public GradientEditorWindow() : this(new GtBrush()) { }

    public GradientEditorWindow(GtBrush? source)
    {
        InitializeComponent();

        _brush = source?.Clone() ?? new GtBrush();
        if (_brush.Type != GtBrushType.LinearGradient && _brush.Type != GtBrushType.RadialGradient)
            _brush.Type = GtBrushType.LinearGradient;
        EnsureStops();

        foreach (var s in new[] { "Linear", "Radial" })
            TypeBox.Items.Add(s);
        foreach (var s in new[] { "Mirror", "Clamp", "Wrap" })
            WrapBox.Items.Add(s);

        StopCanvas.AddHandler(PointerPressedEvent,  Strip_Pressed,  RoutingStrategies.Tunnel);
        StopCanvas.AddHandler(PointerMovedEvent,    Strip_Moved,    RoutingStrategies.Bubble);
        StopCanvas.AddHandler(PointerReleasedEvent, Strip_Released, RoutingStrategies.Bubble);
        StopHost.SizeChanged += (_, _) => RefreshStrip();

        _selected = _brush.Stops.FirstOrDefault();
        LoadFromBrush();
    }

    /// <summary>a solid brush converted to a gradient needs a sensible two-stop default</summary>
    private void EnsureStops()
    {
        if (_brush.Stops.Count >= 2) return;

        var baseColor = _brush.Stops.Count == 1
            ? _brush.Stops[0].Color
            : (_brush.Color.A == 0 ? Colors.White : _brush.Color);

        var other = (baseColor.R + baseColor.G + baseColor.B) > 600 ? Colors.Black : Colors.White;

        _brush.Stops.Clear();
        _brush.Stops.Add(new GtGradientStop { Position = 0, Color = baseColor });
        _brush.Stops.Add(new GtGradientStop { Position = 1, Color = other });
    }

    private void LoadFromBrush()
    {
        _updating = true;
        try
        {
            TypeBox.SelectedIndex = _brush.Type == GtBrushType.RadialGradient ? 1 : 0;
            WrapBox.SelectedIndex = (int)_brush.WrapMode;

            var angle = BrushPreview.ToAngle(_brush.StartPoint, _brush.EndPoint);
            AngleSlider.Value = angle;
            AngleBox.Value    = (decimal)Math.Round(angle);

            var linear = _brush.Type == GtBrushType.LinearGradient;
            AngleSlider.IsEnabled = linear;
            AngleBox.IsEnabled    = linear;
            AngleLabel.Foreground = new SolidColorBrush(linear ? Color.Parse("#cccccc") : Color.Parse("#666666"));
        }
        finally { _updating = false; }

        RefreshStrip();
        LoadSelectedStop();
    }

    private void LoadSelectedStop()
    {
        _updating = true;
        try
        {
            var has = _selected is not null;
            StopHexBox.IsEnabled      = has;
            PositionBox.IsEnabled     = has;
            PickColorButton.IsEnabled = has;
            RemoveStopButton.IsEnabled = has && _brush.Stops.Count > 2;

            if (_selected is null)
            {
                StopHexBox.Text            = "";
                StopColorSwatch.Background = null;
                return;
            }

            var c = _selected.Color;
            StopHexBox.Text            = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            StopColorSwatch.Background = new SolidColorBrush(c);
            PositionBox.Value          = (decimal)Math.Round(_selected.Position, 3);
        }
        finally { _updating = false; }
    }

    private void RefreshStrip()
    {
        StripBorder.Background = BrushPreview.ToStopStripBrush(_brush);

        StopCanvas.Children.Clear();
        var usable = StopHost.Bounds.Width - StripMargin * 2;
        if (usable <= 0) return;

        foreach (var stop in _brush.Stops)
        {
            var thumb = new Border
            {
                Width           = 10,
                Height          = 26,
                Background      = new SolidColorBrush(stop.Color),
                BorderBrush     = new SolidColorBrush(ReferenceEquals(stop, _selected)
                                      ? Colors.White : Color.Parse("#303030")),
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(thumb, StripMargin + stop.Position * usable - 5);
            Canvas.SetTop(thumb, 4);
            StopCanvas.Children.Add(thumb);
        }
    }

    private void Changed()
    {
        RefreshStrip();
        Applied?.Invoke(_brush);
    }

    private double PositionFromX(double x)
    {
        var usable = StopHost.Bounds.Width - StripMargin * 2;
        if (usable <= 0) return 0;
        return Math.Clamp((x - StripMargin) / usable, 0, 1);
    }

    private void Strip_Pressed(object? sender, PointerPressedEventArgs e)
    {
        var pos    = e.GetPosition(StopCanvas);
        var usable = StopHost.Bounds.Width - StripMargin * 2;
        if (usable <= 0) return;

        // nearest stop within 8px of the click
        GtGradientStop? hit = null;
        double best = double.MaxValue;
        foreach (var stop in _brush.Stops)
        {
            var dx = Math.Abs(StripMargin + stop.Position * usable - pos.X);
            if (dx < best) { best = dx; hit = stop; }
        }

        if (hit is not null && best <= 8)
        {
            _selected = hit;
            _dragging = true;
            e.Pointer.Capture(StopCanvas);
        }
        else if (e.ClickCount >= 2)
        {
            var p = PositionFromX(pos.X);
            var stop = new GtGradientStop { Position = p, Color = ColorAt(p) };
            _brush.Stops.Add(stop);
            SortStops();
            _selected = stop;
            Changed();
        }
        else return;

        RefreshStrip();
        LoadSelectedStop();
        e.Handled = true;
    }

    private void Strip_Moved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || _selected is null) return;
        _selected.Position = PositionFromX(e.GetPosition(StopCanvas).X);
        SortStops();
        LoadSelectedStop();
        Changed();
    }

    private void Strip_Released(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void SortStops()
    {
        var sorted = _brush.Stops.OrderBy(s => s.Position).ToList();
        _brush.Stops.Clear();
        _brush.Stops.AddRange(sorted);
    }

    /// <summary>interpolated colour of the current gradient at a normalised position</summary>
    private Color ColorAt(double p)
    {
        if (_brush.Stops.Count == 0) return Colors.White;

        var ordered = _brush.Stops.OrderBy(s => s.Position).ToList();
        if (p <= ordered[0].Position) return ordered[0].Color;
        if (p >= ordered[^1].Position) return ordered[^1].Color;

        for (int i = 1; i < ordered.Count; i++)
        {
            var a = ordered[i - 1];
            var b = ordered[i];
            if (p > b.Position) continue;
            var span = b.Position - a.Position;
            var t    = span <= 1e-9 ? 0 : (p - a.Position) / span;
            return Color.FromArgb(
                Lerp(a.Color.A, b.Color.A, t),
                Lerp(a.Color.R, b.Color.R, t),
                Lerp(a.Color.G, b.Color.G, t),
                Lerp(a.Color.B, b.Color.B, t));
        }
        return ordered[^1].Color;
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

    private void TypeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        _brush.Type = TypeBox.SelectedIndex == 1
            ? GtBrushType.RadialGradient
            : GtBrushType.LinearGradient;

        var linear = _brush.Type == GtBrushType.LinearGradient;
        AngleSlider.IsEnabled = linear;
        AngleBox.IsEnabled    = linear;
        AngleLabel.Foreground = new SolidColorBrush(linear ? Color.Parse("#cccccc") : Color.Parse("#666666"));
        Changed();
    }

    private void WrapBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        _brush.WrapMode = (GtRadialWrap)Math.Max(0, WrapBox.SelectedIndex);
        Changed();
    }

    private void AngleSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        SetAngle(e.NewValue, updateSlider: false);
    }

    private void AngleBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating) return;
        SetAngle((double)(e.NewValue ?? 0m), updateSlider: true);
    }

    private void SetAngle(double degrees, bool updateSlider)
    {
        var (start, end) = BrushPreview.FromAngle(degrees);
        _brush.StartPoint = start;
        _brush.EndPoint   = end;

        _updating = true;
        try
        {
            if (updateSlider) AngleSlider.Value = degrees;
            else              AngleBox.Value    = (decimal)Math.Round(degrees);
        }
        finally { _updating = false; }

        Changed();
    }

    private void PositionBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || _selected is null) return;
        _selected.Position = Math.Clamp((double)(e.NewValue ?? 0m), 0, 1);
        SortStops();
        Changed();
    }

    private void StopHexBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitHex();
        e.Handled = true;
    }

    private void StopHexBox_LostFocus(object? sender, RoutedEventArgs e) => CommitHex();

    private void CommitHex()
    {
        if (_updating || _selected is null) return;
        var text = (StopHexBox.Text ?? "").Trim();
        if (text.Length == 0) return;
        if (!text.StartsWith("#")) text = "#" + text;
        if (text.Length == 7) text = "#FF" + text.Substring(1);

        try { _selected.Color = Color.Parse(text); }
        catch { LoadSelectedStop(); return; }

        LoadSelectedStop();
        Changed();
    }

    private void PickColorButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        if (_colorPicker is null)
        {
            _colorPicker = new ColorPickerControl { ShowGradientButton = false };
            _colorPicker.ColorChanged += c =>
            {
                if (_selected is null) return;
                _selected.Color = c;
                LoadSelectedStop();
                Changed();
            };
        }

        if (_colorPopup is null)
        {
            _colorPopup = new Popup
            {
                Child                 = _colorPicker,
                Placement             = PlacementMode.Bottom,
                IsLightDismissEnabled = true,
                PlacementTarget       = PickColorButton,
            };
        }

        _colorPicker.SetColor(_selected.Color);
        _colorPicker.RefreshRecentColors();
        _colorPopup.IsOpen = true;
    }

    private void AddStopButton_Click(object? sender, RoutedEventArgs e)
    {
        // insert halfway between the selected stop and the next one (or at the largest gap)
        double p;
        var ordered = _brush.Stops.OrderBy(s => s.Position).ToList();
        var idx = _selected is null ? -1 : ordered.FindIndex(s => ReferenceEquals(s, _selected));
        if (idx >= 0 && idx < ordered.Count - 1)
            p = (ordered[idx].Position + ordered[idx + 1].Position) / 2;
        else if (ordered.Count >= 2)
            p = (ordered[^2].Position + ordered[^1].Position) / 2;
        else
            p = 0.5;

        var stop = new GtGradientStop { Position = p, Color = ColorAt(p) };
        _brush.Stops.Add(stop);
        SortStops();
        _selected = stop;
        LoadSelectedStop();
        Changed();
    }

    private void RemoveStopButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is null || _brush.Stops.Count <= 2) return;
        var idx = _brush.Stops.FindIndex(s => ReferenceEquals(s, _selected));
        _brush.Stops.Remove(_selected);
        _selected = _brush.Stops[Math.Clamp(idx, 0, _brush.Stops.Count - 1)];
        LoadSelectedStop();
        Changed();
    }

    private void ApplyButton_Click(object? sender, RoutedEventArgs e) => Applied?.Invoke(_brush);

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        Applied?.Invoke(_brush);
        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
