using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// Fans panel per tools\extracted\fans.json: title "FANS", then one row-group per selected fan
/// channel. Each group is label (left) / rpm (center) / percent (right) on one row, full-width
/// bar underneath (Fans.ini's *Bar meters use StyleBar, not the unreferenced StyleBarSignal).
///
/// Channels come from the widget's "channels" option; with none set, every channel the collector
/// found that is actually spinning is shown. Nicknames are per-channel metric labels
/// (metrics."rpm.2".label), so the panel needs no board-specific knowledge.
///
/// Percentage: the SuperIO chip's own PWM duty when it reports one (fan.&lt;n&gt;.control.pct),
/// else rpm ÷ max where max is the per-channel setting, defaulting to the session's observed
/// maximum floored at 1500 rpm (hardware plan H4). The rpm sensors are elevation-only: when the
/// reading is missing the row shows "N/A" instead of disappearing.
/// </summary>
public static class FansPanel
{
    /// <summary>Floor for the auto max, so a fan that has only ever idled doesn't read 100%.</summary>
    private const double MinAutoMaxRpm = 1500;

    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();

        p.TitleElements.Add(new TextEl
        {
            Text = c => c.TitleOr("FANS"),
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
            Upper = true,
        });

        foreach (int channel in SelectedChannels(ctx))
        {
            int ch = channel;
            string key = $"rpm.{ch}";
            string rpmMetric = MetricNames.FanRpm(ch);

            // label — bold, left; the nickname if the user set one, else the sensor's own name
            p.Elements.Add(new TextEl
            {
                Text = c =>
                {
                    string custom = c.Metric(key)?.Label ?? "";
                    if (custom.Length > 0) return custom;
                    string sensorName = c.Metrics.Text(MetricNames.FanName(ch));
                    return sensorName.Length > 0 ? sensorName : $"FAN {ch}";
                },
                Style = TextStyle.Bold8,
                Align = TextAlign.Left,
                Color = "text",
                FixedH = 11,
                Advance = ctx.Theme.RowSpacing,
            });
            // rpm — normal weight, center, "N/A" when the elevated sensor isn't available
            p.Elements.Add(new TextEl
            {
                Text = c => c.Metrics.TryValue(rpmMetric, out double rpm) ? $"{ValueFormat.Int0(rpm)} rpm" : "N/A",
                Style = TextStyle.Text8,
                Align = TextAlign.Center,
                Color = "text2",
                FixedH = 11,
                SameRow = true,
            });
            // percent — bold, right
            p.Elements.Add(new TextEl
            {
                Text = c => c.Metrics.TryValue(rpmMetric, out _) ? $"{ValueFormat.Int0(Percent(c, ch, key))}%" : "N/A",
                Style = TextStyle.Bold8,
                Align = TextAlign.Right,
                Color = "text",
                FixedH = 11,
                SameRow = true,
            });
            // bar — full content width (StyleBar), 0-width when rpm invalid, warn color past 75%
            p.Elements.Add(new BarEl
            {
                Value = c => c.Metrics.TryValue(rpmMetric, out _) ? Percent(c, ch, key) / 100 : 0,
                FillColorFn = c => CpuRamPanelImpl.Over(Percent(c, ch, key), c.Warn(key)) ? "barWarn" : "bar",
                Advance = 0,
            });
        }

        return p;
    }

    /// <summary>Duty cycle for a channel: the chip's own PWM value when it has one, else
    /// rpm ÷ max (per-channel setting, or the observed session maximum).</summary>
    private static double Percent(PanelContext c, int channel, string key)
    {
        if (c.Metrics.TryValue(MetricNames.FanControlPct(channel), out double duty))
            return Math.Clamp(duty, 0, 100);

        double rpm = c.Metrics.Value(MetricNames.FanRpm(channel));
        double observedMax = c.Metrics.Value(MetricNames.FanRpm(channel) + MetricNames.MaxSuffix);
        double max = c.MaxOf(key, Math.Max(observedMax, MinAutoMaxRpm));
        return max > 0 ? Math.Clamp(rpm / max * 100, 0, 100) : 0;
    }

    /// <summary>The widget's channel list, or every discovered channel that is spinning.</summary>
    private static List<int> SelectedChannels(PanelContext ctx)
    {
        var configured = ctx.OptionList("channels")
            .Select(s => int.TryParse(s, out int n) ? n : -1)
            .Where(n => n >= 0)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        if (configured.Count > 0) return configured;

        var found = new List<int>();
        int count = (int)ctx.Metrics.Value(MetricNames.FanCount, 0);
        for (int i = 0; i < count; i++)
            if (ctx.Metrics.TryValue(MetricNames.FanRpm(i), out double rpm) && rpm > 0)
                found.Add(i);
        return found;
    }
}
