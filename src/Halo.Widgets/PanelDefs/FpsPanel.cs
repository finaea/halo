using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// FPS / frame-pipeline HUD, rebuilt from tools\extracted\gpu2-fps.json and extended per plan §7.
/// Instantiated twice via Options["stream"] = "presented" | "displayed": identical layout, the
/// metric stream differs (separate Presented and Displayed windows).
///
/// Keeps the original skin's bones — centered title band, centered focal FPS number (warn-colored
/// off the leftover 30/60/90/120 thresholds), "Framerate: N%" row + 1px bar, "1p LOW" row with the
/// signature backing plate, "FRAMETIME" row, and the 188×25 cyan sparkline (50ms hard cap) — and
/// adds the plan's extensions: 0.1% low, worst-frametime, PC-latency and DLSS rows, refresh-relative
/// percent, per-frame graph sampling, and an idle "NO 3D APP" state.
/// </summary>
public static class FpsPanel
{
    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();
        var t = ctx.Theme;

        string stream = ctx.Option("stream");
        bool presented = stream == "presented";

        string fpsMetric = presented ? MetricNames.FpsPresented : MetricNames.FpsDisplayed;
        string low1 = presented ? MetricNames.FpsLow1Presented : MetricNames.FpsLow1Displayed;
        string low01 = presented ? MetricNames.FpsLow01Presented : MetricNames.FpsLow01Displayed;
        string ftMetric = presented ? MetricNames.FpsFrametimePresentedMs : MetricNames.FpsFrametimeDisplayedMs;
        string ftWorstMetric = presented ? MetricNames.FpsFrametimePresentedWorstMs : MetricNames.FpsFrametimeDisplayedWorstMs;

        // ---- title band: centered title + right-slot status chip ----
        p.TitleElements.Add(new TextEl
        {
            Text = c => c.TitleOr(presented ? "FPS COUNTER (PRESENTED)" : "FPS COUNTER (DISPLAYED)"),
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
        });
        // ---- app-name row: full first line, one line only, ellipsis-clipped (never wraps);
        // idle it reads "NO 3D APP" (plan §7 dimmed idle panel) ----
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? "NO 3D APP" : c.Metrics.Text(MetricNames.FpsAppName),
            ColorFn = c => IsIdle(c) ? "inactiveButton" : c.Color("app", "text2"),
            VisibleWhen = c => c.Shows("app"),
            Style = TextStyle.Text8,
            Align = TextAlign.Left,
            WidthClip = ctx.Theme.ContentWidth,
            AbsY = 30,
            FixedH = 11,
        });

        // ---- "Framerate: <N>FPS … N%" row (FPS number beside the label), + 1px usage bar ----
        p.Elements.Add(new TextEl { Text = c => c.Label("fps", "Framerate:"), VisibleWhen = c => c.Shows("fps"), Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", AbsY = 42, FixedH = 11 });
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? "—" : $"{ValueFormat.Int0(c.Metrics.Value(fpsMetric))}FPS",
            VisibleWhen = c => c.Shows("fps"),
            // same five-stage ramp as the temperature rows, but the meaning is reversed here:
            // the "hottest" colour marks the highest framerate, not the worst reading
            ColorFn = c => IsIdle(c) ? "inactiveButton" : CpuRamPanelImpl.WarnColor(c.Metrics.Value(fpsMetric), c.Warn("fps")),
            Style = TextStyle.Bold8,
            Align = TextAlign.Center,     // centered between "Framerate:" and the % value
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? "—" : $"{ValueFormat.Int0(RefreshPct(c, fpsMetric))}%",
            VisibleWhen = c => c.Shows("fps"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            Color = "text",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => IsIdle(c) ? 0 : c.Metrics.Value(fpsMetric) / Math.Max(1, c.Metrics.Value(MetricNames.FpsRefreshHz)),
            VisibleWhen = c => c.Shows("fps"),
            FillColorFn = c => c.Metrics.Value(fpsMetric) > 75 ? "barWarn" : c.Color("fps", "gpuUsage"),
            BgColor = "emptyBar",
            Advance = 0,
        });

        // ---- lows row: "1p LOW: NFPS" (signature backing plate) | "0.1p: NFPS" ----
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? $"{c.Label("low1", "1p LOW:")} —" : $"{c.Label("low1", "1p LOW:")} {ValueFormat.Int0(c.Metrics.Value(low1))}FPS",
            VisibleWhen = c => c.Shows("low1"),
            Style = TextStyle.Text8,
            Align = TextAlign.Left,
            ColorFn = c => c.Color("low1", "text2"),
            SolidColor = "solidLabel",
            SolidW = t.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = 2,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? $"{c.Label("low01", "0.1p:")} —" : $"{c.Label("low01", "0.1p:")} {ValueFormat.Int0(c.Metrics.Value(low01))}FPS",
            VisibleWhen = c => c.Shows("low01"),
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            ColorFn = c => c.Color("low01", "text2"),
            SameRow = true,
            FixedH = 11,
        });

        // ---- frametime row: "FRAMETIME: N.Nms" | "WORST: N.Nms" (white pill like the lows row) ----
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? $"{c.Label("frametime", "FRAMETIME:")} —" : $"{c.Label("frametime", "FRAMETIME:")} {ValueFormat.Fixed(c.Metrics.Value(ftMetric), 1)}ms",
            VisibleWhen = c => c.Shows("frametime"),
            Style = TextStyle.Text8,
            Align = TextAlign.Left,
            ColorFn = c => c.Color("frametime", "text2"),
            SolidColor = "solidLabel",
            SolidW = t.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = 1,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => IsIdle(c) ? $"{c.Label("worst", "WORST:")} —" : $"{c.Label("worst", "WORST:")} {ValueFormat.Fixed(c.Metrics.Value(ftWorstMetric), 1)}ms",
            VisibleWhen = c => c.Shows("worst"),
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            ColorFn = c => c.Color("worst", "text2"),
            SameRow = true,
            FixedH = 11,
        });
        // (game name intentionally not rendered on this row — it collided with the
        // FRAMETIME/WORST texts; the original skin showed no app name either)

        // (PC latency rows moved to the dedicated LATENCY / DLSS panel, 2026-07-19)

        // ---- DLSS badge row (only when any DLSS module is present) ----
        p.Elements.Add(new TextEl
        {
            Text = DlssText,
            VisibleWhen = c => c.Shows("dlss") && !IsIdle(c) && DlssActive(c),
            Style = TextStyle.Text8,
            Align = TextAlign.Left,
            ColorFn = c => c.Color("dlss", "text2"),
            FixedH = 11,
            Advance = 1,
        });

        // ---- per-frame frametime sparkline (cyan, 50ms hard cap), fed from the shared frame ring.
        // Frame graphs stay one bar per frame: their x axis is frames, not seconds (R4). ----
        p.Elements.Add(new GraphEl
        {
            Advance = 4,
            BgColor = "emptyBar",
            Start = GraphStart.Left,
            H = ctx.GraphHeight,
            VisibleWhen = c => c.Graphs("frametime"),
            FrameSample = f => presented ? f.FrametimeMs : f.DisplayedFtMs,
            FrameDisplayedOnly = !presented,
            // lane selection: the presented graph rides the door-1 tap when it's live (falling
            // back to resolved frames otherwise); the displayed graph is always resolved-lane
            FrameFilter = presented
                ? (c, f) => c.Metrics.Value(MetricNames.FpsTapActive) >= 1
                    ? (f.Flags & (uint)FrameFlags.Provisional) != 0
                    : (f.Flags & (uint)FrameFlags.Provisional) == 0
                : (c, f) => (f.Flags & (uint)FrameFlags.Provisional) == 0,
            Series =
            {
                new GraphSeries
                {
                    Color = ctx.Color("frametime", "gpuFan"),  // JSON frametime line = GpuFanColor 0,191,255 (cyan)
                    Ring = new SampleRing(188),                // one slot per drawn frame bar
                    FixedMax = 50,                             // JSON MaxValue=50 clip
                    Sample = _ => 0,                           // unused: frame-driven graph pulls from FrameSample
                },
            },
        });

        return p;
    }

    /// <summary>Idle = no fresh presented-FPS sample (no 3D app rendering). Plan §7.</summary>
    private static bool IsIdle(PanelContext c) => !c.Metrics.TryValue(MetricNames.FpsPresented, out _, maxAgeS: 3);

    /// <summary>FPS as a percent of the active monitor refresh (replaces the skin's hardcoded /144).</summary>
    private static double RefreshPct(PanelContext c, string fpsMetric)
        => c.Metrics.Value(fpsMetric) / Math.Max(1, c.Metrics.Value(MetricNames.FpsRefreshHz)) * 100;

    private static bool DlssActive(PanelContext c)
        => c.Metrics.Value(MetricNames.DlssSrPresent) == 1
        || c.Metrics.Value(MetricNames.DlssFgPresent) == 1
        || c.Metrics.Value(MetricNames.DlssRrPresent) == 1;

    private static string DlssText(PanelContext c)
    {
        bool sr = c.Metrics.Value(MetricNames.DlssSrPresent) == 1;
        bool fg = c.Metrics.Value(MetricNames.DlssFgPresent) == 1;
        bool rr = c.Metrics.Value(MetricNames.DlssRrPresent) == 1;
        string s = $"DLSS {c.Metrics.Text(MetricNames.DlssVersion)} ";
        if (sr) s += "SR";
        if (fg) s += " FG";
        if (rr) s += " RR";
        if (fg) s += $" · FG {ValueFormat.Fixed(c.Metrics.Value(MetricNames.FpsFgRatio), 1)}×";
        return s;
    }
}
