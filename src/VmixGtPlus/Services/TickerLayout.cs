using System;
using System.Collections.Generic;
using System.IO;
using VmixGtPlus.Models;

namespace VmixGtPlus.Services;

/// <summary>where one chunk sits along the scroll axis, in ticker-local coordinates</summary>
public readonly struct TickerPlacement
{
    public TickerPlacement(int index, double position)
    {
        Index    = index;
        Position = position;
    }

    /// <summary>index into the chunk list the placement was computed from</summary>
    public int Index { get; }

    /// <summary>leading edge along the scroll axis: X for Left/Right, Y for Top/Bottom</summary>
    public double Position { get; }
}

/// <summary>GT's ticker text splitting and per-frame position walk as a pure function of the frame number, so the editor can draw any scroll point without a live engine</summary>
/// <remarks>original runs one Tick() per rendered frame off the composition clock and mutates clone positions in place, so Speed is pixels per frame not per second; Simulate replays from frame 0, cheap at the frame counts a preview reaches (seconds x Fps)</remarks>
public static class TickerLayout
{
    /// <summary>characters per clone on the horizontal axis (GT's MAX_TEXT_PER_OBJECT_X)</summary>
    public const int MaxTextPerObjectX = 100;

    /// <summary>frames per second the walk is replayed at, GT ticks once per rendered frame</summary>
    public const double Fps = 60.0;

    /// <summary>ceiling on replayed frames so a long scrub cannot stall a render</summary>
    private const int MaxSimulatedFrames = 200_000;

    /// <summary>splits one data value into the chunks that become clones; the separator GT appends to every value comes first, a space horizontally or a newline vertically, the only thing keeping consecutive items apart</summary>
    public static List<string> Split(string? text, bool vertical)
    {
        var value = text ?? "";
        if (vertical)
        {
            var chunks = SplitVertical(value + "\r\n");
            return chunks;
        }

        // horizontal ticker forces SingleLine and GT's FormatText turns every line break into a space, so a multi-line value scrolls as one line
        var flat = value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        return SplitHorizontal(flat + " ", MaxTextPerObjectX);
    }

    /// <summary>greedy fixed-width split broken at the last space inside the window; the space stays at the start of the remainder so chunks rejoin to the original, a window with no space is cut hard</summary>
    public static List<string> SplitHorizontal(string text, int max)
    {
        var chunks = new List<string>();
        var rest   = text ?? "";
        if (max < 1) max = 1;

        while (rest.Length > max)
        {
            int cut = rest.LastIndexOf(' ', max - 1);
            if (cut < 0)
            {
                chunks.Add(rest.Substring(0, max));
                rest = rest.Substring(max);
            }
            else
            {
                chunks.Add(rest.Substring(0, cut));
                rest = rest.Substring(cut);
            }
        }

        chunks.Add(rest);
        return chunks;
    }

    /// <summary>one chunk per line; an empty line becomes a single space so it still measures to a visible height and scrolls past like any other chunk</summary>
    public static List<string> SplitVertical(string text)
    {
        var chunks = new List<string>();
        using var reader = new StringReader(text ?? "");
        string? line;
        while ((line = reader.ReadLine()) is not null)
            chunks.Add(line.Length == 0 ? " " : line);
        if (chunks.Count == 0) chunks.Add(" ");
        return chunks;
    }

    /// <summary>layout for a ticker that is not being previewed; GT shows nothing until its clock runs so at rest the chunks lay out contiguously from the leading edge in reading order</summary>
    public static List<TickerPlacement> Rest(IReadOnlyList<double> lengths, double screenLength)
    {
        var result = new List<TickerPlacement>();
        double pos = 0;
        for (int i = 0; i < lengths.Count && pos < screenLength; i++)
        {
            result.Add(new TickerPlacement(i, pos));
            pos += lengths[i];
        }
        return result;
    }

    private sealed class Item
    {
        public int    Index;
        public double Length;
        public double Position;
        /// <summary>GT renders one queued clone per frame and refuses an unrendered one, so fresh content ramps in at one chunk per frame; recycled clones are already drawn</summary>
        public bool   Rendered;
    }

    /// <summary>replays frames ticks and returns where each on-screen chunk ended up</summary>
    /// <param name="lengths">chunk sizes along the scroll axis, in the chunk list's order</param>
    /// <param name="screenLength">the ticker's own size along that axis</param>
    /// <param name="speed">pixels per frame</param>
    public static List<TickerPlacement> Simulate(
        IReadOnlyList<double> lengths, double screenLength, double speed,
        GtTickerDirection direction, GtTickerType type, int frames)
    {
        var result = new List<TickerPlacement>();
        if (lengths.Count == 0 || screenLength <= 0) return result;

        // Right and Bottom travel towards growing coordinates and every geometry rule flips with them; GT tests the axis and the sign separately, this keeps the pair together
        bool reverse = direction is GtTickerDirection.Right or GtTickerDirection.Bottom;

        var queue  = new Queue<Item>();
        var active = new List<Item>();
        for (int i = 0; i < lengths.Count; i++)
            queue.Enqueue(new Item { Index = i, Length = lengths[i] });

        int count = Math.Clamp(frames, 0, MaxSimulatedFrames);
        for (int f = 0; f < count; f++)
        {
            // fill: pull from the queue until the on-screen run covers the ticker
            double len = ActiveLength(active, screenLength, reverse);
            while (len <= screenLength && queue.Count > 0 && queue.Peek().Rendered)
            {
                var item = queue.Dequeue();
                len += item.Length;

                double last = active.Count > 0 ? LastPosition(active[active.Count - 1], reverse) : 0.0;

                // a tail that drifted back inside the visible area means the run ran dry, so the new chunk snaps to the far edge instead of popping mid-screen, otherwise it butts onto the previous chunk
                item.Position = last >= 0 && last <= screenLength
                    ? (reverse ? -item.Length : screenLength)
                    : (reverse ? last - item.Length : last);

                active.Add(item);
            }

            foreach (var item in active)
                item.Position += reverse ? speed : -speed;

            for (int i = active.Count - 1; i >= 0; i--)
            {
                var item = active[i];
                bool done = reverse
                    ? item.Position > screenLength
                    : item.Position + item.Length < 0;
                if (!done) continue;

                active.RemoveAt(i);
                // Replace loops forever by recycling finished clones onto the tail of the queue, Add destroys them so an unfed ticker empties out
                if (type == GtTickerType.Replace) queue.Enqueue(item);
            }

            if (queue.Count > 0) queue.Peek().Rendered = true;
        }

        foreach (var item in active)
            result.Add(new TickerPlacement(item.Index, item.Position));
        return result;
    }

    /// <summary>trailing edge of a chunk, in the direction of travel</summary>
    private static double LastPosition(Item item, bool reverse) =>
        reverse ? item.Position : item.Position + item.Length;

    /// <summary>how much of the ticker the on-screen run covers, only positive contributions count; GT leaves its accumulator uninitialised which double-counts a chunk fully past the far edge on Right/Bottom, corrected here so the next chunk is admitted a frame or two earlier than vMix</summary>
    private static double ActiveLength(List<Item> active, double screenLength, bool reverse)
    {
        double sum = 0;
        foreach (var item in active)
        {
            double contribution;
            if (!reverse)
                contribution = item.Position >= 0 ? item.Length : item.Position + item.Length;
            else if (item.Position <= 0)
                contribution = item.Length;
            else if (item.Position < screenLength)
                contribution = Math.Min(screenLength - item.Position, item.Length);
            else
                contribution = 0;

            if (contribution > 0) sum += contribution;
        }
        return sum;
    }
}
