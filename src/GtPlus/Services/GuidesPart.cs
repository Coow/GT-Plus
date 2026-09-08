using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Avalonia.Media;
using GtPlus.Models;

namespace GtPlus.Services;

/// <summary>a guide set on its own, the payload of a .gtguides file or the legacy zip part</summary>
public class GuideSet
{
    public List<GtGuide> Guides { get; set; } = new();
    public GtPoint RulerOrigin { get; set; } = GtPoint.Zero;
    public bool Locked { get; set; }

    /// <summary>canvas size the guides were authored against, used to offer rescaling on import</summary>
    public double SourceWidth  { get; set; }
    public double SourceHeight { get; set; }
}

/// <summary>reads and writes guides; GT Title Designer has no format for them and two tidier hiding places both lost the data the first time GT re-saved a template, an extra zip part (<c>gtplus/guides.xml</c>, dropped when GT rebuilds the package) and a private <c>&lt;GTPlus&gt;</c> element in document.xml (dropped when GT reserialises the XML); what GT does keep is objects it understands names and all, so guides now travel inside the name of a hidden 1×1 transparent rectangle written on save and stripped back out on load, both older schemes are still read and a standalone <c>.gtguides</c> file remains the way to share guides between titles</summary>
public static class GuidesPart
{
    public const string EntryName = "gtplus/guides.xml";

    /// <summary>extension of the shareable guides-only file</summary>
    public const string FileExtension = "gtguides";

    /// <summary>name of the single element carrying guides inside document.xml</summary>
    public const string ElementName = "GTPlus";

    /// <summary>name prefix of the hidden rectangle that carries guides through GT</summary>
    public const string CarrierPrefix = "GTPlus_";

    /// <summary>builds the hidden 1×1 rectangle whose <see cref="GtElement.Name"/> carries the guide state, or null when there is nothing to store; GT Title Designer discards both extra zip parts and elements it does not recognise so the data has to look like an ordinary object, a fully transparent invisible locked 1×1 rectangle with the payload base64url-encoded into its name, GT round-trips object names verbatim and a 1×1 transparent rectangle renders nothing if it ever is drawn</summary>
    public static GtRectangleElement? CreateCarrier(GtDocument document)
    {
        bool hasOrigin = document.RulerOrigin.X != 0 || document.RulerOrigin.Y != 0;
        if (document.Guides.Count == 0 && !document.GuidesLocked && !hasOrigin) return null;

        return new GtRectangleElement
        {
            Name            = CarrierPrefix + Encode(document),
            Location        = GtPoint.Zero,
            Dimensions      = new GtSize(1, 1),
            Visible         = false,
            Opacity         = 0,
            Locked          = true,
            StrokeThickness = 0,
            // explicitly hidden from the vMix title editor; a flagless shape already behaves that way at playout, but saying so keeps the carrier out of the field list GT itself shows
            DataFlags       = GtDataFlags.Hidden,
            Fill            = new GtBrush
            {
                Type  = GtBrushType.Solid,
                Color = Color.FromArgb(0, 0, 0, 0)
            },
        };
    }

    /// <summary>pulls guide state out of any carrier rectangles and removes them from the document so the rest of the editor never sees the carrier as a real object; returns false when the document holds no carrier</summary>
    public static bool ExtractCarrier(GtDocument document)
    {
        string? payload = null;

        foreach (var layer in document.Layers)
        {
            for (int i = layer.Elements.Count - 1; i >= 0; i--)
            {
                var name = layer.Elements[i].Name;
                if (name is null || !name.StartsWith(CarrierPrefix, StringComparison.Ordinal)) continue;

                // keep the first payload found, drop every carrier including stale duplicates
                payload ??= name.Substring(CarrierPrefix.Length);
                layer.Elements.RemoveAt(i);
            }
        }

        if (payload is null) return false;

        try
        {
            Decode(payload, document);
            Logger.Debug($"Guides loaded from carrier object: {document.Guides.Count}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to decode guide carrier: {ex.Message}");
            return false;
        }
    }

    /// <summary>packs guides into <c>version|guides|locked|origin</c>, base64url-encoded</summary>
    private static string Encode(GtDocument document)
    {
        var guides = string.Join(";", document.Guides.ConvertAll(g =>
            (g.Orientation == GtGuideOrientation.Vertical ? "V:" : "H:") + Fmt(g.Position)));

        var payload = string.Join("|", new[]
        {
            "1",
            guides,
            document.GuidesLocked ? "1" : "0",
            Fmt(document.RulerOrigin.X) + "," + Fmt(document.RulerOrigin.Y)
        });

        // base64url keeps the name to [A-Za-z0-9_-], which no name sanitiser should object to
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
                      .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void Decode(string encoded, GtDocument document)
    {
        var b64 = encoded.Replace('-', '+').Replace('_', '/');
        b64 += new string('=', (4 - b64.Length % 4) % 4);
        var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
        if (parts.Length < 4) throw new FormatException("Unexpected payload shape");

        document.Guides.Clear();
        foreach (var token in parts[1].Split(';'))
        {
            if (token.Length < 3) continue;
            document.Guides.Add(new GtGuide(
                token[0] is 'V' or 'v' ? GtGuideOrientation.Vertical : GtGuideOrientation.Horizontal,
                ParseDouble(token.Substring(2))));
        }

        document.GuidesLocked = parts[2] == "1";

        var origin = parts[3].Split(',');
        document.RulerOrigin = origin.Length >= 2
            ? new GtPoint(ParseDouble(origin[0]), ParseDouble(origin[1]))
            : GtPoint.Zero;
    }

    /// <summary>loads guides from the document.xml element and strips it from the tree so the rest of the parser never sees an element it does not know; returns false when absent</summary>
    public static bool ReadDocumentElement(XElement root, GtDocument document)
    {
        var el = root.Element(ElementName);
        if (el is null) return false;
        el.Remove();

        try
        {
            document.GuidesLocked = string.Equals(el.Attribute("Locked")?.Value, "true",
                                                  StringComparison.OrdinalIgnoreCase);

            var origin = (el.Attribute("Origin")?.Value ?? "0,0").Split(',');
            document.RulerOrigin = origin.Length >= 2
                ? new GtPoint(ParseDouble(origin[0]), ParseDouble(origin[1]))
                : GtPoint.Zero;

            document.Guides.Clear();
            foreach (var token in (el.Attribute("Guides")?.Value ?? "").Split(';'))
            {
                if (token.Length < 3) continue;
                var vertical = token[0] is 'V' or 'v';
                document.Guides.Add(new GtGuide(
                    vertical ? GtGuideOrientation.Vertical : GtGuideOrientation.Horizontal,
                    ParseDouble(token.Substring(2))));
            }

            Logger.Debug($"Guides loaded from document.xml: {document.Guides.Count}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to read <{ElementName}> from document.xml: {ex.Message}");
            return false;
        }
    }

    /// <summary>loads guides from the legacy <see cref="EntryName"/> part, kept for files this editor wrote before guides moved into document.xml; nothing writes this part any more because GT Title Designer discards it whenever it re-saves the package, never throws</summary>
    public static void Read(ZipArchive zip, GtDocument document)
    {
        var entry = zip.GetEntry(EntryName);
        if (entry is null) return;

        try
        {
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            var set = Parse(XDocument.Load(ms).Root);
            if (set is null) return;

            document.GuidesLocked = set.Locked;
            document.RulerOrigin  = set.RulerOrigin;
            document.Guides.Clear();
            document.Guides.AddRange(set.Guides);

            Logger.Debug($"Guides loaded: {document.Guides.Count}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to read {EntryName}: {ex.Message}");
        }
    }

    /// <summary>exports the document's guides to a shareable file, throws on I/O failure</summary>
    public static void WriteFile(string path, GtDocument document)
    {
        new XDocument(new XDeclaration("1.0", "utf-8", null), Build(document)).Save(path);
        Logger.Info($"Exported {document.Guides.Count} guides to {path}");
    }

    /// <summary>reads a shareable guides file, throws when the file is not a guide set</summary>
    public static GuideSet ReadFile(string path)
    {
        var set = Parse(XDocument.Load(path).Root)
            ?? throw new InvalidDataException("Not a guides file");
        Logger.Info($"Imported {set.Guides.Count} guides from {path}");
        return set;
    }

    private static XElement Build(GtDocument document)
    {
        var root = new XElement("Guides",
            new XAttribute("Locked",       document.GuidesLocked ? "true" : "false"),
            new XAttribute("OriginX",      Fmt(document.RulerOrigin.X)),
            new XAttribute("OriginY",      Fmt(document.RulerOrigin.Y)),
            new XAttribute("CanvasWidth",  Fmt(document.Width)),
            new XAttribute("CanvasHeight", Fmt(document.Height)));

        foreach (var guide in document.Guides)
            root.Add(new XElement("Guide",
                new XAttribute("Orientation", guide.Orientation.ToString()),
                new XAttribute("Position",    Fmt(guide.Position))));

        return root;
    }

    private static GuideSet? Parse(XElement? root)
    {
        if (root is null || root.Name.LocalName != "Guides") return null;

        var set = new GuideSet
        {
            Locked = string.Equals(root.Attribute("Locked")?.Value, "true",
                                   StringComparison.OrdinalIgnoreCase),
            RulerOrigin = new GtPoint(
                ParseDouble(root.Attribute("OriginX")?.Value),
                ParseDouble(root.Attribute("OriginY")?.Value)),
            SourceWidth  = ParseDouble(root.Attribute("CanvasWidth")?.Value),
            SourceHeight = ParseDouble(root.Attribute("CanvasHeight")?.Value),
        };

        foreach (var g in root.Elements("Guide"))
        {
            var orientation = string.Equals(g.Attribute("Orientation")?.Value, "Vertical",
                                            StringComparison.OrdinalIgnoreCase)
                ? GtGuideOrientation.Vertical
                : GtGuideOrientation.Horizontal;
            set.Guides.Add(new GtGuide(orientation, ParseDouble(g.Attribute("Position")?.Value)));
        }

        return set;
    }

    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
