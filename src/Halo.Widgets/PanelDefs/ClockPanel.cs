using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// Clock/Uptime panel (Clock.ini): date + day-of-year in the title band, big time,
/// weekday, uptime pill. Day-of-year is intentional (plan §16.4).
/// </summary>
public static class ClockPanel
{
    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();

        // title band: date left, "Day: N" right (styleTitle base but colorText2, size 8, no upper)
        p.TitleElements.Add(new TextEl
        {
            Text = c => c.Now.ToString("d/M/yyyy"),
            Style = new TextStyle(8, true),
            Align = TextAlign.Left,
            Color = "text2",
        });
        p.TitleElements.Add(new TextEl
        {
            Text = c => $"Day: {c.Now.DayOfYear}",
            Style = new TextStyle(8, true),
            Align = TextAlign.Right,
            Color = "text2",
        });

        // big time — Rainformer meterTime: FontSize=20 with InlineSetting=Size|13 applied to
        // the pattern's capture group, i.e. "H:mm" renders at 20 and only the ":ss" tail at 13
        p.Elements.Add(new TextEl
        {
            Text = c => c.Now.ToString("H:mm:ss"),
            Style = new TextStyle(20, true),
            InlineSizePt = 13,
            InlineRange = s => { int i = s.LastIndexOf(':'); return i > 0 ? (i, s.Length - i) : (0, 0); },
            Align = TextAlign.Center,
            Color = "text",
            AbsY = ctx.Theme.TopMarginFormula - 4,
            FixedH = 28,
        });

        // weekday
        p.Elements.Add(new TextEl
        {
            Text = c => c.Now.DayOfWeek.ToString(),
            Upper = true,
            Style = TextStyle.Bold8,
            Align = TextAlign.Center,
            Color = "text",
            FixedH = 12,
            Advance = ctx.Theme.RowSpacing,
        });

        // uptime pill
        p.Elements.Add(new TextEl
        {
            Text = c => c.Na(Halo.Metrics.MetricNames.SysUptimeS, ValueFormat.Uptime),
            VisibleWhen = c => c.Shows("uptime"),
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            X = ctx.Theme.CenterAlign - 1,
            ColorFn = c => c.Color("uptime", "text2"),
            SolidColor = "solidLabel",
            SolidW = ctx.Theme.ContentWidth,
            SolidH = 12,
            FixedH = 12,
            Advance = ctx.Theme.RowSpacing,
        });

        return p;
    }
}
