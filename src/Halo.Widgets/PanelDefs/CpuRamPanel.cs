using Halo.Shared.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// CPU/RAM panel per tools\extracted\cpu-ram.json: temp row (warn-colored), CPU total row +
/// bar, 20 core rows at exact 12-unit pitch (1-12 P-cores, 13-20 E-cores), Clock/FAN chip row,
/// RAM row + bar, and the 3-series overlay graph (temp/usage/RAM).
/// </summary>
public static class CpuRamPanelImpl
{
    private const int Cores = 20;

    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();
        var t = ctx.Theme;

        p.TitleElements.Add(new TextEl
        {
            Text = c => c.Options.GetValueOrDefault("title", "").Length > 0
                ? c.Options["title"]
                : c.Metrics.Text(MetricNames.CpuName, "CPU"),
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
        });

        // temp, centered at abs Y=30, staged warn colors (<50/<60/<70/<80/else)
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(MetricNames.CpuPackageTempC))}°C",
            ColorFn = c => WarnColor(c.Metrics.Value(MetricNames.CpuPackageTempC), 50, 60, 70, 80),
            VisibleWhen = c => c.Metrics.TryValue(MetricNames.CpuPackageTempC, out _),
            Style = TextStyle.Bold8,
            Align = TextAlign.Center,
            AbsY = 30,
            FixedH = 11,
        });

        // CPU total row: label left, % right, full bar under
        p.Elements.Add(new TextEl { Text = _ => "CPU:", Style = TextStyle.Bold8, Align = TextAlign.Left, AbsY = 32, FixedH = 11 });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Fixed(c.Metrics.Value(MetricNames.CpuTotalPct), 1)}%",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => c.Metrics.Value(MetricNames.CpuTotalPct) / 100,
            FillColorFn = c => c.Metrics.Value(MetricNames.CpuTotalPct) > 75 ? "barWarn" : "cpuUsage",
            Advance = 0,
        });

        // 20 core rows, 12-unit pitch (P-cores logical 0-11 = rows 1-12, E-cores 12-19 = rows 13-20)
        for (int i = 0; i < Cores; i++)
        {
            int core = i;
            p.Elements.Add(new TextEl
            {
                Text = _ => $"Core {core + 1}:",
                Style = TextStyle.Text8,
                Color = "text2",
                Align = TextAlign.Left,
                FixedH = 11,
                Advance = i == 0 ? -1 : 1,   // first row rides right on the CPU bar (row-adjustor)
            });
            p.Elements.Add(new TextEl
            {
                Text = c => $"{ValueFormat.Fixed(c.Metrics.Value(MetricNames.CpuCorePct(core)), 1)}%",
                Style = TextStyle.Text8,
                Color = "text2",
                Align = TextAlign.Right,
                SameRow = true,
                FixedH = 11,
            });
            // bar sits 7 units under the row start, inset (X=47 W=120), pitch stays 12
            p.Elements.Add(new BarEl
            {
                Value = c => c.Metrics.Value(MetricNames.CpuCorePct(core)) / 100,
                FillColor = "bar",
                X = 47, W = 120, H = 1,
                SameRow = true, SameRowOffset = 7,
            });
        }

        // Clock / FAN chip row
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.TryValue(MetricNames.CpuClockMhz, out double mhz) && mhz > 0
                ? $"Clock: {ValueFormat.Int0(mhz)} MHz" : "Clock: N/A",
            Style = TextStyle.Text8,
            Color = "text2",
            Align = TextAlign.Left,
            SolidColor = "solidLabel",
            SolidW = ctx.Theme.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = 1,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.TryValue(MetricNames.CpuFanRpm, out double rpm)
                ? $"FAN: {ValueFormat.Int0(rpm)}rpm" : "FAN: N/A",
            Style = TextStyle.Text8,
            Color = "text2",
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // RAM row: label left, used/total center, % right, bar under
        p.Elements.Add(new TextEl { Text = _ => "RAM:", Style = TextStyle.Bold8, Align = TextAlign.Left, FixedH = 11, Advance = 2 });
        p.Elements.Add(new TextEl
        {
            Text = c =>
            {
                double used = c.Metrics.Value(MetricNames.RamUsedGb) * 1073741824;
                double total = c.Metrics.Value(MetricNames.RamTotalGb) * 1073741824;
                return $"{ValueFormat.AutoScale(used)}B/{ValueFormat.AutoScale(total)}B";
            },
            Style = TextStyle.Bold8,
            Align = TextAlign.Center,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(MetricNames.RamPct))}%",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => c.Metrics.Value(MetricNames.RamPct) / 100,
            FillColorFn = c => c.Metrics.Value(MetricNames.RamPct) > 75 ? "barWarn" : "ramUsage",
            Advance = 1,
        });

        // 3-series overlay graph (temp red / usage lavender / RAM green), 5 Hz sampling.
        // Each line is toggleable via Options graphCpuTemp/graphCpuUsage/graphRamUsage (default on).
        var graph = new GraphEl
        {
            Advance = 4,
            BgColor = "emptyBar",
            Start = GraphStart.Left,
            SampleRateHz = 5,
        };
        if (ctx.GraphLineVisible("graphCpuTemp"))
            graph.Series.Add(new GraphSeries { Color = "cpuTemp", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(MetricNames.CpuPackageTempC) });
        if (ctx.GraphLineVisible("graphCpuUsage"))
            graph.Series.Add(new GraphSeries { Color = "cpuUsage", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(MetricNames.CpuTotalPct) });
        if (ctx.GraphLineVisible("graphRamUsage"))
            graph.Series.Add(new GraphSeries { Color = "ramUsage", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(MetricNames.RamPct) });
        p.Elements.Add(graph);

        return p;
    }

    /// <summary>Staged device warn colors (DevTempWarnColorTh1..5).</summary>
    internal static string WarnColor(double v, double t1, double t2, double t3, double t4)
        => v < t1 ? "devWarn1" : v < t2 ? "devWarn2" : v < t3 ? "devWarn3" : v < t4 ? "devWarn4" : "devWarn5";
}
