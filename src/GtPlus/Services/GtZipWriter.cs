using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Media;
using GtPlus.Models;

namespace GtPlus.Services;

/// <summary>writes a GtDocument and asset dictionary back to a .gtzip file, producing a valid ZIP with document.xml (UTF-16), resources.xml, [Content_Types].xml, and raw asset blobs under freshly-generated GUIDs</summary>
public class GtZipWriter
{
    public void Write(string path, GtDocument document, GtAssetLibrary assets)
    {
        // assign a fresh GUID to every asset so we always produce a clean package
        var guidMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in assets.Blobs.Keys)
            guidMap[key] = Guid.NewGuid().ToString();

        // the carrier object exists only inside the file, added for the duration of the write and taken straight back out so the live document never carries a fake element
        var carrier = GuidesPart.CreateCarrier(document);
        var carrierLayer = carrier is not null && document.Layers.Count > 0 ? document.Layers[0] : null;
        carrierLayer?.Elements.Insert(0, carrier!);

        // write to a temp file first, avoids corrupting the original on failure
        var tempPath = path + ".gtzip_tmp";
        try
        {
            WriteTo(tempPath, document, assets, guidMap);
            File.Move(tempPath, path, overwrite: true);
            Logger.Info($"Saved: {path}");
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
        finally
        {
            if (carrier is not null) carrierLayer?.Elements.Remove(carrier);
        }
    }

    private static void WriteTo(string path, GtDocument document,
        GtAssetLibrary assets, Dictionary<string, string> guidMap)
    {
        using var fs  = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        WriteContentTypes(zip, guidMap);
        WriteDocumentXml(zip, document);
        WriteResourcesXml(zip, assets, guidMap);
        WriteAssets(zip, assets, guidMap);
    }

    private static void WriteContentTypes(ZipArchive zip, Dictionary<string, string> guidMap)
    {
        const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";

        var root = new XElement($"{{{ns}}}Types",
            new XElement($"{{{ns}}}Default",
                new XAttribute("Extension",   "xml"),
                new XAttribute("ContentType", "text/xml")),
            new XElement($"{{{ns}}}Default",
                new XAttribute("Extension",   "png"),
                new XAttribute("ContentType", "image/png")));

        foreach (var guid in guidMap.Values)
            root.Add(new XElement($"{{{ns}}}Override",
                new XAttribute("PartName",    $"/{guid}"),
                new XAttribute("ContentType", "application/octet-stream")));

        var entry = zip.CreateEntry("[Content_Types].xml");
        using var stream = entry.Open();
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(stream);
    }

    private static void WriteDocumentXml(ZipArchive zip, GtDocument document)
    {
        var root = new XElement("Composition",
            new XAttribute("Width",  Fmt(document.Width)),
            new XAttribute("Height", Fmt(document.Height)));

        foreach (var layer in document.Layers)
            root.Add(SerializeLayer(layer));

        // GT drops a DataChange storyboard whose field no longer exists (object deleted, renamed away, or a ticker template changed) rather than writing a scope nothing can ever trigger
        foreach (var storyboard in document.Storyboards)
        {
            if (storyboard.IsScoped && !GtDataFieldService.IsKnownField(document, storyboard.DataName))
            {
                Logger.Debug($"  skipping storyboard '{storyboard.EventLabel}': no such data field");
                continue;
            }

            root.Add(SerializeStoryboard(storyboard));
        }



        var entry    = zip.CreateEntry("document.xml");
        using var stream = entry.Open();
        var settings = new XmlWriterSettings
        {
            Encoding    = new UTF8Encoding(false),  // UTF-8, no BOM, vMix rejects BOM
            Indent      = true,
            IndentChars = "  ",
        };
        using var writer = XmlWriter.Create(stream, settings);
        new XDocument(root).WriteTo(writer);
    }

    private static XElement SerializeLayer(GtLayer layer)
    {
        double innerW = layer.InnerWidth  > 0 ? layer.InnerWidth  : layer.Dimensions.Width;
        double innerH = layer.InnerHeight > 0 ? layer.InnerHeight : layer.Dimensions.Height;

        var comp = new XElement("Composition",
            new XAttribute("Width",  Fmt(innerW)),
            new XAttribute("Height", Fmt(innerH)));

        foreach (var el in layer.Elements)
        {
            XElement? node = el switch
            {
                GtTickerElement    ticker  => SerializeTicker(ticker),
                GtTextBlock        tb      => SerializeTextBlock(tb),
                GtImageElement     img     => SerializeImage(img),
                GtRectangleElement rect    => SerializeRectangle(rect),
                GtEllipseElement   ellipse => SerializeEllipse(ellipse),
                _                          => null
            };
            if (node is not null) comp.Add(node);
        }

        return new XElement("Layer",
            new XAttribute("Name",       layer.Name),
            new XAttribute("Dimensions", Sz(layer.Dimensions)),
            new XAttribute("Location",   Pt(layer.Location)),
            new XAttribute("Locked",     Bool(layer.Locked)),
            new XAttribute("Visible",    Bool(layer.Visible)),
            new XElement("Layer.Composition", comp));
    }

    private static XElement SerializeStoryboard(GtStoryboard storyboard)
    {
        var animations = new XElement("Storyboard.Animations");
        foreach (var anim in storyboard.Animations)
            animations.Add(SerializeAnimation(anim));

        var el = new XElement("Storyboard");
        // TransitionIn is the default and GT writes no attribute for it
        if (storyboard.Type is not null) el.Add(new XAttribute("Type", storyboard.Type));
        if (storyboard.IsScoped) el.Add(new XAttribute("DataName", storyboard.DataName));
        el.Add(animations);
        return el;
    }

    /// <summary>writes one animation; attribute order and omission mirror GT Designer output, defaults (Delay 0, Reverse False, Linear interpolation, absent Direction/CenterAxis) are left out entirely so round-tripped files stay byte-comparable</summary>
    private static XElement SerializeAnimation(GtAnimation anim)
    {
        var el = new XElement(anim.TypeName);
        el.Add(new XAttribute("Object", anim.Object));
        if (anim.Delay    != 0)    el.Add(new XAttribute("Delay",    Fmt(anim.Delay)));
        if (anim.Duration is { } d) el.Add(new XAttribute("Duration", Fmt(d)));
        if (anim.Reverse)           el.Add(new XAttribute("Reverse",  "True"));
        if (anim.Interpolation != GtInterpolation.Linear)
            el.Add(new XAttribute("Interpolation", anim.Interpolation.ToString()));
        // Direction is omitted while it matches the type's implicit default (Bottom for Scroll, Left otherwise) and CenterAxis while it matches GT's Both default, GT drops every property still sitting on its registered default
        if (!anim.DirectionIsDefault)
            el.Add(new XAttribute("Direction", anim.Direction.ToString()));
        if (!anim.CenterAxisIsDefault)
            el.Add(new XAttribute("CenterAxis", anim.CenterAxis.ToString()));
        if (anim.Speed is { } spd) el.Add(new XAttribute("Speed", Fmt(spd)));

        foreach (var extra in anim.ExtraAttributes)
            if (el.Attribute(extra.Name) is null)
                el.Add(new XAttribute(extra.Name, extra.Value));

        return el;
    }

    private static XElement SerializeTextBlock(GtTextBlock tb)
    {
        var el = new XElement("TextBlock");
        WriteBaseAttribs(el, tb, "TextBlock");
        el.Add(new XAttribute("Text", tb.Text));
        WriteTextAttribs(el, tb, "TextBlock");
        // TextObject-only property, omitted at GT's default so untouched files stay byte-clean
        if (tb.AutoSize != GtAutoSize.Fixed)
            el.Add(new XAttribute("AutoSize", tb.AutoSize.ToString()));
        WriteMask(el, tb, "TextBlock");
        WriteCrop(el, tb, "TextBlock");
        WriteBounding(el, tb, "TextBlock");
        return el;
    }

    /// <summary>writes the font/fill/stroke property set shared by every text object; <c>Text</c> is left to the caller, a text block carries it as an attribute and a ticker keeps it in its template</summary>
    private static void WriteTextAttribs(XElement el, GtTextBlock tb, string typeName)
    {
        el.Add(new XAttribute("FontFamily",       tb.FontFamily));
        el.Add(new XAttribute("FontSize",         Fmt(tb.FontSize)));
        el.Add(new XAttribute("FontWeight",       SerializeFontWeight(tb.FontWeight)));
        el.Add(new XAttribute("TextAlign",        tb.TextAlign.ToString()));
        el.Add(new XAttribute("VerticalAlign",    tb.VerticalAlign.ToString()));
        el.Add(new XAttribute("LineSpacing",      Fmt(tb.LineSpacing)));
        el.Add(new XAttribute("StrokeThickness",  Fmt(tb.StrokeThickness)));
        if (tb.Uppercase)      el.Add(new XAttribute("TextEffect",       "Uppercase"));
        if (tb.IgnoreOverhang) el.Add(new XAttribute("IgnoreOverhang",   "True"));
        if (tb.NoWrap)         el.Add(new XAttribute("TextWordWrapping", "NoWrap"));
        if (tb.Fill   is not null) el.Add(new XElement(typeName + ".Fill",   SerializeBrush(tb.Fill)));
        if (tb.Stroke is not null) el.Add(new XElement(typeName + ".Stroke", SerializeBrush(tb.Stroke)));
    }

    /// <summary>writes a Ticker; the scroll properties are omitted while they sit on GT's defaults (Speed 1, Left, Replace) and the text goes into the template rather than onto the object, since GT re-defaults the ticker's own Text property after every write so it never reaches the file</summary>
    private static XElement SerializeTicker(GtTickerElement ticker)
    {
        var el = new XElement("Ticker");
        WriteBaseAttribs(el, ticker, "Ticker");
        if (ticker.Speed != 1.0)
            el.Add(new XAttribute("Speed", Fmt(ticker.Speed)));
        if (ticker.Direction != GtTickerDirection.Left)
            el.Add(new XAttribute("Direction", ticker.Direction.ToString()));
        if (ticker.TickerType != GtTickerType.Replace)
            el.Add(new XAttribute("Type", ticker.TickerType.ToString()));
        WriteTextAttribs(el, ticker, "Ticker");
        el.Add(new XElement("Ticker.Template", SerializeTickerTemplate(ticker)));
        WriteMask(el, ticker, "Ticker");
        WriteCrop(el, ticker, "Ticker");
        WriteBounding(el, ticker, "Ticker");
        return el;
    }

    /// <summary>the template child; a template this editor did not recognise as text is written back exactly as it was read, everything else is a text template carrying only the text since GT overwrites the rest from the ticker on every clone</summary>
    private static XElement SerializeTickerTemplate(GtTickerElement ticker)
    {
        if (ticker.TemplateXml is not null)
        {
            try
            {
                var template = XElement.Parse(ticker.TemplateXml);
                // a text template goes back out as GT wrote it (name, brushes and all) with only the edited text put back, a Layer template is left completely alone
                if (GtTickerElement.IsTextTemplate(template.Name.LocalName))
                    template.SetAttributeValue("Text", ticker.Text);
                return template;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Ticker '{ticker.Name}': template XML could not be re-parsed " +
                            $"({ex.Message}); writing a fresh text template instead");
            }
        }

        var name = string.IsNullOrEmpty(ticker.TemplateElementName)
            ? GtTickerElement.DefaultTemplateElementName
            : ticker.TemplateElementName;
        return new XElement(name, new XAttribute("Text", ticker.Text));
    }

    private static XElement SerializeImage(GtImageElement img)
    {
        var el = new XElement("Image");
        WriteBaseAttribs(el, img, "Image");
        // Centered is GT's default, the designer omits the attribute at that value
        if (img.SizeMode != GtImageSizeMode.Centered)
            el.Add(new XAttribute("SizeMode", img.SizeMode.ToString()));
        if (img.BitmapSource is not null)
        {
            var bitmap = new XElement("Bitmap",
                new XAttribute("Source", img.BitmapSource.Replace('/', '\\')));
            if (img.SequencePosition is { } pos)
                bitmap.Add(new XAttribute("Position", Fmt(pos)));
            el.Add(new XElement("Image.Bitmap", bitmap));
        }
        WriteMask(el, img, "Image");
        WriteCrop(el, img, "Image");
        WriteBounding(el, img, "Image");
        return el;
    }

    private static XElement SerializeRectangle(GtRectangleElement rect)
    {
        var el = new XElement("Rectangle");
        WriteBaseAttribs(el, rect, "Rectangle");
        el.Add(new XAttribute("StrokeThickness", Fmt(rect.StrokeThickness)));
        if (rect.Style == GtRectangleStyle.Square)
            el.Add(new XAttribute("Style", "Square"));
        if (rect.Fill   is not null) el.Add(new XElement("Rectangle.Fill",   SerializeBrush(rect.Fill)));
        if (rect.Stroke is not null) el.Add(new XElement("Rectangle.Stroke", SerializeBrush(rect.Stroke)));
        if (rect.StrokeDashStyle != GtStrokeDashStyle.Solid)
            el.Add(new XElement("Rectangle.StrokeStyle",
                new XElement("StrokeStyle",
                    new XAttribute("DashStyle", SerializeDashStyle(rect.StrokeDashStyle)))));
        WriteMask(el, rect, "Rectangle");
        WriteCrop(el, rect, "Rectangle");
        WriteBounding(el, rect, "Rectangle");
        return el;
    }

    private static XElement SerializeEllipse(GtEllipseElement ellipse)
    {
        var el = new XElement("Ellipse");
        WriteBaseAttribs(el, ellipse, "Ellipse");
        el.Add(new XAttribute("StrokeThickness", Fmt(ellipse.StrokeThickness)));
        if (ellipse.Fill   is not null) el.Add(new XElement("Ellipse.Fill",   SerializeBrush(ellipse.Fill)));
        if (ellipse.Stroke is not null) el.Add(new XElement("Ellipse.Stroke", SerializeBrush(ellipse.Stroke)));
        if (ellipse.StrokeDashStyle != GtStrokeDashStyle.Solid)
            el.Add(new XElement("Ellipse.StrokeStyle",
                new XElement("StrokeStyle",
                    new XAttribute("DashStyle", SerializeDashStyle(ellipse.StrokeDashStyle)))));
        WriteMask(el, ellipse, "Ellipse");
        WriteCrop(el, ellipse, "Ellipse");
        WriteBounding(el, ellipse, "Ellipse");
        return el;
    }

    /// <summary>emits &lt;ElementType.Mask&gt;&lt;Mask Object="Name"/&gt;&lt;/ElementType.Mask&gt;, always written last so round-tripped child order matches GT Designer output</summary>
    private static void WriteMask(XElement el, GtElement src, string typeName)
    {
        if (src.MaskObject is null) return;
        el.Add(new XElement(typeName + ".Mask",
            new XElement("Mask", new XAttribute("Object", src.MaskObject))));
    }

    /// <summary>emits &lt;ElementType.Crop&gt;&lt;Crop Range="..." Feather="..."/&gt;&lt;/ElementType.Crop&gt;; an untouched (full) Range is omitted while feathering is still written, matching GT Designer output</summary>
    /// <remarks>every non-null crop is written even one that changes nothing; an absent Feather attribute means 8px per edge not 0, so a deliberately zeroed feather has to be on the element to survive a round trip since dropping it would silently restore the 8px default; GT Designer behaves the same way, it writes &lt;Crop Feather="..."/&gt; once its crop panel has been touched cropped or not, and the panel drops the whole crop back to null when the user resets it which is what removes the element from the file</remarks>
    private static void WriteCrop(XElement el, GtElement src, string typeName)
    {
        var crop = src.Crop;
        if (crop is null) return;

        var node = new XElement("Crop");
        if (!crop.RangeIsFull)
            node.Add(new XAttribute("Range",
                $"{Fmt(crop.X0)},{Fmt(crop.Y0)},{Fmt(crop.X1)},{Fmt(crop.Y1)}"));
        node.Add(new XAttribute("Feather",
            $"{Fmt(crop.FeatherLeft)},{Fmt(crop.FeatherTop)}," +
            $"{Fmt(crop.FeatherRight)},{Fmt(crop.FeatherBottom)}"));

        el.Add(new XElement(typeName + ".Crop", node));
    }

    /// <summary>emits &lt;ElementType.Bounding&gt;&lt;Bounding Object="Name" Padding="l,t,r,b"/&gt;&lt;/ElementType.Bounding&gt;; each attribute is dropped while it sits on its default and a binding left with neither a source nor any padding is dropped whole, so an untouched file stays byte-clean</summary>
    private static void WriteBounding(XElement el, GtElement src, string typeName)
    {
        var bounding = src.Bounding;
        if (bounding is null || bounding.IsDefault) return;

        var node = new XElement("Bounding");
        if (bounding.HasSource)
            node.Add(new XAttribute("Object", bounding.Object!));
        if (bounding.HasPadding)
            node.Add(new XAttribute("Padding",
                $"{Fmt(bounding.PaddingLeft)},{Fmt(bounding.PaddingTop)}," +
                $"{Fmt(bounding.PaddingRight)},{Fmt(bounding.PaddingBottom)}"));

        el.Add(new XElement(typeName + ".Bounding", node));
    }

    private static string SerializeDashStyle(GtStrokeDashStyle s) => s switch
    {
        GtStrokeDashStyle.Dash       => "Dash",
        GtStrokeDashStyle.Dot        => "Dot",
        GtStrokeDashStyle.DashDot    => "DashDot",
        GtStrokeDashStyle.DashDotDot => "DashDotDot",
        _                            => "Solid"
    };

    /// <summary>renders the set flags as vMix's comma-separated list, or null when none are set so the caller can leave the attribute off the element</summary>
    private static string? DataFlagsText(GtDataFlags flags)
    {
        if (flags == GtDataFlags.None) return null;

        var parts = new List<string>(3);
        if (flags.HasFlag(GtDataFlags.Hidden))      parts.Add("Hidden");
        if (flags.HasFlag(GtDataFlags.NoEvents))    parts.Add("NoEvents");
        if (flags.HasFlag(GtDataFlags.ShowVisible)) parts.Add("ShowVisible");
        return string.Join(", ", parts);
    }

    private static void WriteBaseAttribs(XElement el, GtElement src, string typeName)
    {
        el.Add(new XAttribute("Name",       src.Name));
        el.Add(new XAttribute("Dimensions", Sz(src.Dimensions)));
        // Location is the anchor point in the file, the editor holds the top-left corner so the anchor offset goes back on here; GT omits Anchor for the default top-left
        el.Add(new XAttribute("Location",   Pt(new GtPoint(src.AnchorX(), src.AnchorY()))));
        if (src.Anchor != GtAnchor.TopLeft)
            el.Add(new XAttribute("Anchor", src.Anchor.ToString()));
        el.Add(new XAttribute("Visible",    Bool(src.Visible)));
        el.Add(new XAttribute("Opacity",    Fmt(src.Opacity)));
        el.Add(new XAttribute("Locked",     Bool(src.Locked)));
        // flags that are off are simply absent, vMix omits the attribute entirely when no flag is set so an untouched element round-trips unchanged; a shape whose only flag is Hidden writes nothing either, for a Rectangle / Ellipse the missing attribute (GT Title's "None") already means hidden and the reader restores the flag
        var flags = src is GtRectangleElement or GtEllipseElement && src.DataFlags == GtDataFlags.Hidden
            ? GtDataFlags.None
            : src.DataFlags;
        var dataFlags = DataFlagsText(flags);
        if (dataFlags is not null)
            el.Add(new XAttribute("DataFlags", dataFlags));

        if (src.RotateX != 0 || src.RotateY != 0 || src.RotateZ != 0)
            el.Add(new XElement(typeName + ".Transform",
                new XElement("Transform",
                    new XAttribute("Rotate",
                        $"{Fmt(src.RotateX)},{Fmt(src.RotateY)},{Fmt(src.RotateZ)}"))));
    }

    private static XElement SerializeBrush(GtBrush brush)
    {
        var el = new XElement("Brush",
            new XAttribute("Color", ColorStr(brush.Color)));

        if (brush.Type != GtBrushType.Solid)
        {
            el.Add(new XAttribute("Type", brush.Type switch
            {
                GtBrushType.LinearGradient => "LinearGradient",
                GtBrushType.RadialGradient => "RadialGradient",
                _                          => "Bitmap"
            }));
            el.Add(new XAttribute("StartPoint", $"{Fmt(brush.StartPoint.X)},{Fmt(brush.StartPoint.Y)}"));
            el.Add(new XAttribute("EndPoint",   $"{Fmt(brush.EndPoint.X)},{Fmt(brush.EndPoint.Y)}"));
            if (brush.Type is GtBrushType.LinearGradient or GtBrushType.RadialGradient
                && brush.WrapMode != GtRadialWrap.Mirror)
            {
                var wrap = brush.WrapMode == GtRadialWrap.Wrap ? "Wrap" : "Clamp";
                el.Add(new XAttribute("WrapX", wrap));
                el.Add(new XAttribute("WrapY", wrap));
            }

            if (brush.Stops.Count > 0)
            {
                var stopsEl = new XElement("Brush.Stops");
                foreach (var stop in brush.Stops)
                {
                    var stopEl = new XElement("GradientStop",
                        new XAttribute("Color", ColorStr(stop.Color)));
                    if (stop.Position != 0)
                        stopEl.Add(new XAttribute("Position", Fmt(stop.Position)));
                    stopsEl.Add(stopEl);
                }
                el.Add(stopsEl);
            }

            if (brush.Type == GtBrushType.Bitmap && brush.BitmapSource is not null)
                el.Add(new XElement("Brush.Bitmap",
                    new XElement("Bitmap",
                        new XAttribute("Source", brush.BitmapSource.Replace('/', '\\')))));
        }

        return el;
    }

    /// <summary>writes resources.xml; a sequence becomes one resource with a source per frame in playback order, every other blob becomes a single-source resource, and frames that belong to a sequence must not also be emitted standalone or vMix sees duplicate resources</summary>
    private static void WriteResourcesXml(ZipArchive zip, GtAssetLibrary assets,
        Dictionary<string, string> guidMap)
    {
        var root = new XElement("resources");

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (anchor, frames) in assets.Sequences)
        {
            var resource = new XElement("resource",
                new XAttribute("filename", anchor.Replace('/', '\\')));

            foreach (var frame in frames)
            {
                if (!guidMap.TryGetValue(frame, out var frameGuid)) continue;
                claimed.Add(frame);
                var frameBackslash = frame.Replace('/', '\\');
                resource.Add(new XElement("source",
                    new XAttribute("guid", frameGuid),
                    frameBackslash));
            }

            root.Add(resource);
        }

        foreach (var (logicalPath, guid) in guidMap)
        {
            if (claimed.Contains(logicalPath)) continue;
            var backslash = logicalPath.Replace('/', '\\');
            root.Add(new XElement("resource",
                new XAttribute("filename", backslash),
                new XElement("source",
                    new XAttribute("guid", guid),
                    backslash)));
        }

        var entry = zip.CreateEntry("resources.xml");
        using var stream = entry.Open();
        var settings = new XmlWriterSettings
        {
            Encoding           = new UTF8Encoding(false),  // no BOM
            OmitXmlDeclaration = true,                     // original has no declaration
        };
        using var writer = XmlWriter.Create(stream, settings);
        new XDocument(root).WriteTo(writer);
    }

    private static void WriteAssets(ZipArchive zip, GtAssetLibrary assets,
        Dictionary<string, string> guidMap)
    {
        foreach (var (logicalPath, bytes) in assets.Blobs)
        {
            var guid  = guidMap[logicalPath];
            var entry = zip.CreateEntry(guid);
            using var stream = entry.Open();
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    private static string Fmt(double v) => v.ToString("G", CultureInfo.InvariantCulture);

    private static string Pt(GtPoint p) => $"{Fmt(p.X)},{Fmt(p.Y)},0";

    private static string Sz(GtSize s) => $"{Fmt(s.Width)},{Fmt(s.Height)},0";

    private static string Bool(bool b) => b ? "True" : "False";

    private static string ColorStr(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string SerializeFontWeight(FontWeight w) => w switch
    {
        FontWeight.Bold     => "Bold",
        FontWeight.Light    => "Light",
        FontWeight.Thin     => "Thin",
        FontWeight.SemiBold => "SemiBold",
        _                   => "Regular"
    };
}
