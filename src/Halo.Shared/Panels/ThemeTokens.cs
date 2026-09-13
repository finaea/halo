using System.Globalization;

namespace Halo.Shared.Panels;

/// <summary>
/// The default colour palette, as #RRGGBBAA strings, plus a one-line description per token.
///
/// Values extracted from the Rainformer skin's @Resources\Variables.inc (light set) — facts read
/// out of a config file, no code copied. This is the single source both the renderer and the
/// Settings app read, replacing the hand-maintained duplicate that used to live in
/// Halo.Settings\Pages\ThemePage.xaml.cs.
/// </summary>
public static class ThemeTokens
{
    /// <summary>Token → default #RRGGBBAA, in the order the Settings UI should list them.</summary>
    public static readonly (string Token, string Color, string Description)[] Defaults =
    [
        ("title",           "#000000FF", "Panel title text"),
        ("activeTitle",     "#FF8000FF", "Highlighted / active title text"),
        ("text",            "#000000CD", "Primary text"),
        ("text2",           "#3C413ECD", "Secondary text"),
        ("bar",             "#5D8DACFF", "Bar fill"),
        ("histogram",       "#B0C4DEFA", "Graph line & fill"),
        ("netDown",         "#3399FFCD", "Network download"),
        ("netUp",           "#33FF00CD", "Network upload"),
        ("red",             "#CC0000FF", "Alert accent"),
        ("redText",         "#CC0000CD", "Alert text"),
        ("emptyBar",        "#FFFFFF19", "Bar background (empty part)"),
        ("bgTop",           "#A3B2E6B4", "Panel header background"),
        ("bgBody",          "#E6E6E6B4", "Panel body background"),
        ("solidLabel",      "#F8F8F8FF", "Solid label plate"),
        ("inactiveButton",  "#787878FF", "Inactive glyphs"),
        ("barWarn",         "#DC143CFF", "Bar colour when warning"),
        ("cpuTemp",         "#CC0000FF", "CPU temperature graph"),
        ("cpuUsage",        "#B0C4DEFF", "CPU usage graph"),
        ("ramUsage",        "#66CC00FF", "RAM usage graph"),
        ("gpuTemp",         "#CC0000FF", "GPU temperature graph"),
        ("gpuUsage",        "#B0C4DEFF", "GPU usage graph"),
        ("gpuMemUsage",     "#66CC00FF", "GPU memory graph"),
        ("gpuFan",          "#00BFFFFF", "GPU fan graph / frametime line"),
        ("maxLabelGray",    "#787878FF", "Session-max labels"),
        ("devWarn1",        "#2FBAFFFF", "Staged warning 1 (coolest)"),
        ("devWarn2",        "#FFFF24FF", "Staged warning 2"),
        ("devWarn3",        "#FF8F1EFF", "Staged warning 3"),
        ("devWarn4",        "#FF0000FF", "Staged warning 4"),
        ("devWarn5",        "#CC0000FF", "Staged warning 5 (critical)"),
        ("staleBadge",      "#FF5050DC", "Stale-data badge"),
    ];

    /// <summary>Default colour for a token, or null when the token is unknown.</summary>
    public static string? Default(string token)
    {
        foreach (var d in Defaults)
            if (d.Token == token) return d.Color;
        return null;
    }

    /// <summary>Parse "#RRGGBBAA" (or "#RRGGBB", alpha 255). Returns false on anything else.</summary>
    public static bool TryParse(string? hex, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = 0; a = 255;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        ReadOnlySpan<char> s = hex.AsSpan().Trim();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length != 6 && s.Length != 8) return false;
        if (!byte.TryParse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)) return false;
        if (!byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)) return false;
        if (!byte.TryParse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) return false;
        if (s.Length == 8 && !byte.TryParse(s[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a)) return false;
        return true;
    }

    public static string ToHex(byte r, byte g, byte b, byte a) => $"#{r:X2}{g:X2}{b:X2}{a:X2}";
}
