using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

internal static class Placeholder
{
    public static Panel Build(PanelContext ctx, string title)
    {
        var p = new Panel();
        p.TitleElements.Add(new TextEl { Text = _ => title, Style = TextStyle.Bold9, Align = TextAlign.Center, Color = "title", Upper = true });
        p.Elements.Add(new TextEl { Text = _ => "(under construction)", Style = TextStyle.Text8, Align = TextAlign.Center, Color = "text2", FixedH = 12 });
        return p;
    }
}
