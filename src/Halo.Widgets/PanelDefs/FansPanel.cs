using Halo.Shared.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// Fans panel per tools\extracted\fans.json: title "FANS", then one row-group per configured
/// fan channel (ctx.Settings.FanNames, e.g. {"2":"BACK","3":"FRONT"}), ordered by channel
/// number. Each group is label (left) / rpm (center) / percent (right) on one row, full-width
/// bar underneath (Fans.ini's *Bar meters use StyleBar, not the unreferenced StyleBarSignal).
/// Only channels present in FanNames are rendered — the 11 other HWiNFO channels (CHA3-5, CPU,
/// FAN21-22, GPU1-2, PUMP1, WPUMP1, PSU) are Hidden=1 in the source skin and are intentionally
/// not implemented statically here; a differently-configured FanNames would surface them.
/// The rpm/pct sensors are elevated-only: when the rpm metric is unavailable we show "N/A"
/// instead of hiding the row, and the bar collapses to zero width rather than disappearing.
/// </summary>
public static class FansPanel
{
    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();

        p.TitleElements.Add(new TextEl
        {
            Text = _ => "FANS",
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
            Upper = true,
        });

        var channels = ctx.Settings.FanNames
            .Select(kv => (Channel: int.Parse(kv.Key), Nick: kv.Value))
            .OrderBy(c => c.Channel)
            .ToList();

        foreach (var (channel, nick) in channels)
        {
            string rpmMetric = MetricNames.FanRpm(channel);
            string pctMetric = MetricNames.FanPct(channel);

            // label — bold, left ("BACK"/"FRONT" per FanNames)
            p.Elements.Add(new TextEl
            {
                Text = _ => nick,
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
                Text = c => c.Metrics.TryValue(rpmMetric, out _) ? $"{ValueFormat.Int0(c.Metrics.Value(pctMetric))}%" : "N/A",
                Style = TextStyle.Bold8,
                Align = TextAlign.Right,
                Color = "text",
                FixedH = 11,
                SameRow = true,
            });
            // bar — full content width (StyleBar), 0-width when rpm invalid, warn color past 75%
            p.Elements.Add(new BarEl
            {
                Value = c => c.Metrics.TryValue(rpmMetric, out _) ? c.Metrics.Value(pctMetric) / 100 : 0,
                FillColorFn = c => c.Metrics.Value(pctMetric) > 75 ? "barWarn" : "bar",
                Advance = 0,
            });
        }

        return p;
    }
}
