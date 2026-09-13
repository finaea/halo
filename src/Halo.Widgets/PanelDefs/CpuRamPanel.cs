using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

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
    /// <summary>Logical CPUs to draw. Falls back to this process's view when the collector is
    /// down, so the panel still has the right shape before the first publish.</summary>
    private static int CoreCount(PanelContext ctx)
        => (int)Math.Clamp(ctx.Metrics.Value(MetricNames.CpuLogicalCount, Environment.ProcessorCount), 1, 256);

    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();
        string cpuFanMetric = MetricNames.FanRpm(CpuFanChannel(ctx));

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
            Text = c => c.TempText(c.Metrics.Value(MetricNames.CpuPackageTempC)),
            ColorFn = c => WarnColor(c.Metrics.Value(MetricNames.CpuPackageTempC), c.Warn("temp")),
            VisibleWhen = c => c.Shows("temp") && c.Metrics.TryValue(MetricNames.CpuPackageTempC, out _),
            Style = TextStyle.Bold8,
            Align = TextAlign.Center,
            AbsY = 30,
            FixedH = 11,
        });

        // CPU total row: label left, % right, full bar under
        p.Elements.Add(new TextEl { Text = c => c.Label("usage", "CPU:"), VisibleWhen = c => c.Shows("usage"), Style = TextStyle.Bold8, Align = TextAlign.Left, AbsY = 32, FixedH = 11 });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Fixed(c.Metrics.Value(MetricNames.CpuTotalPct), 1)}%",
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
            FillColorFn = c => Over(c.Metrics.Value(MetricNames.CpuTotalPct), c.Warn("usage")) ? "barWarn" : c.Color("usage", "cpuUsage"),
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
            Text = c =>
            {
                double used = c.Metrics.Value(MetricNames.RamUsedGb) * 1073741824;
                double total = c.Metrics.Value(MetricNames.RamTotalGb) * 1073741824;
                return $"{ValueFormat.AutoScale(used)}B/{ValueFormat.AutoScale(total)}B";
            },
            VisibleWhen = c => c.Shows("ram"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Center,
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"{ValueFormat.Int0(c.Metrics.Value(MetricNames.RamPct))}%",
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
            FillColorFn = c => Over(c.Metrics.Value(MetricNames.RamPct), c.Warn("ram")) ? "barWarn" : c.Color("ram", "ramUsage"),
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
        if (ctx.Graphs("temp"))
            graph.Series.Add(new GraphSeries { Color = ctx.Color("temp", "cpuTemp"), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.Metrics.Value(MetricNames.CpuPackageTempC) });
        if (ctx.Graphs("usage"))
            graph.Series.Add(new GraphSeries { Color = ctx.Color("usage", "cpuUsage"), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.Metrics.Value(MetricNames.CpuTotalPct) });
        if (ctx.Graphs("ram"))
            graph.Series.Add(new GraphSeries { Color = ctx.Color("ram", "ramUsage"), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.Metrics.Value(MetricNames.RamPct) });
        p.Elements.Add(graph);

        return p;
    }

    // ---- per-core grid (hardware plan H3) ----

    /// <summary>One row of the grid: a core, or a "P-cores" / "E-cores" group heading.</summary>
    private readonly record struct CoreRow(int Logical, string Label, bool IsHeading);

    /// <summary>
    /// Which core rows exist and how many columns they are laid out in.
    /// <para>
    /// <c>coreView</c>: auto (threads up to 48, physical cores above) · thread · core · hidden.
    /// <c>coreColumns</c>: auto (1 up to 16 rows, 2 up to 32, 3 up to 48) · 1 · 2 · 3.
    /// </para>
    /// </summary>
    private static (List<CoreRow> Rows, int Columns) CoreGrid(PanelContext ctx)
    {
        int threads = CoreCount(ctx);
        string view = ctx.Option("coreView");
        if (view.Length == 0) view = "auto";
        if (view == "hidden") return ([], 1);

        // class 0 = performance, 1 = efficiency (published already inverted by the collector)
        var logical = new List<(int Index, int Class, int Physical)>(threads);
        for (int i = 0; i < threads; i++)
        {
            int cls = (int)ctx.Metrics.Value(MetricNames.CpuCoreClass(i), 0);
            int phys = (int)ctx.Metrics.Value(MetricNames.CpuCorePhysical(i), i);
            logical.Add((i, cls, phys));
        }

        bool perCore = view == "core" || (view == "auto" && threads > 48);
        var ordered = logical.OrderBy(l => l.Class).ThenBy(l => l.Index).ToList();
        if (perCore)
        {
            // one row per physical core; the row reads the first logical CPU on that core
            var seen = new HashSet<int>();
            ordered = ordered.Where(l => seen.Add(l.Physical)).ToList();
        }

        int columns = ctx.Option("coreColumns") switch
        {
            "1" => 1,
            "2" => 2,
            "3" => 3,
            _ => ordered.Count <= 16 ? 1 : ordered.Count <= 32 ? 2 : 3,
        };

        // group headings only when the part actually has both kinds of core
        bool hybrid = ordered.Any(l => l.Class == 0) && ordered.Any(l => l.Class == 1);
        string template = ctx.UserLabel("cores") ?? (columns == 1 ? "Core {n}:" : "C{n}");

        var rows = new List<CoreRow>(ordered.Count + 2);
        int lastClass = -1;
        int ordinal = 0;
        foreach (var l in ordered)
        {
            if (hybrid && l.Class != lastClass)
            {
                rows.Add(new CoreRow(-1, l.Class == 0 ? "P-cores" : "E-cores", true));
                lastClass = l.Class;
            }
            ordinal++;
            string label = (ctx.UserLabel($"cores.{l.Index}") ?? template)
                .Replace("{n}", (perCore ? ordinal : l.Index + 1).ToString());
            rows.Add(new CoreRow(l.Index, label, false));
        }
        return (rows, columns);
    }

    private static void AddCoreGrid(Panel p, PanelContext ctx)
    {
        var (rows, columns) = CoreGrid(ctx);
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
                        ? c => $"{ValueFormat.Fixed(c.Metrics.Value(MetricNames.CpuCorePct(core)), 1)}%"
                        : c => $"{ValueFormat.Int0(c.Metrics.Value(MetricNames.CpuCorePct(core)))}%",
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

    /// <summary>Which discovered fan channel is the CPU fan: the user's pick, else the first
    /// channel whose sensor name mentions "CPU", else channel 0.</summary>
    private static int CpuFanChannel(PanelContext ctx)
    {
        if (int.TryParse(ctx.Option("cpuFanChannel"), out int pinned) && pinned >= 0) return pinned;
        int count = (int)ctx.Metrics.Value(MetricNames.FanCount, 0);
        for (int i = 0; i < count; i++)
            if (ctx.Metrics.Text(MetricNames.FanName(i)).Contains("CPU", StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
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
