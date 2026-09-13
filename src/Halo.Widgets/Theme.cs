using Halo.Shared.Config;
using Halo.Shared.Panels;
using Vortice.Mathematics;

namespace Halo.Widgets;

/// <summary>
/// The resolved look of one widget: global appearance from settings.json merged with that
/// widget's own overrides. Built once per widget whenever config changes — never read from a
/// file here (theme.json is gone, settings plan S4).
///
/// Colour values come from <see cref="ThemeTokens"/>, extracted from the Rainformer skin's
/// @Resources\Variables.inc (values only, no code).
/// </summary>
public sealed class Theme
{
    public double Scale = 1.7;
    public string FontFamily = "Trebuchet MS";
    public double TextSizePt = 8;
    public double TitleSizePt = 9;
    public bool ShowTitle = true;

    // panel geometry (logical units, pre-scale)
    public double BgOffset = 5;
    public double BgShapeW = 196;
    public double BgWidth = 206;          // BgShapeW + 2*BgOffset
    public double AbsMargin = 3;
    public double TopMargin = 28;
    public double BottomMargin = 3;
    public double RowSpacing = 1;
    public double CornerRadius = 4;
    public double StrokeWidth = 0;
    public double TitleZoneH = 21;        // top rounded band height

    // derived
    public double ContentMargin => AbsMargin + BgOffset - 1;            // 7
    public double RightAlign => BgWidth - (AbsMargin + BgOffset) - 1;   // 197
    public double ContentWidth => BgWidth - (AbsMargin + BgOffset) * 2; // 190
    public double CenterAlign => BgWidth / 2;                           // 103
    public double TopMarginFormula => TopMargin + BgOffset - 1;         // 32

    public Dictionary<string, Color4> Colors = DefaultColors();

    public Color4 Color(string token) => Colors.TryGetValue(token, out var c) ? c : new Color4(1, 0, 1, 1);

    /// <summary>Device-independent px per pt (Rainmeter FontSize is points; px = pt·96/72).</summary>
    public float FontPx(double pt) => (float)(pt * 96.0 / 72.0);

    private static Dictionary<string, Color4> DefaultColors()
    {
        var map = new Dictionary<string, Color4>(StringComparer.Ordinal);
        foreach (var (token, hex, _) in ThemeTokens.Defaults)
            if (ThemeTokens.TryParse(hex, out byte r, out byte g, out byte b, out byte a))
                map[token] = C(r, g, b, a);
        return map;
    }

    private static Color4 C(byte r, byte g, byte b, byte a) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>
    /// Resolve a widget's theme: built-in defaults ← global appearance ← per-widget overrides.
    /// <paramref name="autoScale"/> supplies the number for a "auto" scale (hardware plan H7);
    /// until ticket 03 wires per-monitor DPI it is simply the reference 1.7.
    /// </summary>
    public static Theme Resolve(AppearanceSettings global, WidgetAppearance? widget, double autoScale)
    {
        var t = new Theme
        {
            FontFamily = widget?.FontFamily ?? global.FontFamily,
            TextSizePt = global.TextSizePt,
            CornerRadius = global.CornerRadius,
            ShowTitle = widget?.ShowTitle ?? true,
        };
        t.Scale = (widget?.Scale ?? global.Scale).Or(autoScale);
        if (widget?.Width is { } w && w > 0)
        {
            t.BgWidth = w;
            t.BgShapeW = w - 2 * t.BgOffset;
        }
        Apply(t, global.Colors);
        Apply(t, widget?.Colors);
        return t;
    }

    private static void Apply(Theme t, Dictionary<string, string>? colors)
    {
        if (colors == null) return;
        foreach (var (token, hex) in colors)
            if (ThemeTokens.TryParse(hex, out byte r, out byte g, out byte b, out byte a))
                t.Colors[token] = C(r, g, b, a);
    }
}
