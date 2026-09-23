using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// GPU panel per tools\extracted\gpu1.json: temp row (staged warn colors), GPU usage row + bar,
/// VRAM row + bar, FAN row + bar, CORE/MEM clock chip row, and the 4-series overlay graph
/// (temp/usage/VRAM%/fan%).
///
/// Bound to one device by the "gpuIndex" option: two cards = two widgets, no code change
/// (hardware plan H2). The temp row hides itself when its metric never registers; the other rows
/// read N/A, which is how a card exposing only a subset (AMD/Intel through LHM) still renders.
/// </summary>
public static class GpuPanel
{
    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();
        int gpu = ctx.OptionInt("gpuIndex", 0);
        string temp = MetricNames.GpuTempC(gpu);
        string usage = MetricNames.GpuUsagePct(gpu);
        string vramUsed = MetricNames.GpuVramUsedMb(gpu);
        string vramTotal = MetricNames.GpuVramTotalMb(gpu);
        string vramPct = MetricNames.GpuVramPct(gpu);
        string fanRpm = MetricNames.GpuFanRpm(gpu);
        string fanPct = MetricNames.GpuFanPct(gpu);
        string clockCore = MetricNames.GpuClockCoreMhz(gpu);
        string clockMem = MetricNames.GpuClockMemMhz(gpu);

        p.TitleElements.Add(new TextEl
        {
            Text = c => c.TitleOr(c.Metrics.Text(MetricNames.GpuName(gpu), "GPU")),
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
        });

        // temp, centered at abs Y=30, staged warn colors
        p.Elements.Add(new TextEl
        {
            Text = c => c.Na(temp, c.TempText),
            // Muted when absent rather than devWarn1 — see the same rule on the CPU temp row.
            ColorFn = c => c.Metrics.TryValue(temp, out double t)
                ? CpuRamPanelImpl.WarnColor(t, c.Warn("temp")) : "text2",
            // Gated on registration, not on a readable value: a card that never reports a
            // temperature has no row, one whose sensor stopped answering reads N/A in place.
            VisibleWhen = c => c.Shows("temp") && c.Metrics.Has(temp),
            Style = TextStyle.Bold8,
            Align = TextAlign.Center,
            AbsY = 30,
            FixedH = 11,
        });

        // GPU usage row: label left, % right, full bar under
        p.Elements.Add(new TextEl { Text = c => c.Label("usage", "GPU:"), VisibleWhen = c => c.Shows("usage"), Style = TextStyle.Bold8, Align = TextAlign.Left, AbsY = 32, FixedH = 11 });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Na(usage, v => $"{ValueFormat.Int0(v)}%"),
            VisibleWhen = c => c.Shows("usage"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => c.Metrics.Value(usage) / 100,
            VisibleWhen = c => c.Shows("usage"),
            FillColorFn = c => CpuRamPanelImpl.Over(c.Metrics.Value(usage), c.Warn("usage")) ? "barWarn" : c.Color("usage", "gpuUsage"),
            Advance = 0,
        });

        // VRAM row: label left, used/total center, % right, bar under
        p.Elements.Add(new TextEl { Text = c => c.Label("vram", "MEM:"), VisibleWhen = c => c.Shows("vram"), Style = TextStyle.Bold8, Align = TextAlign.Left, FixedH = 11, Advance = 1 });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Na(vramUsed, vramTotal, (u, t) => $"{ValueFormat.Int0(u)}MB/{ValueFormat.Int0(t)}MB"),
            VisibleWhen = c => c.Shows("vram"),
            Style = TextStyle.Text8,
            Color = "text2",
            Align = TextAlign.Center,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Na(vramPct, v => $"{ValueFormat.Int0(v)}%"),
            VisibleWhen = c => c.Shows("vram"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => c.Metrics.Value(vramPct) / 100,
            VisibleWhen = c => c.Shows("vram"),
            FillColorFn = c => CpuRamPanelImpl.Over(c.Metrics.Value(vramPct), c.Warn("vram")) ? "barWarn" : c.Color("vram", "gpuMemUsage"),
            Advance = 0,
        });

        // FAN row: label left, rpm center, % right, bar under (no gap after VRAM bar, per skin)
        p.Elements.Add(new TextEl { Text = c => c.Label("fan", "FAN:"), VisibleWhen = c => c.Shows("fan"), Style = TextStyle.Bold8, Align = TextAlign.Left, FixedH = 11, Advance = 0 });
        p.Elements.Add(new TextEl
        {
            // 0 rpm is a real reading — zero-RPM / fan-stop mode — and shows as "0 rpm".
            // Only an unreadable sensor reads N/A.
            Text = c => c.Na(fanRpm, v => $"{ValueFormat.Int0(v)} rpm"),
            VisibleWhen = c => c.Shows("fan"),
            Style = TextStyle.Text8,
            Color = "text2",
            Align = TextAlign.Center,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Na(fanPct, v => $"{ValueFormat.Int0(v)}%"),
            VisibleWhen = c => c.Shows("fan"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => c.Metrics.Value(fanPct) / 100,
            VisibleWhen = c => c.Shows("fan"),
            FillColorFn = c => CpuRamPanelImpl.Over(c.Metrics.Value(fanPct), c.Warn("fan")) ? "barWarn" : c.Color("fan", "gpuFan"),
            Advance = 0,
        });

        // CORE / MEM clock chip row
        p.Elements.Add(new TextEl
        {
            Text = c => $"{c.Label("clockCore", "CORE:")} {c.Na(clockCore, v => $"{ValueFormat.Int0(v)}MHz")}",
            VisibleWhen = c => c.Shows("clockCore"),
            Style = TextStyle.Text8,
            ColorFn = c => c.Color("clockCore", "text2"),
            Align = TextAlign.Left,
            SolidColor = "solidLabel",
            SolidW = ctx.Theme.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = 2,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{c.Label("clockMem", "MEM:")} {c.Na(clockMem, v => $"{ValueFormat.Int0(v)}MHz")}",
            VisibleWhen = c => c.Shows("clockMem"),
            Style = TextStyle.Text8,
            ColorFn = c => c.Color("clockMem", "text2"),
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // 4-series overlay graph (temp red / usage lavender / VRAM% green / fan% sky-blue),
        // one sample per tick drawn as time buckets over graph.historyS (R4).
        var graph = new GraphEl
        {
            Advance = 4,
            BgColor = "emptyBar",
            Start = GraphStart.Left,
            H = ctx.GraphHeight,
            HistoryS = ctx.GraphHistoryS,
            Style = ctx.GraphStyle,
        };
        // NaSample: an absent reading skips the sample rather than drawing a zero.
        if (ctx.Graphs("temp"))
            graph.Series.Add(new GraphSeries { Color = ctx.Color("temp", "gpuTemp"), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.NaSample(temp) });
        if (ctx.Graphs("usage"))
            graph.Series.Add(new GraphSeries { Color = ctx.Color("usage", "gpuUsage"), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.NaSample(usage) });
        if (ctx.Graphs("vram"))
            graph.Series.Add(new GraphSeries { Color = ctx.Color("vram", "gpuMemUsage"), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.NaSample(vramPct) });
        if (ctx.Graphs("fan"))
            graph.Series.Add(new GraphSeries { Color = ctx.Color("fan", "gpuFan"), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.NaSample(fanPct) });
        p.Elements.Add(graph);

        return p;
    }
}
