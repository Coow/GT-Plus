using System;
using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace GtPlus.Services;

/// <summary>vertical font metrics the way DirectWrite reports them, which is not the way Avalonia does; Avalonia takes ascent/descent/lineGap from the <c>hhea</c> table and places the line gap below the baseline, DirectWrite (and therefore GT) takes ascent/descent from the <c>OS/2</c> <c>usWinAscent</c>/<c>usWinDescent</c> pair (unless the font sets USE_TYPO_METRICS), adds <c>hhea.lineGap</c> on top and puts that gap above the baseline, so fonts whose win metrics differ from their hhea metrics get a different line box in the two engines which shifts vertically centred text; measured against DirectWrite at 100 DIP, Brixton hhea 750/250 gap 0 gives Avalonia 100.00/75.00 vs DWrite 116.20/91.50, Arial hhea 1854/434 gap 67 gives Avalonia 114.99/90.53 vs DWrite 114.99/93.80; tables are read from the glyph typeface Avalonia itself resolved, so a family name that only fuzzy-matches an installed font still yields the metrics of the face actually being drawn</summary>
public static class FontMetricsService
{
    /// <summary>metrics normalised to a 1.0 em, so they scale by font size</summary>
    public readonly struct VerticalMetrics
    {
        public VerticalMetrics(double ascent, double descent, double lineGap)
        {
            Ascent = ascent; Descent = descent; LineGap = lineGap;
        }

        public double Ascent  { get; }
        public double Descent { get; }
        public double LineGap { get; }

        public double Height   => Ascent + Descent + LineGap;
        public double Baseline => Ascent + LineGap;   // DWrite puts the gap above the baseline

        public VerticalMetrics Scale(double fontSize)
            => new VerticalMetrics(Ascent * fontSize, Descent * fontSize, LineGap * fontSize);
    }

    // Avalonia caches glyph typefaces, so the resolved instance is a stable cache key
    private static readonly ConcurrentDictionary<GlyphTypeface, VerticalMetrics?> _cache
        = new ConcurrentDictionary<GlyphTypeface, VerticalMetrics?>();

    private const ushort UseTypoMetrics = 0x0080;   // OS/2 fsSelection bit 7

    /// <summary>line height and baseline for one line of <paramref name="fontSize"/> text in DIPs, or null when the font could not be resolved or its tables could not be read (the caller then falls back to Avalonia's own line metrics)</summary>
    public static VerticalMetrics? ForTypeface(Typeface typeface, double fontSize)
    {
        if (!FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface))
            return null;

        var em = _cache.GetOrAdd(glyphTypeface, Read);
        return em?.Scale(fontSize);
    }

    private static VerticalMetrics? Read(GlyphTypeface glyphTypeface)
    {
        try
        {
            double upem = glyphTypeface.Metrics.DesignEmHeight;
            if (upem <= 0) return null;

            var os2  = Table(glyphTypeface, "OS/2");
            var hhea = Table(glyphTypeface, "hhea");

            double ascent, descent, lineGap;

            if (os2 != null && os2.Length >= 78)
            {
                ushort fsSelection   = U16(os2, 62);
                short  typoAscender  = S16(os2, 68);
                short  typoDescender = S16(os2, 70);
                short  typoLineGap   = S16(os2, 72);
                ushort winAscent     = U16(os2, 74);
                ushort winDescent    = U16(os2, 76);

                if ((fsSelection & UseTypoMetrics) != 0 && typoAscender - typoDescender > 0)
                {
                    ascent  = typoAscender;
                    descent = -typoDescender;
                    lineGap = typoLineGap;
                }
                else if (winAscent + winDescent > 0)
                {
                    ascent  = winAscent;
                    descent = winDescent;
                    lineGap = hhea != null && hhea.Length >= 10 ? S16(hhea, 8) : 0;
                }
                else if (!FromHhea(hhea, out ascent, out descent, out lineGap))
                {
                    return null;
                }
            }
            else if (!FromHhea(hhea, out ascent, out descent, out lineGap))
            {
                return null;
            }

            if (ascent + descent <= 0) return null;
            return new VerticalMetrics(ascent / upem, descent / upem, lineGap / upem);
        }
        catch (Exception ex)
        {
            Logger.Warn($"FontMetricsService: could not read metrics for '{glyphTypeface.FamilyName}': {ex.Message}");
            return null;
        }
    }

    private static bool FromHhea(byte[]? hhea, out double ascent, out double descent, out double lineGap)
    {
        ascent = descent = lineGap = 0;
        if (hhea is null || hhea.Length < 10) return false;
        ascent  = S16(hhea, 4);
        descent = -S16(hhea, 6);
        lineGap = S16(hhea, 8);
        return true;
    }

    /// <summary>raw bytes of one OpenType table, or null when the backend will not hand them over; Avalonia 12 dropped GlyphTypeface.TryGetTable so the tables now come off the platform typeface, which the Skia backend exposes through IFontMemory</summary>
    private static byte[]? Table(GlyphTypeface glyphTypeface, string tag)
        => glyphTypeface.PlatformTypeface is IFontMemory memory
           && memory.TryGetTable(OpenTypeTag.Parse(tag), out var data)
            ? data.ToArray()
            : null;

    private static ushort U16(byte[] b, int offset) => (ushort)((b[offset] << 8) | b[offset + 1]);
    private static short  S16(byte[] b, int offset) => (short)U16(b, offset);
}
