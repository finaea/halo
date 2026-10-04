using System.Numerics;
using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using static Halo.Widgets.Skins.AzurArchive.Az;

namespace Halo.Widgets.Skins.AzurArchive;

/// <summary>
/// How Azur Archive draws a <see cref="GraphEl"/> — the look only; sampling, frame wakes and reset
/// keys stay the element's (tech plan §3.4). Three looks:
/// <list type="bullet">
/// <item>the <b>constellation</b> line (design §3): a 1.6 px line, a faint fill under it and hollow
/// node dots every few columns — AL's star-chart motif — with any further series as a plain dashed
/// line, so the panel's own reading stays the one that reads first;</item>
/// <item><b>frame bars</b>: one bar per frame, a frame twice the typical length drawn gold;</item>
/// <item>a <b>histogram</b> of the visible readings for Latency, the newest one's bin in gold.</item>
/// </list>
/// All over a dotted 25/50/75 % grid. Nothing animates: every repaint draws the latest points.
/// </summary>
internal sealed class AzurGraphVisual : GraphVisual
{
    public GraphSeries? Primary;
    public bool Histogram;
    /// <summary>The card is critical: its own line turns red (rule 6: red is critical only).</summary>
    public Func<bool>? Critical;

    private static nint _factory;
    private static ID2D1StrokeStyle? _dots, _dash, _long;

    /// <summary>4-on 3-off, for reference lines across a graph.</summary>
    public static ID2D1StrokeStyle LongDash(RenderContext rc)
    {
        Styles(rc);
        return _long!;
    }

    private static (ID2D1StrokeStyle Dots, ID2D1StrokeStyle Dash) Styles(RenderContext rc)
    {
        using var factory = rc.DC.Factory;
        if (_dots == null || factory.NativePointer != _factory)
        {
            _dots?.Dispose(); _dash?.Dispose(); _long?.Dispose();
            var round = new StrokeStyleProperties { DashStyle = DashStyle.Custom, DashCap = CapStyle.Flat, LineJoin = LineJoin.Round };
            _dots = factory.CreateStrokeStyle(round, [1f, 3f]);
            _dash = factory.CreateStrokeStyle(round, [3f, 2f]);
            _long = factory.CreateStrokeStyle(round, [4f, 3f]);
            _factory = factory.NativePointer;
        }
        return (_dots, _dash!);
    }

    public override void DrawBackdrop(RenderContext rc, Theme theme, GraphArea a)
    {
        var brush = rc.Brush(theme.Color("emptyBar"));
        var dots = Styles(rc).Dots;
        for (int i = 1; i < 4; i++)
        {
            float y = (float)(a.Y + a.H * i / 4);
            rc.DC.DrawLine(new Vector2((float)a.X, y), new Vector2((float)(a.X + a.W), y), brush, U(1), dots);
        }
    }

    public override void DrawSeries(RenderContext rc, Theme theme, GraphSeries series,
        ReadOnlySpan<Vector2> points, GraphArea area, GraphStyle style, bool perFrame)
    {
        var color = theme.Color(Critical?.Invoke() == true && (Primary == null || ReferenceEquals(series, Primary)) ? "red" : series.Color);
        if (perFrame) { FrameBars(rc, theme, points, area, color); return; }
        if (Histogram) { Histo(rc, theme, points, area, color); return; }

        bool primary = Primary == null || ReferenceEquals(series, Primary);
        using var factory = rc.DC.Factory;
        if (!primary)
        {
            using var line2 = Build(factory, points, 0, closed: false);
            rc.DC.DrawGeometry(line2, rc.Brush(color), U(1.3), Styles(rc).Dash);
            return;
        }

        float fill = style == GraphStyle.Filled ? 0.4f : 0.16f;
        using (var under = Build(factory, points, (float)area.Bottom, closed: true))
            rc.DC.FillGeometry(under, rc.LinearGradient(Alpha(color, fill), Alpha(color, fill * 0.35f), new(0, (float)area.Y), new(0, (float)area.Bottom)));
        using (var line = Build(factory, points, 0, closed: false))
            rc.DC.DrawGeometry(line, rc.Brush(color), U(1.6));

        // node dots: hollow, every ~27 columns, starting half a step in so none sits on an edge
        var dotFill = rc.Brush(theme.Color("diamondFill"));
        var ring = rc.Brush(color);
        int every = Math.Max(8, points.Length / 7);
        for (int i = every / 2; i < points.Length; i += every)
        {
            if (float.IsNaN(points[i].Y)) continue;
            var e = new Ellipse(points[i], U(2.3), U(2.3));
            rc.DC.FillEllipse(e, dotFill);
            rc.DC.DrawEllipse(e, ring, U(1.4));
        }
    }

    private static void FrameBars(RenderContext rc, Theme t, ReadOnlySpan<Vector2> pts, GraphArea a, Color4 color)
    {
        if (pts.Length == 0) return;
        float bottom = (float)a.Bottom, sum = 0;
        int n = 0;
        foreach (var p in pts) if (!float.IsNaN(p.Y)) { sum += bottom - p.Y; n++; }
        if (n == 0) return;
        float spike = 1.8f * sum / n;                 // a frame ~twice the typical length is a hitch
        var normal = rc.Brush(color);
        var gold = rc.Brush(t.Color("barWarn"));
        float bw = 0.7f;
        foreach (var p in pts)
        {
            if (float.IsNaN(p.Y)) continue;
            float h = Math.Max(U(1), bottom - p.Y);
            rc.DC.FillRectangle(new Rect(p.X - bw, bottom - h, bw, h), h > spike ? gold : normal);
        }
    }

    private static void Histo(RenderContext rc, Theme t, ReadOnlySpan<Vector2> pts, GraphArea a, Color4 color)
    {
        const int Bins = 20;
        Span<int> bins = stackalloc int[Bins];
        int newest = -1;
        float h = (float)a.H;
        // bin over the spread actually on screen, not 0..max: latency lives in a narrow band and a
        // 0-based axis would pile every reading into two or three bars
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var p in pts)
            if (!float.IsNaN(p.Y)) { lo = Math.Min(lo, p.Y); hi = Math.Max(hi, p.Y); }
        if (lo > hi) return;
        float pad = Math.Max((hi - lo) * 0.15f, h * 0.05f);
        float top = lo - pad, span = hi - lo + 2 * pad;
        for (int i = 0; i < pts.Length; i++)
        {
            if (float.IsNaN(pts[i].Y)) continue;
            float f = Math.Clamp(1 - (pts[i].Y - top) / span, 0, 0.9999f);
            int b = (int)(f * Bins);
            bins[b]++;
            if (newest < 0) newest = b;               // points arrive newest first
        }
        int max = 0;
        foreach (int b in bins) max = Math.Max(max, b);
        if (max == 0) return;
        float slot = (float)a.W / Bins, bw = slot * 0.8f;
        var normal = rc.Brush(Alpha(color, 0.75f));
        var gold = rc.Brush(t.Color("barWarn"));
        for (int b = 0; b < Bins; b++)
        {
            if (bins[b] == 0) continue;
            float bh = Math.Max(U(1.5), h * 0.95f * bins[b] / max);
            rc.DC.FillRectangle(new Rect((float)a.X + b * slot + (slot - bw) / 2, (float)a.Bottom - bh, bw, bh), b == newest ? gold : normal);
        }
    }

    /// <summary>One figure per unbroken run; a closed run drops to <paramref name="floor"/> at both ends.
    /// Measured against per-column rectangles and per-segment lines: two paths per repaint are cheaper.</summary>
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
