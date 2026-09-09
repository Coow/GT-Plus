using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using GtPlus.Controls;
using GtPlus.Services;

namespace GtPlus.Views;

public partial class PreferencesWindow : Window
{
    /// <summary>which of the three background swatches the shared picker is editing</summary>
    private enum Swatch { Solid, GridPrimary, GridSecondary }

    private readonly PreferencesService _prefs;
    private readonly GtCanvasControl _canvas;
    private readonly Action<bool>? _onDebugToggled;
    private readonly Action? _onCheckUpdates;
    private bool _suppressSlider;

    private Popup? _colorPopup;
    private ColorPickerControl? _colorPicker;
    private Swatch _editing;
    private bool _suppressPicker;

    public PreferencesWindow(PreferencesService prefs, GtCanvasControl canvas, Action<bool>? onDebugToggled = null,
                             Action? onCheckUpdates = null)
    {
        InitializeComponent();
        _prefs           = prefs;
        _canvas          = canvas;
        _onDebugToggled  = onDebugToggled;
        _onCheckUpdates  = onCheckUpdates;

        _suppressSlider          = true;
        TransparencySlider.Value = prefs.OutsideCanvasTransparency * 100;
        _suppressSlider          = false;
        UpdateLabel();

        _suppressSlider               = true;
        LayerTransparencySlider.Value = prefs.OutsideLayerTransparency * 100;
        _suppressSlider               = false;
        UpdateLayerLabel();

        _suppressSlider     = true;
        NudgeStepBox.Value  = (decimal)prefs.NudgeLargeStep;
        _suppressSlider     = false;

        DebugPanelCheck.IsChecked      = prefs.ShowDebugPanel;
        StartupUpdateCheck.IsChecked   = prefs.CheckUpdatesOnStartup;
        AutoFitTimelineCheck.IsChecked = prefs.AutoFitOnTimelineOpen;

        _suppressSlider           = true;
        SnapDistanceSlider.Value  = prefs.SnapDistance;
        _suppressSlider           = false;
        UpdateSnapLabel();

        _suppressSlider              = true;
        SolidBackgroundRadio.IsChecked = prefs.CanvasBackground == CanvasBackgroundMode.Solid;
        GridBackgroundRadio.IsChecked  = prefs.CanvasBackground == CanvasBackgroundMode.Grid;
        GridSizeBox.Value              = (decimal)prefs.CanvasGridSize;
        _suppressSlider              = false;
        UpdateBackgroundUI();
    }

    private void BackgroundMode_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressSlider) return;

        _prefs.CanvasBackground = GridBackgroundRadio.IsChecked == true
            ? CanvasBackgroundMode.Grid
            : CanvasBackgroundMode.Solid;
        _prefs.Save();
        UpdateBackgroundUI();
    }

    private void GridSizeBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_suppressSlider) return;

        _prefs.CanvasGridSize = (double)(e.NewValue ?? 32m);
        _prefs.Save();
        ApplyBackground();
    }

    /// <summary>repaints the swatches, greys out the half of the section the current mode does not use, and pushes the brush to the canvas</summary>
    private void UpdateBackgroundUI()
    {
        SolidColorSwatch.Background         = new SolidColorBrush(_prefs.CanvasSolidColor);
        GridColorSwatch.Background          = new SolidColorBrush(_prefs.CanvasGridColor);
        GridSecondaryColorSwatch.Background = new SolidColorBrush(_prefs.CanvasGridSecondaryColor);

        var grid = _prefs.CanvasBackground == CanvasBackgroundMode.Grid;
        SolidBackgroundPanel.IsEnabled = !grid;
        GridBackgroundPanel.IsEnabled  = grid;

        ApplyBackground();
    }

    private void ApplyBackground() => _canvas.BackgroundBrush = CanvasBackgroundBrush.Build(_prefs);

    private void SolidColorButton_Click(object? sender, RoutedEventArgs e)
        => OpenColorPicker(Swatch.Solid, SolidColorButton);

    private void GridColorButton_Click(object? sender, RoutedEventArgs e)
        => OpenColorPicker(Swatch.GridPrimary, GridColorButton);

    private void GridSecondaryColorButton_Click(object? sender, RoutedEventArgs e)
        => OpenColorPicker(Swatch.GridSecondary, GridSecondaryColorButton);

    private void OpenColorPicker(Swatch swatch, Control target)
    {
        _editing = swatch;

        if (_colorPicker is null)
        {
            _colorPicker = new ColorPickerControl { ShowGradientButton = false };
            _colorPicker.ColorChanged += OnBackgroundColorChanged;
        }

        if (_colorPopup is null)
        {
            _colorPopup = new Popup
            {
                Child                 = _colorPicker,
                Placement             = PlacementMode.Bottom,
                IsLightDismissEnabled = true,
            };
            _colorPopup.Closed += (_, _) => ColorPickerControl.AddToRecent(_colorPicker.Color);
        }

        // seeding the picker raises ColorChanged, which must not count as an edit
        _suppressPicker = true;
        _colorPicker.SetColor(CurrentColor(swatch));
        _suppressPicker = false;
        _colorPicker.RefreshRecentColors();

        _colorPopup.PlacementTarget = target;
        _colorPopup.IsOpen          = true;
    }

    private Color CurrentColor(Swatch swatch) => swatch switch
    {
        Swatch.GridPrimary   => _prefs.CanvasGridColor,
        Swatch.GridSecondary => _prefs.CanvasGridSecondaryColor,
        _                    => _prefs.CanvasSolidColor,
    };

    private void OnBackgroundColorChanged(Color c)
    {
        if (_suppressPicker) return;

        switch (_editing)
        {
            case Swatch.GridPrimary:   _prefs.CanvasGridColor          = c; break;
            case Swatch.GridSecondary: _prefs.CanvasGridSecondaryColor = c; break;
            default:                   _prefs.CanvasSolidColor         = c; break;
        }
        _prefs.Save();
        UpdateBackgroundUI();
    }

    private void SnapDistanceSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlider) return;

        _prefs.SnapDistance  = e.NewValue;
        _canvas.SnapDistance = e.NewValue;
        _prefs.Save();
        UpdateSnapLabel();
    }

    private void UpdateSnapLabel() =>
        SnapDistanceLabel.Text = $"{SnapDistanceSlider.Value:F0}px";

    private void NudgeStepBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_suppressSlider) return;

        _prefs.NudgeLargeStep = (double)(e.NewValue ?? 10m);
        _prefs.Save();
    }

    private void TransparencySlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlider) return;

        _prefs.OutsideCanvasTransparency = e.NewValue / 100.0;
        _canvas.OutsideCanvasOpacity     = _prefs.OutsideCanvasOpacity;
        _prefs.Save();
        UpdateLabel();
    }

    private void UpdateLabel() =>
        TransparencyLabel.Text = $"{TransparencySlider.Value:F0}%";

    private void LayerTransparencySlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlider) return;

        _prefs.OutsideLayerTransparency = e.NewValue / 100.0;
        _canvas.OutsideLayerOpacity     = _prefs.OutsideLayerOpacity;
        _prefs.Save();
        UpdateLayerLabel();
    }

    private void UpdateLayerLabel() =>
        LayerTransparencyLabel.Text = $"{LayerTransparencySlider.Value:F0}%";

    private void DebugPanelCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var isOn = DebugPanelCheck.IsChecked == true;
        _prefs.ShowDebugPanel = isOn;
        _prefs.Save();
        _onDebugToggled?.Invoke(isOn);
    }

    private void AutoFitTimelineCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        _prefs.AutoFitOnTimelineOpen = AutoFitTimelineCheck.IsChecked == true;
        _prefs.Save();
    }

    private void StartupUpdateCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        _prefs.CheckUpdatesOnStartup = StartupUpdateCheck.IsChecked == true;
        _prefs.Save();
    }

    private void CheckUpdates_Click(object? sender, RoutedEventArgs e) => _onCheckUpdates?.Invoke();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
