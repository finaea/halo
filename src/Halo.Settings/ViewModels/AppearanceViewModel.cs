using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Halo.Settings.Services;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Shared.Panels;
using Halo.Shared.Skins;

namespace Halo.Settings.ViewModels;

public sealed class SkinCardViewModel(SkinInfo skin) : ObservableObject
{
    private bool _isSelected;
    private ImageSource? _preview;

    public SkinInfo Skin { get; } = skin;
    public string Id => Skin.Id;
    public string Name => Skin.Name;
    public string Description => Skin.Description;
    public string Attribution => Skin.Attribution;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public ImageSource? Preview { get => _preview; set { if (Set(ref _preview, value)) Raise(nameof(HasNoPreview)); } }
    public bool HasNoPreview => Preview == null;
}

/// <summary>One choice in the Motion box: the stored value and what it is called on screen.</summary>
public sealed record MotionChoice(string Id, string Name);

public sealed class PresetChipViewModel(SkinInfo skin, PresetSpec preset) : ObservableObject
{
    private bool _isSelected;

    public PresetSpec Preset { get; } = preset;
    public string Id => Preset.Id;
    public string Name => Preset.Name;

    /// <summary>The skin's own swatch tokens (<see cref="SkinInfo.SwatchTokens"/>): the colours
    /// that cover most of its card. Drawn opaque: a 70 % body over the white Settings card would
    /// read as a pastel of itself.</summary>
    public IReadOnlyList<Brush> Swatches { get; } = (skin.SwatchTokens ?? ["bgTop", "bgBody", "text", "bar"])
        .Select(token => (Brush)new SolidColorBrush(AppearanceViewModel.Opaque(preset.Colors.GetValueOrDefault(token))))
        .ToArray();

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>
/// The Appearance page: skin, preset, the skin's options, scale, font and the global colour tweaks
/// (skin system tech plan §7). Config v3 stores a preset <b>reference</b> plus tweaks, keyed per
/// skin, so the page can say "2 colours changed from Nord" instead of guessing which preset a dump
/// of 31 colours spells. The page keeps its own copy of the active skin's entry and writes each
/// change as one path-keyed mutation; reading back through <see cref="LiveConfigService.Settings"/>
/// would lag the 200 ms write coalescing.
/// </summary>
public sealed class AppearanceViewModel : ObservableObject, IDisposable
{
    /// <summary>The font box's first entry: stored as null, so the widget follows its skin's font.</summary>
    public const string SkinFontChoice = "Use skin font";

    private readonly LiveConfigService _config;
    private SkinInfo _skin = SkinCatalog.Rainformer;
    private PresetSpec _preset = SkinCatalog.Rainformer.DefaultPreset;
    private readonly Dictionary<string, string> _tweaks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    /// <summary>The active skin's <see cref="SkinSettings.PresetColors"/>: other presets' tweaks.</summary>
    private Dictionary<string, Dictionary<string, string>>? _parked;
    private bool _applying;
    private long _skinSwitches;
    /// <summary>One skin change at a time: the next one's "from" is whatever the last one left in
    /// the file, which is only known once its writes are checked.</summary>
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private bool _autoScale = true;
    private double _fixedScale = 1.7;
    private string _fontFamily = SkinFontChoice;
    private string _motion = "subtle";
    private int _motionFps = 30;

    public ObservableCollection<SkinCardViewModel> Skins { get; } = [];
    public ObservableCollection<PresetChipViewModel> Presets { get; } = [];
    public ObservableCollection<SkinOptionViewModel> SkinOptions { get; } = [];
    public ObservableCollection<ColorGroupViewModel<GlobalColorViewModel>> ColorGroups { get; } = [];
    public IReadOnlyList<string> FontFamilies { get; }

    public IReadOnlyList<MotionChoice> MotionChoices { get; } =
        [new("off", "Off"), new("subtle", "Subtle"), new("full", "Full")];

    public IReadOnlyList<int> MotionFpsChoices { get; } = AppearanceSettings.MotionFpsChoices;

    public string PresetName => _preset.Name;
    public int TweakCount => _tweaks.Count;
    public bool HasTweaks => TweakCount > 0;
    public string TweakSummary => TweakCount switch
    {
        0 => $"Using {_preset.Name} as it ships",
        1 => $"1 colour changed from {_preset.Name}",
        int n => $"{n} colours changed from {_preset.Name}",
    };

    /// <summary>Shown under the chips while the high-contrast preset is taken from Windows.</summary>
    public bool FollowsWindowsHighContrast => _preset.HighContrast && SystemParameters.HighContrast;

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
            value = string.IsNullOrWhiteSpace(value) ? SkinFontChoice : value.Trim();
            if (!Set(ref _fontFamily, value) || _applying) return;
            string? stored = value == SkinFontChoice ? null : value;
            _config.QueueSettings("appearance.fontFamily", s => s.Appearance.FontFamily = stored);
        }
    }

    public string FontHint => $"{_skin.Name}'s own font is {_skin.FontFamily}.";

    /// <summary>"off" | "subtle" | "full" — what the widgets' motion level starts from before
    /// Windows' "Animation effects" caps it.</summary>
    public string Motion
    {
        get => _motion;
        set
        {
            value = value is "off" or "full" ? value : "subtle";
            if (!Set(ref _motion, value)) return;
            Raise(nameof(MotionHint));
            Raise(nameof(IsMotionFull));
            if (_applying) return;
            _config.QueueSettings("appearance.motion", s => s.Appearance.Motion = value);
        }
    }

    /// <summary>The loop frame rate only matters at Full, so its box is enabled only there.</summary>
    public bool IsMotionFull => _motion == "full";

    /// <summary>How many times a second the ambient loop moves. The widgets read it every loop
    /// pass, so a change applies without a restart.</summary>
    public int MotionFps
    {
        get => _motionFps;
        set
        {
            value = AppearanceSettings.ValidMotionFps(value);
            if (!Set(ref _motionFps, value) || _applying) return;
            _config.QueueSettings("appearance.motionFps", s => s.Appearance.MotionFps = value);
        }
    }

    public string MotionFpsHint => "How often the halos and the companion move at Full. Lower uses less CPU.";

    // Full's wording carries the cost on purpose: the loop never redraws a panel, but it does make
    // Windows' compositor redraw 30 times a second while it runs (one loop measured for ticket 09:
    // dwm.exe +0–2.5 % of a core at 30 Hz; the default layout is ticket 10's to measure), and nobody would
    // guess that from "Full".
    public string MotionHint => !SystemParameters.ClientAreaAnimation
        ? "Windows' animation effects are off, so nothing moves whatever is chosen here."
        : _motion switch
        {
            "off" => "Nothing moves. Every change appears at once.",
            "full" => "State changes ease in, the halos turn with CPU load and the companion breathes. The movement has Windows' compositor redraw at the loop frame rate below, which costs a little CPU. It pauses while a game is running.",
            _ => "Faces, warnings and newly opened widgets ease in. Readings always change at once.",
        };

    public AppearanceViewModel(LiveConfigService config)
    {
        _config = config;
        FontFamilies = [SkinFontChoice, .. Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.CurrentCultureIgnoreCase)];
        foreach (SkinInfo skin in SkinCatalog.All) Skins.Add(new SkinCardViewModel(skin));
        Load(_config.Settings);
        _config.ExternalChanged += Config_ExternalChanged;
    }

    public void Refresh() => Load(_config.Settings);

    /// <summary>
    /// Each skin keeps its own preset and tweaks, so switching back finds them where they were; the
    /// widgets that follow the global skin get their placement back too
    /// (<see cref="WidgetsConfig.SwitchGlobalSkin"/>, decision D21). widgets.json is written first.
    /// </summary>
    public async Task SelectSkinAsync(string id)
    {
        await _switchGate.WaitAsync();
        try
        {
            if (SkinCatalog.Find(id) is not { } skin) return;
            string from = _skin.Id;
            LoadSkin(skin, _config.Settings.Appearance.Skins.GetValueOrDefault(skin.Id));
            Rebuild();
            await CommitSkinChangeAsync(from, skin.Id, "appearance.skin", s => s.Appearance.Skin = skin.Id);
        }
        finally { _switchGate.Release(); }
    }

    /// <summary>
    /// The two halves of a global skin change, one at a time and each checked: the placement swap
    /// in widgets.json, then the skin itself in settings.json. A half that fails is cancelled so it
    /// cannot land on its own later; a settings failure also swaps the placements back, or a retry
    /// would park the new skin's placement under the old skin and lose the old one. Either way the
    /// page goes back to the skin the file really holds.
    /// </summary>
    private async Task CommitSkinChangeAsync(string from, string to, string settingsPath, Action<AppSettings> apply)
    {
        (string Path, long Generation)? placement = QueuePlacementSwitch(from, to);
        if (placement is { } queued && !await _config.FlushFileAsync(ConfigFileKind.Widgets))
        {
            _config.CancelPending(ConfigFileKind.Widgets, queued.Path, queued.Generation);
            Load(_config.Settings);
            return;
        }

        long generation = _config.QueueSettings(settingsPath, apply);
        if (await _config.FlushFileAsync(ConfigFileKind.Settings)) return;

        _config.CancelPending(ConfigFileKind.Settings, settingsPath, generation);
        if (placement is not null)
        {
            // Its own path again, like any switch. Should this write fail as well it stays queued:
            // it is the repair, and it lands with the next widgets.json write that succeeds.
            QueuePlacementSwitch(to, from);
            await _config.FlushFileAsync(ConfigFileKind.Widgets);
        }
        Load(_config.Settings);
    }

    /// <summary>Queued under a path of its own per switch: two switches inside one write window must
    /// both run, in order — keyed by one path, the second would replace the first and park the
    /// placement under the wrong skin.</summary>
    private (string Path, long Generation)? QueuePlacementSwitch(string from, string to)
    {
        if (from == to) return null;
        string path = $"widgets.skinSwitch.{++_skinSwitches}";
        return (path, _config.QueueWidgets(path, w => w.SwitchGlobalSkin(from, to)));
    }

    /// <summary>
    /// Picking a preset parks the old preset's colour tweaks and brings back the new one's
    /// (<see cref="SkinSettings.SwitchPreset"/>, decision D19): tweaks were made against one preset,
    /// so they stay with it. Options stay — they are the user's, not the palette's. The
    /// high-contrast preset is the exception: while Windows' high contrast is on it follows the
    /// system colours, re-derived as tweaks on every pick (never restored from the stash) so the
    /// static preset is still what a non-HC session sees.
    /// </summary>
    public void SelectPreset(string id)
    {
        if (SkinCatalog.FindPreset(_skin, id) is not { } preset) return;
        Dictionary<string, string>? derived = null;
        if (preset.HighContrast && SystemParameters.HighContrast)
            derived = HighContrast.FromSystemColors(_skin)
                .Where(p => !SameColor(preset.Colors.GetValueOrDefault(p.Key), p.Value))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var local = new SkinSettings { Preset = _preset.Id, Colors = new(_tweaks, StringComparer.Ordinal), PresetColors = _parked };
        local.SwitchPreset(id, _skin.DefaultPreset.Id, derived);
        _preset = preset;
        _parked = local.PresetColors;
        _tweaks.Clear();
        foreach (var (token, hex) in local.Colors) _tweaks[token] = hex;

        string skinId = _skin.Id;
        var tweaks = new Dictionary<string, string>(_tweaks, StringComparer.Ordinal);
        var parked = local.Clone().PresetColors;
        _config.QueueSettings($"appearance.skins.{skinId}.preset", s => s.Appearance.SkinFor(skinId).Preset = id);
        _config.QueueSettings($"appearance.skins.{skinId}.colors", s => s.Appearance.SkinFor(skinId).Colors = tweaks);
        _config.QueueSettings($"appearance.skins.{skinId}.presetColors", s => s.Appearance.SkinFor(skinId).PresetColors = parked);
        Rebuild();
    }

    public void ResetToPreset()
    {
        _tweaks.Clear();
        string skinId = _skin.Id;
        _config.QueueSettings($"appearance.skins.{skinId}.colors", s => s.Appearance.SkinFor(skinId).Colors = new());
        Rebuild();
    }

    /// <summary>Back to a fresh install's look: every skin's preset, tweaks and options go too. The
    /// skin goes back to the default one, so its widgets move like on any other skin switch; the
    /// parked placements are layout, not look, and stay.</summary>
    public async Task ResetAppearanceAsync()
    {
        await _switchGate.WaitAsync();
        try
        {
            var fresh = new AppSettings();
            string from = _skin.Id;
            Load(fresh);
            await CommitSkinChangeAsync(from, fresh.Appearance.Skin, "appearance", s => s.Appearance = new AppearanceSettings());
        }
        finally { _switchGate.Release(); }
    }

    private void Config_ExternalChanged(object? sender, ConfigChangedEventArgs e)
    {
        // Our own queued appearance writes have not landed yet; reloading now would show the file
        // from before them. The next external change after the flush brings everything in line.
        if (e.File == ConfigFileKind.Settings && !e.DirtyPaths.Any(p => p == "$" || p.StartsWith("appearance", StringComparison.Ordinal)))
            Load(_config.Settings);
    }

    private void Load(AppSettings settings)
    {
        _applying = true;
        try
        {
            AppearanceSettings a = settings.Appearance;
            AutoScale = a.Scale.IsAuto;
            FixedScale = a.Scale.Or(1.7);
            FontFamily = a.FontFamily ?? SkinFontChoice;
            Motion = a.Motion;
            MotionFps = a.MotionFps;
        }
        finally { _applying = false; }
        // An id this Halo does not know is drawn as Rainformer (Theme.Resolve), so show that.
        SkinInfo skin = SkinCatalog.Find(settings.Appearance.Skin) ?? SkinCatalog.Rainformer;
        LoadSkin(skin, settings.Appearance.Skins.GetValueOrDefault(skin.Id));
        Rebuild();
    }

    private void LoadSkin(SkinInfo skin, SkinSettings? entry)
    {
        _skin = skin;
        _preset = SkinCatalog.FindPreset(skin, entry?.Preset) ?? skin.DefaultPreset;
        _tweaks.Clear();
        _options.Clear();
        _parked = entry?.Clone().PresetColors;
        foreach (var (k, v) in entry?.Colors ?? []) _tweaks[k] = v;
        foreach (var (k, v) in entry?.Options ?? []) _options[k] = v;
    }

    /// <summary>Re-derive every control from the local state. Cheap: one skin, ~31 rows.</summary>
    private void Rebuild()
    {
        foreach (SkinCardViewModel card in Skins)
        {
            card.IsSelected = card.Skin == _skin;
            string presetId = card.IsSelected ? _preset.Id
                : SkinPalette.GlobalPreset(_config.Settings.Appearance, card.Skin).Id;
            card.Preview = LoadPreview(card.Id, presetId);
        }

        if (Presets.Count == 0 || Presets[0].Preset != _skin.Presets[0])
        {
            Presets.Clear();
            foreach (PresetSpec preset in _skin.Presets) Presets.Add(new PresetChipViewModel(_skin, preset));
        }
        foreach (PresetChipViewModel chip in Presets) chip.IsSelected = chip.Preset == _preset;

        RebuildOptions();
        RebuildColors();
        Raise(nameof(PresetName)); Raise(nameof(TweakCount)); Raise(nameof(HasTweaks)); Raise(nameof(TweakSummary));
        Raise(nameof(FollowsWindowsHighContrast)); Raise(nameof(FontHint));
    }

    private void RebuildOptions()
    {
        if (SkinOptions.Count != _skin.Options.Count || SkinOptions.Select(o => o.Key).Except(_skin.Options.Select(o => o.Key)).Any())
        {
            SkinOptions.Clear();
            foreach (OptionSpec spec in _skin.Options) SkinOptions.Add(new SkinOptionViewModel(spec, OptionChanged, canInherit: false));
        }
        Dictionary<string, string> baseline = SkinPalette.OptionBaseline(_skin, _preset);
        foreach (SkinOptionViewModel row in SkinOptions)
            row.Load(_options.GetValueOrDefault(row.Key), baseline[row.Key]);
    }

    private void RebuildColors()
    {
        var rows = ColorGroups.SelectMany(g => g.Rows).ToDictionary(r => r.Token, StringComparer.Ordinal);
        if (rows.Count != _skin.Tokens.Count || _skin.Tokens.Any(t => !rows.ContainsKey(t.Token)))
        {
            ColorGroups.Clear();
            rows.Clear();
            foreach (var group in _skin.Tokens.GroupBy(t => t.Core ? t.Group : "Skin extras").OrderBy(g => GroupOrder(g.Key)))
            {
                var groupRows = group.Select(t => new GlobalColorViewModel(t.Token, FriendlyToken(t.Token), t.Description, ColorChanged)).ToArray();
                foreach (var row in groupRows) rows[row.Token] = row;
                ColorGroups.Add(new ColorGroupViewModel<GlobalColorViewModel>(group.Key, groupRows));
            }
        }
        foreach (GlobalColorViewModel row in rows.Values)
        {
            string presetHex = _preset.Colors.GetValueOrDefault(row.Token) ?? "#00000000";
            row.ApplyExternal(_tweaks.GetValueOrDefault(row.Token) ?? presetHex, presetHex);
        }
    }

    private void ColorChanged(string token, string hex)
    {
        // A colour equal to the preset's is no tweak at all.
        bool isTweak = !SameColor(_preset.Colors.GetValueOrDefault(token), hex);
        if (isTweak) _tweaks[token] = hex; else _tweaks.Remove(token);
        string skinId = _skin.Id;
        _config.QueueSettings($"appearance.skins.{skinId}.colors.{token}", s =>
        {
            SkinSettings entry = s.Appearance.SkinFor(skinId);
            if (isTweak) entry.Colors[token] = hex; else entry.Colors.Remove(token);
        });
        Raise(nameof(TweakCount)); Raise(nameof(HasTweaks)); Raise(nameof(TweakSummary));
    }

    private void OptionChanged(string key, string? value)
    {
        if (value is null) _options.Remove(key); else _options[key] = value;
        string skinId = _skin.Id;
        _config.QueueSettings($"appearance.skins.{skinId}.options.{key}", s =>
        {
            SkinSettings entry = s.Appearance.SkinFor(skinId);
            if (value is null) entry.Options.Remove(key); else entry.Options[key] = value;
        });
    }

    /// <summary>The gallery picture build.ps1 rendered for this skin × preset: the one with the
    /// skin's game art when its <c>game-art</c> folder ships one, else the art-free one, else null
    /// (a plain <c>dotnet build</c> before the first <c>build.ps1</c>). Art-bearing previews live
    /// inside <c>game-art\</c> so a takedown that deletes the folder takes them too.
    /// Loaded fully into memory so the file is never held open.</summary>
    private static ImageSource? LoadPreview(string skinId, string presetId)
    {
        string skinDir = Path.Combine(Paths.AssetsDir, "skins", skinId);
        string file = Path.Combine(skinDir, "game-art", "previews", presetId + ".png");
        if (!File.Exists(file)) file = Path.Combine(skinDir, "previews", presetId + ".png");
        if (!File.Exists(file)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Colour groups in the wireframe's order; a skin's extras go last.</summary>
    internal static int GroupOrder(string group)
        => Array.IndexOf(new[] { "Surfaces", "Text", "Data", "Warnings" }, group) is int i and >= 0 ? i : int.MaxValue;

    internal static Color Opaque(string? hex)
        => ThemeTokens.TryParse(hex, out byte r, out byte g, out byte b, out _) ? Color.FromRgb(r, g, b) : Colors.Transparent;

    internal static bool SameColor(string? a, string? b)
        => ThemeTokens.TryParse(a, out byte r1, out byte g1, out byte b1, out byte a1)
           && ThemeTokens.TryParse(b, out byte r2, out byte g2, out byte b2, out byte a2)
           && r1 == r2 && g1 == g2 && b1 == b2 && a1 == a2;

    internal static string FriendlyToken(string token)
    {
        var words = new List<string>();
        int start = 0;
        for (int index = 1; index < token.Length; index++)
        {
            if (!char.IsUpper(token[index]) && !(char.IsDigit(token[index]) && !char.IsDigit(token[index - 1]))) continue;
            words.Add(token[start..index]);
            start = index;
        }
        words.Add(token[start..]);
        string label = string.Join(" ", words).Replace("bg", "background", StringComparison.OrdinalIgnoreCase);
        return label.Length == 0 ? token : char.ToUpper(label[0]) + label[1..];
    }

    private static double ResolvePrimaryScale()
    {
        Rect work = SystemParameters.WorkArea;
        double shortSideDip = Math.Min(work.Width, work.Height);
        double raw = 1.7 * shortSideDip / 1080.0;
        return Math.Round(Math.Clamp(raw, 1, 3.5) / 0.05) * 0.05;
    }

    public void Dispose() => _config.ExternalChanged -= Config_ExternalChanged;
}

/// <summary>
/// Windows high contrast → a full palette. Every core token gets a system colour; the old
/// derivation set 15 of 30 and left the rest on Rainformer's light defaults, e.g. alert text at
/// 2.6:1 on black (tech plan §8). Each colour is checked against the window background and swapped
/// for the most readable system colour when it falls under 7:1.
/// </summary>
internal static class HighContrast
{
    public static Dictionary<string, string> FromSystemColors(SkinInfo skin)
    {
        Color background = SystemColors.WindowColor;
        string Readable(Color c) => Hex(MostReadable(c, background, 7));
        string bg = Hex(background), text = Readable(SystemColors.WindowTextColor),
            highlight = Readable(SystemColors.HighlightColor), hot = Readable(SystemColors.HotTrackColor),
            gray = Readable(SystemColors.GrayTextColor);

        var roles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bgTop"] = bg, ["bgBody"] = bg, ["solidLabel"] = bg, ["emptyBar"] = gray, ["stroke"] = text,
            ["title"] = text, ["text"] = text, ["text2"] = text, ["maxLabelGray"] = gray, ["inactiveButton"] = gray,
            ["activeTitle"] = hot, ["redText"] = hot,
            ["bar"] = highlight, ["histogram"] = highlight, ["netDown"] = highlight, ["netUp"] = highlight,
            ["cpuUsage"] = highlight, ["ramUsage"] = highlight, ["gpuUsage"] = highlight,
            ["gpuMemUsage"] = highlight, ["gpuFan"] = highlight,
            ["cpuTemp"] = hot, ["gpuTemp"] = hot,
            ["red"] = hot, ["barWarn"] = hot, ["staleBadge"] = hot,
            ["devWarn1"] = highlight, ["devWarn2"] = highlight, ["devWarn3"] = hot, ["devWarn4"] = hot, ["devWarn5"] = hot,
        };
        // A skin's own extra tokens have no system role; they keep the static preset's values.
        return roles.Where(r => skin.Tokens.Any(t => t.Token == r.Key)).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    private static string Hex(Color color) => ThemeTokens.ToHex(color.R, color.G, color.B, 255);

    private static Color MostReadable(Color preferred, Color background, double minimum)
    {
        if (Contrast(preferred, background) >= minimum) return preferred;
        Color[] candidates = [SystemColors.WindowTextColor, SystemColors.HighlightTextColor, Colors.Black, Colors.White];
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
}
