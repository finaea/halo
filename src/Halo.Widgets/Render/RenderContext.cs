using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Halo.Widgets.Render;

public readonly record struct TextStyle(double SizePt, bool Bold, string? FontOverride = null)
{
    public static readonly TextStyle Text8 = new(8, false);
    public static readonly TextStyle Bold8 = new(8, true);
    public static readonly TextStyle Bold9 = new(9, true);
}

/// <summary>
/// Per-window D2D drawing context with cached brushes / text formats / text layouts
/// (plan §8: no per-tick re-parse, layouts swapped only when the string changes).
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

    public RenderContext(ID2D1DeviceContext dc, IDWriteFactory dwrite, IDWriteFontCollection1? customFonts, Theme theme)
    {
        DC = dc;
        DWrite = dwrite;
        CustomFonts = customFonts;
        Theme = theme;
    }

    public ID2D1SolidColorBrush Brush(Color4 color)
    {
        uint key = ((uint)(color.R * 255) << 24) | ((uint)(color.G * 255) << 16) | ((uint)(color.B * 255) << 8) | (uint)(color.A * 255);
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = DC.CreateSolidColorBrush(color);
        _brushes[key] = b;
        return b;
    }

    public IDWriteTextFormat Format(TextStyle style)
    {
        if (_formats.TryGetValue(style, out var f)) return f;
        string family = style.FontOverride ?? Theme.FontFamily;
        // custom fonts (ElegantIcons etc.) come from the private collection; system fonts from default
        IDWriteFontCollection? collection = null;
        if (CustomFonts != null && style.FontOverride != null && CustomFonts.FindFamilyName(style.FontOverride, out uint _))
            collection = CustomFonts;
        f = DWrite.CreateTextFormat(family, collection,
            style.Bold ? FontWeight.Bold : FontWeight.Normal,
            Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal,
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
        _layouts[key] = l;
        return l;
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
        DC.DrawTextLayout(new Vector2(dx, (float)y), layout, Brush(color), DrawTextOptions.None);
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

    public void Dispose()
    {
        foreach (var b in _brushes.Values) b.Dispose();
        foreach (var f in _formats.Values) f.Dispose();
        foreach (var l in _layouts.Values) l.Dispose();
        foreach (var l in _layoutsPrev.Values) l.Dispose();
        _brushes.Clear(); _formats.Clear(); _layouts.Clear(); _layoutsPrev.Clear();
    }
}

public enum TextAlign { Left, Center, Right }
