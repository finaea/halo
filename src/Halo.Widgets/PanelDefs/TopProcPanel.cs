using Halo.Shared.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// TOP CPU / TOP RAM panel per tools\extracted\topcpu.json + topram.json: title + (TopCPU-only)
/// bare process-count top-right, then 5 fixed rows (name/RAM/CPU% for TopCPU, name/CPU%/RAM for
/// TopRAM — center and right columns swap between the two variants). Row 1 sits at
/// TopMarginFormula (first flowed element), rows 2-5 stack at +RowSpacing (Y=1R); each row's three
/// columns share one Y (Y=r). Name column left, clipped to W=70 (ellipsis via TextEl.WidthClip).
/// Threshold warn (redText = colorRed@205, same alpha as normal text) applies to the name + the
/// "primary" column (CPU% for TopCPU, RAM for TopRAM) only — the secondary column is always the
/// static colorText2 gray, never threshold-colored (verified against both JSONs).
/// </summary>
public static class TopProcPanel
{
    private const int Rows = 5;
    private const double CpuThresholdPct = 20;          // TopCPUThreshold
    private const double RamThresholdBytes = 1024.0 * 1024 * 500; // TopRAMThreshold=500 -> bytes

    public static Panel Build(PanelContext ctx, bool byRam)
    {
        var p = new Panel();

        // title: "TOP CPU" / "TOP RAM", Bold9 Center Upper
        p.TitleElements.Add(new TextEl
        {
            Text = _ => byRam ? "TOP RAM" : "TOP CPU",
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
                Style = TextStyle.Bold8,
                Align = TextAlign.Right,
                Color = "text2",
            });
        }

        for (int i = 0; i < Rows; i++)
        {
            int rank = i;

            Func<PanelContext, string> nameText = byRam
                ? c => Empty(c.Metrics.Text(MetricNames.TopRamName(rank)))
                : c => Empty(c.Metrics.Text(MetricNames.TopCpuName(rank)));

            // threshold color shared by name + primary column
            Func<PanelContext, string> warnColor = byRam
                ? c => c.Metrics.Value(MetricNames.TopRamB(rank)) >= RamThresholdBytes ? "redText" : "text"
                : c => c.Metrics.Value(MetricNames.TopCpuPct(rank)) >= CpuThresholdPct ? "redText" : "text";

            // name (left, clipped)
            p.Elements.Add(new TextEl
            {
                Text = nameText,
                ColorFn = warnColor,
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
                    Text = c => $"{ValueFormat.Fixed(c.Metrics.Value(MetricNames.TopRamCpuPct(rank)), 1)}%",
                    Style = TextStyle.Bold8,
                    Align = TextAlign.Center,
                    Color = "text2",
                    SameRow = true,
                    FixedH = 11,
                });
                // right: RAM autoscaled bytes (threshold-colored)
                p.Elements.Add(new TextEl
                {
                    Text = c => $"{ValueFormat.AutoScale(c.Metrics.Value(MetricNames.TopRamB(rank)), 1)}B",
                    ColorFn = warnColor,
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
                    Text = c => $"{ValueFormat.AutoScale(c.Metrics.Value(MetricNames.TopCpuRamB(rank)), 1)}B",
                    Style = TextStyle.Bold8,
                    Align = TextAlign.Center,
                    Color = "text2",
                    SameRow = true,
                    FixedH = 11,
                });
                // right: CPU% (threshold-colored)
                p.Elements.Add(new TextEl
                {
                    Text = c => $"{ValueFormat.Fixed(c.Metrics.Value(MetricNames.TopCpuPct(rank)), 1)}%",
                    ColorFn = warnColor,
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
}
