using Halo.Shared.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// GPU panel per tools\extracted\gpu1.json: temp row (staged warn colors), GPU usage row + bar,
/// VRAM row + bar, FAN row + bar, CORE/MEM clock chip row, and the 4-series overlay graph
/// (temp/usage/VRAM%/fan%).
/// </summary>
public static class GpuPanel
{
    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();
        var t = ctx.Theme;

        p.TitleElements.Add(new TextEl
        {
            Text = c => c.Options.GetValueOrDefault("title", "").Length > 0
                ? c.Options["title"]
                : c.Metrics.Text(MetricNames.GpuName, "GPU"),
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
        });

        // temp, centered at abs Y=30, staged warn colors (<45/<55/<65/<75/else)
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(MetricNames.GpuTempC))}°C",
            ColorFn = c => CpuRamPanelImpl.WarnColor(c.Metrics.Value(MetricNames.GpuTempC), 45, 55, 65, 75),
            VisibleWhen = c => c.Metrics.TryValue(MetricNames.GpuTempC, out _),
            Style = TextStyle.Bold8,
            Align = TextAlign.Center,
            AbsY = 30,
            FixedH = 11,
        });

        // GPU usage row: label left, % right, full bar under
        p.Elements.Add(new TextEl { Text = _ => "GPU:", Style = TextStyle.Bold8, Align = TextAlign.Left, AbsY = 32, FixedH = 11 });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(MetricNames.GpuUsagePct))}%",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => c.Metrics.Value(MetricNames.GpuUsagePct) / 100,
            FillColorFn = c => c.Metrics.Value(MetricNames.GpuUsagePct) > 75 ? "barWarn" : "gpuUsage",
            Advance = 0,
        });

        // VRAM row: label left, used/total center, % right, bar under
        p.Elements.Add(new TextEl { Text = _ => "MEM:", Style = TextStyle.Bold8, Align = TextAlign.Left, FixedH = 11, Advance = 1 });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(MetricNames.GpuVramUsedMb))}MB/{ValueFormat.Int0(c.Metrics.Value(MetricNames.GpuVramTotalMb))}MB",
            Style = TextStyle.Text8,
            Color = "text2",
            Align = TextAlign.Center,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(MetricNames.GpuVramPct))}%",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => c.Metrics.Value(MetricNames.GpuVramPct) / 100,
            FillColorFn = c => c.Metrics.Value(MetricNames.GpuVramPct) > 75 ? "barWarn" : "gpuMemUsage",
            Advance = 0,
        });

        // FAN row: label left, rpm center, % right, bar under (no gap after VRAM bar, per skin)
        p.Elements.Add(new TextEl { Text = _ => "FAN:", Style = TextStyle.Bold8, Align = TextAlign.Left, FixedH = 11, Advance = 0 });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(MetricNames.GpuFanRpm))} rpm",
            Style = TextStyle.Text8,
            Color = "text2",
            Align = TextAlign.Center,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(MetricNames.GpuFanPct))}%",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => c.Metrics.Value(MetricNames.GpuFanPct) / 100,
            FillColorFn = c => c.Metrics.Value(MetricNames.GpuFanPct) > 75 ? "barWarn" : "gpuFan",
            Advance = 0,
        });

        // CORE / MEM clock chip row
        p.Elements.Add(new TextEl
        {
            Text = c => $"CORE: {ValueFormat.Int0(c.Metrics.Value(MetricNames.GpuClockCoreMhz))}MHz",
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
            Text = c => $"MEM: {ValueFormat.Int0(c.Metrics.Value(MetricNames.GpuClockMemMhz))}MHz",
            Style = TextStyle.Text8,
            Color = "text2",
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // 4-series overlay graph (temp red / usage lavender / VRAM% green / fan% sky-blue), 1 Hz sampling.
        // Each line is toggleable via Options graphGpuTemp/graphGpuUsage/graphGpuMem/graphGpuFan (default on).
        var graph = new GraphEl
        {
            Advance = 4,
            BgColor = "emptyBar",
            Start = GraphStart.Left,
            SampleRateHz = 1,
        };
        if (ctx.GraphLineVisible("graphGpuTemp"))
            graph.Series.Add(new GraphSeries { Color = "gpuTemp", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(MetricNames.GpuTempC) });
        if (ctx.GraphLineVisible("graphGpuUsage"))
            graph.Series.Add(new GraphSeries { Color = "gpuUsage", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(MetricNames.GpuUsagePct) });
        if (ctx.GraphLineVisible("graphGpuMem"))
            graph.Series.Add(new GraphSeries { Color = "gpuMemUsage", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(MetricNames.GpuVramPct) });
        if (ctx.GraphLineVisible("graphGpuFan"))
            graph.Series.Add(new GraphSeries { Color = "gpuFan", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(MetricNames.GpuFanPct) });
        p.Elements.Add(graph);

        return p;
    }
}
