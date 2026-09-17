using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace GtPlus.Models;

/// <summary>one entry of GT's Shadow gallery; the gallery is the only way GT lets a user author a shadow, so these twelve pairs are every (Offset, BlurAmount) a GT-authored file can hold. The values are absolute composition pixels and do not scale with the element or the composition, a 5px shadow is 5px on a 1920x1080 title</summary>
public sealed record GtShadowPreset(string Name, double OffsetX, double OffsetY, double BlurAmount)
{
    public bool Matches(double offsetX, double offsetY, double blurAmount) =>
        OffsetX == offsetX && OffsetY == offsetY && BlurAmount == blurAmount;
}

/// <summary>reading, writing and rendering of the one effect this editor draws. GT builds a shadow by taking the alpha channel of the object's rendered surface, blurring it with a Gaussian, colourising it and compositing the object back over the top, so a multicoloured element casts one flat-coloured shadow behind itself</summary>
public static class GtShadow
{
    /// <summary>GT's Shadow gallery in ribbon order; index 0 is "None", which deletes the effect rather than disabling it</summary>
    public static readonly IReadOnlyList<GtShadowPreset> Presets = new GtShadowPreset[]
    {
        new("None",                 0,  0, 0),
        new("Glow Small",           0,  0, 3),
        new("Glow Medium",          0,  0, 5),
        new("Glow Large",           0,  0, 9),
        new("Bottom Right Sharp",   5,  5, 0),
        new("Bottom Right Smooth",  5,  5, 3),
        new("Bottom Left Sharp",   -5,  5, 0),
        new("Bottom Left Smooth",  -5,  5, 3),
        new("Bottom Sharp",         0,  5, 0),
        new("Bottom Smooth",        0,  5, 3),
        new("Top Sharp",            0, -5, 0),
        new("Top Smooth",           0, -5, 3),
    };

    /// <summary>gallery index for a shadow's geometry, or -1 when it matches no preset; GT compares on (Offset, BlurAmount) alone, colour never takes part</summary>
    public static int PresetIndex(double offsetX, double offsetY, double blurAmount)
    {
        for (int i = 0; i < Presets.Count; i++)
            if (Presets[i].Matches(offsetX, offsetY, blurAmount)) return i;
        return -1;
    }

    /// <summary>true when the list holds a shadow at all; the cheap test for a render path that only needs to know whether to take the shadow-aware route, almost every list is empty</summary>
    public static bool Any(IReadOnlyList<GtEffect> effects)
    {
        for (int i = 0; i < effects.Count; i++)
            if (effects[i].IsShadow) return true;
        return false;
    }

    /// <summary>the first shadow in <paramref name="effects"/>, or null when there is none; unlike GT's own lookup this does not delete the duplicates it walks past, reading a list never edits it here</summary>
    public static GtEffect? First(IReadOnlyList<GtEffect> effects)
    {
        foreach (var e in effects)
            if (e.IsShadow) return e;
        return null;
    }

    /// <summary>GT's SetShadow: every existing shadow is removed first, then one new effect goes in at index 0 if it would draw anything. A shadow with no offset and no blur is therefore not stored at all, which is what makes the gallery's "None" a deletion; effects of any other type or stage keep their order</summary>
    public static void Apply(IList<GtEffect> effects, double offsetX, double offsetY,
                             double blurAmount, Color color)
    {
        for (int i = effects.Count - 1; i >= 0; i--)
            if (effects[i].IsShadow) effects.RemoveAt(i);

        if (offsetX == 0 && offsetY == 0 && blurAmount == 0) return;

        effects.Insert(0, new GtEffect
        {
            Type       = GtEffectType.Shadow,
            Mode       = GtEffectMode.Shadow,
            BlurAmount = blurAmount,
            Color      = color,
            Offset     = new GtPoint(offsetX, offsetY),
        });
    }

    public static void Apply(IList<GtEffect> effects, GtShadowPreset preset, Color color) =>
        Apply(effects, preset.OffsetX, preset.OffsetY, preset.BlurAmount, color);

    /// <summary>the shadow stage of an object's effect graph collapsed into the single equivalent shadow, or null when the object casts none</summary>
    /// <remarks>GT chains the stage, feeding one shadow's output into the next, and a shadow only ever reads the alpha it is given: chaining two is a Gaussian convolved with a Gaussian, which is one Gaussian of <c>sqrt(s1^2 + s2^2)</c> displaced by the sum of the offsets and scaled by the product of the alphas, taking its colour from the last link. Collapsing is exact, not an approximation, and the single-shadow case (the only one GT's UI can author) falls straight out of it. Effects sitting in the shadow stage that are not shadows are skipped, this editor implements no other type</remarks>
    public static GtResolvedShadow? Resolve(IReadOnlyList<GtEffect> effects)
    {
        if (effects.Count == 0) return null;

        double variance = 0, offsetX = 0, offsetY = 0, alpha = 1;
        Color color = GtEffect.DefaultColor;
        bool found = false;

        foreach (var e in effects)
        {
            if (!e.IsShadow) continue;
            found     = true;
            variance += e.BlurAmount * e.BlurAmount;
            offsetX  += e.Offset.X;
            offsetY  += e.Offset.Y;
            alpha    *= e.Color.A / 255.0;
            color     = e.Color;
        }

        if (!found || alpha <= 0) return null;

        return new GtResolvedShadow(
            offsetX, offsetY, Math.Sqrt(variance),
            Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255),
                           color.R, color.G, color.B));
    }

    // Avalonia's Skia backend turns a drop shadow's BlurRadius into the Gaussian sigma Skia actually
    // blurs with as `sigma = 0.288675f * radius + 0.5f`, clamping a radius of zero or less to sigma 0
    // (Avalonia.Skia.DrawingContextImpl.SkBlurRadiusToSigma, verified against 12.1.2). GT's BlurAmount
    // *is* the sigma - it goes straight into D2D1_SHADOW_PROP_BLUR_STANDARD_DEVIATION - so it has to be
    // fed back through that inverted. This is the only place either constant appears.
    private const double SigmaPerRadius = 0.288675;
    private const double SigmaAtZeroRadius = 0.5;

    /// <summary>the <c>BlurRadius</c> to hand Avalonia so Skia blurs with a standard deviation of <paramref name="sigma"/> pixels</summary>
    /// <remarks>the backend's bias means sigmas below half a pixel are not expressible and come back as a hard edge; that is under the resolution of the blur itself and no GT preset lands there (they are 0, 3, 5 and 9)</remarks>
    public static double BlurRadiusForSigma(double sigma) =>
        sigma > SigmaAtZeroRadius ? (sigma - SigmaAtZeroRadius) / SigmaPerRadius : 0;
}

/// <summary>a whole shadow stage reduced to one Gaussian, ready to draw; a record so the immutable Avalonia effect built from it can be cached by value</summary>
public sealed record GtResolvedShadow(double OffsetX, double OffsetY, double Sigma, Color Color)
{
    /// <summary>how far past the element's own box this shadow reaches, per edge; a Gaussian is taken to have died out at 3 sigma, the same rule D2D sizes its kernel by</summary>
    public (double Left, double Top, double Right, double Bottom) Padding()
    {
        double reach = Sigma * 3;
        return (Math.Max(0, reach - OffsetX), Math.Max(0, reach - OffsetY),
                Math.Max(0, reach + OffsetX), Math.Max(0, reach + OffsetY));
    }

    /// <summary>the widest of the four edges, for callers that only need one number</summary>
    public double MaxReach()
    {
        var (l, t, r, b) = Padding();
        return Math.Max(Math.Max(l, t), Math.Max(r, b));
    }

    /// <summary>Avalonia draws the shadow and then the source over it, which is exactly GT's <c>element OVER shadow</c> composite; object opacity is left at 1 because GT has no shadow-only opacity, the alpha lives in <see cref="Color"/></summary>
    public IEffect ToEffect() => new ImmutableDropShadowEffect(
        OffsetX, OffsetY, GtShadow.BlurRadiusForSigma(Sigma), Color, 1.0);
}
