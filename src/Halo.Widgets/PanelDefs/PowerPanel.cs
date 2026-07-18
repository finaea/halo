using Halo.Shared.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// POWER panel per tools\extracted\temps-power.json: the user's repurposed "Temps" skin — 4
/// text-only rows (no bars; the source .ini's *Perc/*Bar meters for these rows are dead code,
/// see JSON notes), each row = label (left) + current value (warn-colored, right) + session max
/// ("Max: …", gray, center). VCORE and CPU POWER come from elevated-only sensors (SuperIO
/// voltage / MSR package power via LibreHardwareMonitor); when that reading isn't available the
/// current value shows "N/A" (text2) and the max shows "Max: —" — the row itself is never
/// hidden. GPU VOLT / GPU POWER don't need that guard.
/// </summary>
public static class PowerPanel
{
    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();
        var t = ctx.Theme;

        p.TitleElements.Add(new TextEl
        {
            Text = c => c.Options.GetValueOrDefault("title", "").Length > 0 ? c.Options["title"] : "POWER",
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
        });

        // Row 1 — VCORE (elevated-only), warn thresholds 1.1 / 1.3 / 1.4 / 1.5 V
        p.Elements.Add(new TextEl
        {
            Text = _ => "VCORE",
            Style = TextStyle.Bold8,
            Align = TextAlign.Left,
            Color = "text",
            AbsY = t.TopMarginFormula,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.TryValue(MetricNames.CpuVcoreV, out _)
                ? $"Max: {ValueFormat.Fixed(c.Metrics.Value(MetricNames.CpuVcoreV + MetricNames.MaxSuffix), 3)} V"
                : "Max: —",
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            Color = "maxLabelGray",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.TryValue(MetricNames.CpuVcoreV, out double v) ? ValueFormat.Fixed(v, 3) + " V" : "N/A",
            ColorFn = c => c.Metrics.TryValue(MetricNames.CpuVcoreV, out double v) ? CpuRamPanelImpl.WarnColor(v, 1.1, 1.3, 1.4, 1.5) : "text2",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // Row 2 — GPU VOLT, warn thresholds 0.85 / 0.95 / 1.0 / 1.05 V
        p.Elements.Add(new TextEl
        {
            Text = _ => "GPU VOLT",
            Style = TextStyle.Bold8,
            Align = TextAlign.Left,
            Color = "text",
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"Max: {ValueFormat.Fixed(c.Metrics.Value(MetricNames.GpuVoltageV + MetricNames.MaxSuffix), 3)} V",
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            Color = "maxLabelGray",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => ValueFormat.Fixed(c.Metrics.Value(MetricNames.GpuVoltageV), 3) + " V",
            ColorFn = c => CpuRamPanelImpl.WarnColor(c.Metrics.Value(MetricNames.GpuVoltageV), 0.85, 0.95, 1.0, 1.05),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // Row 3 — CPU POWER (elevated-only), warn thresholds 50 / 100 / 150 / 200 W
        p.Elements.Add(new TextEl
        {
            Text = _ => "CPU POWER",
            Style = TextStyle.Bold8,
            Align = TextAlign.Left,
            Color = "text",
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.TryValue(MetricNames.CpuPackagePowerW, out _)
                ? $"Max: {ValueFormat.Int0(c.Metrics.Value(MetricNames.CpuPackagePowerW + MetricNames.MaxSuffix))}W"
                : "Max: —",
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            Color = "maxLabelGray",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.TryValue(MetricNames.CpuPackagePowerW, out double v) ? ValueFormat.Int0(v) + "W" : "N/A",
            ColorFn = c => c.Metrics.TryValue(MetricNames.CpuPackagePowerW, out double v) ? CpuRamPanelImpl.WarnColor(v, 50, 100, 150, 200) : "text2",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // Row 4 — GPU POWER, warn thresholds 50 / 150 / 200 / 250 W
        p.Elements.Add(new TextEl
        {
            Text = _ => "GPU POWER",
            Style = TextStyle.Bold8,
            Align = TextAlign.Left,
            Color = "text",
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"Max: {ValueFormat.Int0(c.Metrics.Value(MetricNames.GpuPowerW + MetricNames.MaxSuffix))}W",
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            Color = "maxLabelGray",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => ValueFormat.Int0(c.Metrics.Value(MetricNames.GpuPowerW)) + "W",
            ColorFn = c => CpuRamPanelImpl.WarnColor(c.Metrics.Value(MetricNames.GpuPowerW), 50, 150, 200, 250),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        return p;
    }
}
