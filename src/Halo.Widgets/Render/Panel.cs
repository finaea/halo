namespace Halo.Widgets.Render;

/// <summary>
/// A widget panel: a skin's card chrome + a flow of elements.
/// Layout mirrors the skins: title row inside the top band, content flows from
/// TopMarginFormula downward; panel height = last element bottom + margins.
/// </summary>
public sealed class Panel : IDisposable
{
    /// <summary>The card behind the elements. Set by the skin that built this panel.</summary>
    public ICardChrome? Chrome;

    /// <summary>The window fades this panel in when it first appears (motion subtle or full). Set by
    /// a skin that wants motion; Rainformer leaves it off and so never moves at all.</summary>
    public bool Entrance;

    /// <summary>Some element of this panel may offer an ambient loop. Panels that never do (all of
    /// Rainformer) skip the loop gate and CPU step entirely, so a game starting or stopping never
    /// repaints them on motion's account.</summary>
    public bool Loops;

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
        Chrome?.DrawCard(rc, theme, ComputedHeight);

        if (theme.ShowTitle)
            foreach (var e in TitleElements)
                if (e.IsVisible(ctx)) e.Draw(rc, ctx);
        foreach (var e in _visible)
            e.Draw(rc, ctx);

        if (ctx.Stale)
            Chrome?.DrawStaleBadge(rc, theme);
    }

    /// <summary>A click without drag at (x, y) in logical units: the first element that claims it
    /// reacts (a character pokes, the companion marks its thread read). False = nothing there.</summary>
    public bool Poke(PanelContext ctx, double x, double y)
    {
        foreach (var e in _visible)
            if (e is IPokeTarget t && t.Poke(ctx, x, y)) return true;
        return false;
    }

    /// <summary>A mouse wheel turn over (x, y), in logical units, of <paramref name="notches"/>
    /// (positive = away from the user, so up and back in time). True = something scrolled.</summary>
    public bool Wheel(PanelContext ctx, double x, double y, int notches)
    {
        foreach (var e in _visible)
            if (e is IScrollTarget t && t.Wheel(ctx, x, y, notches)) return true;
        return false;
    }

    public void Dispose() => Chrome?.Dispose();
}

/// <summary>An element with a zone that the mouse wheel scrolls — the companion's MomoTalk thread.</summary>
public interface IScrollTarget
{
    /// <summary>Scroll if (x, y), in logical units, is over this element's zone; true if it moved.</summary>
    bool Wheel(PanelContext ctx, double x, double y, int notches);
}

/// <summary>An element with something on it that answers a click — a character's face.</summary>
public interface IPokeTarget
{
    /// <summary>React if (x, y), in logical units, is on this element's target; true if it was.</summary>
    bool Poke(PanelContext ctx, double x, double y);
}
