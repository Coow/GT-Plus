using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GtPlus.Services;

/// <summary>turns a set of image files on disk into the frame list of a GTZIP image sequence: one resource whose sources are the frames in playback order, anchored on frame 0 (the only path document.xml ever references)</summary>
public static class ImageSequenceBuilder
{
    /// <summary>file extensions GT accepts as a bitmap, shared by the single-image and sequence paths</summary>
    public static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".dds" };

    public static bool IsImageFile(string path) =>
        Array.IndexOf(ImageExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>the image files directly inside a folder, in playback order; sub-folders are ignored since a render folder holds one sequence</summary>
    public static List<string> ImageFilesInFolder(string folder)
    {
        var files = Directory.EnumerateFiles(folder).Where(IsImageFile);
        return SortNatural(files);
    }

    /// <summary>orders frames the way a human reads them: Name2.png before Name10.png, which plain string ordering gets backwards whenever the render was not zero-padded</summary>
    public static List<string> SortNatural(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        list.Sort((a, b) =>
        {
            int cmp = NaturalCompare(Path.GetFileName(a), Path.GetFileName(b));
            return cmp != 0 ? cmp : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    /// <summary>case-insensitive compare that reads runs of digits as numbers rather than characters</summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;

                // leading zeros are padding, not value: 007 and 7 are the same frame number
                var da = a.AsSpan(si, i - si).TrimStart('0');
                var db = b.AsSpan(sj, j - sj).TrimStart('0');
                if (da.Length != db.Length) return da.Length - db.Length;

                int digits = da.SequenceCompareTo(db);
                if (digits != 0) return digits;
            }
            else
            {
                int cmp = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (cmp != 0) return cmp;
                i++;
                j++;
            }
        }

        return (a.Length - i) - (b.Length - j);
    }

    /// <summary>a built sequence, ready to be merged into the document's asset library</summary>
    /// <param name="Anchor">frame 0's logical path - what the image element's Source points at</param>
    /// <param name="Frames">every frame's logical path in playback order, the anchor at index 0</param>
    /// <param name="Blobs">frame logical path to file bytes, stored verbatim so each frame keeps its own encoding</param>
    public sealed record BuiltSequence(string Anchor, List<string> Frames, Dictionary<string, byte[]> Blobs);

    /// <summary>reads the frames off disk into logical paths under one freshly-generated folder, mirroring how GT lays a sequence out; blocking file IO, call it off the UI thread</summary>
    /// <param name="paths">local file paths already in playback order</param>
    /// <param name="nameHint">base name for the sequence's asset folder</param>
    public static BuiltSequence Build(IReadOnlyList<string> paths, string nameHint)
    {
        if (paths.Count == 0) throw new ArgumentException("a sequence needs at least one frame", nameof(paths));

        var folder = $"images/{nameHint}_{Guid.NewGuid():N}";
        var frames = new List<string>(paths.Count);
        var blobs  = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < paths.Count; i++)
        {
            var logical = $"{folder}/{Path.GetFileName(paths[i])}";

            // two source folders can contribute the same filename; the index keeps each frame its own logical path so neither blob overwrites the other
            if (blobs.ContainsKey(logical))
                logical = $"{folder}/{i:00000}_{Path.GetFileName(paths[i])}";

            blobs[logical] = File.ReadAllBytes(paths[i]);
            frames.Add(logical);
        }

        return new BuiltSequence(frames[0], frames, blobs);
    }
}
