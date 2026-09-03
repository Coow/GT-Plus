using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using VmixGtPlus.Controls;
using VmixGtPlus.Services;

namespace VmixGtPlus.Views;

public partial class PreferencesWindow : Window
{
    private readonly PreferencesService _prefs;
    private readonly GtCanvasControl _canvas;
    private readonly Action<bool>? _onDebugToggled;
    private bool _suppressSlider;

    public PreferencesWindow(PreferencesService prefs, GtCanvasControl canvas, Action<bool>? onDebugToggled = null)
    {
        InitializeComponent();
        _prefs           = prefs;
        _canvas          = canvas;
        _onDebugToggled  = onDebugToggled;

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
        AutoFitTimelineCheck.IsChecked = prefs.AutoFitOnTimelineOpen;

        _suppressSlider           = true;
        SnapDistanceSlider.Value  = prefs.SnapDistance;
        _suppressSlider           = false;
        UpdateSnapLabel();
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

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
