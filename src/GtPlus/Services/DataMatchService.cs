using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using GtPlus.Models;

namespace GtPlus.Services;

/// <summary>how one title element lines up with the loaded data source</summary>
public enum DataMatchStatus
{
    /// <summary>field found, name matches exactly, value is usable</summary>
    Ok,
    /// <summary>field found only by ignoring case, vMix matches names exactly so this fails there</summary>
    CaseMismatch,
    /// <summary>no field of that name in the data</summary>
    Missing,
    /// <summary>field found but its value is unusable (nested object/array, duplicate column, bad colour)</summary>
    FieldError,
    /// <summary>element is hidden from the vMix title editor, so no data can reach it</summary>
    NotBindable,
    /// <summary>binding for this element kind is not verified yet, reported for information only</summary>
    Unverified,
}

/// <summary>one row of the verifier's element list</summary>
public class DataElementMatch
{
    public string ElementName { get; set; } = "";
    public string LayerName   { get; set; } = "";
    public string Kind        { get; set; } = "";

    /// <summary>data field vMix would read for this element, not always the plain element name</summary>
    public string FieldName   { get; set; } = "";

    public string Value       { get; set; } = "";
    public string Message     { get; set; } = "";
    public DataMatchStatus Status { get; set; }
}

/// <summary>pairs the title's named elements against the fields of one data record; vMix has no separate field list, elements are bindable by their Name except shapes, whose colour comes in under <c>&lt;Name&gt;.Fill.Color</c></summary>
public static class DataMatchService
{
    /// <summary>suffix vMix appends to a shape's name when binding its fill colour</summary>
    public const string FillColorSuffix = ".Fill.Color";

    public static List<DataElementMatch> Match(GtDocument? doc, DataRecord? record)
    {
        var matches = new List<DataElementMatch>();
        if (doc is null) return matches;

        foreach (var layer in doc.Layers)
        {
            foreach (var element in layer.Elements)
            {
                if (string.IsNullOrWhiteSpace(element.Name)) continue;

                var candidates = BindingFieldNames(element);

                var match = new DataElementMatch
                {
                    ElementName = element.Name,
                    LayerName   = layer.Name,
                    Kind        = KindOf(element),
                    FieldName   = candidates[0],
                };

                Evaluate(element, candidates, record, match);
                matches.Add(match);
            }
        }

        return matches;
    }

    /// <summary>field names vMix would accept for this element, preferred one first; shapes take their colour as <c>Name.Fill.Color</c> and the bare name is kept as a fallback so a data source using it is reported as a naming problem rather than a missing field</summary>
    public static List<string> BindingFieldNames(GtElement element)
    {
        if (element is GtRectangleElement or GtEllipseElement)
            return new List<string> { element.Name + FillColorSuffix, element.Name };

        return new List<string> { element.Name };
    }

    /// <summary>sort weight for "worst first" ordering in the verifier list</summary>
    public static int SeverityRank(DataMatchStatus status) => status switch
    {
        DataMatchStatus.FieldError   => 0,
        DataMatchStatus.Missing      => 1,
        DataMatchStatus.CaseMismatch => 2,
        DataMatchStatus.Unverified   => 3,
        DataMatchStatus.NotBindable  => 4,
        _                            => 5,   // Ok
    };

    private static void Evaluate(
        GtElement element, List<string> candidates, DataRecord? record, DataElementMatch match)
    {
        if (element.DataFlags.HasFlag(GtDataFlags.Hidden))
        {
            match.Status  = DataMatchStatus.NotBindable;
            match.Message = "Hidden from the vMix title editor (DataFlags=Hidden)";
            return;
        }

        if (record is null)
        {
            match.Status  = DataMatchStatus.Missing;
            match.Message = "No data loaded";
            return;
        }

        // exact name first, only then the case-insensitive pass which is a warning in itself
        DataField? field = null;
        int matched = -1;
        bool exact = true;

        for (int i = 0; i < candidates.Count && field is null; i++)
        {
            field = record.Find(candidates[i], caseSensitive: true);
            if (field is not null) matched = i;
        }

        if (field is null)
        {
            exact = false;
            for (int i = 0; i < candidates.Count && field is null; i++)
            {
                field = record.Find(candidates[i], caseSensitive: false);
                if (field is not null) matched = i;
            }
        }

        if (field is null)
        {
            match.Status  = DataMatchStatus.Missing;
            match.Message = $"No field named '{candidates[0]}' in the data";
            return;
        }

        match.FieldName = field.Name;
        match.Value     = field.Value;

        if (field.Error is not null)
        {
            match.Status  = DataMatchStatus.FieldError;
            match.Message = field.Error;
            return;
        }

        if (!exact)
        {
            match.Status  = DataMatchStatus.CaseMismatch;
            match.Message = $"Data field is '{field.Name}', element expects '{candidates[matched]}' - " +
                            "vMix matches names exactly";
            return;
        }

        // shape hit on the bare name, vMix will not route it to the fill colour
        if (matched > 0)
        {
            match.Status  = DataMatchStatus.FieldError;
            match.Message = $"vMix binds a {match.Kind.ToLowerInvariant()}'s colour as " +
                            $"'{element.Name}{FillColorSuffix}', not '{element.Name}'";
            return;
        }

        if (element is GtRectangleElement or GtEllipseElement)
        {
            if (IsColor(field.Value))
            {
                match.Status = DataMatchStatus.Ok;
                return;
            }

            match.Status  = DataMatchStatus.FieldError;
            match.Message = string.IsNullOrWhiteSpace(field.Value)
                ? "Empty value - a fill colour must be #AARRGGBB"
                : $"'{Trim(field.Value)}' is not a colour - vMix expects #AARRGGBB";
            return;
        }

        if (element is GtImageElement)
        {
            if (IsImagePath(field.Value))
            {
                match.Status = DataMatchStatus.Ok;
                return;
            }

            match.Status  = DataMatchStatus.FieldError;
            match.Message = string.IsNullOrWhiteSpace(field.Value)
                ? @"Empty value - an image needs an absolute path (C:\...) or a URL"
                : $"'{Trim(field.Value)}' is not an absolute path or URL";
            return;
        }

        if (element is GtTextBlock)
        {
            match.Status = DataMatchStatus.Ok;
            return;
        }

        match.Status  = DataMatchStatus.Unverified;
        match.Message = $"{match.Kind} binding is not verified yet";
    }

    /// <summary>true for values GT accepts as a colour, hex ARGB/RGB or a known colour name</summary>
    private static bool IsColor(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return false;

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = "#" + text.Substring(2);

        if (text[0] == '#')
        {
            var digits = text.Substring(1);
            if (digits.Length is not (3 or 4 or 6 or 8)) return false;
            foreach (var ch in digits)
                if (!Uri.IsHexDigit(ch)) return false;
            return true;
        }

        return Color.TryParse(text, out _);
    }

    /// <summary>true when the value is somewhere an image could live: an absolute local path, a UNC share, or a URL; whether the file is actually there is not our business, vMix resolves that at play out and the path may point at a machine we cannot see</summary>
    private static bool IsImagePath(string value)
    {
        var text = value.Trim().Trim('"');
        if (text.Length == 0) return false;

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps
                || uri.Scheme == Uri.UriSchemeFile || uri.Scheme == Uri.UriSchemeFtp))
            return true;

        if (text.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        // Windows drive paths and UNC shares, a leading slash is root relative not absolute
        try
        {
            return Path.IsPathFullyQualified(text);
        }
        catch
        {
            return false;
        }
    }

    private static string Trim(string value) =>
        value.Length > 24 ? value.Substring(0, 24) + "..." : value;

    /// <summary>data fields no element in the title claims, they simply go nowhere</summary>
    public static List<DataField> UnusedFields(GtDocument? doc, DataRecord? record)
    {
        if (record is null) return new List<DataField>();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc is not null)
            foreach (var layer in doc.Layers)
                foreach (var element in layer.Elements)
                {
                    if (string.IsNullOrWhiteSpace(element.Name)) continue;
                    foreach (var name in BindingFieldNames(element)) names.Add(name);
                }

        return record.Fields.Where(f => !names.Contains(f.Name)).ToList();
    }

    private static string KindOf(GtElement element) => element switch
    {
        GtTickerElement    => "Ticker",
        GtTextBlock        => "Text",
        GtImageElement     => "Image",
        GtRectangleElement => "Rectangle",
        GtEllipseElement   => "Ellipse",
        GtWebElement       => "Web",
        _                  => element.GetType().Name,
    };
}
