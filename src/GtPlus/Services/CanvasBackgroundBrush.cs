using Avalonia;
using Avalonia.Media;

namespace GtPlus.Services;

/// <summary>what the canvas paints behind the document</summary>
public enum CanvasBackgroundMode
{
    /// <summary>one flat colour</summary>
    Solid,
    /// <summary>two colours alternating in a checker grid, the way a transparency backdrop is drawn</summary>
    Grid,
}

/// <summary>builds the brush the canvas paints behind the document from the user's preferences</summary>
public static class CanvasBackgroundBrush
{
    public static IBrush Build(PreferencesService prefs)
        => prefs.CanvasBackground == CanvasBackgroundMode.Grid
            ? BuildGrid(prefs.CanvasGridColor, prefs.CanvasGridSecondaryColor, prefs.CanvasGridSize)
            : new SolidColorBrush(prefs.CanvasSolidColor);

    /// <summary>a checkerboard tile brush; <paramref name="cell"/> is one square in document pixels, so the pattern scales with zoom</summary>
    public static IBrush BuildGrid(Color primary, Color secondary, double cell)
    {
        if (cell < 1) cell = 1;
        var tile = cell * 2;

        // one tile is the primary colour with two secondary squares on the diagonal
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing
        {
            Brush    = new SolidColorBrush(primary),
            Geometry = new RectangleGeometry(new Rect(0, 0, tile, tile)),
        });
        var second = new SolidColorBrush(secondary);
        drawing.Children.Add(new GeometryDrawing
        {
            Brush    = second,
            Geometry = new RectangleGeometry(new Rect(0, 0, cell, cell)),
        });
        drawing.Children.Add(new GeometryDrawing
        {
            Brush    = second,
            Geometry = new RectangleGeometry(new Rect(cell, cell, cell, cell)),
        });

        return new DrawingBrush
        {
            Drawing         = drawing,
            TileMode        = TileMode.Tile,
            Stretch         = Stretch.Fill,
            SourceRect      = new RelativeRect(0, 0, tile, tile, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, tile, tile, RelativeUnit.Absolute),
        };
    }
}
