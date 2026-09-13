using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// GPU panel per tools\extracted\gpu1.json: temp row (staged warn colors), GPU usage row + bar,
/// VRAM row + bar, FAN row + bar, CORE/MEM clock chip row, and the 4-series overlay graph
/// (temp/usage/VRAM%/fan%).
///
/// Bound to one device by the "gpuIndex" option: two cards = two widgets, no code change
/// (hardware plan H2). Rows whose metric never arrives hide themselves, which is how a card
/// exposing only a subset (AMD/Intel through LHM) still renders cleanly.
/// </summary>
public static class GpuPanel
{
    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();
        var t = ctx.Theme;

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
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(temp))}°C",
            ColorFn = c => CpuRamPanelImpl.WarnColor(c.Metrics.Value(temp), c.Warn("temp")),
            VisibleWhen = c => c.Shows("temp") && c.Metrics.TryValue(temp, out _),
            Style = TextStyle.Bold8,
            Align = TextAlign.Center,
            AbsY = 30,
            FixedH = 11,
        });

        // GPU usage row: label left, % right, full bar under
        p.Elements.Add(new TextEl { Text = c => c.Label("usage", "GPU:"), Style = TextStyle.Bold8, Align = TextAlign.Left, AbsY = 32, FixedH = 11 });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(usage))}%",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => c.Metrics.Value(usage) / 100,
            FillColorFn = c => CpuRamPanelImpl.Over(c.Metrics.Value(usage), c.Warn("usage")) ? "barWarn" : "gpuUsage",
            Advance = 0,
        });

        // VRAM row: label left, used/total center, % right, bar under
        p.Elements.Add(new TextEl { Text = c => c.Label("vram", "MEM:"), Style = TextStyle.Bold8, Align = TextAlign.Left, FixedH = 11, Advance = 1 });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(vramUsed))}MB/{ValueFormat.Int0(c.Metrics.Value(vramTotal))}MB",
            Style = TextStyle.Text8,
            Color = "text2",
            Align = TextAlign.Center,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(vramPct))}%",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => c.Metrics.Value(vramPct) / 100,
            FillColorFn = c => CpuRamPanelImpl.Over(c.Metrics.Value(vramPct), c.Warn("vram")) ? "barWarn" : "gpuMemUsage",
            Advance = 0,
        });

        // FAN row: label left, rpm center, % right, bar under (no gap after VRAM bar, per skin)
        p.Elements.Add(new TextEl { Text = c => c.Label("fan", "FAN:"), Style = TextStyle.Bold8, Align = TextAlign.Left, FixedH = 11, Advance = 0 });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(fanRpm))} rpm",
            Style = TextStyle.Text8,
            Color = "text2",
            Align = TextAlign.Center,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(fanPct))}%",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => c.Metrics.Value(fanPct) / 100,
            FillColorFn = c => CpuRamPanelImpl.Over(c.Metrics.Value(fanPct), c.Warn("fan")) ? "barWarn" : "gpuFan",
            Advance = 0,
        });

        // CORE / MEM clock chip row
        p.Elements.Add(new TextEl
        {
            Text = c => $"{c.Label("clockCore", "CORE:")} {ValueFormat.Int0(c.Metrics.Value(clockCore))}MHz",
            Style = TextStyle.Text8,
            Color = "text2",
            Align = TextAlign.Left,
            SolidColor = "solidLabel",
            SolidW = ctx.Theme.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = 2,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{c.Label("clockMem", "MEM:")} {ValueFormat.Int0(c.Metrics.Value(clockMem))}MHz",
            Style = TextStyle.Text8,
            Color = "text2",
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // 4-series overlay graph (temp red / usage lavender / VRAM% green / fan% sky-blue), 5 Hz.
        var graph = new GraphEl
        {
            Advance = 4,
            BgColor = "emptyBar",
            Start = GraphStart.Left,
            SampleRateHz = 5,
        };
        if (ctx.Graphs("temp"))
            graph.Series.Add(new GraphSeries { Color = "gpuTemp", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(temp) });
        if (ctx.Graphs("usage"))
            graph.Series.Add(new GraphSeries { Color = "gpuUsage", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(usage) });
        if (ctx.Graphs("vram"))
            graph.Series.Add(new GraphSeries { Color = "gpuMemUsage", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(vramPct) });
        if (ctx.Graphs("fan"))
            graph.Series.Add(new GraphSeries { Color = "gpuFan", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(fanPct) });
        p.Elements.Add(graph);

        return p;
    }
}
