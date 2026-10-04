using Halo.Shared.Skins;
using Halo.Widgets.Render;

namespace Halo.Widgets.Skins;

/// <summary>
/// The rendering half of a skin; its metadata (tokens, presets, options) is <see cref="SkinInfo"/>
/// in <see cref="SkinCatalog"/>. A skin owns how a panel looks, never what it measures — metrics,
/// options and N/A rules stay in <c>PanelCatalog</c> and <see cref="PanelContext"/>.
/// </summary>
public interface ISkin
{
    SkinInfo Info { get; }

    /// <summary>The card's frame in logical units, for code that lays out against it.</summary>
    SkinGeometry Geometry(Theme t);

    /// <summary>The element tree for a panel type, with its <see cref="Panel.Chrome"/> set, or null
    /// when this skin cannot draw the type.</summary>
    Panel? Build(string type, PanelContext ctx);
}

/// <summary>The few frame measurements a caller outside the skin needs, in logical units:
/// card inset from the window edge, title band height, content column width and text row height.
/// Rainformer reads them straight off <see cref="Theme"/>.</summary>
public sealed record SkinGeometry(double CardInset, double TitleBandH, double ContentWidth, double RowH);
