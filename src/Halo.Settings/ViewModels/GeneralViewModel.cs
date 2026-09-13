using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
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

    private readonly LiveConfigService _config;
    private bool _applying;
    private bool _lockAll;
    private bool _snap;
    private bool _autoScale;
    private double _fixedScale = 1.7;
    private string _fontFamily = "Trebuchet MS";
    private double _textSizePt = 8;
    private double _cornerRadius = 4;
    private ChoiceItem? _transport;
    private ChoiceItem? _presentedTap;
    private double _etwFlushMs = 10;
    private double _frameLowsWindowS = 60;
    private string _networkInterface = "Best";
    private bool _externalIpEnabled;
    private string _externalIpUrl = "https://api.ipify.org";
    private double _externalIpRefreshMinutes = 5;

    public ObservableCollection<GlobalColorViewModel> Colors { get; } = [];
    public IReadOnlyList<string> FontFamilies { get; }
    public ObservableCollection<string> NetworkInterfaces { get; } = [];
    public IReadOnlyList<ChoiceItem> Transports { get; } =
    [
        new("auto", "Auto — bundled PresentMon service"),
        new("sdk", "Service + SDK only"),
    ];
    public IReadOnlyList<ChoiceItem> TapModes { get; } =
    [
        new("auto", "Auto — live tap when supported"),
        new("off", "Off — capture transport only"),
    ];

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

    public ChoiceItem? Transport
    {
        get => _transport;
        set
        {
            if (!Set(ref _transport, value) || value is null || _applying) return;
            string selected = value.Value;
            _config.QueueSettings("collector.presentMonTransport", s => s.Collector.PresentMonTransport = selected);
        }
    }

    public ChoiceItem? PresentedTap
    {
        get => _presentedTap;
        set
        {
            if (!Set(ref _presentedTap, value) || value is null || _applying) return;
            string selected = value.Value;
            _config.QueueSettings("collector.presentedTap", s => s.Collector.PresentedTap = selected);
        }
    }

    public double EtwFlushMs
    {
        get => _etwFlushMs;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, 1000));
            if (!Set(ref _etwFlushMs, value) || _applying) return;
            int selected = (int)value;
            _config.QueueSettings("collector.presentMonEtwFlushMs", s => s.Collector.PresentMonEtwFlushMs = selected);
        }
    }

    public double FrameLowsWindowS
    {
        get => _frameLowsWindowS;
        set
        {
            value = Math.Round(Math.Clamp(value, 10, 600));
            if (!Set(ref _frameLowsWindowS, value) || _applying) return;
            _config.QueueSettings("collector.frameLowsWindowS", s => s.Collector.FrameLowsWindowS = value);
        }
    }

    public string NetworkInterface
    {
        get => _networkInterface;
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? "Best" : value.Trim();
            if (!Set(ref _networkInterface, value) || _applying) return;
            _config.QueueSettings("collector.networkInterface", s => s.Collector.NetworkInterface = value);
        }
    }

    public bool ExternalIpEnabled
    {
        get => _externalIpEnabled;
        set
        {
            if (!Set(ref _externalIpEnabled, value) || _applying) return;
            _config.QueueSettings("collector.externalIp.enabled", s => s.Collector.ExternalIp.Enabled = value);
        }
    }

    public string ExternalIpUrl
    {
        get => _externalIpUrl;
        set
        {
            value = value?.Trim() ?? "";
            if (!Set(ref _externalIpUrl, value) || _applying) return;
            _config.QueueSettings("collector.externalIp.url", s => s.Collector.ExternalIp.Url = value);
        }
    }

    public double ExternalIpRefreshMinutes
    {
        get => _externalIpRefreshMinutes;
        set
        {
            value = Math.Round(Math.Clamp(value, 1, 1440), 1);
            if (!Set(ref _externalIpRefreshMinutes, value) || _applying) return;
            _config.QueueSettings("collector.externalIp.refreshMinutes", s => s.Collector.ExternalIp.RefreshMinutes = value);
        }
    }

    public GeneralViewModel(LiveConfigService config)
    {
        _config = config;
        FontFamilies = Fonts.SystemFontFamilies.Select(font => font.Source).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        LoadNetworkInterfaces();
        foreach ((string token, string hex, string description) in ThemeTokens.Defaults)
            Colors.Add(new GlobalColorViewModel(token, FriendlyToken(token), description, hex, ColorChanged));
        ApplyFromStore(NoDirtyPaths);
        _config.ExternalChanged += Config_ExternalChanged;
    }

    public void ApplyPreset(string name)
    {
        IReadOnlyDictionary<string, string> palette = name switch
        {
            "Light" => CompletePalette(LightPalette),
            "High contrast" => BuildHighContrastPalette(),
            _ => ThemeTokens.Defaults.ToDictionary(x => x.Token, x => x.Color, StringComparer.Ordinal),
        };
        foreach (GlobalColorViewModel row in Colors)
            if (palette.TryGetValue(row.Token, out string? value)) row.Hex = value;
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
        _config.QueueSettings("appearance", s => s.Appearance = new AppearanceSettings(), flushImmediately: true);
    }

    public void ResetFrameData()
    {
        var defaults = new CollectorSettings();
        _applying = true;
        try
        {
            Transport = Transports[0];
            PresentedTap = TapModes[0];
            EtwFlushMs = defaults.PresentMonEtwFlushMs;
            FrameLowsWindowS = defaults.FrameLowsWindowS;
        }
        finally { _applying = false; }
        _config.QueueSettings("collector.presentMonTransport", s => s.Collector.PresentMonTransport = defaults.PresentMonTransport);
        _config.QueueSettings("collector.presentedTap", s => s.Collector.PresentedTap = defaults.PresentedTap);
        _config.QueueSettings("collector.presentMonEtwFlushMs", s => s.Collector.PresentMonEtwFlushMs = defaults.PresentMonEtwFlushMs);
        _config.QueueSettings("collector.frameLowsWindowS", s => s.Collector.FrameLowsWindowS = defaults.FrameLowsWindowS, flushImmediately: true);
    }

    public void ResetNetwork()
    {
        var defaults = new CollectorSettings();
        _applying = true;
        try
        {
            NetworkInterface = defaults.NetworkInterface;
            ExternalIpEnabled = defaults.ExternalIp.Enabled;
            ExternalIpUrl = defaults.ExternalIp.Url;
            ExternalIpRefreshMinutes = defaults.ExternalIp.RefreshMinutes;
        }
        finally { _applying = false; }
        _config.QueueSettings("collector.networkInterface", s => s.Collector.NetworkInterface = defaults.NetworkInterface);
        _config.QueueSettings("collector.externalIp.enabled", s => s.Collector.ExternalIp.Enabled = defaults.ExternalIp.Enabled);
        _config.QueueSettings("collector.externalIp.url", s => s.Collector.ExternalIp.Url = defaults.ExternalIp.Url);
        _config.QueueSettings("collector.externalIp.refreshMinutes", s => s.Collector.ExternalIp.RefreshMinutes = defaults.ExternalIp.RefreshMinutes, flushImmediately: true);
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
        ApplyCollector(settings.Collector, dirty);
    }

    private void ApplyCollector(CollectorSettings collector, IReadOnlySet<string> dirty)
    {
        if (!Conflicts("collector.presentMonTransport", dirty))
            Transport = Transports.FirstOrDefault(x => x.Value.Equals(collector.PresentMonTransport, StringComparison.OrdinalIgnoreCase)) ?? Transports[0];
        if (!Conflicts("collector.presentedTap", dirty))
            PresentedTap = TapModes.FirstOrDefault(x => x.Value.Equals(collector.PresentedTap, StringComparison.OrdinalIgnoreCase)) ?? TapModes[0];
        if (!Conflicts("collector.presentMonEtwFlushMs", dirty)) EtwFlushMs = collector.PresentMonEtwFlushMs;
        if (!Conflicts("collector.frameLowsWindowS", dirty)) FrameLowsWindowS = collector.FrameLowsWindowS;
        if (!Conflicts("collector.networkInterface", dirty)) NetworkInterface = collector.NetworkInterface;
        if (!Conflicts("collector.externalIp.enabled", dirty)) ExternalIpEnabled = collector.ExternalIp.Enabled;
        if (!Conflicts("collector.externalIp.url", dirty)) ExternalIpUrl = collector.ExternalIp.Url;
        if (!Conflicts("collector.externalIp.refreshMinutes", dirty)) ExternalIpRefreshMinutes = collector.ExternalIp.RefreshMinutes;
    }

    private void ColorChanged(string token, string hex)
    {
        if (_applying) return;
        _config.QueueSettings($"appearance.colors.{token}", settings => settings.Appearance.Colors[token] = hex);
    }

    private void LoadNetworkInterfaces()
    {
        NetworkInterfaces.Clear();
        NetworkInterfaces.Add("Best");
        try
        {
            foreach (string name in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                         .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                         .OrderByDescending(item => item.OperationalStatus == OperationalStatus.Up)
                         .ThenBy(item => item.Name)
                         .Select(item => item.Name)
                         .Distinct(StringComparer.CurrentCultureIgnoreCase))
                NetworkInterfaces.Add(name);
        }
        catch { /* The editable combo still accepts a saved adapter while Windows is querying. */ }
    }

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
