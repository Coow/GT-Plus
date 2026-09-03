using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;

namespace GtPlus.Models;

public record GtPoint(double X, double Y)
{
    public static readonly GtPoint Zero = new(0, 0);

    public static GtPoint Parse(string? value)
    {
        if (value is null) return Zero;
        var p = value.Split(',');
        if (p.Length < 2) return Zero;
        return new(
            double.Parse(p[0], CultureInfo.InvariantCulture),
            double.Parse(p[1], CultureInfo.InvariantCulture));
    }
}

public record GtSize(double Width, double Height)
{
    public static GtSize Parse(string? value)
    {
        if (value is null) return new(0, 0);
        var p = value.Split(',');
        if (p.Length < 2) return new(0, 0);
        return new(
            double.Parse(p[0], CultureInfo.InvariantCulture),
            double.Parse(p[1], CultureInfo.InvariantCulture));
    }
}

public enum GtStrokeDashStyle { Solid, Dash, Dot, DashDot, DashDotDot }
public enum GtRectangleStyle  { Rounded, Square }

/// <summary>rectangular crop with per-edge feathering, serialized as <c>&lt;ElementType.Crop&gt;&lt;Crop Range="x0,y0,x1,y1" Feather="l,t,r,b"/&gt;&lt;/ElementType.Crop&gt;</c>; the two ranges use different units as GT does, <see cref="X0"/>..<see cref="Y1"/> are normalised to the element box (0-1) while the feather values are pixels of that box; feather widens the visible area past each crop line rather than eating into it and does nothing on an edge the range does not cut</summary>
public class GtCrop
{
    /// <summary>feather GT applies to a fresh crop, and to an absent Feather attribute</summary>
    public const double DefaultFeather = 8;

    public double X0 { get; set; }
    public double Y0 { get; set; }
    public double X1 { get; set; } = 1;
    public double Y1 { get; set; } = 1;

    public double FeatherLeft   { get; set; } = DefaultFeather;
    public double FeatherTop    { get; set; } = DefaultFeather;
    public double FeatherRight  { get; set; } = DefaultFeather;
    public double FeatherBottom { get; set; } = DefaultFeather;

    public bool RangeIsFull => X0 <= 0 && Y0 <= 0 && X1 >= 1 && Y1 >= 1;

    public bool HasFeather =>
        FeatherLeft > 0 || FeatherTop > 0 || FeatherRight > 0 || FeatherBottom > 0;

    /// <summary>true when the crop changes nothing, so it need not be written or rendered</summary>
    public bool IsDefault => RangeIsFull && !HasFeather;

    public GtCrop Clone() => new()
    {
        X0 = X0, Y0 = Y0, X1 = X1, Y1 = Y1,
        FeatherLeft  = FeatherLeft,  FeatherTop    = FeatherTop,
        FeatherRight = FeatherRight, FeatherBottom = FeatherBottom,
    };

    public static bool AreEqual(GtCrop? a, GtCrop? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.X0 == b.X0 && a.Y0 == b.Y0 && a.X1 == b.X1 && a.Y1 == b.Y1
            && a.FeatherLeft  == b.FeatherLeft  && a.FeatherTop    == b.FeatherTop
            && a.FeatherRight == b.FeatherRight && a.FeatherBottom == b.FeatherBottom;
    }
}

public enum GtBrushType { Solid, LinearGradient, RadialGradient, Bitmap }
public enum GtRadialWrap { Mirror, Clamp, Wrap }

public class GtGradientStop
{
    public double Position { get; set; }
    public Color Color { get; set; }

    public GtGradientStop Clone() => new() { Position = Position, Color = Color };
}

public class GtBrush
{
    public GtBrushType Type { get; set; } = GtBrushType.Solid;
    public Color Color { get; set; } = Colors.Transparent;
    public GtPoint StartPoint { get; set; } = new(0.5, 0);
    public GtPoint EndPoint { get; set; } = new(0.5, 1);
    public List<GtGradientStop> Stops { get; set; } = new();
    public string? BitmapSource { get; set; }  // logical path key into asset dict
    public GtRadialWrap WrapMode { get; set; } = GtRadialWrap.Mirror;

    public GtBrush Clone()
    {
        var copy = new GtBrush
        {
            Type         = Type,
            Color        = Color,
            StartPoint   = StartPoint,
            EndPoint     = EndPoint,
            BitmapSource = BitmapSource,
            WrapMode     = WrapMode,
        };
        foreach (var s in Stops) copy.Stops.Add(s.Clone());
        return copy;
    }

    public static bool AreEqual(GtBrush? a, GtBrush? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Type != b.Type || a.Color != b.Color) return false;
        if (a.StartPoint != b.StartPoint || a.EndPoint != b.EndPoint) return false;
        if (a.BitmapSource != b.BitmapSource || a.WrapMode != b.WrapMode) return false;
        if (a.Stops.Count != b.Stops.Count) return false;
        for (int i = 0; i < a.Stops.Count; i++)
            if (a.Stops[i].Position != b.Stops[i].Position || a.Stops[i].Color != b.Stops[i].Color)
                return false;
        return true;
    }
}

/// <summary>binds an element's box to another element's box plus per-edge padding, serialized as <c>&lt;ElementType.Bounding&gt;&lt;Bounding Object="Name" Padding="l,t,r,b"/&gt;&lt;/ElementType.Bounding&gt;</c>; padding is in composition pixels and grows the destination outward, so <c>10,10,10,10</c> makes it 10px larger than its source on every side</summary>
/// <remarks>the binding is one-way and re-applied every frame, dragging the destination moves it until the next resolve pass snaps it back onto its source; GT allows exactly one edge of dependency (a source that is itself bound makes the whole binding a no-op) which is also its only cycle guard; see <see cref="GtBoundingResolver"/></remarks>
public class GtBounding
{
    /// <summary>name of the source element, or null/empty when the binding is off</summary>
    public string? Object { get; set; }

    public double PaddingLeft   { get; set; }
    public double PaddingTop    { get; set; }
    public double PaddingRight  { get; set; }
    public double PaddingBottom { get; set; }

    public bool HasSource => !string.IsNullOrEmpty(Object);

    public bool HasPadding =>
        PaddingLeft != 0 || PaddingTop != 0 || PaddingRight != 0 || PaddingBottom != 0;

    /// <summary>true when the binding carries nothing worth writing to the file</summary>
    public bool IsDefault => !HasSource && !HasPadding;

    public GtBounding Clone() => new()
    {
        Object        = Object,
        PaddingLeft   = PaddingLeft,
        PaddingTop    = PaddingTop,
        PaddingRight  = PaddingRight,
        PaddingBottom = PaddingBottom
    };

    public static bool AreEqual(GtBounding? a, GtBounding? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.Object, b.Object, StringComparison.Ordinal)
            && a.PaddingLeft   == b.PaddingLeft
            && a.PaddingTop    == b.PaddingTop
            && a.PaddingRight  == b.PaddingRight
            && a.PaddingBottom == b.PaddingBottom;
    }
}

/// <summary>vMix data-binding behaviour, combinable; <c>document.xml</c> stores them as a comma-separated list, e.g. <c>DataFlags="Hidden, NoEvents, ShowVisible"</c></summary>
[Flags]
public enum GtDataFlags
{
    None        = 0,
    /// <summary>hidden from the vMix title editor</summary>
    Hidden      = 1,
    /// <summary>element does not raise click/mouse events in vMix</summary>
    NoEvents    = 2,
    /// <summary>exposed in the vMix title editor with a visibility toggle</summary>
    ShowVisible = 4
}

/// <summary>which point of an element's box its position refers to; GT stores this as the element's <c>Anchor</c> attribute (the names here are its own) and writes <c>Location</c> as the coordinates of that point, omitting the attribute for the default <see cref="TopLeft"/>; the editor holds <see cref="GtElement.Location"/> as the top-left corner throughout and converts at the file boundary, see <see cref="GtAnchorMath"/>; ordinals are row-major (left, centre, right per row), the anchor math depends on it</summary>
public enum GtAnchor
{
    TopLeft,     TopCenter,    TopRight,
    MiddleLeft,  MiddleCenter, MiddleRight,
    BottomLeft,  BottomCenter, BottomRight
}

public abstract class GtElement
{
    public string Name { get; set; } = "";
    public GtPoint Location { get; set; } = GtPoint.Zero;
    public double Z { get; set; } = 0;
    public GtSize Dimensions { get; set; } = new(0, 0);
    public double Depth { get; set; } = 0;
    public double RotateX { get; set; } = 0;
    public double RotateY { get; set; } = 0;
    public double RotateZ { get; set; } = 0;
    public bool Visible { get; set; } = true;
    public double Opacity { get; set; } = 1.0;
    public bool Locked { get; set; }
    public GtDataFlags DataFlags { get; set; } = GtDataFlags.None;

    /// <summary>point of the box the position is measured from; <see cref="Location"/> stays the top-left corner in the editor whatever this is, see <see cref="GtAnchor"/></summary>
    public GtAnchor Anchor { get; set; } = GtAnchor.TopLeft;

    /// <summary>name of another element in the same layer whose geometry clips this one</summary>
    public string? MaskObject { get; set; }
    /// <summary>rectangular crop plus edge feather, null when the element carries no Crop child</summary>
    public GtCrop? Crop { get; set; }
    /// <summary>box binding to another element in the same layer, null when the element carries no Bounding child</summary>
    public GtBounding? Bounding { get; set; }

    /// <summary>deep copy of every property including brushes and crop; <see cref="Name"/> is copied verbatim, so callers that add the copy to a document must give it a unique name and clone the animations targeting the original separately (they live on the storyboards)</summary>
    public abstract GtElement Clone();

    protected void CopyBaseTo(GtElement target)
    {
        target.Name       = Name;
        target.Location   = Location;
        target.Z          = Z;
        target.Dimensions = Dimensions;
        target.Depth      = Depth;
        target.RotateX    = RotateX;
        target.RotateY    = RotateY;
        target.RotateZ    = RotateZ;
        target.Visible    = Visible;
        target.Opacity    = Opacity;
        target.Locked     = Locked;
        target.DataFlags  = DataFlags;
        target.Anchor     = Anchor;
        target.MaskObject = MaskObject;
        target.Crop       = Crop?.Clone();
        target.Bounding   = Bounding?.Clone();
    }
}

/// <summary>anchor arithmetic; in the editor <see cref="GtElement.Location"/> is always the top-left corner, and these helpers translate between that and the point the chosen <see cref="GtAnchor"/> names (which is what the file stores and what the properties bar shows) so the box on screen never moves when the anchor changes, only the numbers reported for it do</summary>
public static class GtAnchorMath
{
    /// <summary>fraction across the box: 0 left, 0.5 centre, 1 right</summary>
    public static double FractionX(this GtAnchor anchor) => ((int)anchor % 3) * 0.5;

    /// <summary>fraction down the box: 0 top, 0.5 middle, 1 bottom</summary>
    public static double FractionY(this GtAnchor anchor) => ((int)anchor / 3) * 0.5;

    /// <summary>layer-local X of the element's anchor point</summary>
    public static double AnchorX(this GtElement el)
        => el.Location.X + el.Anchor.FractionX() * el.Dimensions.Width;

    /// <summary>layer-local Y of the element's anchor point</summary>
    public static double AnchorY(this GtElement el)
        => el.Location.Y + el.Anchor.FractionY() * el.Dimensions.Height;

    /// <summary>moves the element so its anchor point lands on <paramref name="x"/></summary>
    public static void SetAnchorX(this GtElement el, double x)
        => el.Location = new GtPoint(x - el.Anchor.FractionX() * el.Dimensions.Width, el.Location.Y);

    /// <summary>moves the element so its anchor point lands on <paramref name="y"/></summary>
    public static void SetAnchorY(this GtElement el, double y)
        => el.Location = new GtPoint(el.Location.X, y - el.Anchor.FractionY() * el.Dimensions.Height);

    /// <summary>resizes the width around the anchor: the anchor point stays put and the box grows away from it, so a top-left anchor still grows right and a centred one grows both ways</summary>
    public static void SetWidthAboutAnchor(this GtElement el, double width)
    {
        var ax = el.AnchorX();
        el.Dimensions = new GtSize(width, el.Dimensions.Height);
        el.SetAnchorX(ax);
    }

    /// <summary>height counterpart of <see cref="SetWidthAboutAnchor"/></summary>
    public static void SetHeightAboutAnchor(this GtElement el, double height)
    {
        var ay = el.AnchorY();
        el.Dimensions = new GtSize(el.Dimensions.Width, height);
        el.SetAnchorY(ay);
    }
}

public enum GtTextAlign { Left, Center, Right }
public enum GtVerticalAlign { Top, Center, Bottom }

/// <summary>how a text box sizes itself around its text; ordinals are GT's own (<c>GraphicsAutoSize</c>) and are what the file round-trips so they must not move; <see cref="Width"/>/<see cref="Height"/>/<see cref="WidthAndHeight"/> grow the box to fit the text, <see cref="Shrink"/> shrinks the text to fit the box, and <see cref="Fixed"/> does neither and lets the text overflow</summary>
public enum GtAutoSize
{
    Fixed          = 0,
    Width          = 1,
    Height         = 2,
    Shrink         = 3,
    WidthAndHeight = 4
}

public class GtTextBlock : GtElement
{
    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = "Arial";
    public double FontSize { get; set; } = 36;
    public FontWeight FontWeight { get; set; } = FontWeight.Normal;
    public FontStyle FontStyle { get; set; } = FontStyle.Normal;
    public GtTextAlign TextAlign { get; set; } = GtTextAlign.Left;
    public GtVerticalAlign VerticalAlign { get; set; } = GtVerticalAlign.Top;
    public bool Uppercase { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }
    /// <summary>raw GT line spacing; 0 (the default for a new object) is a sentinel meaning "font metrics", 0-2 is a proportional multiplier, above 2 it flips to an absolute line height in DIPs; kept raw so files round-trip, see GtCanvasControl.LineBox</summary>
    public double LineSpacing { get; set; } = 0.0;
    public bool IgnoreOverhang { get; set; }
    public bool NoWrap { get; set; }

    /// <summary>automatic box sizing; GT hangs this off <c>TextObject</c> only so a Ticker never exposes it to the user, it forces its own value on the clones it scrolls (<see cref="GtTickerElement.CreateChunk"/>); default for a new text box is <see cref="GtAutoSize.Fixed"/></summary>
    public GtAutoSize AutoSize { get; set; } = GtAutoSize.Fixed;

    public GtBrush? Fill { get; set; }
    public GtBrush? Stroke { get; set; }
    public double StrokeThickness { get; set; } = 0.0;

    public override GtElement Clone()
    {
        var copy = new GtTextBlock();
        CopyTextPropertiesTo(copy);
        CopyBaseTo(copy);
        return copy;
    }

    /// <summary>copies every text property onto <paramref name="target"/>; split out of <see cref="Clone"/> so <see cref="GtTickerElement"/> (which inherits the whole text property set) can reuse it, and so the ticker can build the throwaway text objects it scrolls</summary>
    public void CopyTextPropertiesTo(GtTextBlock target)
    {
        target.Text            = Text;
        target.FontFamily      = FontFamily;
        target.FontSize        = FontSize;
        target.FontWeight      = FontWeight;
        target.FontStyle       = FontStyle;
        target.TextAlign       = TextAlign;
        target.VerticalAlign   = VerticalAlign;
        target.Uppercase       = Uppercase;
        target.Underline       = Underline;
        target.Strikethrough   = Strikethrough;
        target.LineSpacing     = LineSpacing;
        target.IgnoreOverhang  = IgnoreOverhang;
        target.NoWrap          = NoWrap;
        target.AutoSize        = AutoSize;
        target.Fill            = Fill?.Clone();
        target.Stroke          = Stroke?.Clone();
        target.StrokeThickness = StrokeThickness;
    }
}

/// <summary>direction of travel of the scrolling content</summary>
public enum GtTickerDirection { Left, Right, Top, Bottom }

/// <summary>what an incoming data update does; <see cref="Replace"/> restarts the ticker from the new value and loops it forever, <see cref="Add"/> appends it behind whatever is still on screen and lets finished items expire</summary>
public enum GtTickerType { Replace, Add }

/// <summary>GT's Ticker, a text object that is also a container; it never draws its own text, it splits the template text into chunks, clones them and scrolls the clones through its own bounds; every font/fill/stroke property comes from the ticker itself which is why it subclasses <see cref="GtTextBlock"/>, only the chunk text comes from the template</summary>
public class GtTickerElement : GtTextBlock
{
    /// <summary>pixels per frame at the composition's frame rate, not per second</summary>
    public double Speed { get; set; } = 1.0;

    public GtTickerDirection Direction { get; set; } = GtTickerDirection.Left;

    /// <summary>named <c>Type</c> in the file, <c>TickerType</c> here to keep off Object.GetType</summary>
    public GtTickerType TickerType { get; set; } = GtTickerType.Replace;

    /// <summary>element name the template was read under so it round-trips as GT wrote it; only used for a text template, a template of any other kind is kept whole in <see cref="TemplateXml"/></summary>
    public string TemplateElementName { get; set; } = DefaultTemplateElementName;

    /// <summary>verbatim XML of the template as it was read, so everything this editor does not model (the template's own name and brushes, or a whole Layer template) survives a save; the text of a text template is mirrored into <see cref="GtTextBlock.Text"/> and written back into this XML on save, null means the ticker was created here and gets a fresh template</summary>
    public string? TemplateXml { get; set; }

    /// <summary>element name used for a text template this editor writes from scratch; GT Designer writes <c>&lt;TextBlock&gt;</c> here, same as for a stand-alone text object</summary>
    public const string DefaultTemplateElementName = "TextBlock";

    /// <summary>true for the template element names that carry their content in a <c>Text</c> attribute; anything else (a Layer) is a container template this editor only passes through</summary>
    public static bool IsTextTemplate(string elementName) =>
        elementName is "TextBlock" or "Text";

    /// <summary>Top/Bottom scroll along Y, Left/Right along X; GT tests the axis, not the name</summary>
    public bool IsVertical => Direction is GtTickerDirection.Top or GtTickerDirection.Bottom;

    /// <summary>one scrolling clone with GT's FormatTextObject layout override applied: horizontal tickers force a single unwrapped line and keep the ticker's height, vertical ones wrap inside the ticker's width and grow their own height</summary>
    public GtTextBlock CreateChunk(string text)
    {
        var chunk = new GtTextBlock();
        CopyTextPropertiesTo(chunk);
        chunk.Text   = text;
        chunk.NoWrap = !IsVertical;
        // the auto-sized axis is the one the scroller measures its travel along: a horizontal ticker grows each clone's width and keeps the ticker's height, a vertical one grows the height inside the ticker's width
        chunk.AutoSize = IsVertical ? GtAutoSize.Height : GtAutoSize.Width;
        return chunk;
    }

    public override GtElement Clone()
    {
        var copy = new GtTickerElement
        {
            Speed               = Speed,
            Direction           = Direction,
            TickerType          = TickerType,
            TemplateElementName = TemplateElementName,
            TemplateXml         = TemplateXml,
        };
        CopyTextPropertiesTo(copy);
        CopyBaseTo(copy);
        return copy;
    }
}

/// <summary>how an image's bitmap is laid out inside the element box; ordinals match GT's own <c>GraphicsBitmapSizeMode</c>, which is also the designer's combo order</summary>
public enum GtImageSizeMode { Normal = 0, Stretch = 1, Centered = 2, TopRight = 3 }

public class GtImageElement : GtElement
{
    public string? BitmapSource { get; set; }  // logical path

    /// <summary>bitmap layout inside the box; GT defaults a new image to <see cref="GtImageSizeMode.Centered"/> and omits the attribute at that value, so an absent SizeMode means Centered not Normal</summary>
    public GtImageSizeMode SizeMode { get; set; } = GtImageSizeMode.Centered;

    /// <summary>designer scrub position (0-1) into the image sequence this bitmap belongs to, null when the source Bitmap element carried no Position attribute</summary>
    public double? SequencePosition { get; set; }

    public override GtElement Clone()
    {
        var copy = new GtImageElement
        {
            BitmapSource     = BitmapSource,
            SequencePosition = SequencePosition,
            SizeMode         = SizeMode,
        };
        CopyBaseTo(copy);
        return copy;
    }
}

public class GtRectangleElement : GtElement
{
    public GtBrush? Fill { get; set; }
    public GtBrush? Stroke { get; set; }
    public double StrokeThickness { get; set; } = 0.0;
    public GtStrokeDashStyle StrokeDashStyle { get; set; } = GtStrokeDashStyle.Solid;
    public GtRectangleStyle Style { get; set; } = GtRectangleStyle.Rounded;

    public override GtElement Clone()
    {
        var copy = new GtRectangleElement
        {
            Fill            = Fill?.Clone(),
            Stroke          = Stroke?.Clone(),
            StrokeThickness = StrokeThickness,
            StrokeDashStyle = StrokeDashStyle,
            Style           = Style,
        };
        CopyBaseTo(copy);
        return copy;
    }
}

public class GtEllipseElement : GtElement
{
    public GtBrush? Fill { get; set; }
    public GtBrush? Stroke { get; set; }
    public double StrokeThickness { get; set; } = 0.0;
    public GtStrokeDashStyle StrokeDashStyle { get; set; } = GtStrokeDashStyle.Solid;

    public override GtElement Clone()
    {
        var copy = new GtEllipseElement
        {
            Fill            = Fill?.Clone(),
            Stroke          = Stroke?.Clone(),
            StrokeThickness = StrokeThickness,
            StrokeDashStyle = StrokeDashStyle,
        };
        CopyBaseTo(copy);
        return copy;
    }
}

public class GtLayer
{
    public string Name { get; set; } = "";
    public GtPoint Location { get; set; } = GtPoint.Zero;
    public GtSize Dimensions { get; set; } = new(0, 0);
    public bool Locked { get; set; }
    public bool Visible { get; set; } = true;
    /// <summary>coordinate space of the inner Composition (may differ from Dimensions)</summary>
    public double InnerWidth { get; set; }
    public double InnerHeight { get; set; }
    public List<GtElement> Elements { get; set; } = new();

    /// <summary>deep copy, elements included; <see cref="Name"/> and the element names are copied verbatim, so a caller adding the copy to a document must make every one of them unique since animations resolve their target by name</summary>
    public GtLayer Clone()
    {
        var copy = new GtLayer
        {
            Name        = Name,
            Location    = Location,
            Dimensions  = Dimensions,
            Locked      = Locked,
            Visible     = Visible,
            InnerWidth  = InnerWidth,
            InnerHeight = InnerHeight,
        };
        foreach (var el in Elements) copy.Elements.Add(el.Clone());
        return copy;
    }
}

public class GtDocument
{
    public double Width { get; set; } = 1920;
    public double Height { get; set; } = 1080;
    public List<GtLayer> Layers { get; set; } = new();
    public List<GtStoryboard> Storyboards { get; set; } = new();

    /// <summary>editor-only ruler guides; GT has no format for these so they round-trip through a private <c>gtplus/guides.xml</c> part that vMix ignores</summary>
    public List<GtGuide> Guides { get; set; } = new();

    /// <summary>when true, guides cannot be dragged or deleted on the canvas</summary>
    public bool GuidesLocked { get; set; }

    /// <summary>doc-space point the rulers report as 0,0 (Photoshop's draggable ruler origin)</summary>
    public GtPoint RulerOrigin { get; set; } = GtPoint.Zero;
}

/// <summary>a guide runs the full length of the canvas along one axis</summary>
public enum GtGuideOrientation
{
    /// <summary>runs left-to-right, <see cref="GtGuide.Position"/> is a Y coordinate</summary>
    Horizontal,
    /// <summary>runs top-to-bottom, <see cref="GtGuide.Position"/> is an X coordinate</summary>
    Vertical
}

public class GtGuide
{
    public GtGuideOrientation Orientation { get; set; }

    /// <summary>doc-space coordinate: Y for horizontal guides, X for vertical ones</summary>
    public double Position { get; set; }

    public GtGuide() { }

    public GtGuide(GtGuideOrientation orientation, double position)
    {
        Orientation = orientation;
        Position    = position;
    }
}

/// <summary>animation types observed in real GT files or listed in the GT Designer docs; <see cref="GtAnimationType.Unknown"/> is a catch-all, the original element name is kept in <see cref="GtAnimation.TypeName"/> so unrecognised animations round-trip intact</summary>
public enum GtAnimationType
{
    Unknown,

    /// <summary>GT's placeholder row <c>&lt;None Object="X" /&gt;</c> which animates nothing; kept so files round-trip but hidden from the timeline and ignored everywhere else</summary>
    None,

    // fixed: offered on TransitionIn/Out, DataChangeIn/Out and Page1-10
    Fade,
    Fly,
    Bounce,
    Expand,
    Reveal,
    Rotate,
    Scroll,
    Zoom,
    ZoomFade,
    Hidden,
    ImageSequence,

    // continuous: offered on the Continuous storyboard only; these run forever (GT gives them an infinite-duration sentinel) and advance by a per-frame delta scaled by Speed rather than interpolating between two end states
    RotateContinuous,
    FillOffset,
    StrokeOffset,
    Blink,
    ImageSequenceLoop,
}

/// <summary>GT easing names, written verbatim to the Interpolation attribute</summary>
public enum GtInterpolation
{
    Linear,
    CubicEasingIn,
    CubicEasingOut,
    CubicEasingInOut,
    BounceIn,
    BounceOut,
}

/// <summary>the 9-way direction pad shared by every direction-aware animation; values match GT's GraphicsDirection ordinals exactly, they are sparse and non-sequential on purpose so never renumber them and never rely on declaration order; <see cref="None"/> has no button on GT's pad and is only reachable from hand-edited XML, and it is not a synonym for <see cref="Left"/> since each animation handles it its own way (Fly holds still, Rotate emits nothing at all, Reveal falls in with Left) so it is carried through rather than normalised away; what a direction means differs per animation, Fly/Bounce name the edge the object comes from, Scroll the edge it travels toward, Expand the anchor it grows out of, Reveal where the wipe starts, and the Rotate pair which axis and which sign to spin about</summary>
public enum GtAnimDirection
{
    None        = 0,
    Center      = 5,
    Top         = 10,
    TopLeft     = 20,
    TopRight    = 30,
    Bottom      = 40,
    BottomLeft  = 50,
    BottomRight = 60,
    Left        = 70,
    Right       = 80,
}

/// <summary>axis a Center-direction Reveal wipes along (GT's GraphicsAxis); absent in XML means <see cref="Both"/>, which opens out from the middle in both axes at once</summary>
public enum GtCenterAxis { Both, X, Y }

/// <summary>one entry inside a <see cref="GtStoryboard"/>, maps 1:1 to a child element of &lt;Storyboard.Animations&gt;, e.g. &lt;Fly Object="Name" Delay="0.5" /&gt;</summary>
public class GtAnimation
{
    /// <summary>raw XML element name, e.g. "Fly", authoritative for serialization</summary>
    public string TypeName { get; set; } = "Fade";

    public GtAnimationType Type { get; set; } = GtAnimationType.Fade;

    /// <summary>name of the target layer or element</summary>
    public string Object { get; set; } = "";

    /// <summary>seconds before the animation starts, relative to storyboard start</summary>
    public double Delay { get; set; }

    /// <summary>seconds the animation runs for after the delay, null means GT default (1s)</summary>
    public double? Duration { get; set; }

    public bool Reverse { get; set; }

    public GtInterpolation Interpolation { get; set; } = GtInterpolation.Linear;

    /// <summary>direction of travel, always concrete: when the XML attribute is absent the reader resolves it to <see cref="DefaultDirectionFor"/>, and the writer omits it again when it still matches that default</summary>
    public GtAnimDirection Direction { get; set; } = GtAnimDirection.Left;

    /// <summary>axis a Center Reveal wipes along, only meaningful when <see cref="Direction"/> is <see cref="GtAnimDirection.Center"/> on a <see cref="GtAnimationType.Reveal"/></summary>
    public GtCenterAxis CenterAxis { get; set; } = GtCenterAxis.Both;

    /// <summary>rate of change for Continuous storyboard animations, null when absent</summary>
    public double? Speed { get; set; }

    /// <summary>attributes this editor does not model, preserved verbatim on save</summary>
    public List<GtRawAttribute> ExtraAttributes { get; set; } = new();

    /// <summary>editor state: a muted animation is skipped by the preview so its contribution can be taken out of the picture while the rest of the storyboard is worked on; it is not part of the title, never read, never written, and untouched by undo</summary>
    public bool Muted { get; set; }

    /// <summary>default duration GT applies when the Duration attribute is omitted</summary>
    public const double DefaultDuration = 1.0;

    /// <summary>default Speed GT applies when the Speed attribute is omitted</summary>
    public const double DefaultSpeed = 1.0;

    public double EffectiveDuration => Duration ?? DefaultDuration;

    /// <summary>cycles per second for a continuous animation: one full turn, or one full texture width of brush travel, every <c>1/Speed</c> seconds</summary>
    public double EffectiveSpeed => Speed ?? DefaultSpeed;

    /// <summary>direction GT assumes when the attribute is absent; GT registers <c>Left</c> as the default on the shared direction builder and <see cref="GtAnimationType.Scroll"/> overrides it with <c>Bottom</c>, so a missing attribute means the type's own default, never <see cref="GtAnimDirection.None"/></summary>
    public static GtAnimDirection DefaultDirectionFor(GtAnimationType type) =>
        type == GtAnimationType.Scroll ? GtAnimDirection.Bottom : GtAnimDirection.Left;

    /// <summary>true only for the nine animations GT derives from its direction builder; everything else (Fade, Zoom, ZoomFade, Hidden, Blink, the image-sequence pair and the None placeholder) carries no Direction attribute and shows no pad in the ribbon</summary>
    public static bool SupportsDirection(GtAnimationType type) => type switch
    {
        GtAnimationType.Fly              => true,
        GtAnimationType.Bounce           => true,
        GtAnimationType.Expand           => true,
        GtAnimationType.Reveal           => true,
        GtAnimationType.Rotate           => true,
        GtAnimationType.Scroll           => true,
        GtAnimationType.RotateContinuous => true,
        GtAnimationType.FillOffset       => true,
        GtAnimationType.StrokeOffset     => true,
        _                                => false,
    };

    /// <summary>true for the one direction-conditional extra option in GT, Reveal's Center axis; no other animation exposes it and it only applies while Direction is Center</summary>
    public static bool SupportsCenterAxis(GtAnimationType type) => type == GtAnimationType.Reveal;

    /// <summary>true for the types that only appear on the Continuous storyboard; they ignore Duration (GT marks them infinite) and advance by a Speed-scaled delta every frame</summary>
    public static bool IsContinuous(GtAnimationType type) => type switch
    {
        GtAnimationType.RotateContinuous  => true,
        GtAnimationType.FillOffset        => true,
        GtAnimationType.StrokeOffset      => true,
        GtAnimationType.Blink             => true,
        GtAnimationType.ImageSequenceLoop => true,
        _                                 => false,
    };

    /// <summary>true for entries the editor carries but never acts on, GT's <c>&lt;None&gt;</c> placeholder; they are written back untouched but take no timeline row, drive no preview, and do not count against the per-object limit</summary>
    public bool IsPlaceholder => Type == GtAnimationType.None;

    /// <summary>true when Direction need not be written: at its default, or not applicable</summary>
    public bool DirectionIsDefault => !SupportsDirection(Type) || Direction == DefaultDirectionFor(Type);

    /// <summary>true when CenterAxis need not be written: at GT's <c>Both</c> default, or not applicable to this type</summary>
    public bool CenterAxisIsDefault => !SupportsCenterAxis(Type) || CenterAxis == GtCenterAxis.Both;

    /// <summary>true when this animation runs forever rather than over a fixed Duration</summary>
    public bool IsContinuousType => IsContinuous(Type);

    /// <summary>storyboard-relative time at which this animation finishes</summary>
    public double EndTime => Delay + EffectiveDuration;

    public GtAnimation Clone()
    {
        var copy = new GtAnimation
        {
            TypeName      = TypeName,
            Type          = Type,
            Object        = Object,
            Delay         = Delay,
            Duration      = Duration,
            Reverse       = Reverse,
            Interpolation = Interpolation,
            Direction     = Direction,
            CenterAxis    = CenterAxis,
            Speed         = Speed,
        };
        foreach (var a in ExtraAttributes) copy.ExtraAttributes.Add(new GtRawAttribute(a.Name, a.Value));
        return copy;
    }
}

public record GtRawAttribute(string Name, string Value);

/// <summary>a named collection of animations triggered by a vMix event (TransitionIn, TransitionOut, DataChangeIn/Out, Page1-10, Continuous)</summary>
public class GtStoryboard
{
    public const string None          = "None";
    public const string TransitionIn  = "TransitionIn";
    public const string TransitionOut = "TransitionOut";
    public const string DataChangeIn  = "DataChangeIn";
    public const string DataChangeOut = "DataChangeOut";
    public const string Continuous    = "Continuous";

    /// <summary>number of page storyboards GT offers: Page1 to Page10</summary>
    public const int PageCount = 10;

    /// <summary>the storyboard types this editor can create, in the order GT's own picker lists them; <see cref="None"/> is GT's do-nothing placeholder, and a file may hold other types (DataChangeIn/Out) which are read, shown and written back but never authored here</summary>
    public static readonly IReadOnlyList<string> CreatableTypes = BuildCreatableTypes();

    private static IReadOnlyList<string> BuildCreatableTypes()
    {
        var types = new List<string> { None, TransitionIn, TransitionOut };
        for (int page = 1; page <= PageCount; page++) types.Add("Page" + page.ToString(CultureInfo.InvariantCulture));
        types.Add(DataChangeIn);
        types.Add(DataChangeOut);
        types.Add(Continuous);
        return types;
    }

    /// <summary>new storyboard of <paramref name="type"/> with GT's omit-the-attribute rule applied: TransitionIn is the default and carries no Type; <paramref name="dataName"/> scopes a DataChange storyboard to one field, empty leaves it unscoped</summary>
    public static GtStoryboard Create(string type, string? dataName = null) => new()
    {
        Type = string.Equals(type, TransitionIn, System.StringComparison.OrdinalIgnoreCase) ? null : type,
        DataName = dataName ?? "",
    };

    /// <summary>position of <paramref name="type"/> in <see cref="CreatableTypes"/>; unknown types sort after every known one, keeping their file order among themselves</summary>
    public static int SortIndex(string? type)
    {
        var name = type ?? TransitionIn;
        for (int i = 0; i < CreatableTypes.Count; i++)
            if (string.Equals(CreatableTypes[i], name, System.StringComparison.OrdinalIgnoreCase))
                return i;
        return CreatableTypes.Count;
    }

    /// <summary>which vMix event triggers this storyboard, from the <c>Type</c> attribute; null when the attribute is absent, which GT writes for <see cref="TransitionIn"/> the default</summary>
    public string? Type { get; set; }

    public bool IsTransitionIn  => Type is null || string.Equals(Type, TransitionIn, System.StringComparison.OrdinalIgnoreCase);
    public bool IsTransitionOut => string.Equals(Type, TransitionOut, System.StringComparison.OrdinalIgnoreCase);
    public bool IsDataChangeIn  => string.Equals(Type, DataChangeIn,  System.StringComparison.OrdinalIgnoreCase);
    public bool IsDataChangeOut => string.Equals(Type, DataChangeOut, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>true for either half of the DataChange pair, scoped or not</summary>
    public bool IsDataChangeEvent => IsDataChangeIn || IsDataChangeOut;
    public bool IsContinuousEvent => string.Equals(Type, Continuous,  System.StringComparison.OrdinalIgnoreCase);

    /// <summary>GT's do-nothing storyboard slot; it holds animations like any other but no vMix event ever triggers it</summary>
    public bool IsPlaceholderType => string.Equals(Type, None, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>true when this storyboard is the one named by <paramref name="type"/>, with a missing Type read as TransitionIn; ignores <see cref="DataName"/>, use <see cref="Matches"/> to identify one half of a scoped DataChange pair</summary>
    public bool IsType(string type) =>
        string.Equals(DisplayName, type, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>GT's storyboard identity, the (Type, DataName) pair; a composition may hold an unscoped DataChangeIn and one per data field at the same time, so both halves must match</summary>
    public bool Matches(string type, string? dataName) =>
        IsType(type) && string.Equals(DataName, dataName ?? "", System.StringComparison.Ordinal);

    /// <summary>GT's effective-reverse rule: TransitionOut and DataChangeIn play every animation they hold backwards, flipping each animation's own Reverse flag rather than replacing it</summary>
    public bool PlaysRewound => IsTransitionOut || IsDataChangeIn;

    /// <summary>GT Title Designer only honours three animations per object in a storyboard; anything beyond that is dropped when the title plays, so the editor refuses to author it</summary>
    public const int MaxAnimationsPerObject = 3;

    public List<GtAnimation> Animations { get; set; } = new();

    /// <summary>data field a DataChange storyboard is scoped to, e.g. <c>"Name.Text"</c>; empty for an unscoped storyboard and for every other event type; GT keys a storyboard by (Type, DataName) so the scoped and unscoped halves are separate storyboards that both run when that field changes, and field names are case-sensitive</summary>
    public string DataName { get; set; } = "";

    public bool IsScoped => DataName.Length > 0;

    public string DisplayName => Type ?? TransitionIn;

    /// <summary>GT's display string for the event: the type on its own, or <c>"DataChangeIn (Name.Text)"</c> when the storyboard is scoped to a field</summary>
    public string EventLabel => IsScoped ? $"{DisplayName} ({DataName})" : DisplayName;

    /// <summary>animations that actually do something, GT's None placeholders do not count</summary>
    public int AnimationCount
    {
        get
        {
            int count = 0;
            foreach (var a in Animations)
                if (!a.IsPlaceholder) count++;
            return count;
        }
    }

    public bool HasAnimations => AnimationCount > 0;

    /// <summary>animations targeting <paramref name="objectName"/>, optionally skipping one</summary>
    public int CountForObject(string? objectName, GtAnimation? ignore = null)
    {
        int count = 0;
        foreach (var a in Animations)
            if (!a.IsPlaceholder && !ReferenceEquals(a, ignore) &&
                string.Equals(a.Object, objectName, System.StringComparison.OrdinalIgnoreCase))
                count++;
        return count;
    }

    /// <summary>true when another animation may target <paramref name="objectName"/></summary>
    public bool HasRoomFor(string? objectName, GtAnimation? ignore = null) =>
        CountForObject(objectName, ignore) < MaxAnimationsPerObject;

    /// <summary>object names already at or over the limit, with their counts; non-empty only for files written outside this editor, used to warn rather than to silently discard animations</summary>
    public IEnumerable<KeyValuePair<string, int>> ObjectsOverLimit()
    {
        var counts = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var a in Animations)
        {
            if (a.IsPlaceholder) continue;
            counts[a.Object] = counts.TryGetValue(a.Object, out var n) ? n + 1 : 1;
        }

        foreach (var pair in counts)
            if (pair.Value > MaxAnimationsPerObject)
                yield return pair;
    }

    /// <summary>earliest second any animation starts, never above zero; negative when a clip carries a negative delay, vMix computes those before the storyboard's own time zero</summary>
    public double EarliestStart
    {
        get
        {
            double min = 0;
            foreach (var a in Animations)
                if (!a.IsPlaceholder && a.Delay < min) min = a.Delay;
            return min;
        }
    }

    /// <summary>total storyboard length: the latest animation end time (minimum 1s)</summary>
    public double Duration
    {
        get
        {
            double max = 0;
            foreach (var a in Animations)
                if (!a.IsPlaceholder && a.EndTime > max) max = a.EndTime;
            return max > 0 ? max : GtAnimation.DefaultDuration;
        }
    }
}

/// <summary>one storyboard placed on the timeline at a time offset; a single-segment timeline shows a storyboard on its own, and TransitionIn and TransitionOut are shown as two segments so the pair can be scrubbed and played as one sequence exactly as vMix runs them either side of the template being live; the offset is view state only, it never reaches the file</summary>
public sealed class GtTimelineSegment
{
    public GtTimelineSegment(GtStoryboard storyboard, double offset = 0)
    {
        Storyboard = storyboard;
        Offset = offset;
    }

    public GtStoryboard Storyboard { get; }

    /// <summary>seconds added to every animation's Delay for display and preview</summary>
    public double Offset { get; set; }

    /// <summary>timeline position where this segment's last animation finishes</summary>
    public double EndTime => Offset + Storyboard.Duration;
}
