using Halo.Metrics;
using Halo.Widgets.PanelModels;
using Halo.Widgets.Render;

namespace Halo.Widgets.Skins.Rainformer;

/// <summary>
/// Dedicated latency + DLSS telemetry panel (user request 2026-07-19):
///   PC LAT (latency.pc.ms: PCL Stats markers + PresentMon's display segment) — headline, warn-colored
///   CLICK / INPUT photon latencies (moved here from the FPS panels)
///   SR / RR / FG rows: running + preset + mode, or loaded / off (NgxProvider, DlssRows)
///   FG MULT row: measured multiplier, displayed ÷ rendered (fps.fg.multiplier)
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
            ColorFn = c => !IsIdle(c) && PcLatency(c) > 0 ? PanelData.WarnColor(PcLatency(c), c.Warn("pclat")) : "inactiveButton",
            VisibleWhen = c => c.Shows("pclat"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // ROW 2 — the three components that sum to ROW 1 (queue + render + display), on a pill.
        p.Elements.Add(new TextEl
        {
            Text = c => $"{c.Label("queue", "QUEUE")} {Comp(c, MetricNames.LatencyQueueMs)}",
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
            Text = c => $"{c.Label("render", "REND")} {Comp(c, MetricNames.LatencyRenderMs)}",
            VisibleWhen = c => c.Shows("render"),
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            ColorFn = c => c.Color("render", "text2"),
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{c.Label("display", "DISP")} {Comp(c, MetricNames.FpsDisplayLatencyMs)}",
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
            // No widget-side age policy any more: the collector decides how long an input-photon
            // sample stays valid (ticket 02's 20 s window), and a long input-free stretch —
            // cutscene, menu, pure movement — legitimately reads N/A here. Idle still reads "—",
            // which is the panel-level "no 3D app" state and a different statement.
            Text = c => IsIdle(c)
                ? $"{c.Label("click", "CLICK")} —"
                : $"{c.Label("click", "CLICK")} {c.Na(MetricNames.LatencyClickMs, v => $"{ValueFormat.Int0(v)}ms")}",
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
            Text = c => IsIdle(c)
                ? $"{c.Label("input", "INPUT")} —"
                : $"{c.Label("input", "INPUT")} {c.Na(MetricNames.LatencyAllInputMs, v => $"{ValueFormat.Int0(v)}ms")}",
            VisibleWhen = c => c.Shows("input"),
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            ColorFn = c => c.Color("input", "text2"),
            SameRow = true,
            FixedH = 11,
        });

        // SR / RR / FG: running → "Preset D · Ultra Perf." in the active colour, else "loaded · 310.3.0"
        // or "off" (DlssRows decides the words for every skin). Preset and mode exist only while an
        // NVIDIA App override applies them.
        AddDlssRow(p, "sr", "SR:", c => DlssRows.Feature(c, "sr"), TextStyle.Text8, advance: 2);
        AddDlssRow(p, "rr", "RR:", c => DlssRows.Feature(c, "rr"), TextStyle.Text8, advance: 1);
        AddDlssRow(p, "fg", "FG:", c => DlssRows.Feature(c, "fg"), TextStyle.Text8, advance: 1);
        // FG MULT: the collector's measured displayed ÷ rendered (fps.fg.multiplier)
        AddDlssRow(p, "fgmult", "FG MULT:", DlssRows.Multiplier, TextStyle.Bold8, advance: 1);

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
                    // NaN skips the sample: no PCL reading holds the line rather than dropping it.
                    Sample = c => c.Metrics.TryValue(MetricNames.LatencyPcMs, out double v, maxAgeS: 3) ? v : double.NaN,
                },
            },
        });

        return p;
    }

    private static bool IsIdle(PanelContext c) => !c.Metrics.TryValue(MetricNames.FpsPresented, out _, maxAgeS: 3);

    /// <summary>
    /// One latency component, rendered. A component the collector is not publishing reads "—"
    /// (this panel's idle vocabulary) rather than a plausible <c>0</c> — the whole point of the
    /// freshness contract.
    ///
    /// The 3 s window is the panel's own idle rule, the same one <see cref="IsIdle"/> uses, not a
    /// per-metric age policy: these come from the frame pipeline, which republishes every poll
    /// while a 3D app is alive and simply stops when one is not.
    /// </summary>
    private static string Comp(PanelContext c, string metric)
        => !IsIdle(c) && c.Metrics.TryValue(metric, out double v, maxAgeS: 3) ? ValueFormat.Int0(v) : "—";

    /// <summary>Overlay-equivalent PC latency = queue wait + render + display, summed by the
    /// collector and published as <c>latency.pc.ms</c> (ticket 02). The panel used to add the
    /// three components up itself, which meant two places could disagree about what "PC LAT"
    /// means and only this one applied a staleness rule.</summary>
    private static double PcLatency(PanelContext c)
        => c.Metrics.TryValue(MetricNames.LatencyPcMs, out double v, maxAgeS: 3) ? v : 0;

    /// <summary>A label + right-aligned value pair. Running reads in <c>activeTitle</c>; "could not
    /// tell" in <c>inactiveButton</c> like PC LAT's N/A; everything else in the row's own colour.</summary>
    private static void AddDlssRow(Panel p, string key, string label, Func<PanelContext, DlssRow> row, TextStyle style, int advance)
    {
        p.Elements.Add(new TextEl { Text = c => c.Label(key, label), VisibleWhen = c => c.Shows(key), Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", FixedH = 11, Advance = advance });
        p.Elements.Add(new TextEl
        {
            Text = c => row(c).Text,
            ColorFn = c => row(c).Kind switch
            {
                DlssRowKind.Active => "activeTitle",
                DlssRowKind.Na => "inactiveButton",
                _ => c.Color(key, "text2"),
            },
            VisibleWhen = c => c.Shows(key),
            Style = style,
            Align = TextAlign.Right,
            WidthClip = 130,
            SameRow = true,
            FixedH = 11,
        });
    }
}
