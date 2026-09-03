using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace GtPlus.Services;

/// <summary>data source shapes vMix can bind a title to</summary>
public enum DataSourceFormat { Json, Xml, Csv }

/// <summary>one name/value pair out of a parsed record</summary>
public class DataField
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary>non-null when the value cannot be used by vMix; nested objects and arrays are not supported by GT data sources, so the field is reported as an error rather than flattened</summary>
    public string? Error { get; set; }
}

/// <summary>one row of the data source: a JSON object, an XML row node, a CSV line</summary>
public class DataRecord
{
    public string Label { get; set; } = "";
    public List<DataField> Fields { get; } = new();

    public DataField? Find(string name, bool caseSensitive)
    {
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var f in Fields)
            if (string.Equals(f.Name, name, cmp))
                return f;
        return null;
    }
}

public class DataParseResult
{
    public DataSourceFormat Format { get; set; }
    public List<DataRecord> Records { get; } = new();

    /// <summary>fatal problems, the payload could not be read at all</summary>
    public List<string> Errors { get; } = new();

    public bool Ok => Errors.Count == 0;

    /// <summary>count of fields across all records that vMix cannot bind</summary>
    public int FieldErrorCount => Records.Sum(r => r.Fields.Count(f => f.Error is not null));
}

/// <summary>turns raw JSON / XML / CSV text into flat name-value records, the only shape vMix GT data sources understand; nesting is rejected rather than flattened</summary>
public static class DataSourceService
{
    public const string NestedObjectError = "Nested object - vMix cannot bind nested data";
    public const string NestedArrayError  = "Nested array - vMix cannot bind nested data";

    public static DataParseResult Parse(string? text, DataSourceFormat format, bool firstRowIsHeader)
    {
        var result = new DataParseResult { Format = format };

        if (string.IsNullOrWhiteSpace(text))
        {
            result.Errors.Add("No data. Fetch a URL, load a file, or paste into the data box.");
            return result;
        }

        try
        {
            switch (format)
            {
                case DataSourceFormat.Json: ParseJson(text!, result); break;
                case DataSourceFormat.Xml:  ParseXml(text!, result); break;
                default:                    ParseCsv(text!, result, firstRowIsHeader); break;
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
        }

        if (result.Ok && result.Records.Count == 0)
            result.Errors.Add("Parsed fine but contained no rows.");

        return result;
    }

    private static void ParseJson(string text, DataParseResult result)
    {
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling     = JsonCommentHandling.Skip,
        };

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, options);
        }
        catch (JsonException ex)
        {
            result.Errors.Add("Invalid JSON: " + ex.Message);
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                result.Records.Add(JsonRecord(root, "Row 1"));
                return;
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                result.Errors.Add($"Top level JSON is a {Describe(root.ValueKind)}. " +
                                  "vMix expects an object, or an array of objects.");
                return;
            }

            int index = 0;
            foreach (var item in root.EnumerateArray())
            {
                index++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    result.Errors.Add($"Row {index} is a {Describe(item.ValueKind)}; " +
                                      "every item in the array must be a flat object.");
                    return;
                }
                result.Records.Add(JsonRecord(item, $"Row {index}"));
            }
        }
    }

    private static DataRecord JsonRecord(JsonElement obj, string label)
    {
        var rec = new DataRecord { Label = label };

        foreach (var prop in obj.EnumerateObject())
        {
            var field = new DataField { Name = prop.Name };

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    field.Error = NestedObjectError;
                    field.Value = "{ ... }";
                    break;
                case JsonValueKind.Array:
                    field.Error = NestedArrayError;
                    field.Value = "[ ... ]";
                    break;
                case JsonValueKind.Null:
                    field.Value = "";
                    break;
                case JsonValueKind.String:
                    field.Value = prop.Value.GetString() ?? "";
                    break;
                default:
                    field.Value = prop.Value.GetRawText();
                    break;
            }

            rec.Fields.Add(field);
        }

        rec.Label = LabelFor(label, rec);
        return rec;
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array  => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        _                    => "value",
    };

    private static void ParseXml(string text, DataParseResult result)
    {
        XDocument xdoc;
        try
        {
            xdoc = XDocument.Parse(text);
        }
        catch (Exception ex)
        {
            result.Errors.Add("Invalid XML: " + ex.Message);
            return;
        }

        var root = xdoc.Root;
        if (root is null)
        {
            result.Errors.Add("XML has no root element.");
            return;
        }

        var children = root.Elements().ToList();

        // a repeating node like <Rows><Row .../><Row .../></Rows> is a multi row source
        // anything else reads as one record made of the root's own children and attributes
        var repeating = children.Count > 0
                        && children.Any(c => c.HasElements || c.HasAttributes)
                        && children.Select(c => c.Name.LocalName).Distinct().Count() == 1;

        if (repeating)
        {
            for (int i = 0; i < children.Count; i++)
                result.Records.Add(XmlRecord(children[i], $"Row {i + 1}"));
            return;
        }

        result.Records.Add(XmlRecord(root, "Row 1"));
    }

    private static DataRecord XmlRecord(XElement node, string label)
    {
        var rec = new DataRecord { Label = label };

        foreach (var attr in node.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;
            rec.Fields.Add(new DataField { Name = attr.Name.LocalName, Value = attr.Value });
        }

        foreach (var child in node.Elements())
        {
            var field = new DataField { Name = child.Name.LocalName };

            if (child.HasElements)
            {
                field.Error = NestedObjectError;
                field.Value = "<" + child.Name.LocalName + "> ... </" + child.Name.LocalName + ">";
            }
            else
            {
                field.Value = child.Value;
            }

            rec.Fields.Add(field);
        }

        rec.Label = LabelFor(label, rec);
        return rec;
    }

    private static void ParseCsv(string text, DataParseResult result, bool firstRowIsHeader)
    {
        var delimiter = DetectDelimiter(text);
        var rows      = ParseCsvRows(text, delimiter);

        // trailing blank lines are normal in exported sheets
        while (rows.Count > 0 && rows[rows.Count - 1].All(string.IsNullOrWhiteSpace))
            rows.RemoveAt(rows.Count - 1);

        if (rows.Count == 0)
        {
            result.Errors.Add("No rows found in the CSV data.");
            return;
        }

        var columnCount = rows.Max(r => r.Count);
        var headers     = new List<string>();
        int firstDataRow;

        if (firstRowIsHeader)
        {
            for (int c = 0; c < columnCount; c++)
            {
                var name = c < rows[0].Count ? rows[0][c].Trim() : "";
                headers.Add(string.IsNullOrEmpty(name) ? "Column" + (c + 1) : name);
            }
            firstDataRow = 1;

            if (rows.Count == 1)
            {
                result.Errors.Add("The only row is the header row - no data to bind. " +
                                  "Untick 'First row is column names' if that row is data.");
                return;
            }
        }
        else
        {
            for (int c = 0; c < columnCount; c++) headers.Add("Column" + (c + 1));
            firstDataRow = 0;
        }

        var duplicates = headers.GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
                                .Where(g => g.Count() > 1)
                                .Select(g => g.Key)
                                .ToList();

        for (int r = firstDataRow; r < rows.Count; r++)
        {
            var rec = new DataRecord { Label = $"Row {r - firstDataRow + 1}" };

            for (int c = 0; c < headers.Count; c++)
            {
                var field = new DataField
                {
                    Name  = headers[c],
                    Value = c < rows[r].Count ? rows[r][c] : "",
                };
                if (duplicates.Contains(headers[c], StringComparer.OrdinalIgnoreCase))
                    field.Error = "Duplicate column name - vMix cannot tell these columns apart";
                rec.Fields.Add(field);
            }

            rec.Label = LabelFor(rec.Label, rec);
            result.Records.Add(rec);
        }
    }

    private static char DetectDelimiter(string text)
    {
        var firstLine  = text.Split('\n')[0];
        var candidates = new[] { ',', ';', '\t', '|' };
        char best      = ',';
        int  bestCount = 0;

        foreach (var candidate in candidates)
        {
            int count = 0;
            bool inQuotes = false;
            foreach (var ch in firstLine)
            {
                if (ch == '"') inQuotes = !inQuotes;
                else if (ch == candidate && !inQuotes) count++;
            }
            if (count > bestCount) { bestCount = count; best = candidate; }
        }

        return best;
    }

    /// <summary>RFC 4180 style reader, quoted fields may contain the delimiter and newlines</summary>
    private static List<List<string>> ParseCsvRows(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row  = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(ch);
                continue;
            }

            if (ch == '"') inQuotes = true;
            else if (ch == delimiter) { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\r') { /* the \n that follows ends the row */ }
            else if (ch == '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = new List<string>();
            }
            else cell.Append(ch);
        }

        row.Add(cell.ToString());
        rows.Add(row);
        return rows;
    }

    private static string LabelFor(string fallback, DataRecord rec)
    {
        var first = rec.Fields.FirstOrDefault(f => f.Error is null && !string.IsNullOrWhiteSpace(f.Value));
        if (first is null) return fallback;

        var value = first.Value.Trim();
        if (value.Length > 24) value = value.Substring(0, 24) + "...";
        return fallback + "  -  " + value;
    }

    /// <summary>true when the bytes look like a zip container, which is what .xlsx is</summary>
    public static bool LooksLikeXlsx(byte[] bytes) =>
        bytes.Length > 3 && bytes[0] == 'P' && bytes[1] == 'K' &&
        (bytes[2] == 3 || bytes[2] == 5 || bytes[2] == 7);

    /// <summary>reads the first worksheet of an .xlsx workbook into CSV text so workbooks reuse the CSV path; formulas come through as their cached value</summary>
    public static bool TryConvertXlsxToCsv(byte[] bytes, out string csv, out string error)
    {
        csv   = "";
        error = "";

        try
        {
            using var ms  = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            var shared = ReadSharedStrings(zip);

            var sheetEntry = zip.Entries
                .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                            && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (sheetEntry is null)
            {
                error = "The workbook has no worksheets.";
                return false;
            }

            XDocument sheet;
            using (var stream = sheetEntry.Open())
                sheet = XDocument.Load(stream);

            var ns   = sheet.Root?.Name.Namespace ?? XNamespace.None;
            var rows = sheet.Root?.Element(ns + "sheetData")?.Elements(ns + "row").ToList()
                       ?? new List<XElement>();

            var table = new List<List<string>>();
            int width = 0;

            foreach (var row in rows)
            {
                var cells = new List<string>();
                foreach (var c in row.Elements(ns + "c"))
                {
                    int col = ColumnIndex(c.Attribute("r")?.Value);
                    if (col < 0) col = cells.Count;
                    while (cells.Count < col) cells.Add("");
                    cells.Add(CellValue(c, ns, shared));
                }
                width = Math.Max(width, cells.Count);
                table.Add(cells);
            }

            var sb = new StringBuilder();
            foreach (var row in table)
            {
                for (int c = 0; c < width; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(QuoteCsv(c < row.Count ? row[c] : ""));
                }
                sb.Append('\n');
            }

            csv = sb.ToString();
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not read the workbook: " + ex.Message;
            return false;
        }
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list  = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return list;

        XDocument doc;
        using (var stream = entry.Open())
            doc = XDocument.Load(stream);

        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        foreach (var si in doc.Root?.Elements(ns + "si") ?? Enumerable.Empty<XElement>())
            list.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));

        return list;
    }

    private static string CellValue(XElement cell, XNamespace ns, List<string> shared)
    {
        var type = cell.Attribute("t")?.Value;

        if (type == "inlineStr")
            return string.Concat(cell.Descendants(ns + "t").Select(t => t.Value));

        var v = cell.Element(ns + "v")?.Value ?? "";

        if (type == "s" && int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count)
            return shared[idx];

        return v;
    }

    /// <summary>zero based column from a cell reference such as "AB12", -1 when unparsable</summary>
    private static int ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return -1;

        int col = 0, letters = 0;
        foreach (var ch in cellRef!)
        {
            if (ch >= 'A' && ch <= 'Z') { col = col * 26 + (ch - 'A' + 1); letters++; }
            else if (ch >= 'a' && ch <= 'z') { col = col * 26 + (ch - 'a' + 1); letters++; }
            else break;
        }

        return letters == 0 ? -1 : col - 1;
    }

    private static string QuoteCsv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
