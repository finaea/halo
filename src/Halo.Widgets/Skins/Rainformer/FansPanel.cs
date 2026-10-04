using Halo.Metrics;
using Halo.Widgets.PanelModels;
using Halo.Widgets.Render;

namespace Halo.Widgets.Skins.Rainformer;

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

        foreach (int channel in PanelData.SelectedChannels(ctx))
        {
            int ch = channel;
            string key = $"rpm.{ch}";
            string rpmMetric = MetricNames.FanRpm(ch);

            // label — bold, left; the nickname if the user set one, else the sensor's own name
            p.Elements.Add(new TextEl
            {
                Text = c =>
                {
                    // the catalog default ("Fan {n}") is only reached when the chip publishes no
                    // name, so ask for the user's rename explicitly rather than via Label()
                    if (c.UserLabel(key) is { Length: > 0 } custom) return custom.Replace("{n}", ch.ToString());
                    string sensorName = c.Metrics.Text(MetricNames.FanName(ch));
                    return sensorName.Length > 0 ? sensorName : $"FAN {ch}";
                },
                VisibleWhen = c => c.Shows(key),
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
                VisibleWhen = c => c.Shows(key),
                Style = TextStyle.Text8,
                Align = TextAlign.Center,
                Color = "text2",
                FixedH = 11,
                SameRow = true,
            });
            // percent — bold, right
            p.Elements.Add(new TextEl
            {
                Text = c => c.Metrics.TryValue(rpmMetric, out _) ? $"{ValueFormat.Int0(PanelData.FanPercent(c, ch, key))}%" : "N/A",
                VisibleWhen = c => c.Shows(key),
                Style = TextStyle.Bold8,
                Align = TextAlign.Right,
                Color = "text",
                FixedH = 11,
                SameRow = true,
            });
            // bar — full content width (StyleBar), 0-width when rpm invalid, warn color past 75%
            p.Elements.Add(new BarEl
            {
                Value = c => c.Metrics.TryValue(rpmMetric, out _) ? PanelData.FanPercent(c, ch, key) / 100 : 0,
                VisibleWhen = c => c.Shows(key),
                FillColorFn = c => PanelData.Over(PanelData.FanPercent(c, ch, key), c.Warn(key)) ? "barWarn" : c.Color(key, "bar"),
                Advance = 0,
            });
        }

        return p;
    }
}
