using Halo.Shared.Panels;
using Halo.Widgets.Render;
using Halo.Widgets.Skins.Rainformer;

namespace Halo.Widgets.Skins;

/// <summary>
/// Builds the element tree for a widget type in the widget's skin. The set of types, their options
/// and their metric rows are described once in <see cref="PanelCatalog"/>; the skin decides how
/// they look (plan D6: any type × N instances).
/// </summary>
public static class PanelFactory
{
    public static Panel? Create(string type, PanelContext ctx) => SkinRegistry.For(ctx.Theme.SkinId).Build(type, ctx);

    /// <summary>Types Halo can draw. Rainformer draws every catalog type, so its list is the list.</summary>
    public static IReadOnlyList<string> KnownTypes => RainformerSkin.KnownTypes;
}
