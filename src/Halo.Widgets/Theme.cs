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
    /// <summary>The user's (or auto's) scale, in device-independent terms. The monitor's DPI is
    /// applied separately by the D2D target's own DPI, so this stays the same number on a 4K
    /// laptop as on a 1080p desktop (hardware plan H5).</summary>
    public double BaseScale = 1.7;

    /// <summary>DPI of the monitor this widget currently sits on (96 = 100 %).</summary>
    public double Dpi = 96;

    /// <summary>Physical pixels per logical unit: <c>baseScale × dpi/96</c> (hardware plan H5).</summary>
    public double EffectiveScale => BaseScale * Dpi / 96.0;

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

    /// <summary>Vertical shift applied to every row when the title bar is hidden: the whole card
    /// loses the title band, so the body top moves from 29.5 up to BgOffset.</summary>
    public double ContentShiftY => ShowTitle ? 0 : -24.5;

    public Dictionary<string, Color4> Colors = DefaultColors();

    public Color4 Color(string token) => Colors.TryGetValue(token, out var c) ? c : new Color4(1, 0, 1, 1);

    public bool HasColor(string token) => Colors.ContainsKey(token);

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
    /// Resolve a widget's theme: built-in defaults ← global appearance ← per-widget overrides
    /// ← per-metric colours. <paramref name="autoScale"/> supplies the number an <c>"auto"</c>
    /// scale resolves to on the monitor this widget will live on (hardware plan H7); the DPI
    /// factor is <b>not</b> folded in here — set <see cref="Dpi"/> for that.
    /// </summary>
    public static Theme Resolve(AppearanceSettings global, WidgetInstance? widget, double autoScale)
    {
        var app = widget?.Appearance;
        var t = new Theme
        {
            FontFamily = app?.FontFamily ?? global.FontFamily,
            TextSizePt = global.TextSizePt,
            CornerRadius = global.CornerRadius,
            ShowTitle = app?.ShowTitle ?? true,
        };
        t.BaseScale = (app?.Scale ?? global.Scale).Or(autoScale);
        if (app?.Width is { } w && w > 0)
        {
            t.BgWidth = w;
            t.BgShapeW = w - 2 * t.BgOffset;
        }
        Apply(t, global.Colors);
        Apply(t, app?.Colors);
        ApplyMetricColors(t, widget);
        return t;
    }

    /// <summary>Copy every field of <paramref name="src"/> into this instance. Live widgets hold
    /// the Theme object (so does their RenderContext), so an in-place config apply mutates it
    /// rather than swapping it — a swap would leave the render context painting the old palette
    /// (settings plan §Live-apply).</summary>
    public void CopyFrom(Theme src)
    {
        BaseScale = src.BaseScale;
        FontFamily = src.FontFamily;
        TextSizePt = src.TextSizePt;
        TitleSizePt = src.TitleSizePt;
        ShowTitle = src.ShowTitle;
        BgOffset = src.BgOffset;
        BgShapeW = src.BgShapeW;
        BgWidth = src.BgWidth;
        AbsMargin = src.AbsMargin;
        TopMargin = src.TopMargin;
        BottomMargin = src.BottomMargin;
        RowSpacing = src.RowSpacing;
        CornerRadius = src.CornerRadius;
        StrokeWidth = src.StrokeWidth;
        TitleZoneH = src.TitleZoneH;
        Colors.Clear();
        foreach (var (k, v) in src.Colors) Colors[k] = v;
        // Dpi is a property of the monitor, not of the config — the caller owns it.
    }

    private static void Apply(Theme t, Dictionary<string, string>? colors)
    {
        if (colors == null) return;
        foreach (var (token, hex) in colors)
            if (ThemeTokens.TryParse(hex, out byte r, out byte g, out byte b, out byte a))
                t.Colors[token] = C(r, g, b, a);
    }

    /// <summary>Per-metric colour overrides become synthetic <c>metric:&lt;key&gt;</c> tokens, so a
    /// row still names a token and the element tree never carries a raw colour
    /// (<see cref="Render.PanelContext.Color"/> picks the override when it exists).</summary>
    private static void ApplyMetricColors(Theme t, WidgetInstance? widget)
    {
        if (widget == null) return;
        foreach (var (key, setting) in widget.Metrics)
            if (setting?.Color is { Length: > 0 } hex
                && ThemeTokens.TryParse(hex, out byte r, out byte g, out byte b, out byte a))
                t.Colors["metric:" + key] = C(r, g, b, a);
    }
}
