using Halo.Widgets.Render;

namespace Halo.Widgets.Skins.Rainformer;

/// <summary>
/// The companion panel in the original look: no mascot and no chat (Rainformer ignores the mood's
/// faces), just the mood's status as one plain line under the title. It exists so the type every
/// skin offers still draws something honest when the skin is switched.
/// </summary>
public static class CompanionPanel
{
    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();

        p.TitleElements.Add(new TextEl
        {
            Text = c => c.TitleOr("COMPANION"),
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
            Upper = true,
        });

        p.Elements.Add(new TextEl
        {
            Text = c => c.Mood.Status,
            Style = TextStyle.Bold8,
            Align = TextAlign.Center,
            ColorFn = c => c.Mood.State is MoodState.Hot or MoodState.Critical ? "devWarn5" : "text",
            WidthClip = ctx.Theme.ContentWidth,
            FixedH = 11,
            Advance = ctx.Theme.RowSpacing,
        });

        return p;
    }
}
