using Halo.Metrics;
using Halo.Widgets.PanelModels;
using Halo.Widgets.Render;

namespace Halo.Widgets.Skins.Rainformer;

/// <summary>
/// CPU/RAM panel per tools\extracted\cpu-ram.json: temp row (warn-colored), CPU total row +
/// bar, the per-core grid, Clock/FAN chip row, RAM row + bar, and the 3-series overlay graph
/// (temp/usage/RAM).
///
/// Nothing here knows this PC. The core grid is built from <c>cpu.logical.count</c> plus the
/// per-logical <c>class</c> (P/E) and <c>physical</c> metrics, and laid out by the H3 rule —
/// one column up to 16 threads, two up to 32, three up to 48, per physical core above that,
/// P-cores before E-cores. A 6-thread laptop and a 32-thread desktop both fit the 206-wide card.
/// The CPU fan row follows the <c>cpuFanChannel</c> option, defaulting to the first discovered
/// channel whose sensor name mentions "CPU" (v1 hardcoded fan.0, which is board-specific).
/// </summary>
public static class CpuRamPanelImpl
{
    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();
        string cpuFanMetric = MetricNames.FanRpm(PanelData.CpuFanChannel(ctx));

        p.TitleElements.Add(new TextEl
        {
            Text = c => c.TitleOr(c.Metrics.Text(MetricNames.CpuName, "CPU")),
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
            // A long name is cut with an ellipsis rather than spilling past both card edges.
            ClipToContent = true,
        });

        // temp, centered at abs Y=30, staged warn colors (thresholds from the metric settings)
        p.Elements.Add(new TextEl
        {
            Text = c => c.Na(MetricNames.CpuPackageTempC, c.TempText),
            // An absent reading is muted, not painted with a temperature colour: WarnColor(0) is
            // devWarn1, the coolest step on the scale, so "N/A" would render tinted as though the
            // CPU were cold. Matches how the Power panel colours its N/A rows.
            ColorFn = c => c.Metrics.TryValue(MetricNames.CpuPackageTempC, out double t)
                ? PanelData.WarnColor(t, c.Warn("temp")) : "text2",
            // Gated on registration, not on a readable value: a machine with no package-temp
            // sensor has no row at all, one whose sensor stopped answering reads N/A in place.
            VisibleWhen = c => c.Shows("temp") && c.Metrics.Has(MetricNames.CpuPackageTempC),
            Style = TextStyle.Bold8,
            Align = TextAlign.Center,
            AbsY = 30,
            FixedH = 11,
        });

        // CPU total row: label left, % right, full bar under
        p.Elements.Add(new TextEl { Text = c => c.Label("usage", "CPU:"), VisibleWhen = c => c.Shows("usage"), Style = TextStyle.Bold8, Align = TextAlign.Left, AbsY = 32, FixedH = 11 });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Na(MetricNames.CpuTotalPct, v => $"{ValueFormat.Fixed(v, 1)}%"),
            VisibleWhen = c => c.Shows("usage"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => c.Metrics.Value(MetricNames.CpuTotalPct) / 100,
            VisibleWhen = c => c.Shows("usage"),
            FillColorFn = c => PanelData.Over(c.Metrics.Value(MetricNames.CpuTotalPct), c.Warn("usage")) ? "barWarn" : c.Color("usage", "cpuUsage"),
            Advance = 0,
        });

        AddCoreGrid(p, ctx);

        // Clock / FAN chip row
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.TryValue(MetricNames.CpuClockMhz, out double mhz) && mhz > 0
                ? $"{c.Label("clock", "Clock:")} {ValueFormat.Int0(mhz)} MHz"
                : $"{c.Label("clock", "Clock:")} N/A",
            VisibleWhen = c => c.Shows("clock"),
            Style = TextStyle.Text8,
            ColorFn = c => c.Color("clock", "text2"),
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
            ColorFn = c => c.Color("fan", "text2"),
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // RAM row: label left, used/total center, % right, bar under
        p.Elements.Add(new TextEl { Text = c => c.Label("ram", "RAM:"), VisibleWhen = c => c.Shows("ram"), Style = TextStyle.Bold8, Align = TextAlign.Left, FixedH = 11, Advance = 2 });
        p.Elements.Add(new TextEl
        {
            // Both halves or nothing: "12.4 GB/N/A" is not a ratio anyone can read.
            Text = c => c.Na(MetricNames.RamUsedGb, MetricNames.RamTotalGb,
                (used, total) => $"{ValueFormat.AutoScale(used * 1073741824)}B/{ValueFormat.AutoScale(total * 1073741824)}B"),
            VisibleWhen = c => c.Shows("ram"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Center,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Na(MetricNames.RamPct, v => $"{ValueFormat.Int0(v)}%"),
            VisibleWhen = c => c.Shows("ram"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new BarEl
        {
            Value = c => c.Metrics.Value(MetricNames.RamPct) / 100,
            VisibleWhen = c => c.Shows("ram"),
            FillColorFn = c => PanelData.Over(c.Metrics.Value(MetricNames.RamPct), c.Warn("ram")) ? "barWarn" : c.Color("ram", "ramUsage"),
            Advance = 1,
        });

        // 3-series overlay graph (temp red / usage lavender / RAM green). Columns are time
        // buckets over graph.historyS, so the span means the same at any refresh rate (R4).
        // Each line is toggled by its own metric setting (metrics.temp.graph, …).
        var graph = new GraphEl
        {
            Advance = 4,
            BgColor = "emptyBar",
            Start = GraphStart.Left,
            H = ctx.GraphHeight,
            HistoryS = ctx.GraphHistoryS,
            Style = ctx.GraphStyle,
        };
        // NaSample, not Value: an absent reading skips the sample instead of appending a 0, so a
        // provider blip leaves the line flat rather than drawing a cliff to the floor.
        if (ctx.Graphs("temp"))
            graph.Series.Add(new GraphSeries { Color = ctx.Color("temp", "cpuTemp"), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.NaSample(MetricNames.CpuPackageTempC) });
        if (ctx.Graphs("usage"))
            graph.Series.Add(new GraphSeries { Color = ctx.Color("usage", "cpuUsage"), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.NaSample(MetricNames.CpuTotalPct) });
        if (ctx.Graphs("ram"))
            graph.Series.Add(new GraphSeries { Color = ctx.Color("ram", "ramUsage"), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.NaSample(MetricNames.RamPct) });
        p.Elements.Add(graph);

        return p;
    }

    private static void AddCoreGrid(Panel p, PanelContext ctx)
    {
        var (rows, columns) = PanelData.CoreGrid(ctx);
        if (rows.Count == 0) return;

        var t = ctx.Theme;
        int perColumn = (rows.Count + columns - 1) / columns;
        // enough air that one column's percentage does not read as part of the next one's label
        const double Gap = 8;
        double colW = (t.ContentWidth - Gap * (columns - 1)) / columns;

        // Flow rule: a SameRow element takes the PREVIOUS element's Y. So every cell's text goes
        // in first (all at the row's Y), then the bars — which sit 7 below — go in last, the
        // first carrying the offset and the rest sharing its Y. With one column this emits
        // exactly the skin's label / percent / bar order, unchanged.
        for (int r = 0; r < perColumn; r++)
        {
            bool leader = true;
            for (int col = 0; col < columns; col++)
            {
                int idx = col * perColumn + r;
                if (idx >= rows.Count) continue;
                var row = rows[idx];
                double left = t.ContentMargin + col * (colW + Gap);

                p.Elements.Add(new TextEl
                {
                    Text = _ => row.Label,
                    VisibleWhen = c => c.Shows("cores"),
                    Style = TextStyle.Text8,
                    Color = "text2",
                    Align = TextAlign.Left,
                    X = left,
                    FixedH = 11,
                    SameRow = !leader,
                    // the very first core row rides right on the CPU bar (row-adjustor, -1)
                    Advance = leader && r == 0 ? -1 : 1,
                });
                leader = false;
                if (row.IsHeading) continue;

                int core = row.Logical;
                p.Elements.Add(new TextEl
                {
                    Text = columns == 1
                        ? c => c.Na(MetricNames.CpuCorePct(core), v => $"{ValueFormat.Fixed(v, 1)}%")
                        : c => c.Na(MetricNames.CpuCorePct(core), v => $"{ValueFormat.Int0(v)}%"),
                    VisibleWhen = c => c.Shows("cores"),
                    Style = TextStyle.Text8,
                    Color = "text2",
                    Align = TextAlign.Right,
                    X = left + colW,
                    SameRow = true,
                    FixedH = 11,
                });
            }

            bool firstBar = true;
            for (int col = 0; col < columns; col++)
            {
                int idx = col * perColumn + r;
                if (idx >= rows.Count) continue;
                var row = rows[idx];
                if (row.IsHeading) continue;

                int core = row.Logical;
                double left = t.ContentMargin + col * (colW + Gap);
                // single column keeps the skin's exact X=47 W=120 inset; narrower cells scale it
                p.Elements.Add(new BarEl
                {
                    Value = c => c.Metrics.Value(MetricNames.CpuCorePct(core)) / 100,
                    VisibleWhen = c => c.Shows("cores"),
                    FillColorFn = c => c.Color($"cores.{core}", "bar"),
                    X = columns == 1 ? 47 : left + colW * 0.34,
                    W = columns == 1 ? 120 : colW * 0.38,
                    H = 1,
                    SameRow = true,
                    SameRowOffset = firstBar ? 7 : 0,
                });
                firstBar = false;
            }
        }
    }
}
