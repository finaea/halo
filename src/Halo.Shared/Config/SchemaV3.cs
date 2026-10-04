using System.Globalization;
using Halo.Shared.Panels;
using Halo.Shared.Skins;

namespace Halo.Shared.Config;

/// <summary>
/// Schema v2 → v3: appearance becomes skin-scoped (skin system tech plan §4). Applied in memory
/// to every document <see cref="ConfigStore"/> loads, so nothing downstream ever sees a v2 shape;
/// the file itself turns v3 on its next save, whoever makes it.
/// <para>
/// One rule, on purpose: every v2 install lands on <c>rainformer-light</c>, and every colour that
/// differs from it becomes a tweak. That preset is <see cref="ThemeTokens.Defaults"/> byte for byte,
/// which is also what a v2 file's missing tokens fell back to — so "nobody's look changes" holds
/// by construction. Recognising the old Light / High contrast chips is deliberately not attempted:
/// HC was derived from Windows' system colours at click time and cannot be recognised without WPF.
/// </para>
/// </summary>
public static class SchemaV3
{
    /// <summary>v2's default font. A v2 file naming it meant "the default", which is now null.</summary>
    private const string V2DefaultFont = "Trebuchet MS";

    /// <summary>v2's default text size, which a migrated file leaves out.</summary>
    private const string V2DefaultTextSizePt = "8";

    public static void Upgrade(AppSettings settings)
    {
        if (settings.SchemaVersion >= 3) return;
        var a = settings.Appearance;
        var rf = SkinCatalog.Rainformer;
        PresetSpec preset = rf.DefaultPreset;
        var target = a.SkinFor(rf.Id);
        target.Preset = preset.Id;

        // v2 colours could be sparse; a missing token meant the built-in palette, i.e. the preset.
        // So only tokens the file names can differ, and comparing those alone is the overlay.
        foreach (var (token, hex) in a.V2Colors ?? [])
            if (!preset.Colors.TryGetValue(token, out string? baseline) || !SameColor(hex, baseline))
                target.Colors[token] = hex;

        SetOptionIfNotDefault(target, "cornerRadius", a.V2CornerRadius, rf.Options.First(o => o.Key == "cornerRadius").Default);
        // No longer a Rainformer option (nothing ever drew with it), but a value someone set is
        // still carried over rather than dropped; Theme ignores keys the skin does not declare.
        SetOptionIfNotDefault(target, "textSizePt", a.V2TextSizePt, V2DefaultTextSizePt);

        if (string.Equals(a.FontFamily, V2DefaultFont, StringComparison.OrdinalIgnoreCase)) a.FontFamily = null;

        a.V2Colors = null;
        a.V2CornerRadius = null;
        a.V2TextSizePt = null;
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
    }

    public static void Upgrade(WidgetsConfig widgets)
    {
        if (widgets.SchemaVersion >= 3) return;
        foreach (var w in widgets.Widgets)
        {
            var app = w.Appearance;
            if (app == null) continue;
            // A widget's v2 colours overrode the global ones token by token, over whatever the global
            // look was. Tweaks with no preset of their own mean exactly that in v3, so they move
            // across verbatim — including any that happen to equal the default palette.
            if (app.V2Colors is { Count: > 0 } colors)
            {
                var target = app.SkinFor(SkinCatalog.RainformerId);
                foreach (var (token, hex) in colors) target.Colors[token] = hex;
            }
            app.V2Colors = null;
        }
        widgets.SchemaVersion = AppSettings.CurrentSchemaVersion;
    }

    private static void SetOptionIfNotDefault(SkinSettings target, string key, double? value, string defaultValue)
    {
        if (value is not { } v) return;
        if (double.TryParse(defaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d == v) return;
        target.Options[key] = v.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Same colour, whatever the hex spelling (case, or #RRGGBB vs #RRGGBBFF).</summary>
    private static bool SameColor(string a, string b)
        => ThemeTokens.TryParse(a, out byte r1, out byte g1, out byte b1, out byte a1)
           && ThemeTokens.TryParse(b, out byte r2, out byte g2, out byte b2, out byte a2)
               ? r1 == r2 && g1 == g2 && b1 == b2 && a1 == a2
               : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
