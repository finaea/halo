using System.Numerics;
using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Halo.Widgets.Skins.AzurArchive;

/// <summary>
/// The Azur Archive card (design §3): a frosted gradient body, one baked navy drop shadow, a faint
/// 1 px stroke (Jack's answer 5 — Port Day must keep an edge over a light wallpaper), Momo Pink's
/// header band, and the texture: a dotted grid in the header corner and a fading triangle mosaic in
/// the bottom-right, both kept clear of every reading (rule 8).
///
/// <para>The card's rounded rectangle is the one geometry the shadow needs; it is cached per size
/// and factory like Rainformer's. The shadow bake itself is cached by <see cref="RenderContext.Glow"/>,
/// so a repaint is a bitmap draw, never a blur.</para>
/// </summary>
internal sealed class AzurChrome(bool header) : ICardChrome
{
    private readonly record struct Key(nint Factory, float W, float H);
    private Key _key;
    private ID2D1Geometry? _card;

    /// <summary>The card's rectangle for a laid-out panel height.</summary>
    public static Rect CardRect(Theme t, double panelH)
        => new(Az.CardX, Az.InsetTop, Az.CardW(t), (float)Math.Max(4, panelH - Az.InsetTop - Az.InsetBottom));

    public ID2D1Geometry Card(RenderContext rc, Theme t, double panelH)
    {
        var r = CardRect(t, panelH);
        using var factory = rc.DC.Factory;
        var key = new Key(factory.NativePointer, r.Width, r.Height);
        if (_card == null || key != _key)
        {
            _card?.Dispose();
            _band?.Dispose();
            _band = null;
            _card = factory.CreateRoundedRectangleGeometry(new RoundedRectangle { Rect = r, RadiusX = Az.Radius, RadiusY = Az.Radius });
            _key = key;
        }
        return _card;
    }

    private ID2D1PathGeometry? _band;

    /// <summary>Momo Pink's header band: the header zone cut to the card's rounded top, built once
    /// per card size. A clip layer per repaint did the same and was the dearest part of the chrome.</summary>
    private ID2D1Geometry Band(RenderContext rc, ID2D1Geometry card, Rect r)
    {
        if (_band != null) return _band;
        using var factory = rc.DC.Factory;
        using var zone = factory.CreateRectangleGeometry(new Rect(r.Left, r.Top, r.Width, Az.HeaderH));
        _band = factory.CreatePathGeometry();
        using (var sink = _band.Open())
        {
            card.CombineWithGeometry(zone, CombineMode.Intersect, sink);
            sink.Close();
        }
        return _band;
    }

    public void DrawCard(RenderContext rc, Theme t, double panelH)
    {
        var r = CardRect(t, panelH);
        var card = Card(rc, t, panelH);

        var shadow = t.Color("shadow");
        if (shadow.A > 0) rc.Glow(card, shadow, Az.U(7), new Vector2(0, Az.U(4)));

        rc.DC.FillGeometry(card, rc.LinearGradient(t.Color("bgTop"), t.Color("bgBody"), new(0, r.Top), new(0, r.Bottom)));

        bool showHeader = header && t.ShowTitle;
        if (showHeader && Option(t, "header") == "band")
        {
            // Momo Pink's MomoTalk band: the header zone, clipped to the card's rounded top
            rc.DC.FillGeometry(Band(rc, card, r), rc.Brush(t.Color("tabFill")));
        }

        if (Option(t, "texture") != "false")
        {
            if (showHeader) DrawGrid(rc, t, r);
            DrawMosaic(rc, t, r);
        }

        if (t.StrokeWidth > 0)
        {
            var stroke = t.Color("stroke");
            float sw = Az.U(t.StrokeWidth);     // mockup pixels: "1 px" is one pixel at the default scale
            float inset = sw / 2;
            rc.DC.DrawRoundedRectangle(new RoundedRectangle
            {
                Rect = new Rect(r.Left + inset, r.Top + inset, r.Width - sw, r.Height - sw),
                RadiusX = Az.Radius - inset,
                RadiusY = Az.Radius - inset,
            }, rc.Brush(stroke), sw);
        }
    }

    /// <summary>Dotted grid + a "+" crosshair in the header's right corner, left of the emblem.</summary>
    private static void DrawGrid(RenderContext rc, Theme t, Rect card)
    {
        var c = t.Color("texture");
        if (c.A <= 0) return;
        float x0 = card.Right - Az.U(46) - Az.U(64), y0 = card.Top + Az.U(1);
        var area = new Rect(x0, y0, Az.U(64) + Az.U(8), Az.U(29));
        Az.DrawBaked(rc, "grid:" + c, area, b =>
        {
            var brush = b.Brush(c);
            float step = Az.U(6), dot = Az.U(1), top = y0 + Az.U(5);
            for (float y = top + step / 2; y < top + Az.U(24); y += step)
                for (float x = x0 + step / 2; x < x0 + Az.U(64); x += step)
                    b.DC.FillRectangle(new Rect(x - dot / 2, y - dot / 2, dot, dot), brush);
            float cx = x0 + Az.U(64) + Az.U(2), cy = top - Az.U(1), arm = Az.U(3.5);
            b.DC.DrawLine(new(cx - arm, cy), new(cx + arm, cy), brush, Az.U(1.2));
            b.DC.DrawLine(new(cx, cy - arm), new(cx, cy + arm), brush, Az.U(1.2));
        });
    }

    /// <summary>A triangle lattice — two families of 60° lines — in the bottom-right corner, fading
    /// in toward the corner. Outlines, not filled triangles: texture whispers (rule 8).</summary>
    private static void DrawMosaic(RenderContext rc, Theme t, Rect card)
    {
        var c = t.Color("mosaic");
        if (c.A <= 0) return;
        float w = Az.U(90), h = Az.U(44);
        var area = new Rect(card.Right - w, card.Bottom - h, w, h);
        Az.DrawBaked(rc, "mosaic:" + c, area, b =>
        {
            float x = area.Left, y = area.Top;
            var fade = b.LinearGradient(new Color4(c.R, c.G, c.B, 0), new Color4(c.R, c.G, c.B, Math.Min(1, c.A * 2.2f)), new(x + w * 0.35f, y), new(x + w, y + h));
            b.DC.PushAxisAlignedClip(area, AntialiasMode.PerPrimitive);
            float run = h * 0.577f, step = Az.U(22);       // tan 30°: a 60° line across the box
            for (float sx = x - run; sx < x + w + run; sx += step)
            {
                b.DC.DrawLine(new(sx, y + h), new(sx + run, y), fade, Az.U(1));
                b.DC.DrawLine(new(sx, y), new(sx + run, y + h), fade, Az.U(1));
            }
            b.DC.PopAxisAlignedClip();
        });
    }

    /// <summary>Azur Archive has no separate stale dot: a stale card already says so everywhere —
    /// grey key bar, "–" diamond, NO SIGNAL · COLLECTOR, Manjuu's sign.</summary>
    public void DrawStaleBadge(RenderContext rc, Theme theme) { }

    public static string Option(Theme t, string key) => t.SkinOptions.GetValueOrDefault(key, "");

    public void Dispose()
    {
        _card?.Dispose();
        _card = null;
        _band?.Dispose();
        _band = null;
    }
}
