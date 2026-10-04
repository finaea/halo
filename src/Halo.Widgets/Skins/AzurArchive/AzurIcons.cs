using System.Globalization;
using System.Numerics;
using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Halo.Widgets.Skins.AzurArchive;

/// <summary>
/// The skin's line emblems (one per panel), the status-diamond glyphs and the mood overlays the
/// skin paints on top of character art (sweat, spiral, star, zZz). All vector: they take the
/// preset's colours, scale with the card, and need no file — so they survive a removed art folder.
///
/// <para>Paths are the mockup's SVG (<c>azur.js</c> ICON/FX), drawn in an 18- or 20-unit box and
/// placed with a transform. Arcs and smooth curves were rewritten as cubic curves by hand, so the
/// parser only needs M/L/H/V/C/Z.</para>
/// </summary>
internal static class AzurIcons
{
    private sealed record Icon(string? Stroke, string? Fill, (float X, float Y, float W, float H, float R)[] Rects,
        (float X, float Y, float R, bool Filled)[] Circles);

    private static readonly Dictionary<string, Icon> Icons = new()
    {
        ["cpu-ram"] = new("M7 1V4M11 1V4M7 14V17M11 14V17M1 7H4M1 11H4M14 7H17M14 11H17", null,
            [(4, 4, 10, 10, 1)], []),
        ["gpu"] = new("M12 7H15M12 10H15", null, [(1, 4, 16, 10, 1)], [(7, 9, 3, false)]),
        ["fps"] = new(null, "M7 6.5L12 9L7 11.5Z", [(1.5f, 3, 15, 12, 1.5f)], []),
        ["latency"] = new("M9 10V6.5M7 1.5H11M14.5 4.5L16 3", null, [], [(9, 10, 6.5f, false)]),
        ["power"] = new(null, "M10.5 1L3 10.5H8.5L7 17L15 7H9.5Z", [], []),
        ["drives"] = new("M9 5.5V16M5 8.5H13M2.5 10.5C2.5 14 5.5 16 9 16C12.5 16 15.5 14 15.5 10.5", null, [], [(9, 3.5f, 2, false)]),
        ["network"] = new("M2 6.5C6 2.5 12 2.5 16 6.5M4.5 9.5C7.1 6.9 10.9 6.9 13.5 9.5M7 12.5C8.2 11.3 9.8 11.3 11 12.5", null, [], [(9, 15, 1.2f, true)]),
        ["fans"] = new(null, Propeller, [], [(9, 9, 2, true)]),
        ["topcpu"] = new("M3.5 1.5H14.5V16.5L12.5 15.2L10.7 16.5L9 15.2L7.2 16.5L5.5 15.2L3.5 16.5ZM6 5.5H12M6 8.5H12M6 11.5H9.5", null, [], []),
        ["heart"] = new(null, "M9 16C9 16 1.5 11.3 1.5 6.2C1.5 3.9 3.3 2.3 5.4 2.3C7 2.3 8.4 3.2 9 4.3C9.6 3.2 11 2.3 12.6 2.3C14.7 2.3 16.5 3.9 16.5 6.2C16.5 11.3 9 16 9 16Z", [], []),
    };

    public const string Propeller =
        "M9 7C7.5 3 9 1 11 1.5C13 2 12 6 9 7Z" +
        "M11 9.5C15 8 17 9.5 16.5 11.5C16 13.5 12 12.5 11 9.5Z" +
        "M7.5 10.5C5 14 2.7 14.3 1.8 12.5C0.9 10.7 3.4 8.3 7.5 10.5Z";

    private const string CpuCore = "M7 7H11V11H7Z";

    private static nint _factory;
    private static readonly Dictionary<string, ID2D1PathGeometry> Paths = new();

    private static ID2D1PathGeometry Path(RenderContext rc, string d)
    {
        using var factory = rc.DC.Factory;
        if (factory.NativePointer != _factory)
        {
            foreach (var p in Paths.Values) p.Dispose();
            Paths.Clear();
            _factory = factory.NativePointer;
        }
        if (Paths.TryGetValue(d, out var g)) return g;
        g = Parse(factory, d);
        Paths[d] = g;
        return g;
    }

    /// <summary>Draw a panel emblem in an <paramref name="size"/>-unit square at (x, y).</summary>
    public static void Emblem(RenderContext rc, string type, float x, float y, float size, Color4 color)
    {
        if (type == "topram") type = "topcpu";
        if (type == "companion") type = "heart";
        if (!Icons.TryGetValue(type, out var icon)) return;
        var brush = rc.Brush(color);
        var saved = rc.DC.Transform;
        float k = size / 18f;
        rc.DC.Transform = Matrix3x2.CreateScale(k) * Matrix3x2.CreateTranslation(x, y) * saved;
        const float sw = 1.6f;
        foreach (var r in icon.Rects)
            rc.DC.DrawRoundedRectangle(new RoundedRectangle { Rect = new Rect(r.X, r.Y, r.W, r.H), RadiusX = r.R, RadiusY = r.R }, brush, sw);
        foreach (var c in icon.Circles)
        {
            var e = new Ellipse(new Vector2(c.X, c.Y), c.R, c.R);
            if (c.Filled) rc.DC.FillEllipse(e, brush); else rc.DC.DrawEllipse(e, brush, sw);
        }
        if (icon.Stroke != null) rc.DC.DrawGeometry(Path(rc, icon.Stroke), brush, sw);
        if (icon.Fill != null) rc.DC.FillGeometry(Path(rc, icon.Fill), brush);
        if (type == "cpu-ram") rc.DC.FillGeometry(Path(rc, CpuCore), brush);
        rc.DC.Transform = saved;
    }

    /// <summary>A propeller glyph; <paramref name="turn"/> rotates it (degrees) — the "full" state
    /// is drawn turned, never spun (data never animates).</summary>
    public static void Prop(RenderContext rc, float x, float y, float size, Color4 color, float turn = 0)
    {
        var saved = rc.DC.Transform;
        float k = size / 18f;
        rc.DC.Transform = Matrix3x2.CreateRotation(turn * MathF.PI / 180, new Vector2(9, 9)) * Matrix3x2.CreateScale(k) * Matrix3x2.CreateTranslation(x, y) * saved;
        var brush = rc.Brush(color);
        rc.DC.FillGeometry(Path(rc, Propeller), brush);
        rc.DC.FillEllipse(new Ellipse(new Vector2(9, 9), 2, 2), brush);
        rc.DC.Transform = saved;
    }

    // ---- status diamond glyphs: ✓ ! ▲ – (rule 7 — every warn step changes a glyph) ----

    public enum Status { Ok, Warn, Crit, None }

    /// <summary>The glyph inside the status diamond, centred on (cx, cy), <paramref name="s"/> = its half size.</summary>
    public static void StatusGlyph(RenderContext rc, Status st, float cx, float cy, float s, Color4 color)
    {
        var brush = rc.Brush(color);
        float w = s * 0.32f;
        switch (st)
        {
            case Status.Ok:
                rc.DC.DrawLine(new(cx - s * 0.55f, cy + s * 0.02f), new(cx - s * 0.12f, cy + s * 0.42f), brush, w);
                rc.DC.DrawLine(new(cx - s * 0.12f, cy + s * 0.42f), new(cx + s * 0.6f, cy - s * 0.45f), brush, w);
                break;
            case Status.Warn:
                rc.DC.DrawLine(new(cx, cy - s * 0.55f), new(cx, cy + s * 0.15f), brush, w);
                rc.DC.FillEllipse(new Ellipse(new(cx, cy + s * 0.5f), w * 0.6f, w * 0.6f), brush);
                break;
            case Status.Crit:
                rc.DC.FillGeometry(Az.Poly(rc, "tri", cx, cy, s, 0, 0, () => [new(cx, cy - s * 0.55f), new(cx + s * 0.58f, cy + s * 0.45f), new(cx - s * 0.58f, cy + s * 0.45f)]), brush);
                break;
            default:
                rc.DC.DrawLine(new(cx - s * 0.45f, cy), new(cx + s * 0.45f, cy), brush, w);
                break;
        }
    }

    // ---- mood overlays on character art (design §9.4: sweat / spiral / star / zZz) ----

    public enum Overlay { None, Sweat, Spiral, Star }

    private const string Sweat = "M12 2C15 6.5 17 9 17 11.5C17 14.3 14.8 16.5 12 16.5C9.2 16.5 7 14.3 7 11.5C7 9 9 6.5 12 2Z";
    private const string Star = "M10 1L12.2 7.8L19 10L12.2 12.2L10 19L7.8 12.2L1 10L7.8 7.8Z";

    /// <summary>An overlay in a <paramref name="size"/> square at (x, y) — the mockup's 20 px badge.</summary>
    public static void Draw(RenderContext rc, Overlay o, float x, float y, float size, Theme t)
    {
        if (o == Overlay.None) return;
        var saved = rc.DC.Transform;
        rc.DC.Transform = Matrix3x2.CreateScale(size / 20f) * Matrix3x2.CreateTranslation(x, y) * saved;
        var white = rc.Brush(new Color4(1, 1, 1, 1));
        switch (o)
        {
            case Overlay.Sweat:
                rc.DC.FillGeometry(Path(rc, Sweat), rc.Brush(new Color4(0x9F / 255f, 0xDC / 255f, 0xFA / 255f, 1)));
                rc.DC.DrawGeometry(Path(rc, Sweat), white, 1.5f);
                break;
            case Overlay.Star:
                rc.DC.FillGeometry(Path(rc, Star), rc.Brush(t.Color("barWarn")));
                rc.DC.DrawGeometry(Path(rc, Star), white, 1.2f);
                break;
            case Overlay.Spiral:
                var red = rc.Brush(t.Color("red"));
                rc.DC.FillEllipse(new Ellipse(new(10, 10), 9, 9), white);
                rc.DC.DrawEllipse(new Ellipse(new(10, 10), 9, 9), red, 1.5f);
                rc.DC.DrawGeometry(Path(rc, SpiralPath), red, 1.5f);
                break;
        }
        rc.DC.Transform = saved;
    }

    /// <summary>An Archimedean spiral, unwound from the centre: the "dizzy" mark.</summary>
    private static readonly string SpiralPath = BuildSpiral();

    private static string BuildSpiral()
    {
        var sb = new System.Text.StringBuilder("M10 10");
        for (int i = 1; i <= 40; i++)
        {
            double a = i * 0.32, r = 0.75 * a;
            sb.Append(CultureInfo.InvariantCulture, $"L{10 + r * Math.Cos(a):F2} {10 + r * Math.Sin(a):F2}");
        }
        return sb.ToString();
    }

    // ---- a minimal SVG path reader: M L H V C Z, absolute and relative ----

    private static ID2D1PathGeometry Parse(ID2D1Factory factory, string d)
    {
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        var cur = Vector2.Zero;
        var start = Vector2.Zero;
        bool open = false;
        int i = 0;
        char cmd = 'M';

        float Num()
        {
            while (i < d.Length && (d[i] == ' ' || d[i] == ',')) i++;
            int s = i;
            if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
            while (i < d.Length && (char.IsAsciiDigit(d[i]) || d[i] == '.')) i++;
            return float.Parse(d.AsSpan(s, i - s), CultureInfo.InvariantCulture);
        }
        bool MoreNumbers()
        {
            while (i < d.Length && (d[i] == ' ' || d[i] == ',')) i++;
            return i < d.Length && (char.IsAsciiDigit(d[i]) || d[i] is '-' or '.' or '+');
        }
        void End(bool close)
        {
            if (open) sink.EndFigure(close ? FigureEnd.Closed : FigureEnd.Open);
            open = false;
        }

        while (i < d.Length)
        {
            if (char.IsLetter(d[i])) cmd = d[i++];
            bool rel = char.IsLower(cmd);
            Vector2 o = rel ? cur : Vector2.Zero;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    End(false);
                    cur = o + new Vector2(Num(), Num());
                    start = cur;
                    sink.BeginFigure(cur, FigureBegin.Filled);
                    open = true;
                    cmd = rel ? 'l' : 'L';                 // further pairs are line-tos
                    break;
                case 'L': cur = o + new Vector2(Num(), Num()); sink.AddLine(cur); break;
                case 'H': cur = new Vector2((rel ? cur.X : 0) + Num(), cur.Y); sink.AddLine(cur); break;
                case 'V': cur = new Vector2(cur.X, (rel ? cur.Y : 0) + Num()); sink.AddLine(cur); break;
                case 'C':
                {
                    var p1 = o + new Vector2(Num(), Num());
                    var p2 = o + new Vector2(Num(), Num());
                    var p3 = o + new Vector2(Num(), Num());
                    sink.AddBezier(new BezierSegment { Point1 = p1, Point2 = p2, Point3 = p3 });
                    cur = p3;
                    break;
                }
                case 'Z':
                    End(true);
                    cur = start;
                    while (i < d.Length && d[i] == ' ') i++;
                    continue;
                default:
                    throw new FormatException($"unsupported path command '{cmd}'");
            }
            if (!MoreNumbers() && i < d.Length && !char.IsLetter(d[i])) i++;
        }
        End(false);
        sink.Close();
        return geo;
    }
}
