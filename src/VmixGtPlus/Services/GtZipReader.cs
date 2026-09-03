using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Avalonia.Media;
using VmixGtPlus.Models;

namespace VmixGtPlus.Services;

/// <summary>reads a .gtzip file into a GtDocument and raw asset bytes dictionary; asset dict key is the logical path from resources.xml (forward-slash normalised)</summary>
public class GtZipReader
{
    public (GtDocument Document, GtAssetLibrary Assets) Read(string path)
    {
        Logger.Info($"Opening: {path}");
        using var zip = ZipFile.OpenRead(path);

        Logger.Debug($"ZIP entries: {zip.Entries.Count}");
        foreach (var e in zip.Entries)
            Logger.Debug($"  entry: '{e.FullName}'  size={e.Length}");

        // 1. load all GUID blobs (files with no '.' in name = binary assets)
        var guidBlobs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (!entry.Name.Contains('.'))
            {
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                guidBlobs[entry.Name] = ms.ToArray();
                Logger.Debug($"  blob: '{entry.Name}' ({ms.Length} bytes)");
            }
        }

        // 2. parse resources.xml to logical path to bytes (plus image sequences)
        var assets = new GtAssetLibrary();
        var resourcesEntry = zip.GetEntry("resources.xml");
        if (resourcesEntry != null)
        {
            Logger.Debug("Parsing resources.xml");
            using var stream = resourcesEntry.Open();
            // copy to MemoryStream so XDocument can seek/detect encoding
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            var xres = XDocument.Load(ms);
            foreach (var resource in xres.Root?.Elements("resource") ?? Array.Empty<XElement>())
            {
                var filename = resource.Attribute("filename")?.Value;
                if (filename is null) continue;
                var anchor = filename.Replace('\\', '/');

                // a resource carries one <source> per frame in playback order, reading only the first would drop every later frame of an image sequence
                var frames = new List<string>();
                foreach (var source in resource.Elements("source"))
                {
                    var guid = source.Attribute("guid")?.Value;
                    if (guid is null) continue;

                    // the element text is the frame's own logical path, the anchor's own source repeats the resource filename
                    var text = source.Value?.Trim();
                    var key  = string.IsNullOrEmpty(text) ? anchor : text!.Replace('\\', '/');
                    if (frames.Count == 0) key = anchor;

                    if (guidBlobs.TryGetValue(guid, out var bytes))
                    {
                        assets.Blobs[key] = bytes;
                        frames.Add(key);
                    }
                    else
                    {
                        Logger.Warn($"  asset '{key}' guid '{guid}' not found in blobs");
                    }
                }

                if (frames.Count > 1)
                {
                    assets.AddSequence(anchor, frames);
                    Logger.Debug($"  sequence: '{anchor}' ({frames.Count} frames)");
                }
                else if (frames.Count == 1)
                {
                    Logger.Debug($"  asset: '{anchor}'");
                }
            }
        }
        else
        {
            Logger.Debug("No resources.xml (template has no assets)");
        }

        Logger.Info($"Assets loaded: {assets.Count} blobs, {assets.Sequences.Count} sequences");

        // 3. parse document.xml
        // read raw bytes into a MemoryStream and let XDocument detect encoding from BOM; using StreamReader(Encoding.Unicode) then XDocument.Load(reader) causes "Data at root level is invalid" because the XML declaration's encoding attribute conflicts with how the reader presents the stream to the parser
        var docEntry = zip.GetEntry("document.xml")
            ?? throw new InvalidDataException("document.xml not found in GTZIP");

        Logger.Debug($"Reading document.xml ({docEntry.Length} bytes compressed)");
        XDocument xdoc;
        using (var rawStream = docEntry.Open())
        using (var ms = new MemoryStream())
        {
            rawStream.CopyTo(ms);
            var bytes = ms.ToArray();

            Logger.Debug($"  document.xml first bytes: {BitConverter.ToString(bytes, 0, Math.Min(16, bytes.Length))}");

            // detect actual encoding from BOM, fall back to UTF-8; many GT files claim encoding="utf-16" in the XML declaration but are stored as UTF-8 (no BOM) and XDocument.Load(stream) throws when it sees the utf-16 declaration but no BOM, so decode to string first then use XDocument.Parse(string) which ignores the encoding declaration
            Encoding encoding;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                encoding = Encoding.Unicode;          // UTF-16 LE BOM
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                encoding = Encoding.BigEndianUnicode; // UTF-16 BE BOM
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                encoding = Encoding.UTF8;             // UTF-8 BOM
            else
                encoding = Encoding.UTF8;             // no BOM, assume UTF-8

            Logger.Debug($"  detected encoding: {encoding.EncodingName}");
            var xmlText = encoding.GetString(bytes);
            xdoc = XDocument.Parse(xmlText);
        }

        // 4. editor-only guides, newest storage scheme first: the hidden carrier object, then the <GTPlus> element, then the gtplus zip part; GT wipes the older two so a template that has been through GT only ever comes back with the carrier
        var document = ParseDocument(xdoc);
        if (!GuidesPart.ExtractCarrier(document) && document.Guides.Count == 0)
            GuidesPart.Read(zip, document);

        Logger.Info($"Parsed: {document.Width}x{document.Height}, {document.Layers.Count} layers");
        foreach (var layer in document.Layers)
            Logger.Debug($"  layer '{layer.Name}': {layer.Elements.Count} elements at ({layer.Location.X},{layer.Location.Y})");

        return (document, assets);
    }

    private static GtDocument ParseDocument(XDocument xdoc)
    {
        var root = xdoc.Root ?? throw new InvalidDataException("Empty document.xml");
        var doc = new GtDocument
        {
            Width = ParseDouble(root.Attribute("Width")?.Value, 1920),
            Height = ParseDouble(root.Attribute("Height")?.Value, 1080),
        };
        // read and strip the editor's own element before layers, so nothing else sees it
        GuidesPart.ReadDocumentElement(root, doc);

        foreach (var el in root.Elements("Layer"))
            doc.Layers.Add(ParseLayer(el));
        foreach (var sb in root.Elements("Storyboard"))
            doc.Storyboards.Add(ParseStoryboard(sb));
        return doc;
    }

    private static GtStoryboard ParseStoryboard(XElement el)
    {
        // GT identifies a storyboard by Type, the attribute is omitted for TransitionIn the default; Name is accepted as a fallback only because earlier notes described it
        var storyboard = new GtStoryboard
        {
            Type = el.Attribute("Type")?.Value ?? el.Attribute("Name")?.Value,
            // DataChange storyboards may be scoped to one data field, absent means "any field"
            DataName = el.Attribute("DataName")?.Value ?? "",
        };

        var animations = el.Element("Storyboard.Animations");
        if (animations != null)
            foreach (var child in animations.Elements())
                storyboard.Animations.Add(ParseAnimation(child));

        Logger.Debug($"  storyboard '{storyboard.EventLabel}': {storyboard.Animations.Count} animations");
        return storyboard;
    }

    private static GtAnimation ParseAnimation(XElement el)
    {
        var typeName = el.Name.LocalName;
        var anim = new GtAnimation
        {
            TypeName = typeName,
            Type     = ParseAnimationType(typeName),
            Object   = el.Attribute("Object")?.Value ?? "",
            Delay    = ParseDouble(el.Attribute("Delay")?.Value, 0),
            Reverse  = ParseBool(el.Attribute("Reverse")?.Value),
        };

        var duration = el.Attribute("Duration")?.Value;
        if (duration != null) anim.Duration = ParseDouble(duration, GtAnimation.DefaultDuration);

        var speed = el.Attribute("Speed")?.Value;
        if (speed != null) anim.Speed = ParseDouble(speed, 1.0);

        anim.Interpolation = ParseInterpolation(el.Attribute("Interpolation")?.Value);

        // an absent Direction means the type's default (Bottom for Scroll, Left otherwise), so resolve it here and let the writer drop it again if it is still the default
        anim.Direction = ParseDirection(el.Attribute("Direction")?.Value)
                         ?? GtAnimation.DefaultDirectionFor(anim.Type);

        anim.CenterAxis = ParseCenterAxis(el.Attribute("CenterAxis")?.Value);

        // preserve anything this editor does not model so saves stay lossless
        foreach (var attr in el.Attributes())
        {
            switch (attr.Name.LocalName)
            {
                case "Object":
                case "Delay":
                case "Duration":
                case "Reverse":
                case "Interpolation":
                case "Direction":
                case "CenterAxis":
                case "Speed":
                    continue;
                default:
                    anim.ExtraAttributes.Add(new GtRawAttribute(attr.Name.LocalName, attr.Value));
                    Logger.Debug($"    unmodelled animation attribute '{attr.Name.LocalName}' preserved");
                    break;
            }
        }

        return anim;
    }

    private static GtAnimationType ParseAnimationType(string name) => name switch
    {
        "None"              => GtAnimationType.None,
        "Fade"              => GtAnimationType.Fade,
        "Fly"               => GtAnimationType.Fly,
        "Bounce"            => GtAnimationType.Bounce,
        "Expand"            => GtAnimationType.Expand,
        "Reveal"            => GtAnimationType.Reveal,
        "Rotate"            => GtAnimationType.Rotate,
        "Zoom"              => GtAnimationType.Zoom,
        "ZoomFade"          => GtAnimationType.ZoomFade,
        "Scroll"            => GtAnimationType.Scroll,
        "Hidden"            => GtAnimationType.Hidden,
        "ImageSequence"     => GtAnimationType.ImageSequence,
        "ImageSequenceLoop" => GtAnimationType.ImageSequenceLoop,
        "RotateContinuous"  => GtAnimationType.RotateContinuous,
        "FillOffset"        => GtAnimationType.FillOffset,
        "StrokeOffset"      => GtAnimationType.StrokeOffset,
        "Blink"             => GtAnimationType.Blink,
        _                   => GtAnimationType.Unknown
    };

    private static GtInterpolation ParseInterpolation(string? value) => value switch
    {
        "CubicEasingIn"    => GtInterpolation.CubicEasingIn,
        "CubicEasingOut"   => GtInterpolation.CubicEasingOut,
        "CubicEasingInOut" => GtInterpolation.CubicEasingInOut,
        "BounceIn"         => GtInterpolation.BounceIn,
        "BounceOut"        => GtInterpolation.BounceOut,
        _                  => GtInterpolation.Linear
    };

    /// <summary>GT persists directions by name, so the nine pad names plus the pad-less None are the whole vocabulary; Up/Down are accepted as aliases for Top/Bottom only because this editor briefly wrote them, GT itself never emits either</summary>
    private static GtAnimDirection? ParseDirection(string? value) => value switch
    {
        "None"        => GtAnimDirection.None,
        "Center"      => GtAnimDirection.Center,
        "Top"         => GtAnimDirection.Top,
        "TopLeft"     => GtAnimDirection.TopLeft,
        "TopRight"    => GtAnimDirection.TopRight,
        "Bottom"      => GtAnimDirection.Bottom,
        "BottomLeft"  => GtAnimDirection.BottomLeft,
        "BottomRight" => GtAnimDirection.BottomRight,
        "Left"        => GtAnimDirection.Left,
        "Right"       => GtAnimDirection.Right,
        "Up"          => GtAnimDirection.Top,
        "Down"        => GtAnimDirection.Bottom,
        _             => null
    };

    /// <summary>an absent or unrecognised CenterAxis is GT's Both default, not X</summary>
    private static GtCenterAxis ParseCenterAxis(string? value) => value switch
    {
        "X" => GtCenterAxis.X,
        "Y" => GtCenterAxis.Y,
        _   => GtCenterAxis.Both
    };

    private static GtLayer ParseLayer(XElement el)
    {
        var layer = new GtLayer
        {
            Name = el.Attribute("Name")?.Value ?? "",
            Location = GtPoint.Parse(el.Attribute("Location")?.Value),
            Dimensions = GtSize.Parse(el.Attribute("Dimensions")?.Value),
            Locked = ParseBool(el.Attribute("Locked")?.Value),
            Visible = ParseBool(el.Attribute("Visible")?.Value, true),
        };

        var innerComp = el.Element("Layer.Composition")?.Element("Composition");
        if (innerComp != null)
        {
            layer.InnerWidth = ParseDouble(innerComp.Attribute("Width")?.Value, layer.Dimensions.Width);
            layer.InnerHeight = ParseDouble(innerComp.Attribute("Height")?.Value, layer.Dimensions.Height);

            foreach (var child in innerComp.Elements())
            {
                var element = child.Name.LocalName switch
                {
                    "TextBlock" => (GtElement)ParseTextBlock(child),
                    "Image" => ParseImage(child),
                    "Rectangle" => ParseRectangle(child),
                    "Ellipse" => ParseEllipse(child),
                    "Ticker" => ParseTicker(child),
                    _ => null
                };
                if (element != null) layer.Elements.Add(element);
            }
        }

        return layer;
    }

    /// <summary>parses the comma-separated DataFlags list, e.g. "Hidden, NoEvents, ShowVisible"; unknown tokens are ignored so a newer vMix flag never fails the whole load</summary>
    private static GtDataFlags ParseDataFlags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return GtDataFlags.None;

        var flags = GtDataFlags.None;
        foreach (var token in value!.Split(','))
        {
            flags |= token.Trim() switch
            {
                "Hidden"      => GtDataFlags.Hidden,
                "NoEvents"    => GtDataFlags.NoEvents,
                "ShowVisible" => GtDataFlags.ShowVisible,
                _             => GtDataFlags.None
            };
        }
        return flags;
    }

    /// <summary>parses the Anchor attribute; GT omits it for the default top-left corner and its names are <see cref="GtAnchor"/>'s own, an unknown name falls back to the default rather than failing the load</summary>
    private static GtAnchor ParseAnchor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return GtAnchor.TopLeft;
        if (Enum.TryParse<GtAnchor>(value!.Trim(), ignoreCase: true, out var anchor)) return anchor;

        Logger.Warn($"Unknown Anchor '{value}', using TopLeft");
        return GtAnchor.TopLeft;
    }

    private static T FillBase<T>(XElement el, T element) where T : GtElement
    {
        element.Name = el.Attribute("Name")?.Value ?? "";
        element.Dimensions = GtSize.Parse(el.Attribute("Dimensions")?.Value);
        element.Anchor = ParseAnchor(el.Attribute("Anchor")?.Value);

        // Location names the anchor point in the file but the editor works in top-left corners throughout, so the anchor offset comes off here and the writer puts it back
        var stored = GtPoint.Parse(el.Attribute("Location")?.Value);
        element.Location = new GtPoint(
            stored.X - element.Anchor.FractionX() * element.Dimensions.Width,
            stored.Y - element.Anchor.FractionY() * element.Dimensions.Height);

        element.Visible = ParseBool(el.Attribute("Visible")?.Value, true);
        element.Opacity = ParseDouble(el.Attribute("Opacity")?.Value, 1.0);
        element.Locked = ParseBool(el.Attribute("Locked")?.Value);
        element.DataFlags = ParseDataFlags(el.Attribute("DataFlags")?.Value);

        // GT Title's "None" on a shape means the same thing as Hidden, vMix keeps a flagless Rectangle / Ellipse out of the title editor; normalise it so the flag the user sees matches the behaviour, they stay free to untick it
        if (element is GtRectangleElement or GtEllipseElement
            && element.DataFlags == GtDataFlags.None)
            element.DataFlags = GtDataFlags.Hidden;

        // parse <ElementType.Transform><Transform Rotate="rx,ry,rz"/></ElementType.Transform>
        var rotateAttr = el.Element(el.Name.LocalName + ".Transform")
                           ?.Element("Transform")
                           ?.Attribute("Rotate")?.Value;
        if (rotateAttr != null)
        {
            var parts = rotateAttr.Split(',');
            if (parts.Length >= 3)
            {
                element.RotateX = ParseDouble(parts[0]);
                element.RotateY = ParseDouble(parts[1]);
                element.RotateZ = ParseDouble(parts[2]);
            }
            else if (parts.Length == 1)
                element.RotateX = ParseDouble(parts[0]);
        }

        // parse <ElementType.Mask><Mask Object="OtherElementName"/></ElementType.Mask>
        element.MaskObject = el.Element(el.Name.LocalName + ".Mask")
                               ?.Element("Mask")
                               ?.Attribute("Object")?.Value;

        // parse <ElementType.Crop><Crop Range="x0,y0,x1,y1" Feather="l,t,r,b"/></ElementType.Crop>
        element.Crop = ParseCrop(el.Element(el.Name.LocalName + ".Crop")?.Element("Crop"));

        // parse <ElementType.Bounding><Bounding Object="Name" Padding="l,t,r,b"/></ElementType.Bounding>
        element.Bounding = ParseBounding(el.Element(el.Name.LocalName + ".Bounding")?.Element("Bounding"));

        return element;
    }

    /// <summary>reads a Crop child; either attribute may be absent, an absent Range means the full box and an absent Feather means hard edges</summary>
    private static GtCrop? ParseCrop(XElement? el)
    {
        if (el is null) return null;
        var crop = new GtCrop();

        var range = SplitDoubles(el.Attribute("Range")?.Value);
        if (range != null && range.Length >= 4)
        {
            crop.X0 = range[0];
            crop.Y0 = range[1];
            crop.X1 = range[2];
            crop.Y1 = range[3];
        }

        var feather = SplitDoubles(el.Attribute("Feather")?.Value);
        if (feather != null && feather.Length >= 4)
        {
            crop.FeatherLeft   = feather[0];
            crop.FeatherTop    = feather[1];
            crop.FeatherRight  = feather[2];
            crop.FeatherBottom = feather[3];
        }

        return crop;
    }

    /// <summary>reads a Bounding child; both attributes are optional, GT omits Object when the binding has been cleared and omits Padding while it is still all zeroes</summary>
    private static GtBounding? ParseBounding(XElement? el)
    {
        if (el is null) return null;

        var bounding = new GtBounding
        {
            Object = el.Attribute("Object")?.Value is { Length: > 0 } name ? name : null
        };

        var padding = SplitDoubles(el.Attribute("Padding")?.Value);
        if (padding != null && padding.Length >= 4)
        {
            bounding.PaddingLeft   = padding[0];
            bounding.PaddingTop    = padding[1];
            bounding.PaddingRight  = padding[2];
            bounding.PaddingBottom = padding[3];
        }

        return bounding;
    }

    private static double[]? SplitDoubles(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var parts  = value!.Split(',');
        var result = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            result[i] = ParseDouble(parts[i]);
        return result;
    }

    private static GtTextBlock ParseTextBlock(XElement el)
    {
        var tb = FillBase(el, new GtTextBlock());
        FillTextProperties(el, tb, "Lorem Ipsum");
        // AutoSize is a TextObject property in GT, so it is read here rather than with the shared text properties, a Ticker never carries one
        tb.AutoSize = ParseAutoSize(el.Attribute("AutoSize")?.Value);
        return tb;
    }

    /// <summary>GT stores the property as the <see cref="GtAutoSize"/> ordinal and its serializer writes the enum name; both spellings are accepted and anything unrecognised falls back to the default of Fixed</summary>
    private static GtAutoSize ParseAutoSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return GtAutoSize.Fixed;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
            return Enum.IsDefined(typeof(GtAutoSize), ordinal)
                ? (GtAutoSize)ordinal : GtAutoSize.Fixed;
        return Enum.TryParse<GtAutoSize>(value, ignoreCase: true, out var parsed)
            ? parsed : GtAutoSize.Fixed;
    }

    /// <summary>reads the font/fill/stroke property set shared by every text object; the Fill and Stroke children are named after the element itself, so a Ticker carries <c>&lt;Ticker.Fill&gt;</c> where a text block carries <c>&lt;TextBlock.Fill&gt;</c></summary>
    private static void FillTextProperties(XElement el, GtTextBlock tb, string defaultText)
    {
        var prefix = el.Name.LocalName;
        var raw = el.Attribute("Text")?.Value ?? defaultText;
        tb.FontFamily = el.Attribute("FontFamily")?.Value ?? "Arial";
        tb.FontSize = ParseDouble(el.Attribute("FontSize")?.Value, 36);
        tb.FontWeight = el.Attribute("FontWeight")?.Value switch
        {
            "Bold" => FontWeight.Bold,
            "Light" => FontWeight.Light,
            "Thin" => FontWeight.Thin,
            "SemiBold" => FontWeight.SemiBold,
            // vMix writes "Regular" for the 400 weight, "Normal" tolerated for files written by older versions of this editor
            _ => FontWeight.Normal
        };
        tb.TextAlign = el.Attribute("TextAlign")?.Value switch
        {
            "Center" => GtTextAlign.Center,
            "Right" => GtTextAlign.Right,
            _ => GtTextAlign.Left
        };
        tb.VerticalAlign = el.Attribute("VerticalAlign")?.Value switch
        {
            "Center" => GtVerticalAlign.Center,
            "Bottom" => GtVerticalAlign.Bottom,
            _ => GtVerticalAlign.Top
        };
        tb.Uppercase = el.Attribute("TextEffect")?.Value == "Uppercase";
        tb.LineSpacing = ParseDouble(el.Attribute("LineSpacing")?.Value, 0.0);
        tb.IgnoreOverhang = ParseBool(el.Attribute("IgnoreOverhang")?.Value);
        tb.NoWrap = el.Attribute("TextWordWrapping")?.Value == "NoWrap";
        tb.Text = tb.Uppercase ? raw.ToUpperInvariant() : raw;
        tb.Fill            = ParseBrush(el.Element(prefix + ".Fill")?.Element("Brush"));
        tb.Stroke          = ParseBrush(el.Element(prefix + ".Stroke")?.Element("Brush"));
        tb.StrokeThickness = ParseDouble(el.Attribute("StrokeThickness")?.Value, 0.0);
    }

    /// <summary>reads a Ticker: a text object plus the scroll properties and the template that supplies the scrolling text; GT overwrites every font property of the template with the ticker's own on each clone so only the template's text is worth keeping, and anything that is not a text template (GT allows a Layer, its designer never makes one) is preserved verbatim</summary>
    private static GtTickerElement ParseTicker(XElement el)
    {
        var ticker = FillBase(el, new GtTickerElement());
        FillTextProperties(el, ticker, "");

        ticker.Speed = ParseDouble(el.Attribute("Speed")?.Value, 1.0);
        ticker.Direction = el.Attribute("Direction")?.Value switch
        {
            "Right"  => GtTickerDirection.Right,
            "Top"    => GtTickerDirection.Top,
            "Bottom" => GtTickerDirection.Bottom,
            _        => GtTickerDirection.Left
        };
        ticker.TickerType = el.Attribute("Type")?.Value == "Add"
            ? GtTickerType.Add : GtTickerType.Replace;

        // the template is kept whole, GT gives it a name and its own brushes and a Layer template carries entire child objects; only its text is modelled, every font property is overwritten from the ticker on each clone anyway
        var template = el.Element("Ticker.Template")?.Elements().FirstOrDefault();
        if (template is not null)
        {
            ticker.TemplateElementName = template.Name.LocalName;
            ticker.TemplateXml         = template.ToString(SaveOptions.DisableFormatting);

            if (GtTickerElement.IsTextTemplate(template.Name.LocalName))
            {
                var raw = template.Attribute("Text")?.Value;
                if (raw is not null)
                    ticker.Text = ticker.Uppercase ? raw.ToUpperInvariant() : raw;
            }
            else
            {
                Logger.Debug($"  ticker '{ticker.Name}' has a non-text template " +
                             $"'{template.Name.LocalName}' - preserved verbatim");
            }
        }

        return ticker;
    }

    private static GtImageElement ParseImage(XElement el)
    {
        var img = FillBase(el, new GtImageElement());
        var bitmap = el.Element("Image.Bitmap")?.Element("Bitmap");
        img.BitmapSource = bitmap?.Attribute("Source")?.Value?.Replace('\\', '/');
        var position = bitmap?.Attribute("Position")?.Value;
        if (position != null) img.SequencePosition = ParseDouble(position, 0);
        // SizeMode is a property of the image object, but accept it on the Bitmap child too
        img.SizeMode = ParseSizeMode(el.Attribute("SizeMode")?.Value
                                     ?? bitmap?.Attribute("SizeMode")?.Value);
        return img;
    }

    /// <summary>accepts the enum name or its ordinal; absent/unknown means Centered, GT's default for a new image which is why files written by the designer usually omit it</summary>
    private static GtImageSizeMode ParseSizeMode(string? value) => value?.Trim() switch
    {
        "Normal"   or "0" => GtImageSizeMode.Normal,
        "Stretch"  or "1" => GtImageSizeMode.Stretch,
        "TopRight" or "3" => GtImageSizeMode.TopRight,
        _                 => GtImageSizeMode.Centered
    };

    private static GtRectangleElement ParseRectangle(XElement el)
    {
        var rect = FillBase(el, new GtRectangleElement());
        rect.Fill            = ParseBrush(el.Element("Rectangle.Fill")?.Element("Brush"));
        rect.Stroke          = ParseBrush(el.Element("Rectangle.Stroke")?.Element("Brush"));
        rect.StrokeThickness = ParseDouble(el.Attribute("StrokeThickness")?.Value, 0.0);
        rect.StrokeDashStyle = ParseDashStyle(
            el.Element("Rectangle.StrokeStyle")?.Element("StrokeStyle")?.Attribute("DashStyle")?.Value);
        rect.Style = el.Attribute("Style")?.Value == "Square"
            ? GtRectangleStyle.Square : GtRectangleStyle.Rounded;
        return rect;
    }

    private static GtEllipseElement ParseEllipse(XElement el)
    {
        var ellipse = FillBase(el, new GtEllipseElement());
        ellipse.Fill            = ParseBrush(el.Element("Ellipse.Fill")?.Element("Brush"));
        ellipse.Stroke          = ParseBrush(el.Element("Ellipse.Stroke")?.Element("Brush"));
        ellipse.StrokeThickness = ParseDouble(el.Attribute("StrokeThickness")?.Value, 0.0);
        ellipse.StrokeDashStyle = ParseDashStyle(
            el.Element("Ellipse.StrokeStyle")?.Element("StrokeStyle")?.Attribute("DashStyle")?.Value);
        return ellipse;
    }

    private static GtStrokeDashStyle ParseDashStyle(string? value) => value switch
    {
        "Dash"       => GtStrokeDashStyle.Dash,
        "Dot"        => GtStrokeDashStyle.Dot,
        "DashDot"    => GtStrokeDashStyle.DashDot,
        "DashDotDot" => GtStrokeDashStyle.DashDotDot,
        _            => GtStrokeDashStyle.Solid
    };

    private static GtBrush? ParseBrush(XElement? el)
    {
        if (el is null) return null;
        var brush = new GtBrush();

        brush.Color = ParseColor(el.Attribute("Color")?.Value);
        brush.Type = el.Attribute("Type")?.Value switch
        {
            "LinearGradient" => GtBrushType.LinearGradient,
            "RadialGradient" => GtBrushType.RadialGradient,
            "Bitmap" => GtBrushType.Bitmap,
            _ => GtBrushType.Solid
        };

        if (brush.Type is GtBrushType.LinearGradient or GtBrushType.RadialGradient)
        {
            var wrapX = el.Attribute("WrapX")?.Value;
            var wrapY = el.Attribute("WrapY")?.Value;
            brush.WrapMode = (wrapX ?? wrapY) switch
            {
                "Clamp" => GtRadialWrap.Clamp,
                "Wrap"  => GtRadialWrap.Wrap,
                _       => GtRadialWrap.Mirror
            };
        }

        if (brush.Type is GtBrushType.LinearGradient or GtBrushType.RadialGradient or GtBrushType.Bitmap)
        {
            brush.StartPoint = ParseGtPoint(el.Attribute("StartPoint")?.Value) ?? new(0.5, 0);
            brush.EndPoint = ParseGtPoint(el.Attribute("EndPoint")?.Value) ?? new(0.5, 1);

            foreach (var stop in el.Element("Brush.Stops")?.Elements("GradientStop") ?? Array.Empty<XElement>())
            {
                brush.Stops.Add(new GtGradientStop
                {
                    Position = ParseDouble(stop.Attribute("Position")?.Value, 0),
                    Color = ParseColor(stop.Attribute("Color")?.Value)
                });
            }

            if (brush.Type == GtBrushType.Bitmap)
            {
                var src = el.Element("Brush.Bitmap")?.Element("Bitmap")?.Attribute("Source")?.Value;
                brush.BitmapSource = src?.Replace('\\', '/');
            }
        }

        return brush;
    }

    private static GtPoint? ParseGtPoint(string? value)
    {
        if (value is null) return null;
        var p = value.Split(',');
        if (p.Length < 2) return null;
        return new(
            double.Parse(p[0], CultureInfo.InvariantCulture),
            double.Parse(p[1], CultureInfo.InvariantCulture));
    }

    private static Color ParseColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return Colors.Transparent;
        try { return Color.Parse(hex); }
        catch { return Colors.Transparent; }
    }

    private static double ParseDouble(string? value, double fallback = 0)
    {
        if (value is null) return fallback;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : fallback;
    }

    private static bool ParseBool(string? value, bool fallback = false)
    {
        if (value is null) return fallback;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
