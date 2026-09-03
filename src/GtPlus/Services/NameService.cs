using System;
using System.Collections.Generic;
using GtPlus.Models;

namespace GtPlus.Services;

/// <summary>naming rules shared by element creation, copy/paste and rename; names matter beyond the layers panel since animations target objects by name, so every generated name must be unique across layers and elements</summary>
public static class NameService
{
    public static HashSet<string> CollectElementNames(GtDocument doc)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in doc.Layers)
            foreach (var el in l.Elements)
                names.Add(el.Name);
        return names;
    }

    /// <summary>every name an animation could resolve to, layer names included</summary>
    public static HashSet<string> CollectAllNames(GtDocument doc)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in doc.Layers)
        {
            if (!string.IsNullOrEmpty(l.Name)) names.Add(l.Name);
            foreach (var el in l.Elements)
                if (!string.IsNullOrEmpty(el.Name)) names.Add(el.Name);
        }
        return names;
    }

    /// <summary>first free <c>{baseName}{n}</c>, starting at <paramref name="start"/></summary>
    public static string UniqueName(HashSet<string> taken, string baseName, int start)
    {
        int i = Math.Max(1, start);
        while (taken.Contains($"{baseName}{i}")) i++;
        return $"{baseName}{i}";
    }

    /// <summary>name for a copy of <paramref name="name"/>: trailing number incremented until free ("Title3" -> "Title4"), a name without one gets "1" appended ("Title" -> "Title1")</summary>
    public static string NextCopyName(HashSet<string> taken, string name)
    {
        SplitTrailingNumber(name, out var baseName, out var number);
        if (baseName.Length == 0) baseName = "Element";
        return UniqueName(taken, baseName, number is null ? 1 : number.Value + 1);
    }

    /// <summary>splits "Title03" into base "Title" and number 3, number is null when absent</summary>
    public static void SplitTrailingNumber(string name, out string baseName, out int? number)
    {
        int digits = name.Length;
        while (digits > 0 && char.IsDigit(name[digits - 1])) digits--;

        baseName = name.Substring(0, digits);
        number   = null;
        if (digits < name.Length && int.TryParse(name.Substring(digits), out var n))
            number = n;
    }
}
