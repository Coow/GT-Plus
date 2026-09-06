using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GtPlus.Views;

/// <summary>works out how long an ImageSequence clip has to run for its frames to play at a given rate: an ImageSequence animation fits the whole sequence into its Duration, so the clip length is frameCount / fps</summary>
public partial class SequenceLengthWindow : Window
{
    private readonly int _frameCount;

    /// <summary>frame rate the user settled on, so the caller can offer it again next time</summary>
    public double Fps { get; private set; }

    /// <summary>clip length in seconds the frame count and frame rate imply</summary>
    public double Duration => _frameCount / Math.Max(1.0, Fps);

    // parameterless ctor for the XAML runtime loader, the app always uses the one below
    public SequenceLengthWindow() : this("Sequence", 1, 60, 1) { }

    /// <param name="objectName">the animated object, shown so a multi-clip edit is unambiguous</param>
    /// <param name="frameCount">frames behind the sequence's anchor</param>
    /// <param name="defaultFps">frame rate to start on, 60 unless the user has already picked one</param>
    /// <param name="currentDuration">the clip's present length, shown for comparison</param>
    public SequenceLengthWindow(string objectName, int frameCount, double defaultFps, double currentDuration)
    {
        InitializeComponent();

        _frameCount = Math.Max(1, frameCount);
        Fps         = defaultFps;

        HeaderText.Text  = string.IsNullOrEmpty(objectName) ? "Image sequence" : objectName;
        FramesText.Text  = $"{_frameCount} frame{(_frameCount == 1 ? "" : "s")} in this sequence";
        CurrentText.Text = $"Clip is currently {Fmt(currentDuration)} s long.";

        FpsBox.Value = (decimal)defaultFps;
        UpdateResult();
    }

    private void Fps_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (FpsBox.Value is { } value) Fps = (double)value;
        UpdateResult();
    }

    private void UpdateResult()
    {
        if (ResultText is null) return;
        ResultText.Text = $"{_frameCount} ÷ {Fmt(Fps)} fps = {Fmt(Duration)} s";
    }

    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (FpsBox.Value is { } value) Fps = (double)value;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
