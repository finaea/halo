using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// POWER panel per tools\extracted\temps-power.json: the user's repurposed "Temps" skin — 4
/// text-only rows (no bars; the source .ini's *Perc/*Bar meters for these rows are dead code,
/// see JSON notes), each row = label (left) + current value (warn-colored, right) + session max
/// ("Max: …", gray, center). VCORE and CPU POWER come from elevated-only sensors (SuperIO
/// voltage / MSR package power via LibreHardwareMonitor); when that reading isn't available the
/// current value shows "N/A" (text2) and the max shows "Max: N/A" — the row itself is never
/// hidden. The GPU rows follow the widget's "gpuIndex" option.
///
/// Every value here is read off hardware, so every one of them is N/A when it is not readable —
/// including the maxima, which are read on their own merit rather than gated on the live value: a
/// session peak that really was observed stays on screen when the sensor behind it drops out, and
/// one that never was reads "Max: N/A" rather than "Max: 0.000 V".
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
            VisibleWhen = c => c.Shows("vcore"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Left,
            Color = "text",
            AbsY = t.TopMarginFormula,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"Max: {c.Na(MetricNames.CpuVcoreV + MetricNames.MaxSuffix, v => ValueFormat.Fixed(v, 3) + " V")}",
            VisibleWhen = c => showMax && c.Shows("vcore"),
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            Color = "maxLabelGray",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Na(MetricNames.CpuVcoreV, v => ValueFormat.Fixed(v, 3) + " V"),
            ColorFn = c => c.Metrics.TryValue(MetricNames.CpuVcoreV, out double v) ? CpuRamPanelImpl.WarnColor(v, c.Warn("vcore")) : "text2",
            VisibleWhen = c => c.Shows("vcore"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // Row 2 — GPU VOLT
        p.Elements.Add(new TextEl
        {
            Text = c => c.Label("gpuVolt", "GPU VOLT"),
            VisibleWhen = c => c.Shows("gpuVolt"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Left,
            Color = "text",
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"Max: {c.Na(gpuVolt + MetricNames.MaxSuffix, v => ValueFormat.Fixed(v, 3) + " V")}",
            VisibleWhen = c => showMax && c.Shows("gpuVolt"),
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            Color = "maxLabelGray",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Na(gpuVolt, v => ValueFormat.Fixed(v, 3) + " V"),
            ColorFn = c => c.Metrics.TryValue(gpuVolt, out double v) ? CpuRamPanelImpl.WarnColor(v, c.Warn("gpuVolt")) : "text2",
            VisibleWhen = c => c.Shows("gpuVolt"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // Row 3 — CPU POWER (elevated-only)
        p.Elements.Add(new TextEl
        {
            Text = c => c.Label("cpuPower", "CPU POWER"),
            VisibleWhen = c => c.Shows("cpuPower"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Left,
            Color = "text",
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"Max: {c.Na(MetricNames.CpuPackagePowerW + MetricNames.MaxSuffix, v => ValueFormat.Int0(v) + "W")}",
            VisibleWhen = c => showMax && c.Shows("cpuPower"),
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            Color = "maxLabelGray",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Na(MetricNames.CpuPackagePowerW, v => ValueFormat.Int0(v) + "W"),
            ColorFn = c => c.Metrics.TryValue(MetricNames.CpuPackagePowerW, out double v) ? CpuRamPanelImpl.WarnColor(v, c.Warn("cpuPower")) : "text2",
            VisibleWhen = c => c.Shows("cpuPower"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        // Row 4 — GPU POWER
        p.Elements.Add(new TextEl
        {
            Text = c => c.Label("gpuPower", "GPU POWER"),
            VisibleWhen = c => c.Shows("gpuPower"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Left,
            Color = "text",
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => $"Max: {c.Na(gpuPower + MetricNames.MaxSuffix, v => ValueFormat.Int0(v) + "W")}",
            VisibleWhen = c => showMax && c.Shows("gpuPower"),
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            Color = "maxLabelGray",
            SameRow = true,
            FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Na(gpuPower, v => ValueFormat.Int0(v) + "W"),
            ColorFn = c => c.Metrics.TryValue(gpuPower, out double v) ? CpuRamPanelImpl.WarnColor(v, c.Warn("gpuPower")) : "text2",
            VisibleWhen = c => c.Shows("gpuPower"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            SameRow = true,
            FixedH = 11,
        });

        return p;
    }
}
