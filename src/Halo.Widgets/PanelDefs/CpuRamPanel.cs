using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// CPU/RAM panel per tools\extracted\cpu-ram.json: temp row (warn-colored), CPU total row +
/// bar, one core row per logical CPU at exact 12-unit pitch, Clock/FAN chip row, RAM row + bar,
/// and the 3-series overlay graph (temp/usage/RAM).
///
/// The row count comes from cpu.logical.count, not from a constant — this panel has to fit a
/// 6-thread laptop and a 32-thread desktop. The column and physical-core layout rules
/// (hardware plan H3) land with the widgets ticket; today every logical CPU gets a row.
/// </summary>
public static class CpuRamPanelImpl
{
    /// <summary>Logical CPUs to draw. Falls back to this process's view when the collector is
    /// down, so the panel still has the right shape before the first publish.</summary>
    private static int CoreCount(PanelContext ctx)
        => (int)Math.Clamp(ctx.Metrics.Value(MetricNames.CpuLogicalCount, Environment.ProcessorCount), 1, 256);

    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();
        var t = ctx.Theme;
        int cores = CoreCount(ctx);
        // v1 hardcoded fan.0.rpm as "the CPU fan"; which header it is depends on the board.
        string cpuFanMetric = MetricNames.FanRpm(ctx.OptionInt("cpuFanChannel", 0));

        p.TitleElements.Add(new TextEl
        {
            Text = c => c.TitleOr(c.Metrics.Text(MetricNames.CpuName, "CPU")),
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
        });

        // temp, centered at abs Y=30, staged warn colors (thresholds from the metric settings)
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(MetricNames.CpuPackageTempC))}°C",
            ColorFn = c => WarnColor(c.Metrics.Value(MetricNames.CpuPackageTempC), c.Warn("temp")),
            VisibleWhen = c => c.Shows("temp") && c.Metrics.TryValue(MetricNames.CpuPackageTempC, out _),
            Style = TextStyle.Bold8,
            Align = TextAlign.Center,
            AbsY = 30,
            FixedH = 11,
        });

        // CPU total row: label left, % right, full bar under
        p.Elements.Add(new TextEl { Text = c => c.Label("usage", "CPU:"), Style = TextStyle.Bold8, Align = TextAlign.Left, AbsY = 32, FixedH = 11 });
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
            FillColorFn = c => Over(c.Metrics.Value(MetricNames.CpuTotalPct), c.Warn("usage")) ? "barWarn" : "cpuUsage",
            Advance = 0,
        });

        // one row per logical CPU, 12-unit pitch
        for (int i = 0; i < cores; i++)
        {
            int core = i;
            p.Elements.Add(new TextEl
            {
                Text = c => c.Label($"cores.{core}", "Core {n}:").Replace("{n}", (core + 1).ToString()),
                VisibleWhen = c => c.Shows("cores"),
                Style = TextStyle.Text8,
                Color = "text2",
                Align = TextAlign.Left,
                FixedH = 11,
                Advance = i == 0 ? -1 : 1,   // first row rides right on the CPU bar (row-adjustor)
            });
            p.Elements.Add(new TextEl
            {
                Text = c => $"{ValueFormat.Fixed(c.Metrics.Value(MetricNames.CpuCorePct(core)), 1)}%",
                VisibleWhen = c => c.Shows("cores"),
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
                VisibleWhen = c => c.Shows("cores"),
                FillColor = "bar",
                X = 47, W = 120, H = 1,
                SameRow = true, SameRowOffset = 7,
            });
        }

        // Clock / FAN chip row
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.TryValue(MetricNames.CpuClockMhz, out double mhz) && mhz > 0
                ? $"{c.Label("clock", "Clock:")} {ValueFormat.Int0(mhz)} MHz"
                : $"{c.Label("clock", "Clock:")} N/A",
            VisibleWhen = c => c.Shows("clock"),
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
            Text = c => c.Metrics.TryValue(cpuFanMetric, out double rpm)
                ? $"{c.Label("fan", "FAN:")} {ValueFormat.Int0(rpm)}rpm"
                : $"{c.Label("fan", "FAN:")} N/A",
            VisibleWhen = c => c.Shows("fan"),
            Style = TextStyle.Text8,
            Color = "text2",
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // RAM row: label left, used/total center, % right, bar under
        p.Elements.Add(new TextEl { Text = c => c.Label("ram", "RAM:"), Style = TextStyle.Bold8, Align = TextAlign.Left, FixedH = 11, Advance = 2 });
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
            FillColorFn = c => Over(c.Metrics.Value(MetricNames.RamPct), c.Warn("ram")) ? "barWarn" : "ramUsage",
            Advance = 1,
        });

        // 3-series overlay graph (temp red / usage lavender / RAM green), 5 Hz sampling.
        // Each line is toggled by its own metric setting (metrics.temp.graph, …).
        var graph = new GraphEl
        {
            Advance = 4,
            BgColor = "emptyBar",
            Start = GraphStart.Left,
            SampleRateHz = 5,
        };
        if (ctx.Graphs("temp"))
            graph.Series.Add(new GraphSeries { Color = "cpuTemp", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(MetricNames.CpuPackageTempC) });
        if (ctx.Graphs("usage"))
            graph.Series.Add(new GraphSeries { Color = "cpuUsage", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(MetricNames.CpuTotalPct) });
        if (ctx.Graphs("ram"))
            graph.Series.Add(new GraphSeries { Color = "ramUsage", Ring = new HistoryRing(188), FixedMax = 100, Sample = c => c.Metrics.Value(MetricNames.RamPct) });
        p.Elements.Add(graph);

        return p;
    }

    /// <summary>Staged device warn colors (DevTempWarnColorTh1..5) from a threshold list.</summary>
    internal static string WarnColor(double v, IReadOnlyList<double> thresholds)
    {
        if (thresholds.Count < 4) return "devWarn1";
        return v < thresholds[0] ? "devWarn1"
            : v < thresholds[1] ? "devWarn2"
            : v < thresholds[2] ? "devWarn3"
            : v < thresholds[3] ? "devWarn4"
            : "devWarn5";
    }

    internal static string WarnColor(double v, double t1, double t2, double t3, double t4)
        => WarnColor(v, new[] { t1, t2, t3, t4 });

    /// <summary>Single-threshold warn test ("the bar turns red past 75%").</summary>
    internal static bool Over(double v, IReadOnlyList<double> thresholds)
        => thresholds.Count > 0 && v > thresholds[^1];
}
