using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using GtPlus.Models;

namespace GtPlus.Services;

/// <summary>what kind of value a data field carries, mirrors GT's <c>GraphicsDataType</c></summary>
public enum GtDataFieldKind { Text, Image, Color }

/// <summary>one entry of the composition's data bag, a field vMix can push a value into</summary>
public sealed class GtDataField
{
    public GtDataField(string name, GtDataFieldKind kind, GtLayer layer, GtElement element)
    {
        Name    = name;
        Kind    = kind;
        Layer   = layer;
        Element = element;
    }

    /// <summary>fully qualified field name, e.g. <c>Name.Text</c> or <c>Ticker1.Logo.Source</c></summary>
    public string Name { get; }

    public GtDataFieldKind Kind { get; }

    /// <summary>layer holding the element that registered the field</summary>
    public GtLayer Layer { get; }

    /// <summary>element that registered the field, the ticker itself for a template field</summary>
    public GtElement Element { get; }

    /// <summary>flags come from the owning element, GT has no per-field override</summary>
    public GtDataFlags Flags => Element.DataFlags;

    /// <summary>whether the field is offered as a DataChange event source; GT hides a field from the event list when either <see cref="GtDataFlags.Hidden"/> or <see cref="GtDataFlags.NoEvents"/> is set</summary>
    public bool RaisesEvents =>
        !Flags.HasFlag(GtDataFlags.Hidden) && !Flags.HasFlag(GtDataFlags.NoEvents);
}

/// <summary>the composition's data fields, the sources a <c>DataChangeIn</c> / <c>DataChangeOut</c> storyboard can be scoped to; GT walks only one level of nesting, the direct children of each top-level layer, objects at the composition root and inside a nested layer register no fields and the flat list is reversed so the last layer's last element comes first, and that order is user-visible in the event picker so it is reproduced here; only four element kinds register anything, text (including tickers) exposes <c>Text</c>, images expose <c>Source</c>, shapes expose <c>Fill.Color</c> or <c>Fill.Bitmap</c>, and shapes carry <see cref="GtDataFlags.Hidden"/> by default so their fields exist but are not offered until the user unticks Hidden; unlike GT whose shape field only comes into being on the first fill edit, this editor registers it as soon as the shape has a fill</summary>
public static class GtDataFieldService
{
    public const string TextSuffix       = ".Text";
    public const string SourceSuffix     = ".Source";
    public const string FillColorSuffix  = ".Fill.Color";
    public const string FillBitmapSuffix = ".Fill.Bitmap";

    /// <summary>every registered field, flags ignored, in GT's own (reversed) order</summary>
    public static List<GtDataField> GetFields(GtDocument? document)
    {
        var fields = new List<GtDataField>();
        if (document is null) return fields;

        foreach (var layer in document.Layers)
            foreach (var element in layer.Elements)
                AddFields(fields, layer, element);

        // GT reverses the whole flat list, not just the layer order
        fields.Reverse();
        return fields;
    }

    /// <summary>fields that can trigger a DataChange storyboard, neither Hidden nor NoEvents</summary>
    public static List<GtDataField> GetEventFields(GtDocument? document) =>
        GetFields(document).Where(f => f.RaisesEvents).ToList();

    /// <summary>names of every registered field, flags ignored, used to prune dangling scopes</summary>
    public static List<string> GetNames(GtDocument? document) =>
        GetFields(document).Select(f => f.Name).ToList();

    /// <summary>true when <paramref name="dataName"/> still names a field of <paramref name="document"/>; an empty name is the unscoped storyboard which is always valid, GT compares field names case-sensitively so this does too</summary>
    public static bool IsKnownField(GtDocument? document, string? dataName)
    {
        if (string.IsNullOrEmpty(dataName)) return true;
        foreach (var field in GetFields(document))
            if (string.Equals(field.Name, dataName, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>rewrites a scoped storyboard's field name across an object rename, <c>Old.Text</c> becomes <c>New.Text</c>; returns the name unchanged when the prefix does not match</summary>
    public static string RenameOwner(string dataName, string oldName, string newName)
    {
        if (string.IsNullOrEmpty(dataName) || string.IsNullOrEmpty(oldName)) return dataName;

        var prefix = oldName + ".";
        return dataName.StartsWith(prefix, StringComparison.Ordinal)
            ? newName + dataName.Substring(oldName.Length)
            : dataName;
    }

    private static void AddFields(List<GtDataField> fields, GtLayer layer, GtElement element)
    {
        switch (element)
        {
            // ticker before text, a ticker is a text block and its fields come from its template
            case GtTickerElement ticker:
                AddTickerFields(fields, layer, ticker);
                break;

            case GtTextBlock:
                fields.Add(new GtDataField(Qualify(element.Name, TextSuffix),
                                           GtDataFieldKind.Text, layer, element));
                break;

            case GtImageElement:
                fields.Add(new GtDataField(Qualify(element.Name, SourceSuffix),
                                           GtDataFieldKind.Image, layer, element));
                break;

            case GtRectangleElement rect:
                AddFillField(fields, layer, element, rect.Fill);
                break;

            case GtEllipseElement ellipse:
                AddFillField(fields, layer, element, ellipse.Fill);
                break;
        }
    }

    /// <summary>a ticker's fields come from its template not from its own text; a text template gives one <c>Text</c> field, a layer template gives one field per direct child, the only place a field name carries two dots</summary>
    private static void AddTickerFields(List<GtDataField> fields, GtLayer layer, GtTickerElement ticker)
    {
        if (GtTickerElement.IsTextTemplate(ticker.TemplateElementName) || ticker.TemplateXml is null)
        {
            fields.Add(new GtDataField(Qualify(ticker.Name, TextSuffix),
                                       GtDataFieldKind.Text, layer, ticker));
            return;
        }

        foreach (var child in TemplateChildren(ticker))
        {
            var name = child.Attribute("Name")?.Value;
            if (string.IsNullOrEmpty(name)) continue;

            var local = child.Name.LocalName;
            if (GtTickerElement.IsTextTemplate(local))
            {
                fields.Add(new GtDataField(Qualify(ticker.Name, "." + name + TextSuffix),
                                           GtDataFieldKind.Text, layer, ticker));
            }
            else if (local == "Image" && child.Element("Image.Bitmap") is not null)
            {
                // GT only registers an image child that actually carries a bitmap source
                fields.Add(new GtDataField(Qualify(ticker.Name, "." + name + SourceSuffix),
                                           GtDataFieldKind.Image, layer, ticker));
            }
        }
    }

    /// <summary>direct children of a layer template, or nothing when the XML is not a layer</summary>
    private static IEnumerable<XElement> TemplateChildren(GtTickerElement ticker)
    {
        XElement template;
        try { template = XElement.Parse(ticker.TemplateXml!); }
        catch (System.Xml.XmlException) { yield break; }

        var composition = template.Element(template.Name.LocalName + ".Composition")?
                                  .Element("Composition");
        if (composition is null) yield break;

        foreach (var child in composition.Elements()) yield return child;
    }

    private static void AddFillField(
        List<GtDataField> fields, GtLayer layer, GtElement element, GtBrush? fill)
    {
        if (fill is null) return;

        bool bitmap = fill.Type == GtBrushType.Bitmap;
        fields.Add(new GtDataField(
            Qualify(element.Name, bitmap ? FillBitmapSuffix : FillColorSuffix),
            bitmap ? GtDataFieldKind.Image : GtDataFieldKind.Color,
            layer, element));
    }

    /// <summary>field names are <c>Object.Field</c>, GT drops the prefix for an unnamed object</summary>
    private static string Qualify(string objectName, string suffix) =>
        string.IsNullOrEmpty(objectName) ? suffix.TrimStart('.') : objectName + suffix;
}
