using System.Numerics;
using Halo.Widgets.PanelModels;
using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Halo.Widgets.Skins.AzurArchive;

/// <summary>
/// Measurements, type and colour rules shared by every Azur Archive element.
///
/// <para>The approved mockups are HTML at real size (350 px = 206 units × 1.7), so measurements are
/// written here in mockup CSS pixels and converted once: <see cref="U"/> for lengths, <see cref="Pt"/>
/// for font sizes. That keeps every number checkable against <c>azur.css</c>.</para>
/// </summary>
internal static class Az
{
    /// <summary>CSS px of the mockups → logical units.</summary>
    public static float U(double px) => (float)(px / 1.7);

    /// <summary>CSS px font size → the points <see cref="TextStyle"/> takes (px = pt · 96 / 72).</summary>
    public static double Pt(double px) => px / 1.7 * 0.75;

    // ---- card frame (logical units) ----

    /// <summary>Card inset inside the window: room for the drop shadow, which a card flush with its
    /// window edge would have clipped.</summary>
    public const float InsetX = 4, InsetTop = 3, InsetBottom = 5;
    public static float CardX => InsetX;
    public static float CardW(Theme t) => (float)t.BgWidth - 2 * InsetX;
    /// <summary>Content column: the mockup's 12 px padding inside the card.</summary>
    public static float Left => InsetX + U(12);
    public static float Right(Theme t) => (float)t.BgWidth - InsetX - U(12);
    public static float Width(Theme t) => Right(t) - Left;
    public static readonly float HeaderH = U(37);
    /// <summary>Where the first block starts, just under the header rule.</summary>
    public static float ContentTop => InsetTop + HeaderH + U(2);
    public static readonly float Radius = U(10);

    // ---- type (the family names are DirectWrite's, not the file names — ticket 06) ----

    private const string Barlow = "Barlow", Rounded = "Rounded Mplus 1c", Oxanium = "Oxanium SemiBold";

    public static TextStyle Bar(double px, FontWeight w, float trackPx = 0, bool tab = true)
        => new(Pt(px), false, Barlow) { Weight = w, Stretch = FontStretch.Condensed, Tabular = tab, Tracking = Track(trackPx, px) };

    public static TextStyle Mp(double px, FontWeight w, float trackPx = 0)
        => new(Pt(px), false, Rounded) { Weight = w, Tracking = Track(trackPx, px) };

    public static TextStyle Ox(double px, float trackPx = 0, float shear = 0)
        => new(Pt(px), false, Oxanium) { Tracking = Track(trackPx, px), Shear = shear };

    private static float Track(float trackPx, double px) => trackPx == 0 ? 0 : (float)(trackPx / px);

    /// <summary>tan 10°: sub-labels lean, digits never do (design rule 3).</summary>
    public const float SubShear = 0.176f;

    public static readonly TextStyle Title = Bar(21, FontWeight.Bold, 0.4f, tab: false);
    public static readonly TextStyle Sub = Ox(9, 0.6f, SubShear);
    public static readonly TextStyle Caption = Ox(10, 0.5f);
    public static readonly TextStyle CaptionVal = Bar(12, FontWeight.SemiBold, 0.2f);
    public static readonly TextStyle HeroNum = Bar(48, FontWeight.Light);
    public static readonly TextStyle HeroUnit = Bar(19, FontWeight.Medium);
    public static readonly TextStyle RowLabel = Mp(10.5, FontWeight.ExtraBold, 0.2f);
    public static readonly TextStyle RowDetail = Mp(10, FontWeight.Medium);
    public static readonly TextStyle RowVal = Bar(16, FontWeight.Medium, 0.1f);
    public static readonly TextStyle RowUnit = Bar(12, FontWeight.Medium);
    public static readonly TextStyle MaxLabel = Ox(9, 0.3f);
    public static readonly TextStyle MaxVal = Bar(12, FontWeight.Medium);
    public static readonly TextStyle Band = Ox(9, 0.8f);
    public static readonly TextStyle BandRight = Bar(11, FontWeight.Medium, 0.2f);
    public static readonly TextStyle ChipLabel = Ox(9, 0.5f);
    public static readonly TextStyle ChipVal = Bar(14, FontWeight.Medium, 0.1f);
    public static readonly TextStyle ChipUnit = Bar(11, FontWeight.Medium);
    public static readonly TextStyle Grid = Bar(12, FontWeight.Medium);
    public static readonly TextStyle Tag = Ox(9, 0.6f);
    public static readonly TextStyle Foot = Ox(8.5, 0.8f);

    // ---- drawing text by baseline ----

    private static readonly Dictionary<TextStyle, float> Baselines = new();

    /// <summary>Distance from a layout's top to its baseline. CSS aligns rows by baseline, and
    /// three families with different ascents only line up when placed that way.</summary>
    public static float Ascent(RenderContext rc, TextStyle s)
    {
        if (Baselines.TryGetValue(s, out float b)) return b;
        var layout = rc.Layout("Ag8", s);
        var lines = new LineMetrics[1];
        layout.GetLineMetrics(lines, out _);
        b = lines[0].Baseline;
        Baselines[s] = b;
        return b;
    }

    /// <summary>Draw with the baseline at <paramref name="baseline"/>; returns the text's width.</summary>
    public static float Text(RenderContext rc, string text, TextStyle s, Color4 c, float x, float baseline, TextAlign align = TextAlign.Left)
    {
        if (text.Length == 0) return 0;
        rc.DrawText(text, s, c, x, baseline - Ascent(rc, s), align);
        return rc.TextWidth(text, s);
    }

    public static float W(RenderContext rc, string text, TextStyle s) => text.Length == 0 ? 0 : rc.TextWidth(text, s);

    /// <summary>A number and its smaller unit on one baseline, right-aligned at <paramref name="right"/>
    /// (or left-aligned at it). Returns the left edge.</summary>
    public static float Reading(RenderContext rc, Val v, TextStyle num, TextStyle unit, Color4 numC, Color4 unitC,
        float right, float baseline, bool alignLeft = false, float gap = 0.6f)
    {
        float wn = W(rc, v.Text, num);
        float wu = v.Unit.Length == 0 ? 0 : W(rc, v.Unit, unit) + U(gap);
        float x = alignLeft ? right : right - wn - wu;
        Text(rc, v.Text, num, numC, x, baseline);
        if (wu > 0) Text(rc, v.Unit, unit, unitC, x + wn + U(gap), baseline);
        return x;
    }

    // ---- shapes ----

    private static nint _factory;
    private static readonly Dictionary<(string, float, float, float, float, float), ID2D1Geometry> Geo = new();

    /// <summary>
    /// A path geometry for a polygon or a slanted box, cached by what it is and where. Geometries are
    /// factory resources, so the cache starts over when <see cref="Dx.Recreate"/> brings a new
    /// factory; it is also capped, because a bar's fill width changes with its reading.
    /// </summary>
    public static ID2D1Geometry Poly(RenderContext rc, string kind, float x, float y, float w, float h, float a, Func<Vector2[]> points)
    {
        using var factory = rc.DC.Factory;
        if (factory.NativePointer != _factory) { Clear(); _factory = factory.NativePointer; }
        var key = (kind, x, y, w, h, a);
        if (Geo.TryGetValue(key, out var g)) return g;
        if (Geo.Count > 384) Clear();
        var path = factory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            var pts = points();
            sink.BeginFigure(pts[0], FigureBegin.Filled);
            for (int i = 1; i < pts.Length; i++) sink.AddLine(pts[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        Geo[key] = path;
        return path;
    }

    /// <summary>A rounded rectangle as a cached geometry — a layer mask, so it is not rebuilt per draw.</summary>
    public static ID2D1Geometry RoundRect(RenderContext rc, Rect r, float radius)
    {
        using var factory = rc.DC.Factory;
        if (factory.NativePointer != _factory) { Clear(); _factory = factory.NativePointer; }
        var key = ("rrect", r.Left, r.Top, r.Width, r.Height, radius);
        if (Geo.TryGetValue(key, out var g)) return g;
        if (Geo.Count > 384) Clear();
        g = factory.CreateRoundedRectangleGeometry(new RoundedRectangle { Rect = r, RadiusX = radius, RadiusY = radius });
        Geo[key] = g;
        return g;
    }

    private static void Clear()
    {
        foreach (var g in Geo.Values) g.Dispose();
        Geo.Clear();
    }

    /// <summary>Box whose right edge is cut back at 20° (tan .364) — bars and tags (rule 2).</summary>
    public static ID2D1Geometry CutRight(RenderContext rc, float x, float y, float w, float h, float cut)
        => Poly(rc, "cutR", x, y, w, h, cut, () => [new(x, y), new(x + w, y), new(x + Math.Max(0, w - cut), y + h), new(x, y + h)]);

    /// <summary>Box with its bottom-left corner cut at 45° — AL's Ship-Info row strip.</summary>
    public static ID2D1Geometry Strip(RenderContext rc, float x, float y, float w, float h, float cut)
        => Poly(rc, "strip", x, y, w, h, cut, () => [new(x, y), new(x + w, y), new(x + w, y + h), new(x + cut, y + h), new(x, y + h - cut)]);

    /// <summary>A parallelogram leaning right by <paramref name="slant"/> (BA tags).</summary>
    public static ID2D1Geometry Lean(RenderContext rc, float x, float y, float w, float h, float slant)
        => Poly(rc, "lean", x, y, w, h, slant, () => [new(x + slant, y), new(x + w, y), new(x + w - slant, y + h), new(x, y + h)]);

    public static ID2D1Geometry Diamond(RenderContext rc, float cx, float cy, float r)
        => Poly(rc, "dia", cx, cy, r, r, 0, () => [new(cx, cy - r), new(cx + r, cy), new(cx, cy + r), new(cx - r, cy)]);

    /// <summary>Fill a geometry with a -60° stripe hatch (critical, rule 7): the base colour, then
    /// stripes of the second clipped to the shape.</summary>
    public static void Hatch(RenderContext rc, ID2D1Geometry shape, Color4 a, Color4 b, float stripe, float gap)
    {
        rc.DC.FillGeometry(shape, rc.Brush(a));
        var r = shape.GetBounds();
        rc.DC.PushLayer(new LayerParameters1 { ContentBounds = r, GeometricMask = shape, Opacity = 1, MaskTransform = Matrix3x2.Identity }, null!);
        var brush = rc.Brush(b);
        float h = r.Bottom - r.Top, run = h * 0.577f;     // tan 30° — a -60° stripe
        for (float x = r.Left - run; x < r.Right; x += stripe + gap)
            rc.DC.DrawLine(new Vector2(x, r.Bottom), new Vector2(x + run, r.Top), brush, gap);
        rc.DC.PopLayer();
    }

    // ---- baked decorations ----

    private static nint _bakeDevice;
    private static readonly Dictionary<string, ID2D1Bitmap1> Baked = new();

    /// <summary>
    /// Draw something that never changes between repaints — a clipped face crop, the corner
    /// texture — from a bitmap rendered once. A card repaints whenever a graph column passes, and
    /// redoing a layer mask and a cubic downscale (or a field of triangles) every time cost more
    /// than everything else on the card together (measured: art and texture were ~2 % of a core
    /// across the default layout; baked, they are a bitmap draw each).
    ///
    /// <para><paramref name="key"/> must name everything the drawing depends on (slot, colours);
    /// the size and pixel scale are added here. Baked on a private context of the same device, the
    /// way <see cref="RenderContext.Glow"/> does, and dropped when the device changes.</para>
    /// </summary>
    /// <summary>Drop every baked bitmap and forget their device. <see cref="Dx"/> calls this before its
    /// device goes away, so a skin no widget shows any more does not pin the old device.</summary>
    public static void InvalidateBaked()
    {
        foreach (var b in Baked.Values) b.Dispose();
        Baked.Clear();
        _bakeDevice = 0;
    }

    /// <summary>Bitmaps currently baked (tests).</summary>
    internal static int BakedCount => Baked.Count;

    public static void DrawBaked(RenderContext rc, string key, Rect dest, Action<RenderContext> draw, float opacity = 1f)
    {
        float scale = rc.PixelScale;
        int pw = Math.Max(1, (int)Math.Ceiling(dest.Width * scale)), ph = Math.Max(1, (int)Math.Ceiling(dest.Height * scale));
        string k = $"{key}|{pw}x{ph}";
        using var device = rc.DC.Device;
        if (device.NativePointer != _bakeDevice)
        {
            foreach (var b in Baked.Values) b.Dispose();
            Baked.Clear();
            _bakeDevice = device.NativePointer;
        }
        if (!Baked.TryGetValue(k, out var bmp))
        {
            if (Baked.Count >= 96)
            {
                foreach (var b in Baked.Values) b.Dispose();
                Baked.Clear();
            }
            var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target);
            using var bdc = device.CreateDeviceContext(DeviceContextOptions.None);
            bmp = bdc.CreateBitmap(new SizeI(pw, ph), IntPtr.Zero, 0, props);
            bdc.Target = bmp;
            bdc.BeginDraw();
            bdc.Clear(new Color4(0, 0, 0, 0));
            bdc.Transform = Matrix3x2.CreateTranslation(-dest.Left, -dest.Top) * Matrix3x2.CreateScale(pw / dest.Width, ph / dest.Height);
            using (var brc = new RenderContext(bdc, rc.DWrite, rc.CustomFonts, rc.Theme))
                draw(brc);
            bdc.EndDraw();
            bdc.Target = null;
            Baked[k] = bmp;
        }
        rc.DC.DrawBitmap(bmp, dest, opacity, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
    }

    // ---- colour ----

    /// <summary>A token with its alpha scaled — for the tints the mockups take from one colour.</summary>
    public static Color4 Alpha(Color4 c, float a) => new(c.R, c.G, c.B, c.A * a);

    /// <summary>Key-bar / emblem token for a panel type (design §5: the panel's primary data colour).</summary>
    public static string KeyToken(string type) => type switch
    {
        "cpu-ram" => "cpuUsage",
        "gpu" => "gpuUsage",
        "fps" => "keyFps",
        "latency" => "histogram",
        "power" => "keyPower",
        "drives" => "keyDrives",
        "network" => "netDown",
        "fans" => "keyFans",
        "companion" => "talkHeader",
        _ => "keyTop",
    };
}

/// <summary>
/// One card's state this tick: warn step, why it has no data, collector down. Header, hero, bars and
/// the character all read it, so it is worked out once per tick and shared.
/// </summary>
internal sealed class AzurCard
{
    public required PanelModel Model { get; init; }
    public required string Character { get; init; }
    public required string NormalMood { get; init; }

    private long _tick = long.MinValue, _qpc;

    public WarnLevel Level { get; private set; }
    public NoData Missing { get; private set; }
    public bool Down => Missing == NoData.Collector;
    public bool Warn => Level == WarnLevel.L4;
    public bool Crit => Level == WarnLevel.L5;

    public AzurCard Eval(PanelContext c)
    {
        if (c.TickIndex == _tick && c.NowQpc == _qpc) return this;
        _tick = c.TickIndex; _qpc = c.NowQpc;
        Missing = c.Stale ? NoData.Collector : Model.Missing(c);
        // a card with no reading claims no state: N/A beats any warn step (freshness contract)
        Level = Missing == NoData.None ? Model.State(c) : WarnLevel.None;
        return this;
    }

    /// <summary>The colour a data token is drawn in now: grey when the collector is gone, so the
    /// whole card visibly stops claiming anything (design rule 10).</summary>
    public string Data(string token) => Down ? "inactiveButton" : token;

    public string KeyToken => Down || Missing == NoData.Sensor ? "inactiveButton" : Warn ? "barWarn" : Crit ? "red" : Az.KeyToken(Model.Type);

    /// <summary>The hero number's colour for its own step.</summary>
    public static string HeroToken(WarnLevel l, bool na)
        => na ? "inactiveButton" : l == WarnLevel.L5 ? "devWarn5" : l == WarnLevel.L4 ? "devWarn4" : "text";
}
