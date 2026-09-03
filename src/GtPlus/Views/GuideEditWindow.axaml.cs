using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GtPlus.Models;

namespace GtPlus.Views;

/// <summary>result of the guide editor: what the host should do with the guide</summary>
public enum GuideEditResult { Cancel, Apply, Delete }

/// <summary>precise editor for one guide, typed pixel position or delete; opened by double-clicking a guide on the canvas</summary>
public partial class GuideEditWindow : Window
{
    private readonly GtGuide _guide;

    /// <summary>position typed by the user, only meaningful for <see cref="GuideEditResult.Apply"/></summary>
    public double GuidePosition { get; private set; }

    // parameterless ctor for the XAML runtime loader, the app always uses the one below
    public GuideEditWindow() : this(new GtGuide(GtGuideOrientation.Horizontal, 0), 1920, 1080) { }

    public GuideEditWindow(GtGuide guide, double canvasWidth, double canvasHeight)
    {
        InitializeComponent();
        _guide   = guide;
        GuidePosition = guide.Position;

        var vertical = guide.Orientation == GtGuideOrientation.Vertical;
        HeaderText.Text = vertical ? "Vertical guide" : "Horizontal guide";
        AxisLabel.Text  = vertical ? "X position (px)" : "Y position (px)";
        RangeHint.Text  = $"Canvas {canvasWidth:0.##} × {canvasHeight:0.##} px - " +
                          $"0 to {(vertical ? canvasWidth : canvasHeight):0.##} stays on canvas.";

        PositionBox.Value = (decimal)guide.Position;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        GuidePosition = (double)(PositionBox.Value ?? (decimal)_guide.Position);
        Close(GuideEditResult.Apply);
    }

    private void Delete_Click(object? sender, RoutedEventArgs e) => Close(GuideEditResult.Delete);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(GuideEditResult.Cancel);
}
