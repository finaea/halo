using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using Halo.Shared;
using Halo.Shared.Skins;
using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Halo.Widgets.Skins.AzurArchive;

/// <summary>
/// One character's halo shape (D17): a simplified vector drawing of her in-game halo, read from
/// <c>game-art/halos/&lt;id&gt;.json</c>. The shapes are a derivative of the game's character
/// design, so they live with the art and go with it: no file, an unreadable one, or
/// <c>gameArt</c> off, and the face gets the generic ring instead.
///
/// <para><b>The file.</b> Parts are drawn in a unit plane — x right, y down, the halo inside the
/// unit circle — in the halo colour:</para>
/// <code>
/// { "pitch": 68,            // degrees the halo leans back from facing the viewer (or "tilt":
///                           // its height ÷ width, the older form); the far side is drawn smaller
///   "roll": 0,              // degrees it leans sideways, clockwise
///   "scale": 0.85,          // radius as a share of half the face
///   "dx": 0, "dy": -6,      // its centre from the top middle of the face, in px of a 48 px face
///   "depth": 4,             // perspective distance in radii (smaller = stronger; 0 = none)
///   "spin": true,           // false: drawn still even at full motion
///   "glint": [0, -0.9],     // where the spin's glint sits, in the unit plane
///   "parts": [
///     { "ring":    [r] },                       // optional "at": [x, y]
///     { "arc":     [r, from, to] },             // degrees clockwise from 12 o'clock; optional "at"
///     { "ellipse": [x, y, rx, ry] },
///     { "poly":    [x1, y1, x2, y2, ...], "closed": true },
///     { "star":    [x, y, r, points], "inner": 0.4 },
///     { "dot":     [x, y, r] } ],               // always filled
///   // every part: "w" stroke width (unit plane, default 0.12), "fill", "tone" (-1 black … 1 white),
///   // "alpha" (0..1)
/// }
/// </code>
///
/// <para>The parts are always drawn flat-on, once, and then posed in 3D by one matrix
/// (<see cref="AmbientLoop.Pose"/>: pitch, roll, a mild perspective). Still, the flat bitmap is
/// projected with D2D's perspective <c>DrawBitmap</c> and the result baked; spinning, the window's
/// DComp visual turns the flat surface about the halo's own axis under the same pose. So the two
/// match, and the per-frame cost of the spin is unchanged.</para>
/// </summary>
internal sealed class HaloShape
{
    public const string Folder = "game-art/halos";

    public required string Id { get; init; }
    public float Tilt { get; init; } = DefaultTilt;
    /// <summary>Degrees from facing the viewer; from <see cref="Tilt"/> when the file gives none.</summary>
    public float Pitch { get; init; } = MathF.Acos(DefaultTilt) * 180 / MathF.PI;
    public float Roll { get; init; }
    public float Scale { get; init; } = DefaultScale;
    public float Dx { get; init; }
    public float Dy { get; init; } = DefaultDy;
    /// <summary>Perspective distance in radii: smaller = stronger, 0 = none.</summary>
    public float Depth { get; init; } = DefaultDepth;
    public bool Spin { get; init; } = true;
    public Vector2 Glint { get; init; } = new(0, -0.9f);
    public required IReadOnlyList<HaloPart> Parts { get; init; }

    /// <summary>The generic ring's tilt: 8 px tall on a 28 px ring.</summary>
    public const float DefaultTilt = 4f / 14f;
    public const float DefaultScale = 0.85f, DefaultDy = -6, DefaultDepth = 4;

    public float PitchRad => Pitch * MathF.PI / 180;
    public float RollRad => Roll * MathF.PI / 180;

    /// <summary>The ring a face gets with no shape of her own (no file, or an unknown id).</summary>
    public static readonly HaloShape Generic = new()
    {
        Id = "",
        Parts = [HaloPart.Ring(0.9f, 0.14f)],
    };

    // concurrent only for the tests: they draw faces from parallel classes while another clears it
    private static readonly ConcurrentDictionary<string, HaloShape?> Cache = new(StringComparer.Ordinal);

    /// <summary>The bundled shape for a character, or null (generic ring). Read once per id, so a
    /// repaint never touches the disk.</summary>
    public static HaloShape? For(string id)
        => Cache.GetOrAdd(id, i => Load(Path.Combine(Paths.AssetsDir, "skins", AzurArchiveSkinInfo.Id, Folder, i + ".json"), i));

    internal static void ResetForTests() => Cache.Clear();

    /// <summary>Parse one file. Missing = null quietly; present but unreadable = null with a warning,
    /// so a broken file draws the generic ring rather than half a halo.</summary>
    internal static HaloShape? Load(string path, string id)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var parts = new List<HaloPart>();
            foreach (var p in root.GetProperty("parts").EnumerateArray())
                parts.Add(HaloPart.Parse(p));
            if (parts.Count == 0) throw new JsonException("no parts");
            float tilt = root.TryGetProperty("tilt", out var t) ? t.GetSingle() : DefaultTilt;
            if (tilt is <= 0.05f or > 1.5f) throw new JsonException($"tilt {tilt} out of range");
            float Num(string key, float fallback) => root.TryGetProperty(key, out var v) ? v.GetSingle() : fallback;
            float pitch = Num("pitch", MathF.Acos(Math.Min(tilt, 1)) * 180 / MathF.PI);
            float scale = Num("scale", DefaultScale), depth = Num("depth", DefaultDepth);
            if (pitch is < 0 or > 85) throw new JsonException($"pitch {pitch} out of range");
            if (scale is <= 0.1f or > 2) throw new JsonException($"scale {scale} out of range");
            if (depth is < 0 or > 50) throw new JsonException($"depth {depth} out of range");
            var glint = root.TryGetProperty("glint", out var g) ? new Vector2(g[0].GetSingle(), g[1].GetSingle()) : new Vector2(0, -0.9f);
            return new HaloShape
            {
                Id = id,
                Tilt = tilt,
                Pitch = pitch,
                Roll = Num("roll", 0),
                Scale = scale,
                Dx = Num("dx", 0),
                Dy = Num("dy", DefaultDy),
                Depth = depth,
                Spin = !root.TryGetProperty("spin", out var sp) || sp.GetBoolean(),
                Glint = glint,
                Parts = parts,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        {
            Log.For("azur-halos").Warn($"could not read {path} ({ex.GetType().Name}: {ex.Message}); generic ring");
            return null;
        }
    }

    /// <summary>Draw every part centred on <paramref name="c"/>, the unit plane scaled to
    /// <paramref name="rx"/> × <paramref name="ry"/>. Strokes are <c>w × rx</c> wide whatever the
    /// squash, so a flat halo's front edge does not thin to a hairline.</summary>
    public void Draw(RenderContext rc, Color4 colour, Vector2 c, float rx, float ry)
    {
        using var factory = rc.DC.Factory;
        var m = Matrix3x2.CreateScale(rx, ry) * Matrix3x2.CreateTranslation(c);
        foreach (var part in Parts)
        {
            var geometry = part.Geometry(factory);
            using var placed = factory.CreateTransformedGeometry(geometry, m);
            var brush = rc.Brush(part.Shade(colour));
            if (part.Fill) rc.DC.FillGeometry(placed, brush);
            else rc.DC.DrawGeometry(placed, brush, part.Width * rx, Join(factory));
        }
    }

    /// <summary>This halo's pose for a flat-on radius <paramref name="r"/> (any unit), at its own
    /// pitch or <paramref name="pitch"/> (degrees) — a cramped seat leans it flatter.</summary>
    public Matrix4x4 Pose(float r, float? pitch = null) => AmbientLoop.Pose((pitch ?? Pitch) * MathF.PI / 180, RollRad, Depth, r);

    /// <summary>Margin past the unit circle for strokes, as a share of the radius.</summary>
    private const float Reach = 1.12f;

    /// <summary>Where the posed halo lands, relative to its centre, for radius <paramref name="r"/>.</summary>
    public Rect PosedBounds(float r, float? pitch = null)
    {
        var m = Pose(r, pitch);
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int i = 0; i < 48; i++)
        {
            float a = i * MathF.PI / 24;
            var v = Vector4.Transform(new Vector4(MathF.Cos(a) * r * Reach, MathF.Sin(a) * r * Reach, 0, 1), m);
            float x = v.X / v.W, y = v.Y / v.W;
            x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
        }
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// The halo still, posed, centred on <paramref name="c"/> with flat-on radius <paramref name="r"/>,
    /// baked once per colour and size (<see cref="Az.DrawBaked"/>) so a repaint is one bitmap draw.
    /// The bake draws the parts flat-on at twice the pixel density, then projects that bitmap with
    /// the pose: the picture the spin's visual shows at angle 0.
    /// </summary>
    public void DrawPosed(RenderContext rc, Color4 colour, Vector2 c, float r, float pitch, float opacity)
    {
        var box = PosedBounds(r, pitch);
        var dest = new Rect(c.X + box.Left, c.Y + box.Top, box.Width, box.Height);
        float density = rc.PixelScale * 2;
        Az.DrawBaked(rc, $"halo:{Id}|{colour}|{pitch}|{Roll}|{Depth}|{r:F3}", dest, b =>
        {
            float reach = r * Reach;
            int side = Math.Max(2, (int)MathF.Ceiling(2 * reach * density));
            using var device = b.DC.Device;
            using var fdc = device.CreateDeviceContext(DeviceContextOptions.None);
            using var flat = fdc.CreateBitmap(new SizeI(side, side), IntPtr.Zero, 0, new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
            fdc.Target = flat;
            fdc.BeginDraw();
            fdc.Clear(new Color4(0, 0, 0, 0));
            fdc.Transform = Matrix3x2.CreateScale(side / (2 * reach)) * Matrix3x2.CreateTranslation(side / 2f, side / 2f);
            using (var frc = new RenderContext(fdc, rc.DWrite, rc.CustomFonts, rc.Theme))
                Draw(frc, colour, Vector2.Zero, r, r);
            fdc.EndDraw();
            fdc.Target = null;
            // the perspective applies before the world transform, about the origin: the halo's
            // centre goes there, then the world transform moves it to c
            var saved = b.DC.Transform;
            b.DC.Transform = Matrix3x2.CreateTranslation(c) * saved;
            b.DC.DrawBitmap(flat, new Rect(-reach, -reach, 2 * reach, 2 * reach), 1, Vortice.Direct2D1.InterpolationMode.Linear, null, Pose(r, pitch));
            b.DC.Transform = saved;
        }, opacity);
    }

    private static ID2D1StrokeStyle? _join;
    private static nint _joinFactory;

    /// <summary>Round joins so a polygon's corners stay solid at a few pixels; flat caps so an
    /// arc's ends stay crisp.</summary>
    private static ID2D1StrokeStyle Join(ID2D1Factory factory)
    {
        if (_join == null || _joinFactory != factory.NativePointer)
        {
            _join?.Dispose();
            _join = factory.CreateStrokeStyle(new StrokeStyleProperties { LineJoin = LineJoin.Round, MiterLimit = 2 });
            _joinFactory = factory.NativePointer;
        }
        return _join;
    }
}

/// <summary>One drawn piece of a <see cref="HaloShape"/>, in the unit plane.</summary>
internal sealed class HaloPart
{
    public required string Kind { get; init; }
    public required float[] Args { get; init; }
    public Vector2 At { get; init; }
    public float Width { get; init; } = 0.12f;
    public bool Fill { get; init; }
    public bool Closed { get; init; }
    public float Inner { get; init; } = 0.4f;
    public float Tone { get; init; }
    public float Alpha { get; init; } = 1;

    private ID2D1Geometry? _geometry;
    private nint _factory;

    private static readonly Dictionary<string, int> Arity = new(StringComparer.Ordinal)
    {
        ["ring"] = 1, ["arc"] = 3, ["ellipse"] = 4, ["poly"] = 4, ["star"] = 4, ["dot"] = 3,
    };

    public static HaloPart Ring(float r, float w) => new() { Kind = "ring", Args = [r], Width = w };

    public static HaloPart Parse(JsonElement p)
    {
        string? kind = null;
        foreach (var k in Arity.Keys)
            if (p.TryGetProperty(k, out _)) { kind = k; break; }
        if (kind == null) throw new JsonException("part has no shape (ring, arc, ellipse, poly, star, dot)");
        var args = p.GetProperty(kind).EnumerateArray().Select(e => e.GetSingle()).ToArray();
        if (args.Length < Arity[kind] || (kind == "poly" && args.Length % 2 != 0))
            throw new JsonException($"{kind} needs {Arity[kind]}{(kind == "poly" ? "+ (even)" : "")} numbers, has {args.Length}");
        return new HaloPart
        {
            Kind = kind,
            Args = args,
            At = p.TryGetProperty("at", out var at) ? new Vector2(at[0].GetSingle(), at[1].GetSingle()) : Vector2.Zero,
            Width = p.TryGetProperty("w", out var w) ? w.GetSingle() : 0.12f,
            Fill = kind == "dot" || (p.TryGetProperty("fill", out var f) && f.GetBoolean()),
            Closed = kind != "poly" || !p.TryGetProperty("closed", out var cl) || cl.GetBoolean(),
            Inner = p.TryGetProperty("inner", out var i) ? i.GetSingle() : 0.4f,
            Tone = p.TryGetProperty("tone", out var t) ? Math.Clamp(t.GetSingle(), -1, 1) : 0,
            Alpha = p.TryGetProperty("alpha", out var a) ? Math.Clamp(a.GetSingle(), 0, 1) : 1,
        };
    }

    /// <summary>The halo colour toned toward white (tone &gt; 0) or black (&lt; 0).</summary>
    public Color4 Shade(Color4 c)
    {
        float k = Math.Abs(Tone), to = Tone > 0 ? 1 : 0;
        return new Color4(c.R + (to - c.R) * k, c.G + (to - c.G) * k, c.B + (to - c.B) * k, c.A * Alpha);
    }

    /// <summary>The part's unit-plane geometry, built once per D2D factory (geometries are factory
    /// resources, shared by every device context on it).</summary>
    public ID2D1Geometry Geometry(ID2D1Factory factory)
    {
        if (_geometry != null && _factory == factory.NativePointer) return _geometry;
        _geometry?.Dispose();
        _geometry = Build(factory);
        _factory = factory.NativePointer;
        return _geometry;
    }

    private static Vector2 OnCircle(Vector2 c, float r, float deg)
    {
        float a = deg * MathF.PI / 180;
        return c + new Vector2(MathF.Sin(a) * r, -MathF.Cos(a) * r);
    }

    private ID2D1Geometry Build(ID2D1Factory factory)
    {
        var a = Args;
        switch (Kind)
        {
            case "ring": return factory.CreateEllipseGeometry(new Ellipse(At, a[0], a[0]));
            case "ellipse": return factory.CreateEllipseGeometry(new Ellipse(new Vector2(a[0], a[1]), a[2], a[3]));
            case "dot": return factory.CreateEllipseGeometry(new Ellipse(new Vector2(a[0], a[1]), a[2], a[2]));
        }

        var path = factory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            switch (Kind)
            {
                case "arc":
                {
                    float r = a[0], from = a[1], to = a[2];
                    while (to <= from) to += 360;
                    sink.BeginFigure(OnCircle(At, r, from), FigureBegin.Hollow);
                    // two halves, so a sweep of 180° or more never hits the large-arc ambiguity
                    float mid = (from + to) / 2;
                    sink.AddArc(new ArcSegment { Point = OnCircle(At, r, mid), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Small });
                    sink.AddArc(new ArcSegment { Point = OnCircle(At, r, to), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Small });
                    sink.EndFigure(FigureEnd.Open);
                    break;
                }
                case "poly":
                {
                    sink.BeginFigure(new Vector2(a[0], a[1]), Fill ? FigureBegin.Filled : FigureBegin.Hollow);
                    for (int i = 2; i + 1 < a.Length; i += 2) sink.AddLine(new Vector2(a[i], a[i + 1]));
                    sink.EndFigure(Closed ? FigureEnd.Closed : FigureEnd.Open);
                    break;
                }
                case "star":
                {
                    var c = new Vector2(a[0], a[1]);
                    float r = a[2];
                    int n = Math.Max(2, (int)a[3]);
                    sink.BeginFigure(OnCircle(c, r, 0), Fill ? FigureBegin.Filled : FigureBegin.Hollow);
                    for (int i = 1; i < n * 2; i++)
                        sink.AddLine(OnCircle(c, i % 2 == 0 ? r : r * Inner, i * 180f / n));
                    sink.EndFigure(FigureEnd.Closed);
                    break;
                }
            }
            sink.Close();
        }
        return path;
    }
}
