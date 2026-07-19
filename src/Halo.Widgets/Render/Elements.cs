using Vortice.Mathematics;

namespace Halo.Widgets.Render;

/// <summary>Live data + options passed to element callbacks each tick.</summary>
public sealed class PanelContext
{
    public required MetricCache Metrics { get; init; }
    public required Theme Theme { get; init; }
    public required Halo.Shared.Config.GeneralSettings Settings { get; init; }
    public Dictionary<string, string> Options { get; set; } = new();
    public bool Stale;
    public DateTime Now;
    public long TickIndex;
}

/// <summary>
/// One visual row/fragment. Flow model mirrors Rainmeter: absolute Y, next-row (prevBottom +
/// advance) or same-row (prevY + offset). Hidden elements are skipped in the chain, like
/// Rainmeter's Hidden=1.
/// </summary>
public abstract class Element
{
    public double? AbsY;
    public double Advance = 1;          // "nR" spacing when flowed to next row
    public bool SameRow;                // "r" — share Y with previous element
    public double SameRowOffset;        // e.g. -2 for Y=-2r
    public Func<PanelContext, bool>? VisibleWhen;

    public double Y;                    // computed (logical units)
    public double Height;               // computed

    public bool IsVisible(PanelContext ctx) => VisibleWhen?.Invoke(ctx) ?? true;

    /// <summary>Refresh cached display state; return true if a redraw is needed.</summary>
    public abstract bool Update(PanelContext ctx);

    /// <summary>Set Height (logical units) for flow layout.</summary>
    public abstract void Measure(RenderContext rc, PanelContext ctx);

    public abstract void Draw(RenderContext rc, PanelContext ctx);
}

public sealed class TextEl : Element
{
    public required Func<PanelContext, string> Text;
    public TextStyle Style = TextStyle.Bold8;
    public TextAlign Align = TextAlign.Left;
    public double? X;                        // default: theme margin per alignment
    public string Color = "text";
    public Func<PanelContext, string>? ColorFn;  // dynamic (warn thresholds)
    public bool Upper;
    public double? FixedH;
    public string? SolidColor;               // label pill behind the text
    public double? SolidW, SolidH;
    public double WidthClip;                 // >0: clip/ellipsis to this width (ClipString)

    private string _cached = "";
    private string _cachedColor = "";

    public override bool Update(PanelContext ctx)
    {
        string t = Text(ctx);
        if (Upper) t = t.ToUpperInvariant();
        string c = ColorFn?.Invoke(ctx) ?? Color;
        bool changed = t != _cached || c != _cachedColor;
        _cached = t;
        _cachedColor = c;
        return changed;
    }

    public override void Measure(RenderContext rc, PanelContext ctx)
        => Height = FixedH ?? rc.LineHeight(Style);

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        var theme = rc.Theme;
        double x = X ?? Align switch
        {
            TextAlign.Center => theme.CenterAlign,
            TextAlign.Right => theme.RightAlign,
            _ => theme.ContentMargin,
        };
        if (SolidColor != null)
            rc.FillTextBox(theme.Color(SolidColor), x, Y, SolidW ?? rc.TextWidth(_cached, Style), SolidH ?? Height, Align);

        string text = _cached;
        if (WidthClip > 0)
        {
            while (text.Length > 1 && rc.TextWidth(text + "…", Style) > WidthClip)
                text = text[..^1];
            if (text.Length < _cached.Length) text += "…";
        }
        rc.DrawText(text, Style, theme.Color(_cachedColor), x, Y, Align);
    }
}

public sealed class BarEl : Element
{
    public required Func<PanelContext, double> Value;    // 0..1
    public double? X, W;
    public double H = 1;
    public string FillColor = "bar";
    public Func<PanelContext, string>? FillColorFn;
    public string BgColor = "emptyBar";

    private double _cached = -1;
    private string _cachedColor = "";

    public override bool Update(PanelContext ctx)
    {
        double v = Math.Clamp(Value(ctx), 0, 1);
        string c = FillColorFn?.Invoke(ctx) ?? FillColor;
        // quantize to bar pixel resolution to avoid redraws for invisible changes
        double q = Math.Round(v * 200) / 200;
        bool changed = q != _cached || c != _cachedColor;
        _cached = q;
        _cachedColor = c;
        return changed;
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = H;

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        var theme = rc.Theme;
        double x = X ?? theme.ContentMargin + 1;
        double w = W ?? theme.ContentWidth - 2;
        rc.DC.FillRectangle(new Rect((float)x, (float)Y, (float)w, (float)H), rc.Brush(theme.Color(BgColor)));
        rc.DC.FillRectangle(new Rect((float)x, (float)Y, (float)(w * _cached), (float)H), rc.Brush(theme.Color(_cachedColor)));
    }
}

public enum GraphStart { Left, Right }

public sealed class GraphSeries
{
    public required string Color;
    public required HistoryRing Ring;
    /// <summary>Fixed max for scaling; null = autoscale to ring max (min 1).</summary>
    public double? FixedMax;
    /// <summary>Pull one sample per sample-tick.</summary>
    public required Func<PanelContext, double> Sample;
}

public sealed class GraphEl : Element
{
    public double? X, W;
    public double H = 25;
    public GraphStart Start = GraphStart.Right;
    public string? BgColor;                      // e.g. emptyBar for half graphs
    public List<GraphSeries> Series = new();
    /// <summary>Sampling cadence; Rainformer skins sample 1/s (Update=1000).</summary>
    public double SampleRateHz = 1;
    /// <summary>When set, samples come from the shared frame ring instead (per-frame graph).</summary>
    public Func<Halo.Shared.Metrics.FrameEntry, double>? FrameSample;
    public bool FrameDisplayedOnly;
    /// <summary>Optional per-frame predicate — lane selection by FrameFlags (tap vs resolved).</summary>
    public Func<PanelContext, Halo.Shared.Metrics.FrameEntry, bool>? FrameFilter;

    private long _lastSampleTick = -1;
    private bool _dirty = true;

    public override bool Update(PanelContext ctx)
    {
        if (FrameSample != null)
        {
            // frames arrive in small timestamped batches (≤17 ms resolved lane, per-flush tap
            // lane) and are drawn the tick they land — the 1.25 s pacing queue that smoothed
            // the old console transport's 1 s stdout bursts is gone with the transport
            foreach (ref readonly var f in ctx.Metrics.NewFrames)
            {
                if (FrameDisplayedOnly && (f.Flags & (uint)Halo.Shared.Metrics.FrameFlags.Displayed) == 0) continue;
                if (FrameFilter != null && !FrameFilter(ctx, f)) continue;
                double v = FrameSample(f);
                foreach (var s in Series) s.Ring.Add(v);
                _dirty = true;
            }
        }
        else
        {
            // sample on our own cadence, independent of widget tick rate
            long due = (long)(ctx.Now.Ticks * SampleRateHz / TimeSpan.TicksPerSecond);
            if (due != _lastSampleTick)
            {
                _lastSampleTick = due;
                foreach (var s in Series) s.Ring.Add(s.Sample(ctx));
                _dirty = true;
            }
        }
        bool d = _dirty;
        _dirty = false;
        return d;
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = H;

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        var theme = rc.Theme;
        double x = X ?? theme.ContentMargin + 1;
        double w = W ?? theme.ContentWidth - 2;
        if (BgColor != null)
            rc.DC.FillRectangle(new Rect((float)x, (float)Y, (float)w, (float)H), rc.Brush(theme.Color(BgColor)));

        foreach (var s in Series)
        {
            int n = s.Ring.Count;
            if (n < 2) continue;
            double max = s.FixedMax ?? Math.Max(1e-9, s.Ring.Max());
            int points = Math.Min(n, (int)w);
            var brush = rc.Brush(theme.Color(s.Color));
            // newest sample at the Start edge; 1 logical px per sample (Rainmeter Line meter)
            System.Numerics.Vector2? prev = null;
            for (int i = 0; i < points; i++)
            {
                double v = s.Ring[n - points + i];
                float px = Start == GraphStart.Right
                    ? (float)(x + w - (points - 1 - i))
                    : (float)(x + (points - 1 - i));
                float py = (float)(Y + H - Math.Clamp(v / max, 0, 1) * H);
                var pt = new System.Numerics.Vector2(px, py);
                if (prev != null) rc.DC.DrawLine(prev.Value, pt, brush, 1.0f);
                prev = pt;
            }
        }
    }
}

public sealed class HLineEl : Element
{
    public string Color = "horizLine";
    public override bool Update(PanelContext ctx) => false;
    public override void Measure(RenderContext rc, PanelContext ctx) => Height = 1;
    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        var theme = rc.Theme;
        rc.DC.FillRectangle(new Rect((float)theme.ContentMargin, (float)Y, (float)theme.ContentWidth, 1f),
            rc.Brush(theme.Color(Color)));
    }
}

public sealed class SpacerEl : Element
{
    public double H;
    public override bool Update(PanelContext ctx) => false;
    public override void Measure(RenderContext rc, PanelContext ctx) => Height = H;
    public override void Draw(RenderContext rc, PanelContext ctx) { }
}
