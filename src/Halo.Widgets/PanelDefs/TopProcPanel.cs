using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// TOP CPU / TOP RAM panel per tools\extracted\topcpu.json + topram.json: title + (TopCPU-only)
/// bare process-count top-right, then N rows (name/RAM/CPU% for TopCPU, name/CPU%/RAM for
/// TopRAM — center and right columns swap between the two variants). Row 1 sits at
/// TopMarginFormula (first flowed element), later rows stack at +RowSpacing (Y=1R); each row's
/// three columns share one Y (Y=r). Name column left, clipped to W=70 (ellipsis via
/// TextEl.WidthClip). Threshold warn (redText = colorRed@205, same alpha as normal text) applies
/// to the name + the "primary" column (CPU% for TopCPU, RAM for TopRAM) only — the secondary
/// column is always the static colorText2 gray (verified against both JSONs).
///
/// Row count is the "topN" option (1–10); the collector always publishes 10 ranks, so changing it
/// costs nothing.
/// </summary>
public static class TopProcPanel
{
    public const int MaxRows = 10;

    public static Panel Build(PanelContext ctx, bool byRam)
    {
        var p = new Panel();
        int rows = Math.Clamp(ctx.OptionInt("topN", 5), 1, MaxRows);

        // title: "TOP CPU" / "TOP RAM", Bold9 Center Upper
        p.TitleElements.Add(new TextEl
        {
            Text = c => c.TitleOr(byRam ? "TOP RAM" : "TOP CPU"),
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
        });

        // bare process-count number, top-right — TopCPU only (no "Processes:" label, per JSON)
        if (!byRam)
        {
            p.TitleElements.Add(new TextEl
            {
                Text = c => ValueFormat.Int0(c.Metrics.Value(MetricNames.ProcCount)),
                VisibleWhen = c => c.Shows("count"),
                Style = TextStyle.Bold8,
                Align = TextAlign.Right,
                Color = "text2",
            });
        }

        for (int i = 0; i < rows; i++)
        {
            int rank = i;

            // Agg(c) is read per tick from the live options, so the context-menu "Sum same-name
            // processes" toggle applies without waiting for a rebuild.
            Func<PanelContext, string> nameText = byRam
                ? c => Empty(c.Metrics.Text(MetricNames.TopRamName(rank, Agg(c))))
                : c => Empty(c.Metrics.Text(MetricNames.TopCpuName(rank, Agg(c))));

            // threshold color shared by name + primary column
            Func<PanelContext, string> warnColor = byRam
                ? c => Exceeds(c.Metrics.Value(MetricNames.TopRamB(rank, Agg(c))), c.Warn("ram")) ? "redText" : "text"
                : c => Exceeds(c.Metrics.Value(MetricNames.TopCpuPct(rank, Agg(c))), c.Warn("cpu")) ? "redText" : "text";

            // name (left, clipped)
            p.Elements.Add(new TextEl
            {
                Text = nameText,
                ColorFn = warnColor,
                VisibleWhen = c => c.Shows("name"),
                Style = TextStyle.Bold8,
                Align = TextAlign.Left,
                WidthClip = 70,
                FixedH = 11,
                Advance = ctx.Theme.RowSpacing,
            });

            if (byRam)
            {
                // center: CPU% (static text2, never threshold-colored)
                p.Elements.Add(new TextEl
                {
                    Text = c => $"{ValueFormat.Fixed(c.Metrics.Value(MetricNames.TopRamCpuPct(rank, Agg(c))), 1)}%",
                    VisibleWhen = c => c.Shows("cpu"),
                    Style = TextStyle.Bold8,
                    Align = TextAlign.Center,
                    Color = "text2",
                    SameRow = true,
                    FixedH = 11,
                });
                // right: RAM autoscaled bytes (threshold-colored)
                p.Elements.Add(new TextEl
                {
                    Text = c => $"{ValueFormat.AutoScale(c.Metrics.Value(MetricNames.TopRamB(rank, Agg(c))), 1)}B",
                    ColorFn = warnColor,
                    VisibleWhen = c => c.Shows("ram"),
                    Style = TextStyle.Bold8,
                    Align = TextAlign.Right,
                    SameRow = true,
                    FixedH = 11,
                });
            }
            else
            {
                // center: RAM autoscaled bytes (static text2, never threshold-colored)
                p.Elements.Add(new TextEl
                {
                    Text = c => $"{ValueFormat.AutoScale(c.Metrics.Value(MetricNames.TopCpuRamB(rank, Agg(c))), 1)}B",
                    VisibleWhen = c => c.Shows("ram"),
                    Style = TextStyle.Bold8,
                    Align = TextAlign.Center,
                    Color = "text2",
                    SameRow = true,
                    FixedH = 11,
                });
                // right: CPU% (threshold-colored)
                p.Elements.Add(new TextEl
                {
                    Text = c => $"{ValueFormat.Fixed(c.Metrics.Value(MetricNames.TopCpuPct(rank, Agg(c))), 1)}%",
                    ColorFn = warnColor,
                    VisibleWhen = c => c.Shows("cpu"),
                    Style = TextStyle.Bold8,
                    Align = TextAlign.Right,
                    SameRow = true,
                    FixedH = 11,
                });
            }
        }

        return p;
    }

    /// <summary>Empty process name -> "---" (mirrors the plugin's Substitute "":"---").</summary>
    private static string Empty(string name) => string.IsNullOrEmpty(name) ? "---" : name;

    private static bool Exceeds(double v, IReadOnlyList<double> thresholds)
        => thresholds.Count > 0 && v >= thresholds[^1];

    /// <summary>Per-widget ranking mode: "aggregate"=true sums same-name processes (Task Manager
    /// style); default false = per-instance (Rainformer/UsageMonitor parity).</summary>
    private static bool Agg(PanelContext c) => c.OptionBool("aggregate");
}
