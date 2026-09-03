using System;
using Avalonia;
using Avalonia.Media;
using VmixGtPlus.Models;

namespace VmixGtPlus.Controls;

/// <summary>conversion helpers between <see cref="GtBrush"/> and Avalonia brushes for UI previews, plus the angle to StartPoint/EndPoint mapping used by the gradient editor; angle convention 0 = left to right, 90 = top to bottom (clockwise, Y down)</summary>
public static class BrushPreview
{
    /// <summary>Avalonia brush for showing a GtBrush in a swatch / preview strip</summary>
    public static IBrush ToPreviewBrush(GtBrush? brush)
    {
        if (brush is null) return new SolidColorBrush(Colors.Transparent);

        switch (brush.Type)
        {
            case GtBrushType.LinearGradient:
            {
                var lgb = new LinearGradientBrush
                {
                    StartPoint   = new RelativePoint(brush.StartPoint.X, brush.StartPoint.Y, RelativeUnit.Relative),
                    EndPoint     = new RelativePoint(brush.EndPoint.X,   brush.EndPoint.Y,   RelativeUnit.Relative),
                    SpreadMethod = ToSpread(brush.WrapMode),
                };
                foreach (var s in brush.Stops)
                    lgb.GradientStops.Add(new GradientStop(s.Color, s.Position));
                return lgb;
            }
            case GtBrushType.RadialGradient:
            {
                var center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                var rgb = new RadialGradientBrush
                {
                    Center         = center,
                    GradientOrigin = center,
                    RadiusX        = new RelativeScalar(0.5, RelativeUnit.Relative),
                    RadiusY        = new RelativeScalar(0.5, RelativeUnit.Relative),
                    SpreadMethod   = ToSpread(brush.WrapMode),
                };
                foreach (var s in brush.Stops)
                    rgb.GradientStops.Add(new GradientStop(s.Color, s.Position));
                return rgb;
            }
            default:
                return new SolidColorBrush(brush.Color);
        }
    }

    /// <summary>left-to-right gradient of the stops only (ignores angle), used for the stop strip</summary>
    public static IBrush ToStopStripBrush(GtBrush brush)
    {
        var lgb = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        foreach (var s in brush.Stops)
            lgb.GradientStops.Add(new GradientStop(s.Color, s.Position));
        return lgb;
    }

    public static GradientSpreadMethod ToSpread(GtRadialWrap wrap) => wrap switch
    {
        GtRadialWrap.Clamp => GradientSpreadMethod.Pad,
        GtRadialWrap.Wrap  => GradientSpreadMethod.Repeat,
        _                  => GradientSpreadMethod.Reflect
    };

    /// <summary>angle in degrees (0-360) of the vector StartPoint to EndPoint</summary>
    public static double ToAngle(GtPoint start, GtPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return 0;
        var deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (deg < 0) deg += 360;
        return deg;
    }

    /// <summary>StartPoint/EndPoint pair centred on 0.5,0.5 for the given angle in degrees</summary>
    public static (GtPoint start, GtPoint end) FromAngle(double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        var dx  = Math.Cos(rad) / 2.0;
        var dy  = Math.Sin(rad) / 2.0;
        return (new GtPoint(Round(0.5 - dx), Round(0.5 - dy)),
                new GtPoint(Round(0.5 + dx), Round(0.5 + dy)));
    }

    private static double Round(double v) => Math.Round(v, 7);
}
