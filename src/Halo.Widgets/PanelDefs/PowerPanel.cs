using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// POWER panel per tools\extracted\temps-power.json: the user's repurposed "Temps" skin — 4
/// text-only rows (no bars; the source .ini's *Perc/*Bar meters for these rows are dead code,
/// see JSON notes), each row = label (left) + current value (warn-colored, right) + session max
/// ("Max: …", gray, center). VCORE and CPU POWER come from elevated-only sensors (SuperIO
/// voltage / MSR package power via LibreHardwareMonitor); when that reading isn't available the
/// current value shows "N/A" (text2) and the max shows "Max: —" — the row itself is never
/// hidden. The GPU rows follow the widget's "gpuIndex" option.
/// </summary>
public static class PowerPanel
{
    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();
        var t = ctx.Theme;

        int gpu = ctx.OptionInt("gpuIndex", 0);
        string gpuVolt = MetricNames.GpuVoltageV(gpu);
        string gpuPower = MetricNames.GpuPowerW(gpu);
        bool showMax = !ctx.Options.ContainsKey("showMax") || ctx.OptionBool("showMax");

        p.TitleElements.Add(new TextEl
        {
            Text = c => c.TitleOr("POWER"),
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
        });

        // Row 1 — VCORE (elevated-only)
        p.Elements.Add(new TextEl
        {
            Text = c => c.Label("vcore", "VCORE"),
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
            VisibleWhen = _ => showMax,
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            Color = "maxLabelGray",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.TryValue(MetricNames.CpuVcoreV, out double v) ? ValueFormat.Fixed(v, 3) + " V" : "N/A",
            ColorFn = c => c.Metrics.TryValue(MetricNames.CpuVcoreV, out double v) ? CpuRamPanelImpl.WarnColor(v, c.Warn("vcore")) : "text2",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // Row 2 — GPU VOLT
        p.Elements.Add(new TextEl
        {
            Text = c => c.Label("gpuVolt", "GPU VOLT"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Left,
            Color = "text",
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"Max: {ValueFormat.Fixed(c.Metrics.Value(gpuVolt + MetricNames.MaxSuffix), 3)} V",
            VisibleWhen = _ => showMax,
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            Color = "maxLabelGray",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => ValueFormat.Fixed(c.Metrics.Value(gpuVolt), 3) + " V",
            ColorFn = c => CpuRamPanelImpl.WarnColor(c.Metrics.Value(gpuVolt), c.Warn("gpuVolt")),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // Row 3 — CPU POWER (elevated-only)
        p.Elements.Add(new TextEl
        {
            Text = c => c.Label("cpuPower", "CPU POWER"),
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
            VisibleWhen = _ => showMax,
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            Color = "maxLabelGray",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.TryValue(MetricNames.CpuPackagePowerW, out double v) ? ValueFormat.Int0(v) + "W" : "N/A",
            ColorFn = c => c.Metrics.TryValue(MetricNames.CpuPackagePowerW, out double v) ? CpuRamPanelImpl.WarnColor(v, c.Warn("cpuPower")) : "text2",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // Row 4 — GPU POWER
        p.Elements.Add(new TextEl
        {
            Text = c => c.Label("gpuPower", "GPU POWER"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Left,
            Color = "text",
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"Max: {ValueFormat.Int0(c.Metrics.Value(gpuPower + MetricNames.MaxSuffix))}W",
            VisibleWhen = _ => showMax,
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            Color = "maxLabelGray",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => ValueFormat.Int0(c.Metrics.Value(gpuPower)) + "W",
            ColorFn = c => CpuRamPanelImpl.WarnColor(c.Metrics.Value(gpuPower), c.Warn("gpuPower")),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        return p;
    }
}
