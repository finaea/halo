using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Halo.Widgets.Render;

/// <summary>
/// A text look. The three positional members are all Rainformer ever sets; everything after them
/// is for skins that bundle their own faces, and at its default changes nothing — a style built
/// as <c>new(8, false)</c> resolves to exactly the format, layout and draw call it always did.
/// </summary>
public readonly record struct TextStyle(double SizePt, bool Bold, string? FontOverride = null)
{
    public static readonly TextStyle Text8 = new(8, false);
    public static readonly TextStyle Bold8 = new(8, true);
    public static readonly TextStyle Bold9 = new(9, true);

    /// <summary>Exact weight; null = <see cref="Bold"/> decides (Bold or Normal).</summary>
    public FontWeight? Weight { get; init; }

    /// <summary>Width class; <see cref="FontStretch.Undefined"/> (the default) means Normal.</summary>
    public FontStretch Stretch { get; init; }

    /// <summary>Extra space after every character, in ems (0.05 = 5 % of the font size).</summary>
    public float Tracking { get; init; }

    /// <summary>Every digit the same advance, so a changing number does not jitter its neighbours.
    /// OpenType <c>tnum</c> when the face has it, else a forced minimum advance per digit.</summary>
    public bool Tabular { get; init; }

    /// <summary>Horizontal shear as a tangent: 0.364 ≈ 20°, top leaning right. For labels and tags
    /// only — numbers stay upright (tech plan §5: no italic faces, digits never lean).</summary>
    public float Shear { get; init; }
}

/// <summary>
/// Per-window D2D drawing context with cached brushes / text formats / text layouts
/// (plan §8: no per-tick re-parse, layouts swapped only when the string changes).
///
/// <para>Everything cached here is either a device resource (brushes, glow bitmaps) or a factory
/// resource (geometries), so this object lives exactly as long as the window's device context:
/// a device recreate builds a new one.</para>
/// </summary>
public sealed class RenderContext : IDisposable
{
    public ID2D1DeviceContext DC { get; }
    public IDWriteFactory DWrite { get; }
    public IDWriteFontCollection1? CustomFonts { get; }
    public Theme Theme { get; }

    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();
    private readonly Dictionary<TextStyle, IDWriteTextFormat> _formats = new();
    // two-generation layout cache: fps panels create new layouts continuously (their numbers
    // change per repaint), and the old clear-everything sweep also disposed hot static labels,
    // forcing rebuilds. Hits promote from the previous generation; a full generation without a
    // hit means an entry is truly stale and gets disposed at the next rotation.
    private Dictionary<(string, TextStyle), IDWriteTextLayout> _layouts = new();
    private Dictionary<(string, TextStyle), IDWriteTextLayout> _layoutsPrev = new();
    private readonly Dictionary<TextStyle, float> _lineHeights = new();

    // skin-only caches: empty for Rainformer, which never asks for any of these
    private IDWriteTypography? _tabular;
    private readonly Dictionary<TextStyle, float> _digitAdvance = new();
    private readonly Dictionary<(uint, uint), ID2D1LinearGradientBrush> _linear = new();
    private readonly Dictionary<(uint, uint), ID2D1RadialGradientBrush> _radial = new();
    private readonly Dictionary<ShapeSpec, ID2D1Geometry> _shapes = new();
    private readonly Dictionary<GlowKey, ID2D1Bitmap1> _glows = new();

    public RenderContext(ID2D1DeviceContext dc, IDWriteFactory dwrite, IDWriteFontCollection1? customFonts, Theme theme)
    {
        DC = dc;
        DWrite = dwrite;
        CustomFonts = customFonts;
        Theme = theme;
    }

    private static uint Pack(Color4 c)
        => ((uint)(c.R * 255) << 24) | ((uint)(c.G * 255) << 16) | ((uint)(c.B * 255) << 8) | (uint)(c.A * 255);

    public ID2D1SolidColorBrush Brush(Color4 color)
    {
        uint key = Pack(color);
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = DC.CreateSolidColorBrush(color);
        _brushes[key] = b;
        return b;
    }

    /// <summary>
    /// Two-stop linear gradient from <paramref name="start"/> to <paramref name="end"/> (logical
    /// units, current transform). Cached by its colours only: the end points are cheap setters, so
    /// one brush serves every rectangle that shares a ramp instead of one brush per position.
    /// </summary>
    public ID2D1LinearGradientBrush LinearGradient(Color4 from, Color4 to, Vector2 start, Vector2 end)
    {
        var key = (Pack(from), Pack(to));
        if (!_linear.TryGetValue(key, out var b))
        {
            // a colour picker dragged in Settings applies in place, a new pair every ~200 ms
            if (_linear.Count >= GradientCap) Clear(_linear);
            using var stops = Stops(from, to);
            b = DC.CreateLinearGradientBrush(new LinearGradientBrushProperties(start, end), stops);
            _linear[key] = b;
        }
        b.StartPoint = start;
        b.EndPoint = end;
        return b;
    }

    /// <summary>Two-stop radial gradient, <paramref name="from"/> at the centre. Cached like
    /// <see cref="LinearGradient"/>.</summary>
    public ID2D1RadialGradientBrush RadialGradient(Color4 from, Color4 to, Vector2 center, float rx, float ry)
    {
        var key = (Pack(from), Pack(to));
        if (!_radial.TryGetValue(key, out var b))
        {
            if (_radial.Count >= GradientCap) Clear(_radial);
            using var stops = Stops(from, to);
            b = DC.CreateRadialGradientBrush(new RadialGradientBrushProperties(center, Vector2.Zero, rx, ry), stops);
            _radial[key] = b;
        }
        b.Center = center;
        b.RadiusX = rx;
        b.RadiusY = ry;
        return b;
    }

    /// <summary>Most gradient brushes of one kind kept per window; past it the cache starts over.</summary>
    internal const int GradientCap = 32;

    internal int LinearGradientCount => _linear.Count;
    internal int RadialGradientCount => _radial.Count;

    private static void Clear<T>(Dictionary<(uint, uint), T> cache) where T : IDisposable
    {
        foreach (var b in cache.Values) b.Dispose();
        cache.Clear();
    }

    private ID2D1GradientStopCollection Stops(Color4 from, Color4 to)
        => DC.CreateGradientStopCollection([new GradientStop(0, from), new GradientStop(1, to)]);

    /// <summary>
    /// The geometry for a shape, built on first use and kept until the spec changes. Specs carry
    /// absolute logical coordinates, so a gradient brush lines up with the shape without a second
    /// transform; an element that does not move asks for the same spec every frame and gets the
    /// same object.
    /// </summary>
    public ID2D1Geometry Shape(ShapeSpec spec)
    {
        if (_shapes.TryGetValue(spec, out var g)) return g;
        // a spec that keeps changing (an element resized every frame) must not grow this forever
        if (_shapes.Count >= 128) ClearShapes();
        using var factory = DC.Factory;
        g = Shapes.Build(factory, spec);
        _shapes[spec] = g;
        return g;
    }

    /// <summary>Physical pixels per logical unit right now: the theme scale in the transform
    /// times the monitor DPI on the context.</summary>
    public float PixelScale
    {
        get
        {
            DC.GetDpi(out float dpiX, out _);
            return DC.Transform.M11 * dpiX / 96f;
        }
    }

    private readonly record struct GlowKey(nint Geometry, Rect Bounds, uint Color, float Sigma, float Scale);

    /// <summary>
    /// A soft glow (or, with an <paramref name="offset"/> and a dark colour, a drop shadow) of a
    /// geometry, drawn under whatever the caller draws next.
    ///
    /// <para><b>Baked, never live.</b> The first call renders the geometry into an offscreen
    /// bitmap, runs D2D's Gaussian blur once and keeps the result; every later call with the same
    /// geometry, colour, radius and pixel scale is one <c>DrawBitmap</c>. A live blur would be GPU
    /// work on every repaint for a decoration that never changes (tooling research §2.4).</para>
    ///
    /// <para>The bake runs on a private device context from the same device, so it can happen in
    /// the middle of this context's <c>BeginDraw</c> without touching its target, transform or the
    /// opacity layer the window has pushed. A bitmap made on one context of a device is usable on
    /// every other context of it.</para>
    /// </summary>
    public void Glow(ID2D1Geometry geometry, Color4 color, float sigma, Vector2 offset = default)
    {
        Rect b = geometry.GetBounds();
        float scale = PixelScale;
        var key = new GlowKey(geometry.NativePointer, b, Pack(color), sigma, scale);
        float pad = sigma * 3;
        if (!_glows.TryGetValue(key, out var bmp))
        {
            if (_glows.Count >= 32) ClearGlows();
            bmp = BakeGlow(geometry, b, color, sigma, pad, scale);
            _glows[key] = bmp;
        }
        var size = bmp.PixelSize;
        DC.DrawBitmap(bmp,
            new Rect(b.Left - pad + offset.X, b.Top - pad + offset.Y, size.Width / scale, size.Height / scale),
            1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
    }

    private ID2D1Bitmap1 BakeGlow(ID2D1Geometry geometry, Rect b, Color4 color, float sigma, float pad, float scale)
    {
        var size = new SizeI(
            Math.Max(1, (int)Math.Ceiling((b.Width + 2 * pad) * scale)),
            Math.Max(1, (int)Math.Ceiling((b.Height + 2 * pad) * scale)));
        var props = new BitmapProperties1(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target);

        using var device = DC.Device;
        using var bdc = device.CreateDeviceContext(DeviceContextOptions.None);
        using var shape = bdc.CreateBitmap(size, IntPtr.Zero, 0, props);
        var result = bdc.CreateBitmap(size, IntPtr.Zero, 0, props);

        bdc.Target = shape;
        bdc.BeginDraw();
        bdc.Clear(new Color4(0, 0, 0, 0));
        bdc.Transform = Matrix3x2.CreateTranslation(pad - b.Left, pad - b.Top) * Matrix3x2.CreateScale(scale);
        using (var brush = bdc.CreateSolidColorBrush(color))
            bdc.FillGeometry(geometry, brush);
        bdc.EndDraw();

        using var blur = new GaussianBlur(bdc)
        {
            StandardDeviation = sigma * scale,
            BorderMode = BorderMode.Soft,
            Optimization = GaussianBlurOptimization.Quality,
        };
        blur.SetInput(0, shape, true);
        bdc.Target = result;
        bdc.BeginDraw();
        bdc.Clear(new Color4(0, 0, 0, 0));
        bdc.Transform = Matrix3x2.Identity;
        using (var output = blur.Output)
            bdc.DrawImage(output, Vector2.Zero, null, Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver);
        bdc.EndDraw();
        bdc.Target = null;
        return result;
    }

    public IDWriteTextFormat Format(TextStyle style)
    {
        if (_formats.TryGetValue(style, out var f)) return f;
        string family = style.FontOverride ?? Theme.FontFamily;
        // a family the private collection has (ElegantIcons, a skin's bundled faces) comes from
        // it; anything else — Rainformer's Trebuchet MS — from the system collection as always
        IDWriteFontCollection? collection = null;
        if (CustomFonts != null && CustomFonts.FindFamilyName(family, out uint _))
            collection = CustomFonts;
        f = DWrite.CreateTextFormat(family, collection,
            style.Weight ?? (style.Bold ? FontWeight.Bold : FontWeight.Normal),
            Vortice.DirectWrite.FontStyle.Normal,
            style.Stretch == FontStretch.Undefined ? FontStretch.Normal : style.Stretch,
            Theme.FontPx(style.SizePt), "en-us");
        _formats[style] = f;
        return f;
    }

    public IDWriteTextLayout Layout(string text, TextStyle style, float maxWidth = 4096,
        (int Start, int Len, double SizePt)? inlineSize = null)
    {
        // inline-sized layouts get a distinct cache key (\u0001 never occurs in display text)
        string keyText = inlineSize is { Len: > 0 } r0
            ? $"{text}\u0001{r0.Start},{r0.Len},{r0.SizePt}" : text;
        var key = (keyText, style);
        if (_layouts.TryGetValue(key, out var l)) return l;
        if (_layoutsPrev.Remove(key, out l))
        {
            _layouts[key] = l; // still hot: promote instead of rebuilding
            return l;
        }
        if (_layouts.Count >= 384)
        {
            foreach (var v in _layoutsPrev.Values) v.Dispose();
            _layoutsPrev.Clear();
            (_layouts, _layoutsPrev) = (_layoutsPrev, _layouts);
        }
        l = DWrite.CreateTextLayout(text, Format(style), maxWidth, 512);
        // Rainmeter InlineSetting=Size equivalent: a sub-range at a different size, sharing
        // the line's baseline (Rainformer's clock renders "H:mm" at 20 and ":ss" at 13)
        if (inlineSize is { Len: > 0 } r)
            l.SetFontSize(Theme.FontPx((float)r.SizePt), new Vortice.DirectWrite.TextRange((uint)r.Start, (uint)r.Len));
        if (style.Tracking != 0 || style.Tabular)
            ApplySpacing(l, text, style, inlineSize);
        _layouts[key] = l;
        return l;
    }

    /// <summary>
    /// Tracking and tabular figures for one layout. Both go through
    /// <c>IDWriteTextLayout1.SetCharacterSpacing</c>, which takes a minimum advance as well as the
    /// spacing — that minimum is the fallback when a face has no <c>tnum</c>: every digit is padded
    /// to the widest digit's advance, which is what <c>tnum</c> would have done.
    /// </summary>
    private void ApplySpacing(IDWriteTextLayout l, string text, TextStyle style, (int Start, int Len, double SizePt)? inlineSize)
    {
        float track = style.Tracking * Theme.FontPx(style.SizePt);
        using var l1 = l.QueryInterface<IDWriteTextLayout1>();
        if (track != 0)
            l1.SetCharacterSpacing(0, track, 0, new Vortice.DirectWrite.TextRange(0, (uint)text.Length));
        if (!style.Tabular) return;

        l.SetTypography(Tabular(), new Vortice.DirectWrite.TextRange(0, (uint)text.Length));
        float minAdvance = DigitAdvance(style);
        if (minAdvance <= 0) return;
        // the padded advance was measured at the style's size, so an inline-sized run keeps its own
        int skipFrom = inlineSize is { Len: > 0 } r ? r.Start : int.MaxValue;
        int skipTo = inlineSize is { Len: > 0 } r2 ? r2.Start + r2.Len : int.MinValue;
        for (int i = 0; i < text.Length;)
        {
            if (!char.IsAsciiDigit(text[i]) || (i >= skipFrom && i < skipTo)) { i++; continue; }
            int start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]) && !(i >= skipFrom && i < skipTo)) i++;
            l1.SetCharacterSpacing(0, track, minAdvance, new Vortice.DirectWrite.TextRange((uint)start, (uint)(i - start)));
        }
    }

    private IDWriteTypography Tabular()
    {
        if (_tabular != null) return _tabular;
        _tabular = DWrite.CreateTypography();
        _tabular.AddFontFeature(new FontFeature { NameTag = FontFeatureTag.TabularFigures, Parameter = 1 });
        return _tabular;
    }

    /// <summary>0 when the face's <c>tnum</c> already makes every digit one width; otherwise the
    /// widest digit's advance, to force on each digit. Measured once per style.</summary>
    internal float DigitAdvance(TextStyle style)
    {
        if (_digitAdvance.TryGetValue(style, out float adv)) return adv;
        float min = float.MaxValue, max = 0;
        var format = Format(style);
        for (char c = '0'; c <= '9'; c++)
        {
            using var probe = DWrite.CreateTextLayout(c.ToString(), format, 4096, 512);
            probe.SetTypography(Tabular(), new Vortice.DirectWrite.TextRange(0, 1));
            float w = probe.Metrics.WidthIncludingTrailingWhitespace;
            min = Math.Min(min, w);
            max = Math.Max(max, w);
        }
        adv = max - min > 0.01f ? max : 0;
        _digitAdvance[style] = adv;
        return adv;
    }

    /// <summary>Natural single-line height for a style (Rainmeter AccurateText-equivalent).</summary>
    public float LineHeight(TextStyle style)
    {
        if (_lineHeights.TryGetValue(style, out float h)) return h;
        var layout = Layout("Ag8", style);
        h = layout.Metrics.Height;
        _lineHeights[style] = h;
        return h;
    }

    public float TextWidth(string text, TextStyle style)
        => Layout(text, style).Metrics.WidthIncludingTrailingWhitespace;

    public void DrawText(string text, TextStyle style, Color4 color, double x, double y, TextAlign align,
        double? boxW = null, (int Start, int Len, double SizePt)? inlineSize = null)
    {
        var layout = Layout(text, style, 4096, inlineSize);
        float w = layout.Metrics.WidthIncludingTrailingWhitespace;
        float dx = align switch
        {
            TextAlign.Center => (float)x - w / 2f,
            TextAlign.Right => (float)x - w,
            _ => (float)x,
        };
        if (style.Shear == 0)
        {
            DC.DrawTextLayout(new Vector2(dx, (float)y), layout, Brush(color), DrawTextOptions.None);
            return;
        }
        // lean about the line's vertical middle, so the text's box stays where layout put it
        var saved = DC.Transform;
        float cy = (float)y + layout.Metrics.Height / 2f;
        DC.Transform = Matrix3x2.CreateSkew(MathF.Atan(-style.Shear), 0, new Vector2(dx, cy)) * saved;
        DC.DrawTextLayout(new Vector2(dx, (float)y), layout, Brush(color), DrawTextOptions.None);
        DC.Transform = saved;
    }

    /// <summary>Fill the meter box behind text (SolidColor label pill), aligned like the text.</summary>
    public void FillTextBox(Color4 color, double x, double y, double w, double h, TextAlign align)
    {
        double left = align switch
        {
            TextAlign.Center => x - w / 2,
            TextAlign.Right => x - w,
            _ => x,
        };
        DC.FillRectangle(new Rect((float)left, (float)y, (float)w, (float)h), Brush(color));
    }

    private void ClearShapes()
    {
        foreach (var g in _shapes.Values) g.Dispose();
        _shapes.Clear();
    }

    private void ClearGlows()
    {
        foreach (var g in _glows.Values) g.Dispose();
        _glows.Clear();
    }

    public void Dispose()
    {
        foreach (var b in _brushes.Values) b.Dispose();
        foreach (var f in _formats.Values) f.Dispose();
        foreach (var l in _layouts.Values) l.Dispose();
        foreach (var l in _layoutsPrev.Values) l.Dispose();
        foreach (var b in _linear.Values) b.Dispose();
        foreach (var b in _radial.Values) b.Dispose();
        _brushes.Clear(); _formats.Clear(); _layouts.Clear(); _layoutsPrev.Clear();
        _linear.Clear(); _radial.Clear();
        ClearShapes();
        ClearGlows();
        _tabular?.Dispose(); _tabular = null;
    }
}

public enum TextAlign { Left, Center, Right }
