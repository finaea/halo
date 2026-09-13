using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// Dedicated latency + DLSS telemetry panel (user request 2026-07-19):
///   PC LAT (marker-based Reflex PCL via PresentMon app-timing) — headline, warn-colored
///   CLICK / INPUT photon latencies (moved here from the FPS panels)
///   DLSS row: DLL version + loaded features (SR/FG/RR)
///   MODEL row: Transformer/CNN + override-vs-game-DLL origin
///   FRAME GEN row: effective multiplier (displayed ÷ simulated rate)
///   PCL sparkline (autoscaled)
/// Idle (no 3D app) dims like the FPS panels.
/// </summary>
public static class LatencyPanel
{
    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();
        var t = ctx.Theme;

        p.TitleElements.Add(new TextEl
        {
            Text = c => c.TitleOr("LATENCY / DLSS"),
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
        });

        // ROW 1 — headline PC latency the overlay's way: continuous, ping/marker-based,
        // starting at ②a (input enters the game). = queue wait + render + display.
        p.Elements.Add(new TextEl { Text = c => c.Label("pclat", "PC LAT:"), VisibleWhen = c => c.Shows("pclat"), Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", AbsY = 30, FixedH = 11 });
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) || PcLatency(c) <= 0 ? "N/A" : $"{ValueFormat.Int0(PcLatency(c))}ms",
            ColorFn = c => !IsIdle(c) && PcLatency(c) > 0 ? CpuRamPanelImpl.WarnColor(PcLatency(c), c.Warn("pclat")) : "inactiveButton",
            VisibleWhen = c => c.Shows("pclat"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // ROW 2 — the three components that sum to ROW 1 (queue + render + display), on a pill.
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? $"{c.Label("queue", "QUEUE")} —" : $"{c.Label("queue", "QUEUE")} {ValueFormat.Int0(Comp(c, MetricNames.LatencyQueueMs))}",
            VisibleWhen = c => c.Shows("queue"),
            Style = TextStyle.Text8,
            Align = TextAlign.Left,
            ColorFn = c => c.Color("queue", "text2"),
            SolidColor = "solidLabel",
            SolidW = t.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = 2,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? $"{c.Label("render", "REND")} —" : $"{c.Label("render", "REND")} {ValueFormat.Int0(Comp(c, MetricNames.LatencyRenderMs))}",
            VisibleWhen = c => c.Shows("render"),
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            ColorFn = c => c.Color("render", "text2"),
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? $"{c.Label("display", "DISP")} —" : $"{c.Label("display", "DISP")} {ValueFormat.Int0(Comp(c, MetricNames.FpsDisplayLatencyMs))}",
            VisibleWhen = c => c.Shows("display"),
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            ColorFn = c => c.Color("display", "text2"),
            SameRow = true,
            FixedH = 11,
        });

        // ROW 3 — PresentMon click-to-photon + input-to-photon references, on a pill.
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) || !c.Metrics.TryValue(MetricNames.LatencyClickMs, out double v, 10) ? $"{c.Label("click", "CLICK")} —" : $"{c.Label("click", "CLICK")} {ValueFormat.Int0(v)}ms",
            VisibleWhen = c => c.Shows("click"),
            Style = TextStyle.Text8,
            Align = TextAlign.Left,
            ColorFn = c => c.Color("click", "text2"),
            SolidColor = "solidLabel",
            SolidW = t.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = 1,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? $"{c.Label("input", "INPUT")} —" : $"{c.Label("input", "INPUT")} {ValueFormat.Int0(c.Metrics.Value(MetricNames.LatencyAllInputMs))}ms",
            VisibleWhen = c => c.Shows("input"),
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            ColorFn = c => c.Color("input", "text2"),
            SameRow = true,
            FixedH = 11,
        });

        // DLSS: version + loaded features
        p.Elements.Add(new TextEl { Text = c => c.Label("dlss", "DLSS:"), VisibleWhen = c => c.Shows("dlss"), Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", FixedH = 11, Advance = 2 });
        p.Elements.Add(new TextEl
        {
            Text = c =>
            {
                if (IsIdle(c)) return "—";
                bool sr = c.Metrics.Value(MetricNames.DlssSrPresent) > 0;
                bool fg = c.Metrics.Value(MetricNames.DlssFgPresent) > 0;
                bool rr = c.Metrics.Value(MetricNames.DlssRrPresent) > 0;
                if (!sr && !fg && !rr) return "not loaded";
                string feats = string.Join(" ", new[] { sr ? "SR" : null, fg ? "FG" : null, rr ? "RR" : null }.Where(x => x != null));
                string ver = c.Metrics.Text(MetricNames.DlssVersion);
                return ver.Length > 0 ? $"{ver} · {feats}" : feats;
            },
            VisibleWhen = c => c.Shows("dlss"),
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            ColorFn = c => c.Color("dlss", "text2"),
            WidthClip = 130,
            SameRow = true,
            FixedH = 11,
        });

        // MODEL: Transformer/CNN + override-vs-game origin
        p.Elements.Add(new TextEl { Text = c => c.Label("model", "MODEL:"), VisibleWhen = c => c.Shows("model"), Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", FixedH = 11, Advance = 1 });
        p.Elements.Add(new TextEl
        {
            Text = c =>
            {
                if (IsIdle(c)) return "—";
                string m = c.Metrics.Text(MetricNames.DlssModel);
                return m.Length > 0 ? m : "—";
            },
            VisibleWhen = c => c.Shows("model"),
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            ColorFn = c => c.Color("model", "text2"),
            WidthClip = 130,
            SameRow = true,
            FixedH = 11,
        });

        // FRAME GEN: effective multiplier
        p.Elements.Add(new TextEl { Text = c => c.Label("framegen", "FRAME GEN:"), VisibleWhen = c => c.Shows("framegen"), Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", FixedH = 11, Advance = 1 });
        p.Elements.Add(new TextEl
        {
            Text = c =>
            {
                if (IsIdle(c)) return "—";
                bool fgLoaded = c.Metrics.Value(MetricNames.DlssFgPresent) > 0;
                double ratio = FgMult(c);
                if (ratio > 1.15) return $"{ValueFormat.Fixed(ratio, 1)}×";
                return fgLoaded ? "loaded · 1.0×" : "off";
            },
            ColorFn = c => !IsIdle(c) && FgMult(c) > 1.15 ? "activeTitle" : c.Color("framegen", "text2"),
            VisibleWhen = c => c.Shows("framegen"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // PCL sparkline (autoscaled), time-bucketed over graph.historyS
        p.Elements.Add(new GraphEl
        {
            Advance = 4,
            BgColor = "emptyBar",
            Start = GraphStart.Left,
            H = ctx.GraphHeight,
            HistoryS = ctx.GraphHistoryS,
            Style = ctx.GraphStyle,
            VisibleWhen = c => c.Graphs("pclat"),
            Series =
            {
                new GraphSeries
                {
                    Color = ctx.Color("pclat", "histogram"),
                    Ring = ctx.NewRing(),
                    FixedMax = null,
                    Sample = c => PcLatency(c),
                },
            },
        });

        return p;
    }

    private static bool IsIdle(PanelContext c) => !c.Metrics.TryValue(MetricNames.FpsPresented, out _, maxAgeS: 3);

    private static double Comp(PanelContext c, string metric)
        => c.Metrics.TryValue(metric, out double v, maxAgeS: 3) ? v : 0;

    /// <summary>Overlay-equivalent PC latency = queue wait + render + display, summed by the
    /// collector and published as <c>latency.pc.ms</c> (ticket 02). The panel used to add the
    /// three components up itself, which meant two places could disagree about what "PC LAT"
    /// means and only this one applied a staleness rule.</summary>
    private static double PcLatency(PanelContext c)
        => c.Metrics.TryValue(MetricNames.LatencyPcMs, out double v, maxAgeS: 3) ? v : 0;

    /// <summary>Frame-gen multiplier = displayed rate ÷ true rendered (pre-FG) rate from PCL
    /// simulation markers; falls back to PresentMon's sim-pacing ratio when render rate absent.</summary>
    private static double FgMult(PanelContext c)
    {
        double displayed = c.Metrics.Value(MetricNames.FpsDisplayed);
        if (c.Metrics.TryValue(MetricNames.RenderRateHz, out double render, maxAgeS: 3) && render > 1 && displayed > 1)
            return Math.Clamp(displayed / render, 0.25, 8);
        return c.Metrics.Value(MetricNames.FpsFgRatio);
    }
}
