using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Halo.Widgets.Render;

public enum ShapeKind { Chamfer, Parallelogram, Notch, Diamond, TriangleMosaic, Ellipse }

[Flags]
public enum Corners { None = 0, TopLeft = 1, TopRight = 2, BottomRight = 4, BottomLeft = 8, All = 15 }

/// <summary>
/// A shape and where it sits, in absolute logical units. It is the cache key for
/// <see cref="RenderContext.Shape"/>, so build one with the factory methods rather than by hand:
/// what <see cref="A"/> and <see cref="B"/> mean depends on the kind.
///
/// <para>Every shape stays inside its box. A skin slants and chamfers chrome <i>within</i> the
/// rectangle the flow layout gave it, so measuring and click-through never change (UI craft
/// research, rule 5).</para>
/// </summary>
public readonly record struct ShapeSpec(ShapeKind Kind, float X, float Y, float W, float H,
    float A = 0, float B = 0, Corners Corners = Corners.All)
{
    /// <summary>Rectangle with 45° cuts of <paramref name="cut"/> on the chosen corners.</summary>
    public static ShapeSpec Chamfer(float x, float y, float w, float h, float cut, Corners corners = Corners.All)
        => new(ShapeKind.Chamfer, x, y, w, h, cut, 0, corners);

    /// <summary>Box with its top edge pushed <paramref name="slant"/> to the right of its bottom
    /// edge (negative leans left). Use <c>tan(angle) × h</c> for an angle.</summary>
    public static ShapeSpec Parallelogram(float x, float y, float w, float h, float slant)
        => new(ShapeKind.Parallelogram, x, y, w, h, slant);

    /// <summary>Rectangle with a bite out of the middle of its top edge: <paramref name="width"/>
    /// across the top, <paramref name="depth"/> deep, 45° sides — a ticket tab's slot.</summary>
    public static ShapeSpec Notch(float x, float y, float w, float h, float width, float depth)
        => new(ShapeKind.Notch, x, y, w, h, width, depth);

    public static ShapeSpec Diamond(float x, float y, float w, float h)
        => new(ShapeKind.Diamond, x, y, w, h);

    public static ShapeSpec Ellipse(float x, float y, float w, float h)
        => new(ShapeKind.Ellipse, x, y, w, h);

    /// <summary>Square cells of <paramref name="cell"/>, each split on its diagonal; roughly
    /// <paramref name="density"/> (0–1) of the triangles are kept. The pattern is a fixed hash of
    /// row and column, so it is the same on every repaint and every machine.</summary>
    public static ShapeSpec TriangleMosaic(float x, float y, float w, float h, float cell, float density)
        => new(ShapeKind.TriangleMosaic, x, y, w, h, cell, density);
}

/// <summary>Builds the geometry for a <see cref="ShapeSpec"/>. Callers go through
/// <see cref="RenderContext.Shape"/>, which caches the result.</summary>
public static class Shapes
{
    public static ID2D1Geometry Build(ID2D1Factory factory, ShapeSpec s)
    {
        if (s.Kind == ShapeKind.Ellipse)
            return factory.CreateEllipseGeometry(new Ellipse(new Vector2(s.X + s.W / 2, s.Y + s.H / 2), s.W / 2, s.H / 2));

        var geo = factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            switch (s.Kind)
            {
                case ShapeKind.Chamfer: Chamfer(sink, s); break;
                case ShapeKind.Parallelogram:
                {
                    // keep both slanted edges inside the box whichever way it leans
                    float a = Math.Max(0, s.A), b = Math.Max(0, -s.A);
                    Polygon(sink, new(s.X + a, s.Y), new(s.X + s.W - b, s.Y),
                        new(s.X + s.W - a, s.Y + s.H), new(s.X + b, s.Y + s.H));
                    break;
                }
                case ShapeKind.Notch:
                {
                    float cx = s.X + s.W / 2, half = s.A / 2, d = Math.Min(s.B, half);
                    Polygon(sink, new(s.X, s.Y), new(cx - half, s.Y), new(cx - half + d, s.Y + d),
                        new(cx + half - d, s.Y + d), new(cx + half, s.Y), new(s.X + s.W, s.Y),
                        new(s.X + s.W, s.Y + s.H), new(s.X, s.Y + s.H));
                    break;
                }
                case ShapeKind.Diamond:
                    Polygon(sink, new(s.X + s.W / 2, s.Y), new(s.X + s.W, s.Y + s.H / 2),
                        new(s.X + s.W / 2, s.Y + s.H), new(s.X, s.Y + s.H / 2));
                    break;
                case ShapeKind.TriangleMosaic: Mosaic(sink, s); break;
            }
            sink.Close();
        }
        return geo;
    }

    private static void Chamfer(ID2D1GeometrySink sink, ShapeSpec s)
    {
        float c = Math.Min(s.A, Math.Min(s.W, s.H) / 2);
        float l = s.X, t = s.Y, r = s.X + s.W, b = s.Y + s.H;
        bool tl = s.Corners.HasFlag(Corners.TopLeft), tr = s.Corners.HasFlag(Corners.TopRight);
        bool br = s.Corners.HasFlag(Corners.BottomRight), bl = s.Corners.HasFlag(Corners.BottomLeft);
        var pts = new List<Vector2>(8);
        if (tl) { pts.Add(new(l, t + c)); pts.Add(new(l + c, t)); } else pts.Add(new(l, t));
        if (tr) { pts.Add(new(r - c, t)); pts.Add(new(r, t + c)); } else pts.Add(new(r, t));
        if (br) { pts.Add(new(r, b - c)); pts.Add(new(r - c, b)); } else pts.Add(new(r, b));
        if (bl) { pts.Add(new(l + c, b)); pts.Add(new(l, b - c)); } else pts.Add(new(l, b));
        Polygon(sink, [.. pts]);
    }

    private static void Mosaic(ID2D1GeometrySink sink, ShapeSpec s)
    {
        float cell = Math.Max(2, s.A);
        int cols = (int)(s.W / cell), rows = (int)(s.H / cell);
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                float x = s.X + col * cell, y = s.Y + row * cell;
                // alternate the diagonal per cell, so the field reads as triangles, not stripes
                bool flip = ((row + col) & 1) == 1;
                for (int half = 0; half < 2; half++)
                {
                    if (Hash(row, col, half) >= s.B) continue;
                    Vector2 p0 = new(x, y), p1 = new(x + cell, y), p2 = new(x + cell, y + cell), p3 = new(x, y + cell);
                    if (!flip) { if (half == 0) Polygon(sink, p0, p1, p2); else Polygon(sink, p0, p2, p3); }
                    else { if (half == 0) Polygon(sink, p0, p1, p3); else Polygon(sink, p1, p2, p3); }
                }
            }
    }

    /// <summary>Deterministic 0..1 per triangle.</summary>
    private static float Hash(int row, int col, int half)
    {
        uint h = (uint)(row * 73856093) ^ (uint)(col * 19349663) ^ (uint)(half * 83492791);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return (h & 0xFFFF) / 65536f;
    }

    private static void Polygon(ID2D1GeometrySink sink, params Vector2[] pts)
    {
        sink.BeginFigure(pts[0], FigureBegin.Filled);
        for (int i = 1; i < pts.Length; i++) sink.AddLine(pts[i]);
        sink.EndFigure(FigureEnd.Closed);
    }
}
