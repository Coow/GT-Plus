using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

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

        SetButtonIcon(CopyHexButton,  "copy.png");
        SetButtonIcon(PasteHexButton, "paste.png");

        SpectrumPanel.AddHandler(PointerPressedEvent,  Spectrum_Pressed,  RoutingStrategies.Tunnel);
        SpectrumPanel.AddHandler(PointerMovedEvent,    Spectrum_Moved,    RoutingStrategies.Bubble);
        SpectrumPanel.AddHandler(PointerReleasedEvent, Spectrum_Released, RoutingStrategies.Bubble);

        HuePanel.AddHandler(PointerPressedEvent,  Hue_Pressed,  RoutingStrategies.Tunnel);
        HuePanel.AddHandler(PointerMovedEvent,    Hue_Moved,    RoutingStrategies.Bubble);
        HuePanel.AddHandler(PointerReleasedEvent, Hue_Released, RoutingStrategies.Bubble);

        AlphaPanel.AddHandler(PointerPressedEvent,  Alpha_Pressed,  RoutingStrategies.Tunnel);
        AlphaPanel.AddHandler(PointerMovedEvent,    Alpha_Moved,    RoutingStrategies.Bubble);
        AlphaPanel.AddHandler(PointerReleasedEvent, Alpha_Released, RoutingStrategies.Bubble);
    }

    /// <summary>sets the button content to the Assets/Icons/{filename} bitmap</summary>
    private static void SetButtonIcon(Button button, string filename)
    {
        button.Content = new Image
        {
            Source              = LoadIcon(filename),
            Stretch             = Stretch.Uniform,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    private static Bitmap LoadIcon(string filename)
    {
        var uri = new Uri($"avares://GtPlus/Assets/Icons/{filename}");
        using var stream = AssetLoader.Open(uri);
        return new Bitmap(stream);
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
    }

    private void CopyHexButton_Click(object? sender, RoutedEventArgs e)
    {
        var text = HexText.Text ?? "";
        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    private async void PasteHexButton_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (!text.StartsWith("#")) text = "#" + text;
        if (text.Length == 7) text = "#FF" + text.Substring(1);
        try { SetColor(Color.Parse(text)); }
        catch { }
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
        (_hue, _sat, _val) = ColorToHsv(c);
        _alpha = c.A;
        _updating = true;
        Color = c;
        _updating = false;
        UpdateUI(c);
        ColorChanged?.Invoke(c);
    }

    private void ApplyColorToUI(Color c)
    {
        (_hue, _sat, _val) = ColorToHsv(c);
        _alpha = c.A;
        UpdateUI(c);
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

            HexText.Text = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

            RText.Text = c.R.ToString();
            GText.Text = c.G.ToString();
            BText.Text = c.B.ToString();
            AText.Text = c.A.ToString();
        }
        finally { _updating = false; }
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
            var g = (byte)(v * 255);
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
        return Color.FromArgb(a, (byte)(r * 255), (byte)(gd * 255), (byte)(b * 255));
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
