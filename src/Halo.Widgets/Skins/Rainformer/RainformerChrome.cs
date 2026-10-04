using System.Diagnostics.CodeAnalysis;
using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Halo.Widgets.Skins.Rainformer;

/// <summary>
/// Rainformer's two-zone card: rounded-top band + rounded-bottom body (StyleBackground shapes),
/// the optional frame around both (the <c>strokeWidth</c> option, in the <c>stroke</c> token), and
/// the red stale dot in the band corner. With the title bar hidden there is one zone, rounded on
/// all four corners.
///
/// <para>The two path geometries are cached instead of rebuilt every frame. A path geometry is a
/// factory resource, so the cache key includes the D2D factory: <see cref="Dx.Recreate"/> builds a
/// new device and with it a new factory, and filling a geometry from the old one on the new
/// device context is an error. Everything else in the key is what the shapes are made of — inset,
/// width, radius, band height and panel height. The title-hidden card is a plain rounded rect and
/// uses no cached geometry, so toggling the title just stops reading the cache.</para>
/// </summary>
internal sealed class RainformerChrome : ICardChrome
{
    private readonly record struct ShapeKey(nint Factory, float Inset, float W, float R, float BandH, float PanelH);

    private ShapeKey _key;
    private ID2D1PathGeometry? _band, _body;

    public void DrawCard(RenderContext rc, Theme theme, double panelH)
    {
        float x = (float)theme.BgOffset, w = (float)theme.BgShapeW, r = (float)theme.CornerRadius;

        if (!theme.ShowTitle)
        {
            float h = (float)(panelH - 2 * theme.BgOffset);
            if (h > 2)
                rc.DC.FillRoundedRectangle(new RoundedRectangle
                {
                    Rect = new Rect(x, (float)theme.BgOffset, w, h),
                    RadiusX = r,
                    RadiusY = r,
                }, rc.Brush(theme.Color("bgBody")));
        }
        else
        {
            EnsureShapes(rc, theme, (float)panelH, x, w, r);
            rc.DC.FillGeometry(_band, rc.Brush(theme.Color("bgTop")));
            if (_body != null)
                rc.DC.FillGeometry(_body, rc.Brush(theme.Color("bgBody")));
        }

        // StyleBackground's frame (Styles.inc Shape7): one rounded rect half a unit outside the
        // card, spanning band, gap and body, radius + 0.5. Width 0 — Rainformer Light, and every
        // golden — draws nothing at all, not a zero-width hairline.
        if (theme.StrokeWidth > 0)
        {
            float top = (float)theme.BgOffset - 0.5f, frameH = (float)(panelH - 2 * theme.BgOffset) + 1;
            if (frameH > 2)
                rc.DC.DrawRoundedRectangle(new RoundedRectangle
                {
                    Rect = new Rect(x - 0.5f, top, w + 1, frameH),
                    RadiusX = r + 0.5f,
                    RadiusY = r + 0.5f,
                }, rc.Brush(theme.Color("stroke")), (float)theme.StrokeWidth);
        }
    }

    public void DrawStaleBadge(RenderContext rc, Theme theme)
    {
        // per-panel stale badge (plan §11): small red dot in the title band corner
        rc.DC.FillEllipse(new Ellipse(new System.Numerics.Vector2((float)(theme.BgWidth - theme.BgOffset - 6), (float)(theme.BgOffset + 5 + theme.ContentShiftY)), 2.5f, 2.5f),
            rc.Brush(theme.Color("staleBadge")));
    }

    [MemberNotNull(nameof(_band))]
    private void EnsureShapes(RenderContext rc, Theme theme, float panelH, float x, float w, float r)
    {
        using var factory = rc.DC.Factory;
        var key = new ShapeKey(factory.NativePointer, x, w, r, (float)theme.TitleZoneH, panelH);
        if (_band != null && key == _key) return;

        ReleaseShapes();
        _key = key;

        // top band: y 5..26, rounded top corners, square bottom
        _band = HalfRounded(factory, x, (float)theme.BgOffset, w, (float)theme.TitleZoneH, r, roundTop: true);

        // body: y 29.5 .. panelH-5, square top, rounded bottom
        float bodyTop = (float)(theme.BgOffset + 24.5);
        float bodyH = (float)(panelH - theme.BgOffset - bodyTop);
        if (bodyH > 2)
            _body = HalfRounded(factory, x, bodyTop, w, bodyH, r, roundTop: false);
    }

    private static ID2D1PathGeometry HalfRounded(ID2D1Factory factory, float x, float y, float w, float h, float r, bool roundTop)
    {
        var geo = factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            if (roundTop)
            {
                sink.BeginFigure(new System.Numerics.Vector2(x, y + r), FigureBegin.Filled);
                sink.AddArc(new ArcSegment(new System.Numerics.Vector2(x + r, y), new Size(r, r), 0, SweepDirection.Clockwise, ArcSize.Small));
                sink.AddLine(new System.Numerics.Vector2(x + w - r, y));
                sink.AddArc(new ArcSegment(new System.Numerics.Vector2(x + w, y + r), new Size(r, r), 0, SweepDirection.Clockwise, ArcSize.Small));
                sink.AddLine(new System.Numerics.Vector2(x + w, y + h));
                sink.AddLine(new System.Numerics.Vector2(x, y + h));
            }
            else
            {
                sink.BeginFigure(new System.Numerics.Vector2(x, y), FigureBegin.Filled);
                sink.AddLine(new System.Numerics.Vector2(x + w, y));
                sink.AddLine(new System.Numerics.Vector2(x + w, y + h - r));
                sink.AddArc(new ArcSegment(new System.Numerics.Vector2(x + w - r, y + h), new Size(r, r), 0, SweepDirection.Clockwise, ArcSize.Small));
                sink.AddLine(new System.Numerics.Vector2(x + r, y + h));
                sink.AddArc(new ArcSegment(new System.Numerics.Vector2(x, y + h - r), new Size(r, r), 0, SweepDirection.Clockwise, ArcSize.Small));
            }
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        return geo;
    }

    private void ReleaseShapes()
    {
        _band?.Dispose(); _band = null;
        _body?.Dispose(); _body = null;
    }

    public void Dispose() => ReleaseShapes();
}
