using Halo.Shared.Config;

namespace Halo.Shared.Skins;

/// <summary>
/// The colour half of <c>Theme.Resolve</c>'s rule, as plain token → hex maps for the Settings app,
/// which has to show the value a swatch inherits. It must agree with the renderer: a swatch showing
/// a colour the widget is not drawn with is wrong, and unticking "Use global" then persists it.
/// </summary>
public static class SkinPalette
{
    /// <summary>The global preset of <paramref name="skin"/>, or the skin's default.</summary>
    public static PresetSpec GlobalPreset(AppearanceSettings global, SkinInfo skin)
        => SkinCatalog.FindPreset(skin, global.Skins?.GetValueOrDefault(skin.Id)?.Preset) ?? skin.DefaultPreset;

    /// <summary>Global preset with the global tweaks on top.</summary>
    public static Dictionary<string, string> Global(AppearanceSettings global, SkinInfo skin)
    {
        var palette = new Dictionary<string, string>(GlobalPreset(global, skin).Colors, StringComparer.Ordinal);
        if (global.Skins?.GetValueOrDefault(skin.Id)?.Colors is { } tweaks)
            foreach (var (token, hex) in tweaks) palette[token] = hex;
        return palette;
    }

    /// <summary>
    /// What a widget's colours fall back to before its own tweaks. A widget that picked a known
    /// preset of its own gets that preset alone — global tweaks were made against the global
    /// preset and do not stack onto a different one. No preset, or an unknown id, follows the
    /// global look.
    /// </summary>
    public static Dictionary<string, string> InheritedByWidget(AppearanceSettings global, WidgetAppearance? widget, SkinInfo skin)
        => SkinCatalog.FindPreset(skin, widget?.Skins?.GetValueOrDefault(skin.Id)?.Preset) is { } own
            ? new Dictionary<string, string>(own.Colors, StringComparer.Ordinal)
            : Global(global, skin);

    /// <summary>
    /// The option half of the same rule, before the user's own values at the level being edited:
    /// skin defaults ← <paramref name="preset"/>'s options ← (for a widget) the global options.
    /// Unlike colours, global options do apply under a widget's own preset — an option is not a
    /// tweak of a palette, so <c>Theme.Resolve</c> layers it regardless.
    /// </summary>
    public static Dictionary<string, string> OptionBaseline(SkinInfo skin, PresetSpec preset, AppearanceSettings? global = null)
    {
        var options = skin.Options.ToDictionary(o => o.Key, o => o.Default, StringComparer.Ordinal);
        Overlay(options, preset.Options);
        Overlay(options, global?.Skins?.GetValueOrDefault(skin.Id)?.Options);
        return options;
    }

    /// <summary>The preset a widget is drawn with: its own when it names a known one, else the global.</summary>
    public static PresetSpec WidgetPreset(AppearanceSettings global, WidgetAppearance? widget, SkinInfo skin)
        => SkinCatalog.FindPreset(skin, widget?.Skins?.GetValueOrDefault(skin.Id)?.Preset) ?? GlobalPreset(global, skin);

    private static void Overlay(Dictionary<string, string> into, IReadOnlyDictionary<string, string>? from)
    {
        if (from == null) return;
        foreach (var (k, v) in from)
            if (into.ContainsKey(k)) into[k] = v;
    }
}
