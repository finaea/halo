using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Widgets.Render;
using Halo.Widgets.Skins;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Halo.Widgets.Harness;

/// <summary>
/// <c>--render out.png --type capabilities [--warp]</c>: one sheet exercising every renderer feature
/// a non-Rainformer skin can use — bundled fonts, weight/stretch/tracking, tabular figures, sheared
/// labels, a bitmap slot and its empty fallback, gradients, each shape, baked glow and shadow, and
/// each <see cref="GraphVisual"/>. Not a panel and not a golden: it is the page to look at after
/// touching the renderer.
///
/// <para>It is drawn twice with a <see cref="Dx.Recreate"/> in between, and the two images must be
/// byte-identical — the check that bitmaps, fonts and every device-bound cache come back after a
/// device loss. A mismatch is reported on stderr and the run exits non-zero.</para>
/// </summary>
public static class CapabilitySheet
{
    public const string Type = "capabilities";

    private const float W = 300, H = 740, Scale = 1.7f;
    private const string Skin = "azur-archive";

    // the Azur Archive candidates from the UI craft research, as sheet-local tokens
    private static readonly (string Token, Color4 Color)[] Tokens =
    [
        ("cap.bg", Hex(0xF4F7FB)), ("cap.ink", Hex(0x203C5D)), ("cap.blue", Hex(0x128AFA)),
        ("cap.navy", Hex(0x133453)), ("cap.cyan", Hex(0x27CBFC)), ("cap.yellow", Hex(0xEDCB3C)),
        ("cap.pink", Hex(0xF7A8BB)), ("cap.grid", Hex(0x203C5D, 0.15f)), ("cap.dim", Hex(0x203C5D, 0.55f)),
    ];

    public static (PanelImage Image, bool SurvivedRecreate) Render(Dx dx)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var first = RenderOnce(dx);
            dx.Recreate(Paths.FontsDir);
            var second = RenderOnce(dx);
            return (first, first.Pixels.AsSpan().SequenceEqual(second.Pixels));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    private static PanelImage RenderOnce(Dx dx)
    {
        var theme = new Theme { BaseScale = Scale, Dpi = 96 };
        foreach (var (token, color) in Tokens) theme.Colors[token] = color;

        int pxW = (int)Math.Ceiling(W * Scale), pxH = (int)Math.Ceiling(H * Scale);
        using var dc = dx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        using var rc = new RenderContext(dc, dx.DWrite, dx.CustomFonts, theme);
        var format = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var target = dc.CreateBitmap(new SizeI(pxW, pxH), IntPtr.Zero, 0,
            new BitmapProperties1(format, 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));

        // graphs need history before they are drawn, exactly like a panel
        var graphs = Graphs(theme, out var ctx);

        dc.Target = target;
        dc.SetDpi(96, 96);
        dc.BeginDraw();
        dc.Clear(new Color4(0, 0, 0, 0));
        dc.Transform = Matrix3x2.CreateScale(Scale);
        Draw(rc, theme, dx, graphs, ctx);
        dc.EndDraw();
        dc.Target = null;
        return PanelRenderer.Read(dc, target, pxW, pxH);
    }

    private static void Draw(RenderContext rc, Theme t, Dx dx, List<(string Name, GraphEl Graph)> graphs, PanelContext ctx)
    {
        var dc = rc.DC;
        dc.FillRectangle(new Rect(0, 0, W, H), rc.Brush(t.Color("cap.bg")));
        float y = 6;

        // ---- fonts ----
        y = Heading(rc, t, "BUNDLED FONTS", y);
        var families = dx.CustomFonts is { } cf ? string.Join(" · ", Dx.FamilyNames(cf)) : "(none)";
        rc.DrawText(families, new TextStyle(6, false), t.Color("cap.dim"), 8, y, TextAlign.Left); y += 10;
        foreach (var (label, style) in new (string, TextStyle)[]
        {
            ("Barlow Condensed Light 300", new(11, false, "Barlow") { Weight = FontWeight.Light, Stretch = FontStretch.Condensed }),
            ("Barlow Condensed Medium 500", new(11, false, "Barlow") { Weight = FontWeight.Medium, Stretch = FontStretch.Condensed }),
            ("Barlow Condensed SemiBold 600", new(11, false, "Barlow") { Weight = FontWeight.SemiBold, Stretch = FontStretch.Condensed }),
            ("Barlow Condensed Bold 700", new(11, true, "Barlow") { Stretch = FontStretch.Condensed }),
            ("M PLUS Rounded 1c Medium · まもなく", new(9, false, "Rounded Mplus 1c") { Weight = FontWeight.Medium }),
            ("M PLUS Rounded 1c ExtraBold · 先生", new(9, false, "Rounded Mplus 1c") { Weight = FontWeight.ExtraBold }),
            ("OXANIUM SEMIBOLD 0123", new(9, false, "Oxanium SemiBold")),
            ("Trebuchet MS (system, Rainformer)", new(9, false)),
        })
        {
            rc.DrawText(label, style, t.Color("cap.ink"), 8, y, TextAlign.Left);
            y += rc.LineHeight(style) + 1;
        }
        rc.DrawText("7 6", new TextStyle(12, false, "ElegantIcons"), t.Color("cap.blue"), 8, y, TextAlign.Left);
        rc.DrawText("← ElegantIcons via FontOverride", new TextStyle(6, false), t.Color("cap.dim"), 40, y + 4, TextAlign.Left);
        y += 14;

        // ---- tracking + stretch ----
        y = Heading(rc, t, "TRACKING · STRETCH", y);
        var tag = new TextStyle(8, false, "Oxanium SemiBold");
        rc.DrawText("TRACK 0", tag, t.Color("cap.ink"), 8, y, TextAlign.Left);
        rc.DrawText("TRACK .08", tag with { Tracking = 0.08f }, t.Color("cap.ink"), 80, y, TextAlign.Left);
        rc.DrawText("TRACK .2", tag with { Tracking = 0.2f }, t.Color("cap.ink"), 170, y, TextAlign.Left);
        y += 12;
        var trebStretch = new TextStyle(9, false) { Stretch = FontStretch.Condensed };
        rc.DrawText("Trebuchet, Condensed: no such face, unchanged", trebStretch, t.Color("cap.dim"), 8, y, TextAlign.Left);
        y += 14;

        // ---- tabular figures ----
        y = Heading(rc, t, "TABULAR FIGURES (right-aligned at the rule)", y);
        float rule1 = 90, rule2 = 180, rule3 = 270;
        dc.FillRectangle(new Rect(rule1, y, 0.5f, 46), rc.Brush(t.Color("cap.pink")));
        dc.FillRectangle(new Rect(rule2, y, 0.5f, 46), rc.Brush(t.Color("cap.pink")));
        dc.FillRectangle(new Rect(rule3, y, 0.5f, 46), rc.Brush(t.Color("cap.pink")));
        var barlow = new TextStyle(12, false, "Barlow") { Weight = FontWeight.SemiBold, Stretch = FontStretch.Condensed };
        var oxan = new TextStyle(10, false, "Oxanium SemiBold");
        float ty = y;
        foreach (var n in new[] { "1111", "8808", "4170" })
        {
            rc.DrawText(n, barlow, t.Color("cap.dim"), rule1, ty, TextAlign.Right);
            rc.DrawText(n, barlow with { Tabular = true }, t.Color("cap.ink"), rule2, ty, TextAlign.Right);
            rc.DrawText(n, oxan with { Tabular = true }, t.Color("cap.ink"), rule3, ty, TextAlign.Right);
            ty += 15;
        }
        rc.DrawText("Barlow proportional", new TextStyle(5.5, false), t.Color("cap.dim"), rule1, ty, TextAlign.Right);
        rc.DrawText($"Barlow {Mode(rc, barlow)}", new TextStyle(5.5, false), t.Color("cap.dim"), rule2, ty, TextAlign.Right);
        rc.DrawText($"Oxanium {Mode(rc, oxan)}", new TextStyle(5.5, false), t.Color("cap.dim"), rule3, ty, TextAlign.Right);
        var rounded = new TextStyle(9, false, "Rounded Mplus 1c") { Weight = FontWeight.Medium, Tabular = true };
        rc.DrawText($"Rounded Mplus 1c: {Mode(rc, rounded)}  1111 / 8808", rounded, t.Color("cap.dim"), 8, ty + 8, TextAlign.Left);
        // Georgia (system) has no tnum feature at all, so this row is the forced-advance path
        var georgia = new TextStyle(9, false, "Georgia");
        float georgiaY = ty + 22;
        dc.FillRectangle(new Rect(rule1, georgiaY, 0.5f, 26), rc.Brush(t.Color("cap.pink")));
        dc.FillRectangle(new Rect(rule2, georgiaY, 0.5f, 26), rc.Brush(t.Color("cap.pink")));
        rc.DrawText("1111", georgia, t.Color("cap.dim"), rule1, georgiaY, TextAlign.Right);
        rc.DrawText("8808", georgia, t.Color("cap.dim"), rule1, georgiaY + 12, TextAlign.Right);
        rc.DrawText("1111", georgia with { Tabular = true }, t.Color("cap.ink"), rule2, georgiaY, TextAlign.Right);
        rc.DrawText("8808", georgia with { Tabular = true }, t.Color("cap.ink"), rule2, georgiaY + 12, TextAlign.Right);
        rc.DrawText($"← Georgia: {Mode(rc, georgia)}", new TextStyle(6, false), t.Color("cap.dim"), rule2 + 6, georgiaY + 8, TextAlign.Left);
        ty += 46;
        y = ty + 12;

        // ---- shear ----
        y = Heading(rc, t, "SHEARED LABELS · UPRIGHT DIGITS", y);
        var tab = rc.Shape(ShapeSpec.Parallelogram(8, y, 86, 14, 14 * 0.364f));
        dc.FillGeometry(tab, rc.Brush(t.Color("cap.blue")));
        rc.DrawText("STATUS · CPU", tag with { Shear = 0.364f, Tracking = 0.06f }, new Color4(1, 1, 1, 1), 51, y + 1.5, TextAlign.Center);
        rc.DrawText("62", barlow with { Tabular = true, SizePt = 13 }, t.Color("cap.ink"), 104, y - 3, TextAlign.Left);
        rc.DrawText("°C", tag, t.Color("cap.dim"), 120, y + 3, TextAlign.Left);
        rc.DrawText("label lean 20° (tan .364)", tag with { Shear = 0.364f, SizePt = 7 }, t.Color("cap.navy"), 150, y + 3, TextAlign.Left);
        y += 22;

        // ---- bitmaps ----
        y = Heading(rc, t, "BITMAP SLOTS", y);
        var assets = SkinAssets.For(Skin);
        bool a1 = assets.Draw(rc, "arona", "game-art/blue-archive/arona.png", new Rect(8, y, 44, 44));
        bool a2 = assets.Draw(rc, "manjuu", "game-art/azur-lane/manjuu.png", new Rect(58, y + 14, 30, 30));
        var missing = new Rect(96, y, 44, 44);
        bool a3 = assets.Draw(rc, "no-such-slot", "game-art/no-such.png", missing);
        dc.DrawRectangle(missing, rc.Brush(t.Color("cap.grid")), 0.5f);
        rc.DrawText($"bundled: {a1} {a2}", new TextStyle(6, false), t.Color("cap.dim"), 148, y + 8, TextAlign.Left);
        rc.DrawText($"missing slot drew: {a3}", new TextStyle(6, false), t.Color("cap.dim"), 148, y + 18, TextAlign.Left);
        rc.DrawText("(empty box = fallback 'nothing')", new TextStyle(6, false), t.Color("cap.dim"), 148, y + 28, TextAlign.Left);
        y += 52;

        // ---- gradients ----
        y = Heading(rc, t, "GRADIENTS", y);
        dc.FillRectangle(new Rect(8, y, 120, 14), rc.LinearGradient(t.Color("cap.blue"), t.Color("cap.cyan"), new(8, 0), new(128, 0)));
        dc.FillRectangle(new Rect(134, y, 60, 30), rc.LinearGradient(new Color4(1, 1, 1, 0.9f), Hex(0x128AFA, 0.25f), new(0, y), new(0, y + 30)));
        dc.FillEllipse(new Ellipse(new(230, y + 15), 18, 15), rc.RadialGradient(t.Color("cap.cyan"), Hex(0x27CBFC, 0), new(230, y + 15), 18, 15));
        // same ramp, second position: one cached brush, moved
        dc.FillRectangle(new Rect(8, y + 18, 120, 12), rc.LinearGradient(t.Color("cap.blue"), t.Color("cap.cyan"), new(128, 0), new(8, 0)));
        y += 38;

        // ---- shapes ----
        y = Heading(rc, t, "SHAPES", y);
        var ink = rc.Brush(t.Color("cap.navy"));
        dc.FillGeometry(rc.Shape(ShapeSpec.Chamfer(8, y, 44, 26, 6, Corners.TopLeft | Corners.BottomRight)), ink);
        dc.FillGeometry(rc.Shape(ShapeSpec.Parallelogram(58, y, 44, 26, 8)), ink);
        dc.FillGeometry(rc.Shape(ShapeSpec.Notch(108, y, 44, 26, 16, 6)), ink);
        dc.FillGeometry(rc.Shape(ShapeSpec.Diamond(158, y, 26, 26)), rc.Brush(t.Color("cap.yellow")));
        dc.FillGeometry(rc.Shape(ShapeSpec.Ellipse(190, y, 26, 26)), rc.Brush(t.Color("cap.pink")));
        dc.FillGeometry(rc.Shape(ShapeSpec.TriangleMosaic(222, y, 70, 26, 6, 0.35f)), rc.Brush(Hex(0x128AFA, 0.35f)));
        y += 32;

        // ---- glow ----
        y = Heading(rc, t, "BAKED GLOW · SOFT SHADOW", y);
        var card = rc.Shape(ShapeSpec.Chamfer(14, y + 2, 90, 30, 7, Corners.TopRight | Corners.BottomLeft));
        rc.Glow(card, Hex(0x133453, 0.35f), 3f, new Vector2(0, 2));
        dc.FillGeometry(card, rc.Brush(new Color4(1, 1, 1, 1)));
        rc.DrawText("shadow σ3", tag, t.Color("cap.ink"), 59, y + 11, TextAlign.Center);
        var navy = new Rect(120, y, 172, 36);
        dc.FillRectangle(navy, rc.Brush(t.Color("cap.navy")));
        var ring = rc.Shape(ShapeSpec.Ellipse(150, y + 6, 24, 24));
        rc.Glow(ring, Hex(0x27CBFC, 0.9f), 4f);
        dc.DrawGeometry(ring, rc.Brush(t.Color("cap.cyan")), 2f);
        var dia = rc.Shape(ShapeSpec.Diamond(200, y + 9, 18, 18));
        rc.Glow(dia, Hex(0xEDCB3C, 0.8f), 3f);
        dc.FillGeometry(dia, rc.Brush(t.Color("cap.yellow")));
        rc.DrawText("glow σ4 · σ3", new TextStyle(6, false), new Color4(1, 1, 1, 0.8f), 228, y + 14, TextAlign.Left);
        y += 44;

        // ---- graph visuals ----
        y = Heading(rc, t, "GRAPH VISUALS (NaN blip mid-run is held flat)", y);
        int i = 0;
        foreach (var (name, g) in graphs)
        {
            float gx = 8 + (i % 2) * 146, gy = y + (i / 2) * 44;
            g.X = gx; g.W = 138; g.Y = gy + 8;
            rc.DrawText(name, new TextStyle(6, false), t.Color("cap.dim"), gx, gy, TextAlign.Left);
            g.Draw(rc, ctx);
            i++;
        }
    }

    /// <summary>Which way a style gets its equal-width digits: the face's own tnum, or the forced
    /// minimum advance when the face has none.</summary>
    private static string Mode(RenderContext rc, TextStyle s)
        => rc.DigitAdvance(s with { Tabular = true }) > 0 ? "fallback advance" : "tnum";

    private static float Heading(RenderContext rc, Theme t, string text, float y)
    {
        var s = new TextStyle(7, false, "Oxanium SemiBold") { Tracking = 0.1f };
        rc.DC.FillRectangle(new Rect(4, y + 1, 2, 7), rc.Brush(t.Color("cap.blue")));
        rc.DrawText(text, s, t.Color("cap.navy"), 9, y, TextAlign.Left);
        return y + 12;
    }

    /// <summary>Five graphs over one synthetic series, driven through ticks like a real panel.</summary>
    private static List<(string, GraphEl)> Graphs(Theme theme, out PanelContext ctx)
    {
        var widget = new WidgetInstance { Id = "capabilities", Type = "cpu" };
        ctx = new PanelContext
        {
            Metrics = FixtureMetrics.Load("idle"),
            Theme = theme,
            Settings = new AppSettings(),
            Widget = widget,
        };
        var list = new List<(string, GraphEl)>
        {
            ("Rainformer (default)", Graph(GraphVisual.Rainformer, GraphStyle.Line, null)),
            ("Rainformer filled", Graph(GraphVisual.Rainformer, GraphStyle.Filled, "emptyBar")),
            ("skin line 1.5", Graph(new SkinGraphVisual(), GraphStyle.Line, null)),
            ("gradient fill", Graph(new SkinGraphVisual { FillAlpha = 0.5f }, GraphStyle.Line, null)),
            ("node dots", Graph(new SkinGraphVisual { Line = false, DotEvery = 6, DotRadius = 1.4f }, GraphStyle.Line, null)),
            ("grid + line + fill", Graph(new SkinGraphVisual { FillAlpha = 0.3f, GridCols = 6, GridRows = 3, GridColor = "cap.grid" }, GraphStyle.Line, null)),
        };

        long start = 1000L * Stopwatch.Frequency, step = (long)(0.2 * Stopwatch.Frequency);
        for (int k = 0; k < PanelRenderer.Ticks; k++)
        {
            ctx.NowQpc = start + k * step;
            ctx.TickIndex = k;
            foreach (var (_, g) in list) g.Update(ctx);
        }
        return list;
    }

    private static GraphEl Graph(GraphVisual visual, GraphStyle style, string? bg) => new()
    {
        H = 30,
        Visual = visual,
        Style = style,
        BgColor = bg,
        Series =
        [
            new GraphSeries
            {
                Color = "cap.blue",
                Ring = new SampleRing(GraphEl.CapacityFor(40, 5)),
                FixedMax = 100,
                Sample = c =>
                {
                    long k = c.TickIndex;
                    if (k is > 95 and < 115) return double.NaN; // a provider blip
                    return 50 + 30 * Math.Sin(k / 9.0) + 12 * Math.Sin(k / 2.3);
                },
            },
        ],
    };

    private static Color4 Hex(uint rgb, float a = 1)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);
}
