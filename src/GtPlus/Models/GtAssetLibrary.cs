using System;
using System.Collections.Generic;

namespace GtPlus.Models;

/// <summary>the asset side of a GTZIP, every blob keyed by logical path plus the image sequences those blobs form; a <c>&lt;resource&gt;</c> in resources.xml may carry many <c>&lt;source&gt;</c> children, one per frame in playback order, with the resource's <c>filename</c> as the anchor (the path <c>document.xml</c> references) and the sources its frames; treating a resource as a single asset would discard every frame after the first, so sequences are tracked explicitly here</summary>
public class GtAssetLibrary
{
    /// <summary>logical path (forward-slash normalised) to raw bytes, for every frame</summary>
    public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>anchor logical path to ordered frame logical paths, including the anchor at index 0; only populated for resources with more than one source</summary>
    public Dictionary<string, List<string>> Sequences { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>every path that participates in a sequence, for O(1) membership tests</summary>
    private readonly HashSet<string> _sequenceFrames = new(StringComparer.OrdinalIgnoreCase);

    public int Count => Blobs.Count;

    /// <summary>records an ordered frame list under its anchor path</summary>
    public void AddSequence(string anchorPath, List<string> frames)
    {
        Sequences[anchorPath] = frames;
        foreach (var frame in frames) _sequenceFrames.Add(frame);
    }

    /// <summary>true when this path is a frame of some sequence (the anchor included)</summary>
    public bool IsSequenceFrame(string logicalPath) => _sequenceFrames.Contains(logicalPath);

    public byte[] this[string logicalPath]
    {
        get => Blobs[logicalPath];
        set => Blobs[logicalPath] = value;
    }

    public bool TryGetValue(string logicalPath, out byte[] bytes) => Blobs.TryGetValue(logicalPath, out bytes!);

    public bool ContainsKey(string logicalPath) => Blobs.ContainsKey(logicalPath);

    /// <summary>removes a blob and, if it was a sequence anchor, the whole sequence with it</summary>
    public bool Remove(string logicalPath)
    {
        if (Sequences.TryGetValue(logicalPath, out var frames))
        {
            foreach (var frame in frames)
            {
                Blobs.Remove(frame);
                _sequenceFrames.Remove(frame);
            }
            Sequences.Remove(logicalPath);
            return true;
        }
        return Blobs.Remove(logicalPath);
    }

    /// <summary>number of frames behind an anchor path, 1 for a plain (non-sequence) asset</summary>
    public int FrameCount(string? anchorPath) =>
        anchorPath is not null && Sequences.TryGetValue(anchorPath, out var frames) ? frames.Count : 1;

    /// <summary>resolves the frame logical path at a normalised position (0-1) into the sequence anchored at <paramref name="anchorPath"/>; returns the anchor unchanged when it is not a sequence, so callers can use it for every image</summary>
    public string? FrameAt(string? anchorPath, double position)
    {
        if (anchorPath is null) return null;
        if (!Sequences.TryGetValue(anchorPath, out var frames) || frames.Count == 0) return anchorPath;

        int index = (int)Math.Round(Math.Clamp(position, 0.0, 1.0) * (frames.Count - 1));
        return frames[Math.Clamp(index, 0, frames.Count - 1)];
    }
}
