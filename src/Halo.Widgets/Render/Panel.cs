using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Halo.Widgets.Render;

/// <summary>
/// A widget panel: the Rainformer two-zone rounded background + a flow of elements.
/// Layout mirrors the skins: title row inside the top band, content flows from
/// TopMarginFormula downward; panel height = last element bottom + margins.
/// </summary>
public sealed class Panel
{
    /// <summary>Title-zone rows drawn at fixed positions inside the band (Yâ‰ˆ9).</summary>
    public List<Element> TitleElements = new();
    public List<Element> Elements = new();

    public double ComputedHeight { get; private set; }

    private readonly List<Element> _visible = new();

    /// <summary>Update all element states. Returns true if anything needs a redraw.</summary>
    public bool Update(PanelContext ctx)
    {
        bool dirty = false;
        foreach (var e in TitleElements)
            if (e.IsVisible(ctx) && e.Update(ctx)) dirty = true;
        foreach (var e in Elements)
            if (e.IsVisible(ctx) && e.Update(ctx)) dirty = true;
        return dirty;
    }

    /// <summary>Compute Y positions (logical units). Returns panel height in logical units.</summary>
    public double Layout(RenderContext rc, PanelContext ctx)
    {
        var theme = rc.Theme;

        foreach (var e in TitleElements)
        {
            if (!e.IsVisible(ctx)) continue;
            e.Measure(rc, ctx);
            e.Y = e.AbsY ?? (4 + theme.BgOffset);  // styleTitle Y
        }

        _visible.Clear();
        foreach (var e in Elements)
            if (e.IsVisible(ctx)) _visible.Add(e);

        double prevY = theme.TopMarginFormula, prevBottom = theme.TopMarginFormula;
        bool first = true;
        foreach (var e in _visible)
        {
            e.Measure(rc, ctx);
            if (e.AbsY != null)
                e.Y = e.AbsY.Value;
            else if (e.SameRow)
                e.Y = prevY + e.SameRowOffset;
            else if (first)
                e.Y = theme.TopMarginFormula;
            else
                e.Y = prevBottom + e.Advance;
            first = false;
            prevY = e.Y;
            prevBottom = Math.Max(prevBottom, e.Y + e.Height);
            if (!e.SameRow) prevBottom = e.Y + e.Height;
        }

        ComputedHeight = prevBottom + theme.BottomMargin + theme.BgOffset + 2;
        return ComputedHeight;
    }

    public void Draw(RenderContext rc, PanelContext ctx)
    {
        var theme = rc.Theme;
        DrawBackground(rc, theme, ComputedHeight);

        foreach (var e in TitleElements)
            if (e.IsVisible(ctx)) e.Draw(rc, ctx);
        foreach (var e in _visible)
            e.Draw(rc, ctx);

        if (ctx.Stale)
        {
            // per-panel stale badge (plan Â§11): small red dot in the title band corner
            rc.DC.FillEllipse(new Ellipse(new System.Numerics.Vector2((float)(theme.BgWidth - theme.BgOffset - 6), (float)(theme.BgOffset + 5)), 2.5f, 2.5f),
                rc.Brush(theme.Color("staleBadge")));
        }
    }

    /// <summary>Two-zone card: rounded-top band + rounded-bottom body (StyleBackground shapes).</summary>
    private static void DrawBackground(RenderContext rc, Theme theme, double panelH)
    {
        float x = (float)theme.BgOffset, w = (float)theme.BgShapeW, r = (float)theme.CornerRadius;

        // top band: y 5..26, rounded top corners, square bottom
        DrawHalfRounded(rc, x, (float)theme.BgOffset, w, (float)theme.TitleZoneH, r, roundTop: true, theme.Color("bgTop"));

        // body: y 29.5 .. panelH-5, square top, rounded bottom
        float bodyTop = (float)(theme.BgOffset + 24.5);
        float bodyH = (float)(panelH - theme.BgOffset - bodyTop);
        if (bodyH > 2)
            DrawHalfRounded(rc, x, bodyTop, w, bodyH, r, roundTop: false, theme.Color("bgBody"));
    }

    private static void DrawHalfRounded(RenderContext rc, float x, float y, float w, float h, float r, bool roundTop, Color4 color)
    {
        var dc = rc.DC;
        var factory = dc.Factory;
        using var geo = factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            if (roundTop)
            {
                sink.BeginFigure(new System.Numerics.Vector2(x, y + r), FigureBegin.Filled);
                sink.AddArc(new ArcSegment(new System.Numerics.Vector2(x + r, y), new Size(r, r), 0, SweepDirection.Clockwise, ArcSize.Small));
                sink.AddLine(new System.Numerics.Vector2(x + w - r, y));
                sink.AddArc(new ArcSegment(new System.Numerics.Vector2(x + w, y + r), new Size(r, r), 0, SweepDirection.Clockwise, ArcSize.Small));
                sink.AddLine(new System.Numerics.Vector2(x + w, y + h));
                sink.AddLine(new System.Numerics.Vector2(x, y + h));
            }
            else
            {
                sink.BeginFigure(new System.Numerics.Vector2(x, y), FigureBegin.Filled);
                sink.AddLine(new System.Numerics.Vector2(x + w, y));
                sink.AddLine(new System.Numerics.Vector2(x + w, y + h - r));
                sink.AddArc(new ArcSegment(new System.Numerics.Vector2(x + w - r, y + h), new Size(r, r), 0, SweepDirection.Clockwise, ArcSize.Small));
                sink.AddLine(new System.Numerics.Vector2(x + r, y + h));
                sink.AddArc(new ArcSegment(new System.Numerics.Vector2(x, y + h - r), new Size(r, r), 0, SweepDirection.Clockwise, ArcSize.Small));
            }
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        dc.FillGeometry(geo, rc.Brush(color));
    }
}

