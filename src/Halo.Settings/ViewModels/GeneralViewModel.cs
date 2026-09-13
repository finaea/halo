using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Halo.Settings.Services;
using Halo.Shared.Config;
using Halo.Shared.Panels;

namespace Halo.Settings.ViewModels;

public sealed record ChoiceItem(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class GeneralViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlySet<string> NoDirtyPaths = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["bgTop"] = "#E8EEF6FF", ["bgBody"] = "#F7F8FAFF", ["title"] = "#111111FF",
        ["text"] = "#202020FF", ["text2"] = "#595959FF", ["solidLabel"] = "#FFFFFFFF",
        ["emptyBar"] = "#767676FF", ["barWarn"] = "#B3261EFF", ["inactiveButton"] = "#595959FF",
        ["staleBadge"] = "#A80000FF", ["devWarn1"] = "#107C10FF", ["devWarn2"] = "#626200FF",
        ["devWarn3"] = "#806000FF", ["devWarn4"] = "#A33A00FF", ["devWarn5"] = "#A80000FF",
    };

    private static readonly IReadOnlyDictionary<string, string> HighContrastFallback = new Dictionary<string, string>
    {
        ["bgTop"] = "#000000FF", ["bgBody"] = "#000000FF", ["title"] = "#FFFFFFFF",
        ["text"] = "#FFFFFFFF", ["text2"] = "#00FFFFFF", ["solidLabel"] = "#000000FF",
        ["emptyBar"] = "#767676FF", ["barWarn"] = "#FFFF00FF", ["inactiveButton"] = "#C0C0C0FF",
        ["staleBadge"] = "#FFFF00FF", ["devWarn1"] = "#00FFFFFF", ["devWarn2"] = "#00FF00FF",
        ["devWarn3"] = "#FFFF00FF", ["devWarn4"] = "#FFA500FF", ["devWarn5"] = "#FF6060FF",
    };

    /// <summary>Names <see cref="ApplyPreset"/> understands, plus the "nothing matches" state.</summary>
    public const string RainformerPreset = "Rainformer";
    public const string LightPreset = "Light";
    public const string HighContrastPreset = "High contrast";
    public const string CustomPalette = "Custom";

    private readonly LiveConfigService _config;
    private bool _applying;
    private bool _lockAll;
    private bool _snap;
    private bool _autoScale;
    private double _fixedScale = 1.7;
    private string _fontFamily = "Trebuchet MS";
    private double _textSizePt = 8;
    private double _cornerRadius = 4;
    private string _activePreset = CustomPalette;

    public ObservableCollection<GlobalColorViewModel> Colors { get; } = [];
    public IReadOnlyList<string> FontFamilies { get; }

    public bool LockAll
    {
        get => _lockAll;
        set
        {
            if (!Set(ref _lockAll, value) || _applying) return;
            _config.QueueSettings("lockAll", s => s.LockAll = value);
        }
    }

    public bool Snap
    {
        get => _snap;
        set
        {
            if (!Set(ref _snap, value) || _applying) return;
            _config.QueueSettings("snap", s => s.Snap = value);
        }
    }

    public bool AutoScale
    {
        get => _autoScale;
        set
        {
            if (!Set(ref _autoScale, value)) return;
            Raise(nameof(IsFixedScaleEnabled));
            Raise(nameof(ScaleSummary));
            if (_applying) return;
            double fixedValue = FixedScale;
            _config.QueueSettings("appearance.scale", s => s.Appearance.Scale = value ? ScaleValue.Auto : ScaleValue.Fixed(fixedValue));
        }
    }

    public bool IsFixedScaleEnabled => !AutoScale;

    public double FixedScale
    {
        get => _fixedScale;
        set
        {
            value = Math.Round(Math.Clamp(value, 1, 3.5), 2);
            if (!Set(ref _fixedScale, value)) return;
            Raise(nameof(ScaleSummary));
            if (_applying || AutoScale) return;
            _config.QueueSettings("appearance.scale", s => s.Appearance.Scale = ScaleValue.Fixed(value));
        }
    }

    public string ScaleSummary => AutoScale
        ? $"Auto resolves to {ResolvePrimaryScale():0.00}× on the primary monitor"
        : $"Fixed at {FixedScale:0.00}× on every monitor";

    public string FontFamily
    {
        get => _fontFamily;
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? "Trebuchet MS" : value.Trim();
            if (!Set(ref _fontFamily, value) || _applying) return;
            _config.QueueSettings("appearance.fontFamily", s => s.Appearance.FontFamily = value);
        }
    }

    public double TextSizePt
    {
        get => _textSizePt;
        set
        {
            value = Math.Round(Math.Clamp(value, 5, 24), 1);
            if (!Set(ref _textSizePt, value) || _applying) return;
            _config.QueueSettings("appearance.textSizePt", s => s.Appearance.TextSizePt = value);
        }
    }

    public double CornerRadius
    {
        get => _cornerRadius;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, 20), 1);
            if (!Set(ref _cornerRadius, value) || _applying) return;
            _config.QueueSettings("appearance.cornerRadius", s => s.Appearance.CornerRadius = value);
        }
    }

    /// <summary>Which preset the colours currently in the editor spell out, or <see cref="CustomPalette"/>.</summary>
    public string ActivePreset
    {
        get => _activePreset;
        private set
        {
            _activePreset = value;
            Raise();
            Raise(nameof(IsRainformerPreset));
            Raise(nameof(IsLightPreset));
            Raise(nameof(IsHighContrastPreset));
            Raise(nameof(IsCustomPalette));
        }
    }

    public bool IsRainformerPreset => ActivePreset == RainformerPreset;
    public bool IsLightPreset => ActivePreset == LightPreset;
    public bool IsHighContrastPreset => ActivePreset == HighContrastPreset;
    public bool IsCustomPalette => ActivePreset == CustomPalette;

    public GeneralViewModel(LiveConfigService config)
    {
        _config = config;
        FontFamilies = Fonts.SystemFontFamilies.Select(font => font.Source).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        foreach ((string token, string hex, string description) in ThemeTokens.Defaults)
            Colors.Add(new GlobalColorViewModel(token, FriendlyToken(token), description, hex, ColorChanged));
        ApplyFromStore(NoDirtyPaths);
        _config.ExternalChanged += Config_ExternalChanged;
    }

    public void ApplyPreset(string name)
    {
        IReadOnlyDictionary<string, string> palette = PaletteFor(name);
        foreach (GlobalColorViewModel row in Colors)
            if (palette.TryGetValue(row.Token, out string? value)) row.Hex = value;
        // Clicking the already-active chip changes no colour, so ColorChanged never fires and the
        // chip would stay visually unchecked after its own click. Re-assert the state either way.
        RecomputeActivePreset(force: true);
    }

    public void RefreshFromCurrent() => ApplyFromStore(NoDirtyPaths);

    public void ResetAppearance()
    {
        var defaults = new AppearanceSettings();
        _applying = true;
        try
        {
            AutoScale = defaults.Scale.IsAuto;
            FixedScale = defaults.Scale.Or(1.7);
            FontFamily = defaults.FontFamily;
            TextSizePt = defaults.TextSizePt;
            CornerRadius = defaults.CornerRadius;
            foreach (GlobalColorViewModel row in Colors)
                row.ApplyExternal(ThemeTokens.Default(row.Token) ?? "#000000FF");
        }
        finally { _applying = false; }
        RecomputeActivePreset(force: true);
        _config.QueueSettings("appearance", s => s.Appearance = new AppearanceSettings(), flushImmediately: true);
    }

    public void ResetEverything()
    {
        var defaults = new AppSettings();
        _applying = true;
        try { ApplySettings(defaults, NoDirtyPaths); }
        finally { _applying = false; }
        _config.QueueSettings("$", s => CopySettings(defaults, s), flushImmediately: true);
        _config.QueueWidgets("$", widgets =>
        {
            widgets.SchemaVersion = AppSettings.CurrentSchemaVersion;
            widgets.Widgets.Clear();
        }, flushImmediately: true);
    }

    private void Config_ExternalChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (e.File == ConfigFileKind.Settings) ApplyFromStore(e.DirtyPaths);
    }

    private void ApplyFromStore(IReadOnlySet<string> dirty)
    {
        _applying = true;
        try { ApplySettings(_config.Settings, dirty); }
        finally { _applying = false; }
    }

    private void ApplySettings(AppSettings settings, IReadOnlySet<string> dirty)
    {
        if (!Conflicts("lockAll", dirty)) LockAll = settings.LockAll;
        if (!Conflicts("snap", dirty)) Snap = settings.Snap;
        if (!Conflicts("appearance.scale", dirty))
        {
            AutoScale = settings.Appearance.Scale.IsAuto;
            FixedScale = settings.Appearance.Scale.Or(1.7);
        }
        if (!Conflicts("appearance.fontFamily", dirty)) FontFamily = settings.Appearance.FontFamily;
        if (!Conflicts("appearance.textSizePt", dirty)) TextSizePt = settings.Appearance.TextSizePt;
        if (!Conflicts("appearance.cornerRadius", dirty)) CornerRadius = settings.Appearance.CornerRadius;
        foreach (GlobalColorViewModel row in Colors)
        {
            if (Conflicts(row.Path, dirty)) continue;
            string value = settings.Appearance.Colors.TryGetValue(row.Token, out string? configured)
                ? configured
                : ThemeTokens.Default(row.Token) ?? "#000000FF";
            row.ApplyExternal(value);
        }
        RecomputeActivePreset();
    }

    private void ColorChanged(string token, string hex)
    {
        // While _applying, ApplySettings/ResetAppearance recompute once at the end instead.
        if (_applying) return;
        RecomputeActivePreset();
        _config.QueueSettings($"appearance.colors.{token}", settings => settings.Appearance.Colors[token] = hex);
    }

    /// <summary>
    /// Re-derives <see cref="ActivePreset"/> by comparing every token in the editor against each
    /// preset's full palette, so editing a single swatch flips the chips to Custom.
    /// </summary>
    private void RecomputeActivePreset(bool force = false)
    {
        string[] presets = [RainformerPreset, LightPreset, HighContrastPreset];
        string match = presets.FirstOrDefault(name => Matches(PaletteFor(name))) ?? CustomPalette;
        if (force || match != _activePreset) ActivePreset = match;
    }

    private bool Matches(IReadOnlyDictionary<string, string> palette)
        => Colors.All(row => palette.TryGetValue(row.Token, out string? value) &&
            string.Equals(row.Hex, value, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, string> PaletteFor(string name) => name switch
    {
        LightPreset => CompletePalette(LightPalette),
        HighContrastPreset => BuildHighContrastPalette(),
        _ => ThemeTokens.Defaults.ToDictionary(x => x.Token, x => x.Color, StringComparer.Ordinal),
    };

    private static bool Conflicts(string path, IReadOnlySet<string> dirty)
        => dirty.Any(candidate => candidate == "$" || candidate == path ||
            candidate.StartsWith(path + ".", StringComparison.Ordinal) || path.StartsWith(candidate + ".", StringComparison.Ordinal));

    private static double ResolvePrimaryScale()
    {
        Rect work = SystemParameters.WorkArea;
        double shortSideDip = Math.Min(work.Width, work.Height);
        double raw = 1.7 * shortSideDip / 1080.0;
        return Math.Round(Math.Clamp(raw, 1, 3.5) / 0.05) * 0.05;
    }

    private static string FriendlyToken(string token)
    {
        var words = new List<string>();
        int start = 0;
        for (int index = 1; index < token.Length; index++)
        {
            if (!char.IsUpper(token[index])) continue;
            words.Add(token[start..index]);
            start = index;
        }
        words.Add(token[start..]);
        string label = string.Join(" ", words).Replace("bg", "background", StringComparison.OrdinalIgnoreCase);
        return label.Length == 0 ? token : char.ToUpper(label[0]) + label[1..];
    }

    private static IReadOnlyDictionary<string, string> CompletePalette(IReadOnlyDictionary<string, string> roles)
    {
        var result = ThemeTokens.Defaults.ToDictionary(x => x.Token, x => x.Color, StringComparer.Ordinal);
        foreach ((string key, string value) in roles) result[key] = value;
        return result;
    }

    private static IReadOnlyDictionary<string, string> BuildHighContrastPalette()
    {
        if (!SystemParameters.HighContrast) return CompletePalette(HighContrastFallback);

        Color background = SystemColors.WindowColor;
        Color text = MostReadable(SystemColors.WindowTextColor, background, 7);
        Color highlight = MostReadable(SystemColors.HighlightColor, background, 7);
        Color hot = MostReadable(SystemColors.HotTrackColor, background, 7);
        Color highlightText = MostReadable(SystemColors.HighlightTextColor, SystemColors.HighlightColor, 7);
        Color gray = MostReadable(SystemColors.GrayTextColor, background, 7);
        string Hex(Color color) => ThemeTokens.ToHex(color.R, color.G, color.B, 255);

        return CompletePalette(new Dictionary<string, string>
        {
            ["bgTop"] = Hex(background), ["bgBody"] = Hex(background), ["title"] = Hex(text),
            ["text"] = Hex(text), ["text2"] = Hex(text), ["solidLabel"] = Hex(highlightText),
            ["emptyBar"] = Hex(gray), ["inactiveButton"] = Hex(gray), ["barWarn"] = Hex(hot),
            ["staleBadge"] = Hex(hot), ["devWarn1"] = Hex(highlight), ["devWarn2"] = Hex(highlight),
            ["devWarn3"] = Hex(hot), ["devWarn4"] = Hex(hot), ["devWarn5"] = Hex(hot),
        });
    }

    private static Color MostReadable(Color preferred, Color background, double minimum)
    {
        if (Contrast(preferred, background) >= minimum) return preferred;
        Color[] candidates = [SystemColors.WindowTextColor, SystemColors.HighlightTextColor, System.Windows.Media.Colors.Black, System.Windows.Media.Colors.White];
        return candidates.OrderByDescending(candidate => Contrast(candidate, background)).First();
    }

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value)
        {
            double linear = value / 255.0;
            return linear <= 0.04045 ? linear / 12.92 : Math.Pow((linear + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        double a = Luminance(first);
        double b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void CopySettings(AppSettings source, AppSettings destination)
    {
        destination.SchemaVersion = source.SchemaVersion;
        destination.LockAll = source.LockAll;
        destination.Snap = source.Snap;
        destination.Appearance = source.Appearance;
        destination.Collector = source.Collector;
    }

    public void Dispose() => _config.ExternalChanged -= Config_ExternalChanged;
}
