using System.Globalization;
using Halo.Shared.Config;
using Halo.Shared.Panels;
using Vortice.Mathematics;

namespace Halo.Widgets.Render;

/// <summary>
/// Live data + this widget's settings, passed to element callbacks each tick.
///
/// Panels ask questions through the helpers below rather than reading the options dictionary
/// directly: every answer is "what the user set, else what the catalog says", so a new option or
/// a renamed label is one edit in <see cref="PanelCatalog"/> (settings plan S1).
/// </summary>
public sealed class PanelContext
{
    public required MetricCache Metrics { get; init; }
    public required Theme Theme { get; init; }
    // Settings and Widget are re-pointed on an in-place config apply: a reload builds new objects
    // and a window left holding the old ones would render (and save) stale config.
    public required AppSettings Settings { get; set; }
    public required WidgetInstance Widget { get; set; }

    /// <summary>Catalog entry for this widget's type; null only for an unknown type.</summary>
    public PanelType? Type { get; init; }

    /// <summary>Fastest useful repaint for this widget (rates plan R2) — also the sampling rate
    /// the graph rings are sized for.</summary>
    public double MaxRateHz = PanelRates.CeilingHz;

    public Dictionary<string, string> Options => Widget.Options;

    public bool Stale;
    public DateTime Now;
    /// <summary>QPC of this tick. Graph samples are timestamped with it (rates plan R4).</summary>
    public long NowQpc;
    public long TickIndex;

    // ---- options ----

    public string Option(string key)
        => Type?.OptionValue(Options, key) ?? Options.GetValueOrDefault(key, "");

    public bool OptionBool(string key)
        => Option(key).Equals("true", StringComparison.OrdinalIgnoreCase);

    public int OptionInt(string key, int fallback)
        => int.TryParse(Option(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    /// <summary>Comma-separated list option ("C,D,E" → ["C","D","E"]). Empty when unset.</summary>
    public string[] OptionList(string key)
        => Option(key).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Widget title: the user's rename, else the panel's own default (often a live
    /// metric such as the CPU or GPU name).</summary>
    public string TitleOr(string fallback)
        => string.IsNullOrWhiteSpace(Widget.Title) ? fallback : Widget.Title!;

    // ---- per-metric settings ----

    public MetricSetting? Metric(string key) => Widget.Metrics.GetValueOrDefault(key);

    /// <summary>Is this metric's row shown? Default yes.</summary>
    public bool Shows(string key) => Metric(key)?.Show ?? true;

    /// <summary>Is this metric's graph line drawn? Default = the catalog's GraphDefaultOn.</summary>
    public bool Graphs(string key)
        => Metric(key)?.Graph ?? Type?.Metric(BaseKey(key))?.GraphDefaultOn ?? true;

    /// <summary>Row label: the user's rename for this exact row, else the rename they set on the
    /// repeated group ("Core {n}:" applies to every core row), else the catalog default.</summary>
    public string Label(string key, string fallback = "")
        => UserLabel(key) ?? Type?.Metric(BaseKey(key))?.DefaultLabel ?? fallback;

    /// <summary>The user's label for a row (or its repeat group), or null when they set none.</summary>
    public string? UserLabel(string key)
    {
        if (Metric(key)?.Label is { Length: > 0 } exact) return exact;
        string b = BaseKey(key);
        if (b != key && Metric(b)?.Label is { Length: > 0 } group) return group;
        return null;
    }

    /// <summary>Warn thresholds: the user's, else the catalog's defaults, else empty.</summary>
    public double[] Warn(string key)
        => Metric(key)?.Warn ?? Type?.Metric(BaseKey(key))?.WarnDefaults ?? [];

    /// <summary>Scale ceiling for a metric (fan max RPM), or the fallback when unset.</summary>
    public double MaxOf(string key, double fallback) => Metric(key)?.Max ?? fallback;

    /// <summary>
    /// Colour token for a metric row: the widget's own <c>metrics.&lt;key&gt;.color</c> when it set
    /// one, else the catalog's token. Per-metric overrides are pre-resolved into the widget's
    /// <see cref="Halo.Widgets.Theme"/> under <c>metric:&lt;key&gt;</c>, so an element still names a
    /// token and nothing has to carry a Color4 around.
    /// </summary>
    public string Color(string key, string fallbackToken)
    {
        if (Theme.HasColor("metric:" + key)) return "metric:" + key;
        string base_ = BaseKey(key);
        if (base_ != key && Theme.HasColor("metric:" + base_)) return "metric:" + base_;
        return fallbackToken;
    }

    // ---- graphs (widget-level settings shared by every graph in the panel) ----

    public double GraphHistoryS => Math.Clamp(Widget.Graph.HistoryS, 10, 600);

    public double GraphHeight => Math.Clamp(Widget.Graph.Height, 4, 200);

    public GraphStyle GraphStyle
        => string.Equals(Widget.Graph.Style, "filled", StringComparison.OrdinalIgnoreCase)
            ? Render.GraphStyle.Filled : Render.GraphStyle.Line;

    /// <summary>A ring big enough for this widget's history at its fastest possible tick.</summary>
    public SampleRing NewRing() => new(GraphEl.CapacityFor(GraphHistoryS, MaxRateHz));

    // ---- formatting ----

    /// <summary>Per-widget temperature unit (appearance.tempUnit). Thresholds stay in °C;
    /// only the displayed number is converted, at format time.</summary>
    public bool Fahrenheit => string.Equals(Widget.Appearance?.TempUnit, "F", StringComparison.OrdinalIgnoreCase);

    public double Temp(double celsius) => Fahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;

    public string TempUnit => Fahrenheit ? "°F" : "°C";

    /// <summary>"48°C" / "118°F" for a Celsius reading.</summary>
    public string TempText(double celsius) => ValueFormat.Int0(Temp(celsius)) + TempUnit;

    // ---- N/A ----

    /// <summary>
    /// The collector-side freshness contract, rendered: a metric with no fresh value reads
    /// <c>N/A</c>, not a plausible <c>0</c>. The collector already decided (a provider that failed
    /// or stopped polling has its readings marked absent), so there is no age policy here — the
    /// row just asks whether there is a value and says so.
    ///
    /// <b>The unit has to vanish with the number</b>, or a dead sensor reads "N/A °C". Units are
    /// concatenated outside the number formatter in every panel — <c>Int0(v) + TempUnit</c>,
    /// <c>$"{Int0(v)}%"</c>, <c>AutoScale(v) + "B/s"</c> — so the decision cannot live inside
    /// <see cref="ValueFormat"/>. It lives here, one level up, where <paramref name="format"/>
    /// produces the whole string including the unit and the whole string is what gets replaced.
    /// </summary>
    public string Na(string metric, Func<double, string> format)
        => Metrics.TryValue(metric, out double v) ? format(v) : "N/A";

    /// <summary>Two metrics in one row ("12.4 / 32.0 GB"): N/A unless both are readable, since
    /// half a ratio is not a number anyone can use.</summary>
    public string Na(string a, string b, Func<double, double, string> format)
        => Metrics.TryValue(a, out double va) && Metrics.TryValue(b, out double vb) ? format(va, vb) : "N/A";

    /// <summary>
    /// A graph sample, or NaN when the reading is unavailable. NaN is how a series says "no sample
    /// here": <see cref="GraphEl"/>'s bucket drawing already treats an empty column as "hold the
    /// previous value", so a provider blip leaves a flat line rather than a cliff to the floor —
    /// which is what appending a 0 would draw.
    /// </summary>
    public double NaSample(string metric)
        => Metrics.TryValue(metric, out double v) ? v : double.NaN;

    /// <summary>"rpm.2" → "rpm": repeated rows share one catalog spec.</summary>
    private static string BaseKey(string key)
    {
        int dot = key.IndexOf('.');
        return dot < 0 ? key : key[..dot];
    }
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
    /// <summary>Rainmeter InlineSetting=Size equivalent: render the returned (start,len) range
    /// of the text at InlineSizePt instead of the style size (baseline-shared).</summary>
    public Func<string, (int Start, int Len)>? InlineRange;
    public double InlineSizePt;

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
        (int Start, int Len, double SizePt)? inline = null;
        if (InlineRange != null && InlineRange(text) is { Len: > 0 } r)
            inline = (r.Start, r.Len, InlineSizePt);
        rc.DrawText(text, Style, theme.Color(_cachedColor), x, Y, Align, inlineSize: inline);
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

public enum GraphStyle { Line, Filled }

public sealed class GraphSeries
{
    public required string Color;
    public required SampleRing Ring;
    /// <summary>Fixed max for scaling; null = autoscale to the visible window's max.</summary>
    public double? FixedMax;
    /// <summary>Pull one sample per widget tick.</summary>
    public required Func<PanelContext, double> Sample;

    /// <summary>Highest value seen in the column currently being filled — lets the panel skip a
    /// repaint when a new sample cannot change any drawn pixel.</summary>
    internal double BucketMax = double.NegativeInfinity;
}

/// <summary>
/// A history graph. Columns are <b>time buckets</b>, not sample slots: the visible span is
/// <see cref="HistoryS"/> seconds and one column covers <c>HistoryS / width</c> seconds, holding
/// the maximum of whatever landed in it (rates plan R4). That way the span a graph means never
/// changes when the user moves the refresh slider — before this, history was
/// "188 px ÷ sample rate" and silently stretched or shrank.
///
/// Frame graphs (<see cref="FrameSample"/>) are the exception and stay one bar per frame.
/// </summary>
public sealed class GraphEl : Element
{
    private static readonly long Qpf = System.Diagnostics.Stopwatch.Frequency;

    public double? X, W;
    public double H = 25;
    public GraphStart Start = GraphStart.Right;
    public string? BgColor;                      // e.g. emptyBar for half graphs
    public List<GraphSeries> Series = new();
    /// <summary>Visible history in seconds (widget setting graph.historyS, 10–600).</summary>
    public double HistoryS = 40;
    public GraphStyle Style = GraphStyle.Line;
    /// <summary>When set, samples come from the shared frame ring instead (per-frame graph).</summary>
    public Func<Halo.Metrics.FrameEntry, double>? FrameSample;
    public bool FrameDisplayedOnly;
    /// <summary>Optional per-frame predicate — lane selection by FrameFlags (tap vs resolved).</summary>
    public Func<PanelContext, Halo.Metrics.FrameEntry, bool>? FrameFilter;

    private bool _dirty = true;
    private long _lastBucket = long.MinValue;
    private double _lastWidth = 188;
    private double[] _cols = [];

    public override bool Update(PanelContext ctx)
    {
        if (FrameSample != null)
        {
            // frames arrive in small timestamped batches (≤17 ms resolved lane, per-flush tap
            // lane) and are drawn the tick they land — the 1.25 s pacing queue that smoothed
            // the old console transport's 1 s stdout bursts is gone with the transport
            foreach (ref readonly var f in ctx.Metrics.NewFrames)
            {
                if (FrameDisplayedOnly && (f.Flags & (uint)Halo.Metrics.FrameFlags.Displayed) == 0) continue;
                if (FrameFilter != null && !FrameFilter(ctx, f)) continue;
                double v = FrameSample(f);
                foreach (var s in Series) s.Ring.Add(f.Qpc, v);
                _dirty = true;
            }
        }
        else
        {
            // One sample per tick, timestamped: the bucket drawing decides what is visible.
            long colTicks = ColumnTicks(_lastWidth);
            long bucket = ctx.NowQpc / colTicks;
            bool boundary = bucket != _lastBucket;
            _lastBucket = bucket;

            foreach (var s in Series)
            {
                double v = s.Sample(ctx);
                s.Ring.Add(ctx.NowQpc, v);
                if (boundary) s.BucketMax = double.NegativeInfinity;
                // a sample inside the current column that can't raise it changes no pixel
                if (v > s.BucketMax) { s.BucketMax = v; _dirty = true; }
            }
            // crossing a column boundary scrolls every column along, so the image always changes
            if (boundary) _dirty = true;
        }
        bool d = _dirty;
        _dirty = false;
        return d;
    }

    private long ColumnTicks(double width) => Math.Max(1, (long)(HistoryS * Qpf / Math.Max(1, width)));

    /// <summary>Ring capacity for a given refresh bound: one sample per tick over the whole
    /// visible span, plus headroom for a late tick (rates plan R4).</summary>
    public static int CapacityFor(double historyS, double maxRateHz)
        => (int)Math.Ceiling(Math.Clamp(historyS, 1, 3600) * Math.Clamp(maxRateHz, 0.5, 64)) + 16;

    /// <summary>Apply a changed graph.historyS / rate in place, keeping the samples that fit.</summary>
    public void ApplyHistory(double historyS, double maxRateHz)
    {
        HistoryS = historyS;
        if (FrameSample != null) return;         // frame graphs are per-frame, not per-second
        int cap = CapacityFor(historyS, maxRateHz);
        foreach (var s in Series) s.Ring.Resize(cap);
        _dirty = true;
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = H;

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        var theme = rc.Theme;
        double x = X ?? theme.ContentMargin + 1;
        double w = W ?? theme.ContentWidth - 2;
        _lastWidth = w;
        if (BgColor != null)
            rc.DC.FillRectangle(new Rect((float)x, (float)Y, (float)w, (float)H), rc.Brush(theme.Color(BgColor)));

        if (FrameSample != null) DrawPerSample(rc, theme, x, w);
        else DrawBuckets(rc, theme, ctx, x, w);
    }

    /// <summary>Frame graphs: one bar per frame, newest at the Start edge (unchanged).</summary>
    private void DrawPerSample(RenderContext rc, Theme theme, double x, double w)
    {
        foreach (var s in Series)
        {
            int n = s.Ring.Count;
            if (n < 2) continue;
            double max = s.FixedMax ?? Math.Max(1e-9, s.Ring.Max());
            int points = Math.Min(n, (int)w);
            var brush = rc.Brush(theme.Color(s.Color));
            System.Numerics.Vector2? prev = null;
            for (int i = 0; i < points; i++)
            {
                double v = s.Ring.ValueAt(n - points + i);
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

    /// <summary>Time-bucket drawing: column c covers (now-(c+1)·colSpan, now-c·colSpan], value =
    /// the max that landed in it, empty column = hold the previous (older) value.</summary>
    private void DrawBuckets(RenderContext rc, Theme theme, PanelContext ctx, double x, double w)
    {
        int cols = Math.Max(1, (int)w);
        if (_cols.Length < cols) _cols = new double[cols];
        double colTicks = HistoryS * Qpf / cols;
        long now = ctx.NowQpc;

        foreach (var s in Series)
        {
            int n = s.Ring.Count;
            if (n == 0) continue;

            for (int i = 0; i < cols; i++) _cols[i] = double.NaN;

            // newest → oldest; the ring is chronological, so the first sample past the window
            // ends the walk
            for (int i = n - 1; i >= 0; i--)
            {
                var (q, v) = s.Ring[i];
                long age = now - q;
                if (age < 0) age = 0;
                int col = (int)(age / colTicks);
                if (col >= cols) break;
                if (double.IsNaN(_cols[col]) || v > _cols[col]) _cols[col] = v;
            }

            // hold-last: carry an older column's value into the newer, still-empty ones (a 0.5 Hz
            // metric only lands in roughly one column in nine)
            double carry = double.NaN;
            double maxSeen = 0;
            for (int col = cols - 1; col >= 0; col--)
            {
                if (double.IsNaN(_cols[col])) _cols[col] = carry;
                else carry = _cols[col];
                if (!double.IsNaN(_cols[col]) && _cols[col] > maxSeen) maxSeen = _cols[col];
            }

            double max = s.FixedMax ?? Math.Max(1e-9, maxSeen);
            var brush = rc.Brush(theme.Color(s.Color));
            System.Numerics.Vector2? prev = null;
            for (int col = 0; col < cols; col++)
            {
                double v = _cols[col];
                if (double.IsNaN(v)) { prev = null; continue; }
                float px = Start == GraphStart.Right ? (float)(x + w - col) : (float)(x + col);
                float py = (float)(Y + H - Math.Clamp(v / max, 0, 1) * H);
                if (Style == GraphStyle.Filled)
                {
                    rc.DC.FillRectangle(new Rect(px, py, 1f, (float)(Y + H - py)), brush);
                }
                else
                {
                    var pt = new System.Numerics.Vector2(px, py);
                    if (prev != null) rc.DC.DrawLine(prev.Value, pt, brush, 1.0f);
                    prev = pt;
                }
            }
        }
    }
}

public sealed class HLineEl : Element
{
    public string Color = "text2";
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
