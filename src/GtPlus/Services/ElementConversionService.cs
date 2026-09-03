using GtPlus.Models;

namespace GtPlus.Services;

/// <summary>the element types the editor can create, and therefore convert between</summary>
public enum GtElementKind { Text, Rectangle, Ellipse, Image, Ticker }

/// <summary>rebuilds an element as a different type, carrying over everything the two types share; the name is kept verbatim since animations and <see cref="GtElement.MaskObject"/> references resolve by name, so a converted element must answer to the same one</summary>
public static class ElementConversionService
{
    public static GtElementKind? KindOf(GtElement element) => element switch
    {
        GtTickerElement   => GtElementKind.Ticker,
        GtTextBlock       => GtElementKind.Text,
        GtRectangleElement=> GtElementKind.Rectangle,
        GtEllipseElement  => GtElementKind.Ellipse,
        GtImageElement    => GtElementKind.Image,
        _                 => null,
    };

    public static string DisplayName(GtElementKind kind) => kind switch
    {
        GtElementKind.Text      => "Text",
        GtElementKind.Rectangle => "Rectangle",
        GtElementKind.Ellipse   => "Ellipse",
        GtElementKind.Ticker    => "Ticker",
        _                       => "Image",
    };

    /// <summary>a new element of <paramref name="target"/> type carrying <paramref name="source"/>'s geometry, base properties and, where both types have them, fill, stroke and dash style; returns null when the source is already that type, <paramref name="bitmapSource"/> is the logical asset path to use when converting to an image</summary>
    public static GtElement? Convert(GtElement source, GtElementKind target, string? bitmapSource = null)
    {
        if (KindOf(source) == target) return null;

        var (sourceFill, stroke, thickness, dash) = Appearance(source);
        var fill = sourceFill?.Clone();

        GtElement result = target switch
        {
            GtElementKind.Ticker => NewTicker(source, fill, stroke, thickness),
            GtElementKind.Text => new GtTextBlock
            {
                Text            = source.Name,
                FontFamily      = "Arial",
                FontSize        = 36,
                // a source with no fill would convert to invisible text
                Fill            = fill ?? new GtBrush { Type = GtBrushType.Solid, Color = Avalonia.Media.Colors.White },
                Stroke          = stroke?.Clone(),
                StrokeThickness = thickness,
            },
            GtElementKind.Rectangle => new GtRectangleElement
            {
                Fill            = fill ?? DefaultShapeFill(),
                Stroke          = stroke?.Clone(),
                StrokeThickness = thickness,
                StrokeDashStyle = dash,
            },
            GtElementKind.Ellipse => new GtEllipseElement
            {
                Fill            = fill ?? DefaultShapeFill(),
                Stroke          = stroke?.Clone(),
                StrokeThickness = thickness,
                StrokeDashStyle = dash,
            },
            _ => new GtImageElement
            {
                BitmapSource = bitmapSource,
                // the box comes from the replaced element, so stretch rather than crop to it
                SizeMode     = GtImageSizeMode.Stretch,
            },
        };

        CopyBase(source, result);
        return result;
    }

    /// <summary>a ticker built from another element; converting a text block keeps its whole text/font set, the ticker owns the same properties and scrolls the text it was showing</summary>
    private static GtTickerElement NewTicker(GtElement source, GtBrush? fill,
                                             GtBrush? stroke, double thickness)
    {
        if (source is GtTextBlock text)
        {
            var ticker = new GtTickerElement();
            text.CopyTextPropertiesTo(ticker);
            return ticker;
        }

        return new GtTickerElement
        {
            Text            = source.Name,
            FontFamily      = "Arial",
            FontSize        = 36,
            Fill            = fill ?? new GtBrush { Type = GtBrushType.Solid, Color = Avalonia.Media.Colors.White },
            Stroke          = stroke?.Clone(),
            StrokeThickness = thickness,
        };
    }

    private static GtBrush DefaultShapeFill() =>
        new() { Type = GtBrushType.Solid, Color = Avalonia.Media.Colors.Red };

    private static (GtBrush? Fill, GtBrush? Stroke, double Thickness, GtStrokeDashStyle Dash) Appearance(
        GtElement element) => element switch
    {
        GtTextBlock t        => (t.Fill, t.Stroke, t.StrokeThickness, GtStrokeDashStyle.Solid),
        GtRectangleElement r => (r.Fill, r.Stroke, r.StrokeThickness, r.StrokeDashStyle),
        GtEllipseElement e   => (e.Fill, e.Stroke, e.StrokeThickness, e.StrokeDashStyle),
        _                    => (null, null, 0.0, GtStrokeDashStyle.Solid),
    };

    private static void CopyBase(GtElement src, GtElement dst)
    {
        dst.Name       = src.Name;
        dst.Location   = src.Location;
        dst.Z          = src.Z;
        dst.Dimensions = src.Dimensions;
        dst.Depth      = src.Depth;
        dst.RotateX    = src.RotateX;
        dst.RotateY    = src.RotateY;
        dst.RotateZ    = src.RotateZ;
        dst.Visible    = src.Visible;
        dst.Opacity    = src.Opacity;
        dst.Locked     = src.Locked;
        dst.DataFlags  = src.DataFlags;
        dst.MaskObject = src.MaskObject;
        dst.Crop       = src.Crop?.Clone();
        dst.Bounding   = src.Bounding?.Clone();
    }
}
