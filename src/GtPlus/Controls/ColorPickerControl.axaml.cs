using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GtPlus.Services;
using Avalonia.VisualTree;

namespace GtPlus.Controls;

public partial class ColorPickerControl : UserControl
{
    public static readonly List<Color> RecentColors = new();
    private const int MaxRecent = 10;

    private double _hue  = 0;
    private double _sat  = 0;
    private double _val  = 1;
    private byte   _alpha = 255;
    private bool   _updating;

    /// <summary>the field the user is typing into; <see cref="UpdateUI"/> leaves its text alone until it loses focus</summary>
    private TextBox? _typingBox;

    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorPickerControl, Color>(nameof(Color), Colors.White);

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColorProperty && !_updating)
            ApplyColorToUI(Color);
    }

    public event Action<Color>? ColorChanged;

    /// <summary>raised when the user clicks "Gradient..." so the host opens the gradient editor</summary>
    public event EventHandler? GradientRequested;

    /// <summary>hide the "Gradient..." button (e.g. when the picker is hosted inside the gradient editor)</summary>
    public bool ShowGradientButton
    {
        get => GradientButton.IsVisible;
        set => GradientButton.IsVisible = value;
    }

    public ColorPickerControl()
    {
        InitializeComponent();

        SpectrumPanel.AddHandler(PointerPressedEvent,  Spectrum_Pressed,  RoutingStrategies.Tunnel);
        SpectrumPanel.AddHandler(PointerMovedEvent,    Spectrum_Moved,    RoutingStrategies.Bubble);
        SpectrumPanel.AddHandler(PointerReleasedEvent, Spectrum_Released, RoutingStrategies.Bubble);

        HuePanel.AddHandler(PointerPressedEvent,  Hue_Pressed,  RoutingStrategies.Tunnel);
        HuePanel.AddHandler(PointerMovedEvent,    Hue_Moved,    RoutingStrategies.Bubble);
        HuePanel.AddHandler(PointerReleasedEvent, Hue_Released, RoutingStrategies.Bubble);

        AlphaPanel.AddHandler(PointerPressedEvent,  Alpha_Pressed,  RoutingStrategies.Tunnel);
        AlphaPanel.AddHandler(PointerMovedEvent,    Alpha_Moved,    RoutingStrategies.Bubble);
        AlphaPanel.AddHandler(PointerReleasedEvent, Alpha_Released, RoutingStrategies.Bubble);

        foreach (var box in new[] { HexBox, RBox, GBox, BBox, ABox, HBox, SBox, LBox })
        {
            box.TextChanged += Field_TextChanged;
            box.LostFocus   += Field_LostFocus;
            box.KeyDown     += Field_KeyDown;
        }

        WireScrub(RLabel, RBox);
        WireScrub(GLabel, GBox);
        WireScrub(BLabel, BBox);
        WireScrub(ALabel, ABox);
        WireScrub(HLabel, HBox);
        WireScrub(SLabel, SBox);
        WireScrub(LLabel, LBox);

        // the picker takes the keyboard whenever it is clicked outside a field, so Ctrl+C / Ctrl+V reach it
        AddHandler(PointerPressedEvent, Picker_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent,        Picker_KeyDown,        RoutingStrategies.Tunnel);
    }

    public void RefreshRecentColors()
    {
        RecentPanel.Children.Clear();
        foreach (var c in RecentColors)
        {
            var btn = new Button
            {
                Width           = 18,
                Height          = 18,
                Padding         = new Thickness(0),
                Background      = new SolidColorBrush(c),
                BorderThickness = new Thickness(1),
                BorderBrush     = new SolidColorBrush(Color.FromArgb(100, 200, 200, 200)),
                CornerRadius    = new CornerRadius(2),
                Tag             = c,
            };
            btn.Click += RecentBtn_Click;
            RecentPanel.Children.Add(btn);
        }
    }

    private void RecentBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Color c)
            SetColor(c);
    }

    public static void AddToRecent(Color c)
    {
        RecentColors.RemoveAll(x => x == c);
        RecentColors.Insert(0, c);
        if (RecentColors.Count > MaxRecent)
            RecentColors.RemoveRange(MaxRecent, RecentColors.Count - MaxRecent);
    }

    private bool _specDragging;

    private void Spectrum_Pressed(object? s, PointerPressedEventArgs e)
    {
        _specDragging = true;
        e.Pointer.Capture(SpectrumPanel);
        UpdateFromSpectrum(e.GetPosition(SpectrumPanel));
        e.Handled = true;
    }
    private void Spectrum_Moved(object? s, PointerEventArgs e)
    {
        if (!_specDragging) return;
        UpdateFromSpectrum(e.GetPosition(SpectrumPanel));
    }
    private void Spectrum_Released(object? s, PointerReleasedEventArgs e)
    {
        _specDragging = false;
        e.Pointer.Capture(null);
    }

    private void UpdateFromSpectrum(Point pos)
    {
        var w = SpectrumPanel.Bounds.Width;
        var h = SpectrumPanel.Bounds.Height;
        if (w <= 0 || h <= 0) return;
        _sat = Math.Clamp(pos.X / w, 0, 1);
        _val = Math.Clamp(1.0 - pos.Y / h, 0, 1);
        CommitHsv();
    }

    private bool _hueDragging;

    private void Hue_Pressed(object? s, PointerPressedEventArgs e)
    {
        _hueDragging = true;
        e.Pointer.Capture(HuePanel);
        UpdateFromHue(e.GetPosition(HuePanel));
        e.Handled = true;
    }
    private void Hue_Moved(object? s, PointerEventArgs e)
    {
        if (!_hueDragging) return;
        UpdateFromHue(e.GetPosition(HuePanel));
    }
    private void Hue_Released(object? s, PointerReleasedEventArgs e)
    {
        _hueDragging = false;
        e.Pointer.Capture(null);
    }

    private void UpdateFromHue(Point pos)
    {
        var w = HuePanel.Bounds.Width;
        if (w <= 0) return;
        _hue = Math.Clamp(pos.X / w, 0, 1) * 360;
        CommitHsv();
    }

    private bool _alphaDragging;

    private void Alpha_Pressed(object? s, PointerPressedEventArgs e)
    {
        _alphaDragging = true;
        e.Pointer.Capture(AlphaPanel);
        UpdateFromAlpha(e.GetPosition(AlphaPanel));
        e.Handled = true;
    }
    private void Alpha_Moved(object? s, PointerEventArgs e)
    {
        if (!_alphaDragging) return;
        UpdateFromAlpha(e.GetPosition(AlphaPanel));
    }
    private void Alpha_Released(object? s, PointerReleasedEventArgs e)
    {
        _alphaDragging = false;
        e.Pointer.Capture(null);
    }

    private void UpdateFromAlpha(Point pos)
    {
        var w = AlphaPanel.Bounds.Width;
        if (w <= 0) return;
        _alpha = (byte)Math.Clamp(Math.Round(pos.X / w * 255), 0, 255);
        CommitHsv();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateUI(Color);
        // a popup re-attaches its child every time it opens; take the keyboard so Ctrl+C / Ctrl+V work straight away
        Dispatcher.UIThread.Post(() => Focus());
    }

    private void Picker_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is null)
            Focus();
    }

    private void Picker_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return;

        if (e.Key == Key.C)
        {
            // a selection inside a field copies just that text, as a text box normally would
            if (e.Source is TextBox { SelectionStart: var start, SelectionEnd: var end } && start != end) return;
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(ToHex(Color));
            e.Handled = true;
        }
        else if (e.Key == Key.V)
        {
            _ = PasteAsync(e.Source as TextBox);
            e.Handled = true;
        }
    }

    /// <summary>applies a HEX, rgb() or hsl() colour from the clipboard; anything else is pasted into <paramref name="target"/> as text</summary>
    private async Task PasteAsync(TextBox? target)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        var text = (await clipboard.TryGetTextAsync())?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // a bare number into a numeric field is a value, not a 3-digit HEX
        bool number = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        if (!(number && target is not null && target != HexBox) && TryParseColor(text, out var c))
        {
            _typingBox = null;
            SetColor(c);
            return;
        }
        target?.Paste();
    }

    // TextChanged is posted, not raised inside the Text setter, so _updating can't filter out UpdateUI's own
    // writes; instead each box's Tag holds the text last applied to it, and only other text is the user's
    private void Field_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { IsFocused: true } box || box.Text == box.Tag as string) return;
        _typingBox = box;
        CommitField(box);
    }

    private void Field_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        // a click away can beat the posted TextChanged for the last keystroke
        if (box.Text != box.Tag as string) CommitField(box);
        _typingBox = null;
        UpdateUI(Color);
    }

    private void Field_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        // hand the keyboard back to the picker; LostFocus tidies the field's text
        Focus();
        e.Handled = true;
    }

    /// <summary>applies a field's text live as it is typed; numbers are clamped, text that does not parse yet is left for the user to finish</summary>
    private void CommitField(TextBox box)
    {
        box.Tag  = box.Text;
        var text = box.Text?.Trim() ?? "";

        if (box == HexBox)
        {
            // #RGB would fire on the way to #RRGGBB, so typing only accepts the full forms
            var digits = text.TrimStart('#');
            if (digits.Length is 6 or 8 && TryParseHex(digits, out var hex))
                SetColor(hex);
            return;
        }

        if (double.TryParse(text.TrimEnd('%', '°'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            ApplyNumber(box, n, HsvToHsl(_hue, _sat, _val));
    }

    private bool IsRgbaBox(TextBox box) => box == RBox || box == GBox || box == BBox || box == ABox;

    /// <summary>the value a numeric field stands for, unrounded: 0-255 for RGBA, 0-360 for H, 0-100 for S and L</summary>
    private double FieldValue(TextBox box)
    {
        var c = Color;
        if (box == RBox) return c.R;
        if (box == GBox) return c.G;
        if (box == BBox) return c.B;
        if (box == ABox) return c.A;
        var (h, s, l) = HsvToHsl(_hue, _sat, _val);
        return box == HBox ? h : box == SBox ? s * 100 : l * 100;
    }

    /// <summary>sets one numeric field, clamped to its range; an HSL field replaces its own component of <paramref name="hsl"/> and keeps the other two</summary>
    private void ApplyNumber(TextBox box, double n, (double h, double s, double l) hsl)
    {
        if (IsRgbaBox(box))
        {
            var b = (byte)Math.Clamp(Math.Round(n), 0, 255);
            var c = Color;
            SetColor(box == RBox ? Color.FromArgb(c.A, b, c.G, c.B)
                   : box == GBox ? Color.FromArgb(c.A, c.R, b, c.B)
                   : box == BBox ? Color.FromArgb(c.A, c.R, c.G, b)
                   :               Color.FromArgb(b, c.R, c.G, c.B));
            return;
        }

        var (h, s, l) = hsl;
        if      (box == HBox) h = Math.Clamp(n, 0, 360);
        else if (box == SBox) s = Math.Clamp(n, 0, 100) / 100;
        else                  l = Math.Clamp(n, 0, 100) / 100;

        // straight to HSV so the hue survives greys, which RGB can't carry
        _hue = h;
        (_sat, _val) = HslToHsv(s, l);
        CommitHsv();
    }

    // label scrub, as on the Location boxes: drag a field's label left/right, 1 per pixel, Alt = 0.1
    private TextBox?    _scrubBox;
    private double      _scrubStartX;
    private double      _scrubStartValue;
    private (double h, double s, double l) _scrubStartHsl;
    private CursorScrub? _scrub;   // null on platforms that can't warp the cursor

    private void WireScrub(TextBlock label, TextBox box)
    {
        label.Cursor = new Cursor(StandardCursorType.SizeWestEast);

        label.AddHandler(PointerPressedEvent, (object? s, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(label).Properties.IsLeftButtonPressed) return;
            _scrubBox        = box;
            _scrubStartX     = e.GetPosition(label).X;
            _scrubStartValue = FieldValue(box);
            // held fixed for the whole drag: S and L would lose each other at the ends of the range otherwise
            _scrubStartHsl   = HsvToHsl(_hue, _sat, _val);
            _scrub           = CursorScrub.Begin();
            e.Pointer.Capture(label);
            e.Handled = true;
        }, RoutingStrategies.Bubble);

        label.AddHandler(PointerMovedEvent, (object? s, PointerEventArgs e) =>
        {
            if (_scrubBox != box) return;
            // with a scrub active the cursor wraps around the monitor so travel comes from the OS in physical pixels, convert to DIPs to keep the feel identical either way
            var dx = _scrub is not null
                ? _scrub.UpdateX() / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0)
                : e.GetPosition(label).X - _scrubStartX;
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? 0.1 : 1.0;
            ApplyNumber(box, _scrubStartValue + dx * step, _scrubStartHsl);
            e.Handled = true;
        }, RoutingStrategies.Bubble);

        label.AddHandler(PointerReleasedEvent, (object? s, PointerReleasedEventArgs e) =>
        {
            if (_scrubBox != box) return;
            EndScrub();
            e.Pointer.Capture(null);
        }, RoutingStrategies.Bubble);

        label.AddHandler(PointerCaptureLostEvent, (_, _) =>
        {
            if (_scrubBox == box) EndScrub();
        }, RoutingStrategies.Bubble);
    }

    private void EndScrub()
    {
        _scrubBox = null;
        _scrub?.Dispose();   // never leave the cursor hidden
        _scrub = null;
    }

    private void GradientButton_Click(object? sender, RoutedEventArgs e)
        => GradientRequested?.Invoke(this, EventArgs.Empty);

    private void CommitHsv()
    {
        var c = HsvToColor(_hue, _sat, _val, _alpha);
        _updating = true;
        Color = c;
        _updating = false;
        UpdateUI(c);
        ColorChanged?.Invoke(c);
    }

    public void SetColor(Color c)
    {
        SyncHsv(c);
        _updating = true;
        Color = c;
        _updating = false;
        UpdateUI(c);
        ColorChanged?.Invoke(c);
    }

    private void ApplyColorToUI(Color c)
    {
        SyncHsv(c);
        UpdateUI(c);
    }

    /// <summary>takes HSV from <paramref name="c"/>, keeping the current hue for a grey and the saturation too for black, where RGB carries neither</summary>
    private void SyncHsv(Color c)
    {
        var (h, s, v) = ColorToHsv(c);
        if (v > 0)
        {
            _sat = s;
            if (s > 0) _hue = h;
        }
        _val   = v;
        _alpha = c.A;
    }

    private void UpdateUI(Color c)
    {
        _updating = true;
        try
        {
            SpectrumHueBg.Background = new SolidColorBrush(HsvToColor(_hue, 1, 1, 255));

            var sw = SpectrumPanel.Bounds.Width;
            var sh = SpectrumPanel.Bounds.Height;
            Canvas.SetLeft(SpectrumThumb, _sat * sw - 6);
            Canvas.SetTop (SpectrumThumb, (1 - _val) * sh - 6);

            Canvas.SetLeft(HueThumb, _hue / 360.0 * HuePanel.Bounds.Width - 2);

            var opaque = Color.FromArgb(255, c.R, c.G, c.B);
            var transp = Color.FromArgb(0,   c.R, c.G, c.B);
            AlphaGradientBorder.Background = new LinearGradientBrush
            {
                StartPoint    = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint      = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(transp, 0),
                    new GradientStop(opaque, 1),
                }
            };

            Canvas.SetLeft(AlphaThumb, _alpha / 255.0 * AlphaPanel.Bounds.Width - 2);

            PreviewBorder.Background = new SolidColorBrush(c);

            SetField(HexBox, ToHex(c));

            SetField(RBox, c.R.ToString());
            SetField(GBox, c.G.ToString());
            SetField(BBox, c.B.ToString());
            SetField(ABox, c.A.ToString());

            var (h, s, l) = HsvToHsl(_hue, _sat, _val);
            SetField(HBox, Math.Round(h).ToString(CultureInfo.InvariantCulture));
            SetField(SBox, Math.Round(s * 100).ToString(CultureInfo.InvariantCulture));
            SetField(LBox, Math.Round(l * 100).ToString(CultureInfo.InvariantCulture));
        }
        finally { _updating = false; }
    }

    private void SetField(TextBox box, string text)
    {
        if (box == _typingBox) return;
        box.Tag  = text;
        box.Text = text;
    }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>HEX digits without the #: RGB, RRGGBB, or AARRGGBB (GT's alpha-first order)</summary>
    private static bool TryParseHex(string digits, out Color color)
    {
        color = default;
        if (digits.Length == 3)
            digits = string.Concat(digits[0], digits[0], digits[1], digits[1], digits[2], digits[2]);
        if (digits.Length == 6) digits = "FF" + digits;
        if (digits.Length != 8
            || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var argb))
            return false;
        color = Color.FromUInt32(argb);
        return true;
    }

    /// <summary>parses #HEX (the # optional), rgb()/rgba() and hsl()/hsla(); a CSS alpha is 0-1 or a percentage</summary>
    public static bool TryParseColor(string text, out Color color)
    {
        color = default;
        text = text.Trim();

        int open = text.IndexOf('(');
        if (open < 0 || !text.EndsWith(')'))
            return TryParseHex(text.TrimStart('#'), out color);

        var func  = text[..open].Trim().ToLowerInvariant();
        bool rgb  = func is "rgb" or "rgba";
        var parts = text[(open + 1)..^1].Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4)) return false;

        var values = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.EndsWith("deg")) p = p[..^3];
            if (!double.TryParse(p.TrimEnd('%', '°'), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return false;

            if (!p.EndsWith('%')) continue;
            if (i == 3)   values[i] /= 100;   // alpha 50% → 0.5
            else if (rgb) values[i] *= 2.55;  // channel 50% → 127.5
        }

        byte a = parts.Length == 4 ? (byte)Math.Round(Math.Clamp(values[3], 0, 1) * 255) : (byte)255;

        if (rgb)
        {
            byte Channel(double v) => (byte)Math.Round(Math.Clamp(v, 0, 255));
            color = Color.FromArgb(a, Channel(values[0]), Channel(values[1]), Channel(values[2]));
            return true;
        }
        if (func is "hsl" or "hsla")
        {
            double h = ((values[0] % 360) + 360) % 360;
            var (s, v) = HslToHsv(Math.Clamp(values[1], 0, 100) / 100, Math.Clamp(values[2], 0, 100) / 100);
            color = HsvToColor(h, s, v, a);
            return true;
        }
        return false;
    }

    /// <summary>HSV → HSL, hue unchanged; s and l in 0-1</summary>
    public static (double h, double s, double l) HsvToHsl(double h, double s, double v)
    {
        double l  = v * (1 - s / 2);
        double sl = l is <= 0 or >= 1 ? 0 : (v - l) / Math.Min(l, 1 - l);
        return (h, sl, l);
    }

    /// <summary>HSL saturation and lightness (0-1) → HSV saturation and value; hue is the same in both</summary>
    public static (double s, double v) HslToHsv(double s, double l)
    {
        double v  = l + s * Math.Min(l, 1 - l);
        double sv = v <= 0 ? 0 : 2 * (1 - l / v);
        return (sv, v);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateUI(Color);
    }

    public static Color HsvToColor(double h, double s, double v, byte a)
    {
        if (s <= 0)
        {
            var g = (byte)Math.Round(v * 255);
            return Color.FromArgb(a, g, g, g);
        }
        h /= 60.0;
        int    i = (int)Math.Floor(h) % 6;
        double f = h - Math.Floor(h);
        double p = v * (1 - s);
        double q = v * (1 - f * s);
        double t = v * (1 - (1 - f) * s);
        double r, gd, b;
        switch (i)
        {
            case 0:  r = v;  gd = t;  b = p;  break;
            case 1:  r = q;  gd = v;  b = p;  break;
            case 2:  r = p;  gd = v;  b = t;  break;
            case 3:  r = p;  gd = q;  b = v;  break;
            case 4:  r = t;  gd = p;  b = v;  break;
            default: r = v;  gd = p;  b = q;  break;
        }
        // rounded, not truncated, so a colour typed in as HSL lands on the RGB it names
        return Color.FromArgb(a, (byte)Math.Round(r * 255), (byte)Math.Round(gd * 255), (byte)Math.Round(b * 255));
    }

    public static (double h, double s, double v) ColorToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max   = Math.Max(r, Math.Max(g, b));
        double min   = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double h = 0, s = 0, v = max;
        if (delta > 1e-9)
        {
            s = delta / max;
            if      (max == r) h = ((g - b) / delta + (g < b ? 6 : 0)) * 60;
            else if (max == g) h = ((b - r) / delta + 2) * 60;
            else               h = ((r - g) / delta + 4) * 60;
        }
        return (h, s, v);
    }
}
