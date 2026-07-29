using System;
using Avalonia;
using Avalonia.Media;

namespace LiquidLauncher.Controls;

/// <summary>
/// Builds a true superellipse ("squircle") path — the same family of curve Apple uses
/// for continuous corners on macOS/iOS, rather than a simple arc-based rounded rect.
///
/// The curve is defined per-corner by:
///   |x/a|^n + |y/b|^n = 1
///
/// n ≈ 4-5 gives the iOS/macOS "squircle" look. n=2 would be a plain ellipse/arc corner
/// (what WinUI/Avalonia's built-in CornerRadius draws). Higher n = boxier, sharper shoulders.
/// </summary>
public static class SuperellipseGeometry
{
    public static StreamGeometry Create(double width, double height, double cornerRadius, double n = 4.2, int segmentsPerCorner = 24)
    {
        var geometry = new StreamGeometry();
        cornerRadius = Math.Min(cornerRadius, Math.Min(width, height) / 2.0);

        using var ctx = geometry.Open();

        // Precompute one quarter-corner's worth of superellipse offsets (0..radius, 0..radius)
        var pts = new Point[segmentsPerCorner + 1];
        for (int i = 0; i <= segmentsPerCorner; i++)
        {
            double t = (double)i / segmentsPerCorner * (Math.PI / 2.0);
            double cx = Math.Pow(Math.Abs(Math.Cos(t)), 2.0 / n) * Math.Sign(Math.Cos(t));
            double cy = Math.Pow(Math.Abs(Math.Sin(t)), 2.0 / n) * Math.Sign(Math.Sin(t));
            pts[i] = new Point(cx * cornerRadius, cy * cornerRadius);
        }

        // Start at top edge, just right of top-left corner
        ctx.BeginFigure(new Point(cornerRadius, 0), true);

        // Top edge -> top-right corner
        ctx.LineTo(new Point(width - cornerRadius, 0));
        for (int i = segmentsPerCorner; i >= 0; i--)
        {
            var p = pts[i];
            ctx.LineTo(new Point(width - cornerRadius + p.X, cornerRadius - p.Y));
        }

        // Right edge -> bottom-right corner
        ctx.LineTo(new Point(width, height - cornerRadius));
        for (int i = 0; i <= segmentsPerCorner; i++)
        {
            var p = pts[i];
            ctx.LineTo(new Point(width - cornerRadius + p.X, height - cornerRadius + p.Y));
        }

        // Bottom edge -> bottom-left corner
        ctx.LineTo(new Point(cornerRadius, height));
        for (int i = segmentsPerCorner; i >= 0; i--)
        {
            var p = pts[i];
            ctx.LineTo(new Point(cornerRadius - p.X, height - cornerRadius + p.Y));
        }

        // Left edge -> top-left corner
        ctx.LineTo(new Point(0, cornerRadius));
        for (int i = 0; i <= segmentsPerCorner; i++)
        {
            var p = pts[i];
            ctx.LineTo(new Point(cornerRadius - p.X, cornerRadius - p.Y));
        }

        ctx.EndFigure(true);
        return geometry;
    }
}
