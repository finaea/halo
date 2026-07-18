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

        // big time — Rainformer: size 20, but 13 when showing seconds (InlinePattern)
        p.Elements.Add(new TextEl
        {
            Text = c => c.Now.ToString("H:mm:ss"),
            Style = new TextStyle(13, true),
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
            Text = c => ValueFormat.Uptime(c.Metrics.Value(Halo.Shared.Metrics.MetricNames.SysUptimeS)),
            Style = TextStyle.Text8,
            Align = TextAlign.Center,
            X = ctx.Theme.CenterAlign - 1,
            Color = "text2",
            SolidColor = "solidLabel",
            SolidW = ctx.Theme.ContentWidth,
            SolidH = 12,
            FixedH = 12,
            Advance = ctx.Theme.RowSpacing,
        });

        return p;
    }
}
