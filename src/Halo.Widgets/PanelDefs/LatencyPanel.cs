using Halo.Shared.Metrics;
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
            Text = c => c.Options.GetValueOrDefault("title", "").Length > 0 ? c.Options["title"] : "LATENCY / DLSS",
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
        });

        // ROW 1 — headline PC latency the overlay's way: continuous, ping/marker-based,
        // starting at ②a (input enters the game). = queue wait + render + display.
        p.Elements.Add(new TextEl { Text = _ => "PC LAT:", Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", AbsY = 30, FixedH = 11 });
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) || PcLatency(c) <= 0 ? "N/A" : $"{ValueFormat.Int0(PcLatency(c))}ms",
            ColorFn = c => !IsIdle(c) && PcLatency(c) > 0 ? CpuRamPanelImpl.WarnColor(PcLatency(c), 20, 35, 50, 70) : "inactiveButton",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // ROW 2 — the three components that sum to ROW 1 (queue + render + display), on a pill.
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? "QUEUE —" : $"QUEUE {ValueFormat.Int0(Comp(c, MetricNames.LatencyQueueMs))}",
            Style = TextStyle.Text8,
            Align = TextAlign.Left,
            Color = "text2",
            SolidColor = "solidLabel",
            SolidW = t.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = 2,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? "REND —" : $"REND {ValueFormat.Int0(Comp(c, MetricNames.LatencyRenderMs))}",
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            Color = "text2",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? "DISP —" : $"DISP {ValueFormat.Int0(Comp(c, MetricNames.FpsDisplayLatencyMs))}",
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            Color = "text2",
            SameRow = true,
            FixedH = 11,
        });

        // ROW 3 — PresentMon click-to-photon + input-to-photon references, on a pill.
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) || !c.Metrics.TryValue(MetricNames.LatencyClickMs, out double v, 10) ? "CLICK —" : $"CLICK {ValueFormat.Int0(v)}ms",
            Style = TextStyle.Text8,
            Align = TextAlign.Left,
            Color = "text2",
            SolidColor = "solidLabel",
            SolidW = t.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = 1,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? "INPUT —" : $"INPUT {ValueFormat.Int0(c.Metrics.Value(MetricNames.LatencyAllInputMs))}ms",
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            Color = "text2",
            SameRow = true,
            FixedH = 11,
        });

        // DLSS: version + loaded features
        p.Elements.Add(new TextEl { Text = _ => "DLSS:", Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", FixedH = 11, Advance = 2 });
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
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            Color = "text2",
            WidthClip = 130,
            SameRow = true,
            FixedH = 11,
        });

        // MODEL: Transformer/CNN + override-vs-game origin
        p.Elements.Add(new TextEl { Text = _ => "MODEL:", Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", FixedH = 11, Advance = 1 });
        p.Elements.Add(new TextEl
        {
            Text = c =>
            {
                if (IsIdle(c)) return "—";
                string m = c.Metrics.Text(MetricNames.DlssModel);
                return m.Length > 0 ? m : "—";
            },
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            Color = "text2",
            WidthClip = 130,
            SameRow = true,
            FixedH = 11,
        });

        // FRAME GEN: effective multiplier
        p.Elements.Add(new TextEl { Text = _ => "FRAME GEN:", Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", FixedH = 11, Advance = 1 });
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
            ColorFn = c => !IsIdle(c) && FgMult(c) > 1.15 ? "activeTitle" : "text2",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // PCL sparkline (autoscaled, 2 Hz)
        p.Elements.Add(new GraphEl
        {
            Advance = 4,
            BgColor = "emptyBar",
            Start = GraphStart.Left,
            SampleRateHz = 2,
            Series =
            {
                new GraphSeries
                {
                    Color = "histogram",
                    Ring = new HistoryRing(188),
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

    /// <summary>Overlay-equivalent PC latency, continuous over ping-tagged frames, starting at
    /// ②a = queue wait (input post→consume) + render (consume→present) + display (P2D).
    /// Requires at least the render component; queue/display add on when present.</summary>
    private static double PcLatency(PanelContext c)
    {
        if (!c.Metrics.TryValue(MetricNames.LatencyRenderMs, out double render, maxAgeS: 3) || render <= 0) return 0;
        return Comp(c, MetricNames.LatencyQueueMs) + render + Comp(c, MetricNames.FpsDisplayLatencyMs);
    }

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
