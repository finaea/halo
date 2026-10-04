using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Halo.Widgets.Render;

/// <summary>A graph's plot rectangle, logical units. Doubles, because Rainformer's drawing does its
/// arithmetic in double and its pixels must not move by a rounding step.</summary>
public readonly record struct GraphArea(double X, double Y, double W, double H)
{
    public double Bottom => Y + H;
}

/// <summary>
/// How a <see cref="GraphEl"/> looks — never what it samples. The element keeps the rings, the time
/// buckets, the frame-ring wakes and the reset key; a visual only receives the finished points
/// (tech plan §3.4: graphs stay a top-level <c>GraphEl</c>, so <c>Panel.HasFrameGraph</c> and
/// <c>ApplyGraphSettings</c> keep finding them).
///
/// <para>Points come in the element's own drawing order — one per column (newest first) or one per
/// frame (oldest first) — and a point whose Y is NaN is a gap: no sample there, break the line.</para>
/// </summary>
public abstract class GraphVisual
{
    /// <summary>Today's look, unchanged: 1 px lines or 1 px filled columns.</summary>
    public static readonly GraphVisual Rainformer = new RainformerGraphVisual();

    /// <summary>Drawn once per repaint, under every series (after the element's own BgColor).</summary>
    public virtual void DrawBackdrop(RenderContext rc, Theme theme, GraphArea area) { }

    public abstract void DrawSeries(RenderContext rc, Theme theme, GraphSeries series,
        ReadOnlySpan<Vector2> points, GraphArea area, GraphStyle style, bool perFrame);
}

/// <summary>The drawing <see cref="GraphEl"/> did inline before visuals existed, call for call — the
/// golden test is what holds it to that.</summary>
internal sealed class RainformerGraphVisual : GraphVisual
{
    public override void DrawSeries(RenderContext rc, Theme theme, GraphSeries series,
        ReadOnlySpan<Vector2> points, GraphArea area, GraphStyle style, bool perFrame)
    {
        var brush = rc.Brush(theme.Color(series.Color));
        if (!perFrame && style == GraphStyle.Filled)
        {
            foreach (var p in points)
                if (!float.IsNaN(p.Y))
                    rc.DC.FillRectangle(new Rect(p.X, p.Y, 1f, (float)(area.Bottom - p.Y)), brush);
            return;
        }
        Vector2? prev = null;
        foreach (var p in points)
        {
            if (float.IsNaN(p.Y)) { prev = null; continue; }
            if (prev != null) rc.DC.DrawLine(prev.Value, p, brush, 1.0f);
            prev = p;
        }
    }
}

/// <summary>
/// The configurable look for skins: an antialiased polyline, an optional gradient fill under it
/// fading to the floor, node dots and a grid behind. Combine freely — a skin that wants only dots
/// turns <see cref="Line"/> off.
///
/// <para>The line and fill are path geometries built per repaint. A graph repaints when a column
/// boundary passes or a frame lands, so this is a few geometries a second, on skins that opt in;
/// Rainformer's visual allocates nothing.</para>
/// </summary>
public sealed class SkinGraphVisual : GraphVisual
{
    public bool Line = true;
    public float LineWidth = 1.5f;
    /// <summary>Alpha of the fill right under the line, fading to 0 at the floor. 0 = no fill —
    /// unless the user picked the "filled" graph style, which then gets <see cref="FilledAlpha"/>.</summary>
    public float FillAlpha;
    public float FilledAlpha = 0.45f;
    /// <summary>A dot on every Nth point (0 = none).</summary>
    public int DotEvery;
    public float DotRadius = 1.6f;
    /// <summary>Grid cells behind the plot (0 = no lines that way).</summary>
    public int GridCols, GridRows;
    public string GridColor = "emptyBar";

    public override void DrawBackdrop(RenderContext rc, Theme theme, GraphArea a)
    {
        if (GridCols <= 0 && GridRows <= 0) return;
        var brush = rc.Brush(theme.Color(GridColor));
        for (int i = 1; i < GridCols; i++)
        {
            float x = (float)(a.X + a.W * i / GridCols);
            rc.DC.DrawLine(new Vector2(x, (float)a.Y), new Vector2(x, (float)a.Bottom), brush, 0.5f);
        }
        for (int i = 1; i < GridRows; i++)
        {
            float y = (float)(a.Y + a.H * i / GridRows);
            rc.DC.DrawLine(new Vector2((float)a.X, y), new Vector2((float)(a.X + a.W), y), brush, 0.5f);
        }
    }

    public override void DrawSeries(RenderContext rc, Theme theme, GraphSeries series,
        ReadOnlySpan<Vector2> points, GraphArea area, GraphStyle style, bool perFrame)
    {
        var color = theme.Color(series.Color);
        float fillAlpha = FillAlpha > 0 ? FillAlpha : style == GraphStyle.Filled ? FilledAlpha : 0;
        using var factory = rc.DC.Factory;

        if (fillAlpha > 0)
        {
            using var fill = Build(factory, points, (float)area.Bottom, closed: true);
            var top = new Vector2(0, (float)area.Y);
            var bottom = new Vector2(0, (float)area.Bottom);
            rc.DC.FillGeometry(fill, rc.LinearGradient(new Color4(color.R, color.G, color.B, color.A * fillAlpha), new Color4(color.R, color.G, color.B, 0), top, bottom));
        }
        if (Line)
        {
            using var line = Build(factory, points, 0, closed: false);
            rc.DC.DrawGeometry(line, rc.Brush(color), LineWidth);
        }
        if (DotEvery > 0)
        {
            var brush = rc.Brush(color);
            for (int i = 0; i < points.Length; i += DotEvery)
                if (!float.IsNaN(points[i].Y))
                    rc.DC.FillEllipse(new Ellipse(points[i], DotRadius, DotRadius), brush);
        }
    }

    /// <summary>One figure per unbroken run of points. A closed run drops to <paramref name="floor"/>
    /// at both ends, for the fill.</summary>
    private static ID2D1PathGeometry Build(ID2D1Factory factory, ReadOnlySpan<Vector2> pts, float floor, bool closed)
    {
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        int i = 0;
        while (i < pts.Length)
        {
            while (i < pts.Length && float.IsNaN(pts[i].Y)) i++;
            int start = i;
            while (i < pts.Length && !float.IsNaN(pts[i].Y)) i++;
            if (i - start < 2) continue;
            if (closed)
            {
                sink.BeginFigure(new Vector2(pts[start].X, floor), FigureBegin.Filled);
                for (int k = start; k < i; k++) sink.AddLine(pts[k]);
                sink.AddLine(new Vector2(pts[i - 1].X, floor));
                sink.EndFigure(FigureEnd.Closed);
            }
            else
            {
                sink.BeginFigure(pts[start], FigureBegin.Hollow);
                for (int k = start + 1; k < i; k++) sink.AddLine(pts[k]);
                sink.EndFigure(FigureEnd.Open);
            }
        }
        sink.Close();
        return geo;
    }
}
