using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia.Media;
using GtPlus.Models;

namespace GtPlus.Services;

/// <summary>saves web page elements inside a .gtzip the same way <see cref="GuidesPart"/> saves guides: GT Title Designer drops extra zip parts and XML elements it does not recognise, but it round-trips objects it does understand, names and all, so each <see cref="GtWebElement"/> is written as an ordinary rectangle that draws nothing, with the url and flags base64url-encoded into its name. The swap happens in place, so the page comes back in the layer and at the z-position it was saved at without any of that having to be encoded, and the rectangle carries the real geometry in its own attributes where GT can keep it honest</summary>
public static class WebPagePart
{
    /// <summary>name prefix of the rectangle a web page travels in; deliberately not a prefix of, nor prefixed by, <see cref="GuidesPart.CarrierPrefix"/> so neither reader claims the other's objects</summary>
    public const string CarrierPrefix = "GTPlusWeb_";

    /// <summary>one element swapped out for the duration of a save, and what to put back afterwards</summary>
    public readonly record struct Swap(GtLayer Layer, int Index, GtWebElement Web);

    public static bool IsCarrier(GtElement element) =>
        element is GtRectangleElement && element.Name is { } name &&
        name.StartsWith(CarrierPrefix, StringComparison.Ordinal);

    /// <summary>the rectangle a web page is stored as: the page's own geometry, a fully transparent fill and no stroke so it draws nothing wherever it is opened, and the page itself encoded into the name</summary>
    public static GtRectangleElement CreateCarrier(GtWebElement web)
    {
        var carrier = new GtRectangleElement
        {
            StrokeThickness = 0,
            Fill            = new GtBrush { Type = GtBrushType.Solid, Color = Color.FromArgb(0, 0, 0, 0) },
        };

        // geometry, opacity, anchor, crop and the rest ride along as ordinary rectangle attributes
        GtElement.CopyBase(web, carrier);
        carrier.Name = CarrierPrefix + Encode(web);
        // explicitly hidden from the vMix title editor; a flagless shape already behaves that way at playout, but saying so keeps the carrier out of the field list GT itself shows. The page's own flags travel in the payload instead, since this attribute is now spoken for
        carrier.DataFlags = GtDataFlags.Hidden;
        return carrier;
    }

    /// <summary>rebuilds the web page a carrier stands for, or null when the payload is not one this version understands</summary>
    public static GtWebElement? FromCarrier(GtElement carrier)
    {
        if (!IsCarrier(carrier)) return null;

        try
        {
            var web = Decode(carrier.Name.Substring(CarrierPrefix.Length));
            var name  = web.Name;
            var flags = web.DataFlags;
            GtElement.CopyBase(carrier, web);
            // name and data flags live in the payload; the carrier's own are the payload and the Hidden marker
            web.Name      = name;
            web.DataFlags = flags;
            return web;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to decode a web page carrier: {ex.Message}");
            return null;
        }
    }

    /// <summary>swaps every web page in the document for its carrier, in place. Call before writing and hand the result to <see cref="Restore"/> in a finally block, so the live document never keeps the stand-ins</summary>
    public static List<Swap> Replace(GtDocument document)
    {
        var swaps = new List<Swap>();

        foreach (var layer in document.Layers)
        {
            for (int i = 0; i < layer.Elements.Count; i++)
            {
                if (layer.Elements[i] is not GtWebElement web) continue;
                swaps.Add(new Swap(layer, i, web));
                layer.Elements[i] = CreateCarrier(web);
            }
        }

        return swaps;
    }

    /// <summary>puts the real web elements back where <see cref="Replace"/> took them from</summary>
    public static void Restore(List<Swap> swaps)
    {
        foreach (var swap in swaps)
            if (swap.Index < swap.Layer.Elements.Count)
                swap.Layer.Elements[swap.Index] = swap.Web;
    }

    /// <summary>turns every carrier in a freshly read document back into a web page, in place; returns how many were found</summary>
    public static int ExtractCarriers(GtDocument document)
    {
        int found = 0;

        foreach (var layer in document.Layers)
        {
            for (int i = layer.Elements.Count - 1; i >= 0; i--)
            {
                var element = layer.Elements[i];
                if (!IsCarrier(element)) continue;

                if (FromCarrier(element) is { } web)
                {
                    layer.Elements[i] = web;
                    found++;
                }
                else
                {
                    // an unreadable carrier is still not artwork, and leaving it would put a nameless empty rectangle in the layers panel
                    layer.Elements.RemoveAt(i);
                }
            }
        }

        if (found > 0) Logger.Debug($"Web pages loaded from carrier objects: {found}");
        return found;
    }

    /// <summary>names the elements stored as carriers answer to once loaded; the file has them under their payload name instead, so nothing written may reference these</summary>
    public static HashSet<string> CarriedNames(GtDocument document)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var layer in document.Layers)
            foreach (var element in layer.Elements)
            {
                if (element is GtWebElement web)
                {
                    if (!string.IsNullOrEmpty(web.Name)) names.Add(web.Name);
                }
                else if (IsCarrier(element))
                {
                    try
                    {
                        var name = Decode(element.Name.Substring(CarrierPrefix.Length)).Name;
                        if (!string.IsNullOrEmpty(name)) names.Add(name);
                    }
                    catch { /* an undecodable carrier names nothing */ }
                }
            }

        return names;
    }

    /// <summary>packs a page into <c>version|name|url|interactive|transparent|dataflags</c>, the two free-text fields percent-escaped so no value can carry the separator, base64url-encoded</summary>
    private static string Encode(GtWebElement web)
    {
        var payload = string.Join("|", new[]
        {
            "2",
            Uri.EscapeDataString(web.Name ?? ""),
            Uri.EscapeDataString(web.Url  ?? ""),
            web.Interactive           ? "1" : "0",
            web.TransparentBackground ? "1" : "0",
            ((int)web.DataFlags).ToString(CultureInfo.InvariantCulture),
        });

        // base64url keeps the name to [A-Za-z0-9_-], which no name sanitiser should object to
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
                      .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static GtWebElement Decode(string encoded)
    {
        var b64 = encoded.Replace('-', '+').Replace('_', '/');
        b64 += new string('=', (4 - b64.Length % 4) % 4);

        var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
        if (parts.Length < 5) throw new FormatException("Unexpected payload shape");

        return new GtWebElement
        {
            Name                  = Uri.UnescapeDataString(parts[1]),
            Url                   = Uri.UnescapeDataString(parts[2]),
            Interactive           = parts[3] == "1",
            TransparentBackground = parts[4] == "1",
            // version 1 payloads predate the flags field, and carried whatever the carrier's own attribute said
            DataFlags             = parts.Length > 5 && int.TryParse(parts[5], NumberStyles.Integer,
                                                                    CultureInfo.InvariantCulture, out var flags)
                                    ? (GtDataFlags)flags
                                    : GtDataFlags.None,
        };
    }
}
