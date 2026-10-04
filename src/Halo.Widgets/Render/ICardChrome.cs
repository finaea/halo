namespace Halo.Widgets.Render;

/// <summary>
/// The card a panel's elements sit on: background, header band, borders and the stale badge.
/// Owned by the skin (it sets <see cref="Panel.Chrome"/> when it builds the panel), so the element
/// flow stays skin-agnostic. Implementations may cache device-independent geometry, which is why
/// this is disposable — the panel disposes it with itself.
/// </summary>
public interface ICardChrome : IDisposable
{
    /// <summary>Draw the card behind the elements. <paramref name="panelH"/> is the laid-out
    /// height in logical units.</summary>
    void DrawCard(RenderContext rc, Theme theme, double panelH);

    /// <summary>Draw the "this data is stale" marker, on top of the elements.</summary>
    void DrawStaleBadge(RenderContext rc, Theme theme);
}
