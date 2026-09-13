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

    private bool? _hasFrameGraph;
    /// <summary>True when any element draws per-frame data from the shared frame ring — these
    /// panels are pulled forward by the frames-ready event instead of waiting for their tick.</summary>
    public bool HasFrameGraph => _hasFrameGraph ??= Elements.Any(e => e is GraphEl { FrameSample: not null });

    /// <summary>Push a changed graph.historyS / refresh bound into every graph without rebuilding
    /// the window: the rings resize in place and keep the samples that still fit (R4).</summary>
    public void ApplyGraphSettings(double historyS, double height, GraphStyle style, double maxRateHz)
    {
        foreach (var e in Elements)
            if (e is GraphEl g)
            {
                g.H = height;
                g.Style = style;
                g.ApplyHistory(historyS, maxRateHz);
            }
    }

    public double ComputedHeight { get; private set; }

    private readonly List<Element> _visible = new();

    /// <summary>Update all element states. Returns true if anything needs a redraw.</summary>
    public bool Update(PanelContext ctx)
    {
        bool dirty = false;
        if (ctx.Theme.ShowTitle)
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
        // showTitle = false drops the title band, so every row (including the AbsY-anchored ones
        // the skins use for their first lines) moves up by the band's height.
        double shift = theme.ContentShiftY;

        foreach (var e in TitleElements)
        {
            if (!e.IsVisible(ctx)) continue;
            e.Measure(rc, ctx);
            e.Y = e.AbsY ?? (4 + theme.BgOffset);  // styleTitle Y
        }

        _visible.Clear();

        double prevY = theme.TopMarginFormula, prevBottom = theme.TopMarginFormula;
        bool first = true;
        // A row is one non-SameRow leader plus the SameRow elements after it. When the user hides
        // the leader's metric (metrics.<key>.show = false) the next element of that row has to
        // become the leader, or it would silently join the row above and draw on top of it.
        double leaderAdvance = 0;
        bool rowLed = false;
        foreach (var e in Elements)
        {
            if (!e.SameRow) { leaderAdvance = e.Advance; rowLed = false; }
            if (!e.IsVisible(ctx)) continue;
            _visible.Add(e);

            e.Measure(rc, ctx);
            if (e.AbsY != null)
                e.Y = e.AbsY.Value;
            else if (e.SameRow && rowLed)
                e.Y = prevY + e.SameRowOffset;
            else if (first)
                e.Y = theme.TopMarginFormula;
            else
                e.Y = prevBottom + (e.SameRow ? leaderAdvance : e.Advance);
            first = false;
            rowLed = true;
            prevY = e.Y;
            prevBottom = Math.Max(prevBottom, e.Y + e.Height);
            if (!e.SameRow) prevBottom = e.Y + e.Height;
        }

        if (shift != 0)
            foreach (var e in _visible) e.Y += shift;

        ComputedHeight = prevBottom + shift + theme.BottomMargin + theme.BgOffset + 2;
        return ComputedHeight;
    }

    public void Draw(RenderContext rc, PanelContext ctx)
    {
        var theme = rc.Theme;
        DrawBackground(rc, theme, ComputedHeight);

        if (theme.ShowTitle)
            foreach (var e in TitleElements)
                if (e.IsVisible(ctx)) e.Draw(rc, ctx);
        foreach (var e in _visible)
            e.Draw(rc, ctx);

        if (ctx.Stale)
        {
            // per-panel stale badge (plan Â§11): small red dot in the title band corner
            rc.DC.FillEllipse(new Ellipse(new System.Numerics.Vector2((float)(theme.BgWidth - theme.BgOffset - 6), (float)(theme.BgOffset + 5 + theme.ContentShiftY)), 2.5f, 2.5f),
                rc.Brush(theme.Color("staleBadge")));
        }
    }

    /// <summary>Two-zone card: rounded-top band + rounded-bottom body (StyleBackground shapes).
    /// With the title bar hidden there is one zone, rounded on all four corners.</summary>
    private static void DrawBackground(RenderContext rc, Theme theme, double panelH)
    {
        float x = (float)theme.BgOffset, w = (float)theme.BgShapeW, r = (float)theme.CornerRadius;

        if (!theme.ShowTitle)
        {
            float h = (float)(panelH - 2 * theme.BgOffset);
            if (h > 2)
                rc.DC.FillRoundedRectangle(new RoundedRectangle
                {
                    Rect = new Rect(x, (float)theme.BgOffset, w, h),
                    RadiusX = r,
                    RadiusY = r,
                }, rc.Brush(theme.Color("bgBody")));
            return;
        }

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

