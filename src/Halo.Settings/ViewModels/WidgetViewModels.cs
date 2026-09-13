using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Halo.Metrics;
using Halo.Settings.Services;
using Halo.Shared.Config;
using Halo.Shared.Panels;

namespace Halo.Settings.ViewModels;

public sealed record HardwareChoice(string Value, string Label);

public sealed class HardwareSnapshot
{
    public static readonly HardwareSnapshot Offline = new(false, [], [], [], 0, new Dictionary<string, MetricInfo>(StringComparer.Ordinal));

    public bool Online { get; }
    public IReadOnlyList<HardwareChoice> Gpus { get; }
    public IReadOnlyList<HardwareChoice> Fans { get; }
    public IReadOnlyList<HardwareChoice> Volumes { get; }
    public int LogicalCpuCount { get; }
    public IReadOnlyDictionary<string, MetricInfo> Metrics { get; }
    public string Signature { get; }

    private HardwareSnapshot(bool online, IReadOnlyList<HardwareChoice> gpus, IReadOnlyList<HardwareChoice> fans,
        IReadOnlyList<HardwareChoice> volumes, int logicalCpuCount, IReadOnlyDictionary<string, MetricInfo> metrics)
    {
        Online = online;
        Gpus = gpus;
        Fans = fans;
        Volumes = volumes;
        LogicalCpuCount = logicalCpuCount;
        Metrics = metrics;
        Signature = $"{online}|{logicalCpuCount}|{string.Join(';', gpus)}|{string.Join(';', fans)}|{string.Join(';', volumes)}|{metrics.Count}";
    }

    public static HardwareSnapshot Capture(CollectorSession session)
    {
        session.Poll();
        if (!session.Attached || session.Stale) return Offline;
        IReadOnlyList<MetricInfo> registry = session.Metrics();
        var metrics = registry.ToDictionary(metric => metric.Name, StringComparer.Ordinal);

        int gpuCount = Math.Clamp((int)Math.Round(session.Get(MetricNames.GpuCount, 0)), 0, 32);
        var gpus = Enumerable.Range(0, gpuCount)
            .Select(index => new HardwareChoice(index.ToString(),
                $"GPU {index} · {session.GetText(MetricNames.GpuName(index), "Detected GPU")}"))
            .ToArray();

        int fanCount = Math.Clamp((int)Math.Round(session.Get(MetricNames.FanCount, 0)), 0, 256);
        var fans = Enumerable.Range(0, fanCount)
            .Select(index => new HardwareChoice(index.ToString(),
                $"{session.GetText(MetricNames.FanName(index), $"Fan {index}")} · channel {index}"))
            .ToArray();

        var letters = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MetricInfo metric in registry)
        {
            Match match = Regex.Match(metric.Name, @"^drive\.([a-z])\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success) letters.Add(match.Groups[1].Value.ToUpperInvariant());
        }
        var volumes = letters.Select(letter =>
        {
            string label = session.GetText(MetricNames.DriveLabel(letter[0]), "");
            return new HardwareChoice(letter, label.Length == 0 ? $"{letter}:" : $"{letter}: · {label}");
        }).ToArray();

        int logical = Math.Clamp((int)Math.Round(session.Get(MetricNames.CpuLogicalCount, 0)), 0, 512);
        return new HardwareSnapshot(true, gpus, fans, volumes, logical, metrics);
    }
}

public sealed class WarningValueViewModel : ObservableObject
{
    private readonly Action<int, double> _changed;
    private double _value;
    public int Index { get; }
    public double Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value)) return;
            _changed(Index, value);
        }
    }

    public WarningValueViewModel(int index, double value, Action<int, double> changed)
    {
        Index = index;
        _value = value;
        _changed = changed;
    }

    public void Apply(double value) => Set(ref _value, value, nameof(Value));
}

public sealed class MetricRowViewModel : ObservableObject
{
    private readonly WidgetItemViewModel _owner;
    private readonly MetricSpec _spec;
    private bool _applying;
    private bool _show;
    private string _label;
    private bool _graph;
    private Color _color;
    private bool _pickerOpen;
    private double _max;

    public string Key { get; }
    public string MetricName { get; }
    public string Name { get; }
    public string Unit { get; }
    public bool IsGraphable => _spec.Graphable;
    public bool HasWarnings => WarningValues.Count > 0;
    public bool HasMax => _spec.Repeat == Repeat.PerFan;
    public bool NeedsElevation => _spec.NeedsElevation;
    public ObservableCollection<WarningValueViewModel> WarningValues { get; } = [];

    public bool Show
    {
        get => _show;
        set
        {
            if (!Set(ref _show, value) || _applying) return;
            bool? saved = value == _spec.DefaultShow ? null : value;
            _owner.ChangeMetric(Key, "show", metric => metric.Show = saved);
        }
    }

    public string Label
    {
        get => _label;
        set
        {
            value ??= "";
            if (!Set(ref _label, value) || _applying) return;
            string? saved = value == DefaultResolvedLabel ? null : value;
            _owner.ChangeMetric(Key, "label", metric => metric.Label = saved);
        }
    }

    public bool Graph
    {
        get => _graph;
        set
        {
            if (!Set(ref _graph, value) || _applying || !_spec.Graphable) return;
            bool? saved = value == _spec.GraphDefaultOn ? null : value;
            _owner.ChangeMetric(Key, "graph", metric => metric.Graph = saved);
        }
    }

    public Color Color
    {
        get => _color;
        set
        {
            if (!Set(ref _color, value)) return;
            Raise(nameof(Hex));
            Raise(nameof(Swatch));
            if (_applying) return;
            string? saved = Hex.Equals(DefaultColor, StringComparison.OrdinalIgnoreCase) ? null : Hex;
            _owner.ChangeMetric(Key, "color", metric => metric.Color = saved);
        }
    }

    public string Hex
    {
        get => ThemeTokens.ToHex(Color.R, Color.G, Color.B, Color.A);
        set
        {
            if (!ThemeTokens.TryParse(value, out byte r, out byte g, out byte b, out byte a))
            {
                Raise(nameof(Hex));
                return;
            }
            Color = Color.FromArgb(a, r, g, b);
        }
    }

    public Brush Swatch => new SolidColorBrush(Color);
    public string DefaultColor => ThemeTokens.Default(_spec.ColorToken) ?? "#000000FF";
    public string DefaultResolvedLabel { get; }

    public bool IsPickerOpen
    {
        get => _pickerOpen;
        set => Set(ref _pickerOpen, value);
    }

    public double Max
    {
        get => _max;
        set
        {
            value = Math.Max(100, Math.Round(value));
            if (!Set(ref _max, value) || _applying || !HasMax) return;
            double? saved = Math.Abs(value - 2000) < 0.01 ? null : value;
            _owner.ChangeMetric(Key, "max", metric => metric.Max = saved);
        }
    }

    public MetricRowViewModel(WidgetItemViewModel owner, MetricSpec spec, string key, string metricName,
        string name, string defaultLabel, MetricSetting? setting)
    {
        _owner = owner;
        _spec = spec;
        Key = key;
        MetricName = metricName;
        Name = name;
        Unit = UnitLabel(spec.Unit);
        DefaultResolvedLabel = defaultLabel;
        _show = setting?.Show ?? spec.DefaultShow;
        _label = setting?.Label ?? defaultLabel;
        _graph = setting?.Graph ?? spec.GraphDefaultOn;
        _color = Parse(setting?.Color ?? DefaultColor);
        _max = setting?.Max ?? 2000;
        double[] warnings = setting?.Warn ?? spec.WarnDefaults ?? [];
        for (int index = 0; index < warnings.Length; index++)
            WarningValues.Add(new WarningValueViewModel(index, warnings[index], WarningChanged));
    }

    private void WarningChanged(int index, double value)
    {
        if (_applying) return;
        double[] next = WarningValues.Select(item => item.Value).ToArray();
        double lower = index == 0 ? double.NegativeInfinity : next[index - 1];
        double upper = index == next.Length - 1 ? double.PositiveInfinity : next[index + 1];
        next[index] = Math.Clamp(value, lower, upper);
        if (next[index] != value)
        {
            _applying = true;
            try { WarningValues[index].Apply(next[index]); }
            finally { _applying = false; }
        }
        double[] defaults = _spec.WarnDefaults ?? [];
        double[]? saved = next.SequenceEqual(defaults) ? null : next;
        _owner.ChangeMetric(Key, "warn", metric => metric.Warn = saved);
    }

    private static string UnitLabel(MetricUnit unit) => unit switch
    {
        MetricUnit.Percent => "%", MetricUnit.Celsius => "°C", MetricUnit.Milliseconds => "ms",
        MetricUnit.Megahertz => "MHz", MetricUnit.Hertz => "Hz", MetricUnit.Rpm => "RPM",
        MetricUnit.Watts => "W", MetricUnit.Volts => "V", MetricUnit.Fps => "FPS",
        MetricUnit.Bytes => "bytes", MetricUnit.BytesPerSecond => "B/s", MetricUnit.Seconds => "s",
        _ => "",
    };

    private static Color Parse(string hex)
        => ThemeTokens.TryParse(hex, out byte r, out byte g, out byte b, out byte a)
            ? Color.FromArgb(a, r, g, b)
            : System.Windows.Media.Colors.Transparent;
}

public sealed class HardwareChoiceViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;
    public string Value { get; }
    public string Label { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) _changed(); }
    }

    public HardwareChoiceViewModel(string value, string label, bool selected, Action changed)
    {
        Value = value;
        Label = label;
        _isSelected = selected;
        _changed = changed;
    }
}

public enum OptionEditorKind { Boolean, Number, Choice, Text, Checklist }

public sealed class OptionEditorViewModel : ObservableObject
{
    private readonly WidgetItemViewModel _owner;
    private readonly OptionSpec _spec;
    private bool _boolValue;
    private double _numberValue;
    private ChoiceItem? _selectedChoice;
    private string _textValue = "";

    public string Key => _spec.Key;
    public string Label => _spec.Label;
    public string Help => _spec.Help;
    public bool Structural => _spec.Structural;
    public OptionEditorKind EditorKind { get; }
    public bool IsBoolean => EditorKind == OptionEditorKind.Boolean;
    public bool IsNumber => EditorKind == OptionEditorKind.Number;
    public bool IsChoice => EditorKind == OptionEditorKind.Choice;
    public bool IsText => EditorKind == OptionEditorKind.Text;
    public bool IsChecklist => EditorKind == OptionEditorKind.Checklist;
    public double Minimum { get; }
    public double Maximum { get; }
    public ObservableCollection<ChoiceItem> Choices { get; } = [];
    public ObservableCollection<HardwareChoiceViewModel> HardwareChoices { get; } = [];
    public string SourceHint { get; }

    public bool BoolValue
    {
        get => _boolValue;
        set { if (Set(ref _boolValue, value)) Save(value ? "true" : "false"); }
    }
    public double NumberValue
    {
        get => _numberValue;
        set
        {
            value = Math.Clamp(value, Minimum, Maximum);
            if (Set(ref _numberValue, value)) Save(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
    public ChoiceItem? SelectedChoice
    {
        get => _selectedChoice;
        set { if (Set(ref _selectedChoice, value) && value is not null) Save(value.Value); }
    }
    public string TextValue
    {
        get => _textValue;
        set { value ??= ""; if (Set(ref _textValue, value)) Save(value.Trim()); }
    }

    public OptionEditorViewModel(WidgetItemViewModel owner, OptionSpec spec, string current, HardwareSnapshot hardware)
    {
        _owner = owner;
        _spec = spec;
        current = string.IsNullOrWhiteSpace(current) ? spec.Default : current;
        (Minimum, Maximum) = ParseRange(spec.Range);

        // Only the hardware-backed keys take their choices from the collector, and only they get the
        // "keep the saved value visible even if that hardware is gone" fallback. Asking HardwareFor
        // about anything else hands back a one-item list holding nothing but the current value, which
        // both shadows spec.Choices below and promotes plain options to a combo box.
        IReadOnlyList<HardwareChoice> hardwareChoices =
            IsHardwareOption(spec.Key) ? HardwareFor(spec.Key, hardware, current) : [];
        if (spec.Kind == OptionKind.Bool) EditorKind = OptionEditorKind.Boolean;
        else if (spec.Kind == OptionKind.List) EditorKind = OptionEditorKind.Checklist;
        else if (spec.Kind == OptionKind.Enum || hardwareChoices.Count > 0) EditorKind = OptionEditorKind.Choice;
        else if (spec.Kind is OptionKind.Int or OptionKind.Double) EditorKind = OptionEditorKind.Number;
        else EditorKind = OptionEditorKind.Text;

        _boolValue = current.Equals("true", StringComparison.OrdinalIgnoreCase);
        _numberValue = double.TryParse(current, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double number) ? Math.Clamp(number, Minimum, Maximum) : Minimum;
        _textValue = current;

        if (EditorKind == OptionEditorKind.Choice)
        {
            IEnumerable<ChoiceItem> choices = hardwareChoices.Count > 0
                ? hardwareChoices.Select(choice => new ChoiceItem(choice.Value, choice.Label))
                : (spec.Choices ?? []).Select(choice => new ChoiceItem(choice, FriendlyChoice(choice)));
            foreach (ChoiceItem choice in choices) Choices.Add(choice);
            if (!Choices.Any(choice => choice.Value.Equals(current, StringComparison.OrdinalIgnoreCase)))
                Choices.Add(new ChoiceItem(current, current.Length == 0 ? "Automatic" : current + " · saved"));
            _selectedChoice = Choices.FirstOrDefault(choice => choice.Value.Equals(current, StringComparison.OrdinalIgnoreCase));
        }

        if (EditorKind == OptionEditorKind.Checklist)
        {
            HashSet<string> selected = Split(current);
            if (current.Length == 0 && spec.Key is "volumes" or "channels")
                selected.UnionWith(hardwareChoices.Select(choice => choice.Value));
            foreach (HardwareChoice choice in hardwareChoices)
                HardwareChoices.Add(new HardwareChoiceViewModel(choice.Value, choice.Label, selected.Contains(choice.Value), ChecklistChanged));
        }

        SourceHint = hardware.Online || !IsHardwareOption(spec.Key)
            ? spec.Structural ? "Rebuilds only this widget." : "Applies without rebuilding the window."
            : "Collector not running — showing the saved choice or a manifest default.";
    }

    private void ChecklistChanged()
    {
        Save(string.Join(',', HardwareChoices.Where(choice => choice.IsSelected).Select(choice => choice.Value)));
    }

    private void Save(string value) => _owner.ChangeOption(_spec.Key, value == _spec.Default ? null : value, _spec.Structural);

    /// <summary>Collector-discovered choices for one of the <see cref="IsHardwareOption"/> keys.
    /// Never call it for anything else: the saved-value fallback below would turn an empty list
    /// into a one-item list and the option would offer only the value it already has.</summary>
    private static IReadOnlyList<HardwareChoice> HardwareFor(string key, HardwareSnapshot hardware, string current)
    {
        List<HardwareChoice> choices = key switch
        {
            "gpuIndex" => hardware.Gpus.ToList(),
            "cpuFanChannel" or "channels" => hardware.Fans.ToList(),
            "volumes" or "freeMode" => hardware.Volumes.ToList(),
            _ => [],
        };
        if (key == "cpuFanChannel") choices.Insert(0, new HardwareChoice("", "Automatic · first CPU fan"));
        foreach (string saved in Split(current))
            if (!choices.Any(choice => choice.Value.Equals(saved, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new HardwareChoice(saved, saved.Length == 0 ? "Automatic" : saved + " · saved"));
        if (choices.Count == 0 && key == "gpuIndex") choices.Add(new("0", "GPU 0 · default"));
        if (choices.Count == 0 && key == "cpuFanChannel") choices.Add(new("", "Automatic · first CPU fan"));
        if (choices.Count == 0 && key == "volumes") choices.Add(new("C", "C: · manifest default"));
        if (choices.Count == 0 && key == "channels") choices.Add(new("0", "Fan channel 0 · manifest default"));
        return choices;
    }

    private static bool IsHardwareOption(string key) => key is "gpuIndex" or "cpuFanChannel" or "channels" or "volumes" or "freeMode";
    private static HashSet<string> Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static (double Minimum, double Maximum) ParseRange(string? range)
    {
        string[] pieces = (range ?? "0..100").Split("..", StringSplitOptions.TrimEntries);
        if (pieces.Length == 2 && double.TryParse(pieces[0], out double minimum) && double.TryParse(pieces[1], out double maximum))
            return (minimum, maximum);
        return (0, 100);
    }
    private static string FriendlyChoice(string value) => value switch
    {
        "auto" => "Automatic", "thread" => "Logical processors", "core" => "Physical cores",
        "hidden" => "Hidden", "displayed" => "Displayed frames", "presented" => "Presented frames",
        "bytes" => "Bytes per second", "bits" => "Bits per second", "line" => "Line", "filled" => "Filled",
        _ => value,
    };
}

public sealed class WidgetColorViewModel : ObservableObject
{
    private readonly WidgetItemViewModel _owner;
    private bool _useGlobal;
    private Color _color;
    private bool _pickerOpen;
    public string Token { get; }
    public string Label { get; }

    public bool UseGlobal
    {
        get => _useGlobal;
        set
        {
            if (!Set(ref _useGlobal, value)) return;
            _owner.ChangeAppearanceColor(Token, value ? null : Hex);
            Raise(nameof(IsEnabled));
        }
    }
    public bool IsEnabled => !UseGlobal;
    public Color Color
    {
        get => _color;
        set
        {
            if (!Set(ref _color, value)) return;
            Raise(nameof(Hex));
            Raise(nameof(Swatch));
            if (!UseGlobal) _owner.ChangeAppearanceColor(Token, Hex);
        }
    }
    public string Hex
    {
        get => ThemeTokens.ToHex(Color.R, Color.G, Color.B, Color.A);
        set
        {
            if (!ThemeTokens.TryParse(value, out byte r, out byte g, out byte b, out byte a)) { Raise(nameof(Hex)); return; }
            Color = Color.FromArgb(a, r, g, b);
        }
    }
    public Brush Swatch => new SolidColorBrush(Color);
    public bool IsPickerOpen { get => _pickerOpen; set => Set(ref _pickerOpen, value); }

    public WidgetColorViewModel(WidgetItemViewModel owner, string token, string value, bool useGlobal)
    {
        _owner = owner;
        Token = token;
        Label = ThemeTokens.Defaults.FirstOrDefault(item => item.Token == token).Description ?? token;
        _color = Parse(value);
        _useGlobal = useGlobal;
    }

    private static Color Parse(string value) => ThemeTokens.TryParse(value, out byte r, out byte g, out byte b, out byte a)
        ? Color.FromArgb(a, r, g, b) : System.Windows.Media.Colors.Transparent;
}

public sealed class WidgetItemViewModel : ObservableObject
{
    private readonly WidgetsPageViewModel _owner;
    private bool _applying;
    private HardwareSnapshot _hardware;
    private string _title = "";
    private bool _enabled;
    private double _rateHz;
    private double _rateMaximum = 10;
    private string _rateHelp = "";
    private double _historyS;
    private double _graphHeight;
    private ChoiceItem? _graphStyle;
    private ChoiceItem? _monitor;
    private double _x;
    private double _y;
    private ChoiceItem? _zMode;
    private bool _clickThrough;
    private bool _keepOnScreen;
    private bool _locked;
    private double _opacity;
    private bool _hideOnFullscreen;
    private bool _useGlobalFont;
    private string _fontFamily = "Trebuchet MS";
    private bool _useGlobalScale;
    private bool _scaleAuto;
    private double _scale = 1.7;
    private bool _useGlobalShowTitle;
    private bool _showTitle = true;
    private bool _useGlobalWidth;
    private double _width = 206;
    private ChoiceItem? _tempUnit;
    private string _networkInterface = "Best";
    private bool _externalIpEnabled;
    private string _externalIpUrl = "https://api.ipify.org";
    private double _externalIpRefresh = 5;
    private ChoiceItem? _transport;
    private ChoiceItem? _tap;
    private double _flushMs;
    private double _lowsWindow;

    public string Id { get; }
    public string Type { get; }
    public PanelType Panel { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? FriendlyPanelName() : Title;
    public string Subtitle => $"{PanelLabel} · {RateLabel} · {MonitorLabel}";
    /// <summary>Panel name, plus whatever tells two widgets of the same type apart.</summary>
    private string PanelLabel => Type == "fps"
        ? $"{Panel.DisplayName} · {_owner.ModelFor(Id).Options.GetValueOrDefault("stream", "displayed")}"
        : Panel.DisplayName;
    public string RateLabel => Panel.EventDriven ? "event-driven" : $"{RateHz:0.#} Hz";
    public string MonitorLabel => Monitor?.Label ?? "Primary monitor";
    public bool IsEventDriven => Panel.EventDriven;
    public bool HasRateSlider => !Panel.EventDriven;
    public bool HasNetworkDataSource => Type == "network";
    public bool HasFpsDataSource => Type == "fps";
    public bool HasNoDataSource => !HasNetworkDataSource && !HasFpsDataSource;
    public bool HasTemperature => Panel.Metrics.Any(metric => metric.Unit == MetricUnit.Celsius);
    public ObservableCollection<MetricRowViewModel> MetricRows { get; } = [];
    public ObservableCollection<OptionEditorViewModel> Options { get; } = [];
    public ObservableCollection<WidgetColorViewModel> AppearanceColors { get; } = [];
    public ObservableCollection<ChoiceItem> MonitorChoices { get; } = [];
    public IReadOnlyList<ChoiceItem> GraphStyles { get; } = [new("line", "Line"), new("filled", "Filled")];
    public IReadOnlyList<ChoiceItem> ZModes { get; } = [new("Desktop", "On desktop"), new("Normal", "Normal window"), new("Topmost", "Always on top")];
    public IReadOnlyList<ChoiceItem> TemperatureUnits { get; } = [new("C", "°C — Celsius"), new("F", "°F — Fahrenheit")];
    public IReadOnlyList<ChoiceItem> TransportChoices { get; } = [new("auto", "Auto — bundled PresentMon service"), new("sdk", "Service + SDK only")];
    public IReadOnlyList<ChoiceItem> TapChoices { get; } = [new("auto", "Auto — live tap when supported"), new("off", "Off — capture transport only")];
    public IReadOnlyList<string> FontFamilies => _owner.FontFamilies;
    public IReadOnlyList<string> NetworkAdapterChoices => _owner.NetworkAdapterChoices;

    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value) && !_applying) Change("enabled", widget => widget.Enabled = value); } }
    public string Title
    {
        get => _title;
        set
        {
            value ??= "";
            if (!Set(ref _title, value) || _applying) return;
            string? saved = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Change("title", widget => widget.Title = saved);
            Raise(nameof(DisplayName));
        }
    }
    public double RateHz
    {
        get => _rateHz;
        set
        {
            value = Math.Round(Math.Clamp(value, .5, RateMaximum) * 2) / 2;
            if (!Set(ref _rateHz, value) || _applying || IsEventDriven) return;
            Change("rateHz", widget => widget.RateHz = value);
            Raise(nameof(RateLabel)); Raise(nameof(Subtitle));
        }
    }
    public double RateMaximum { get => _rateMaximum; private set { if (Set(ref _rateMaximum, value)) Raise(nameof(RateRangeText)); } }
    public string RateRangeText => _hardware.Online ? $"0.5–{RateMaximum:0.#} Hz · live collector limit" : $"0.5–{RateMaximum:0.#} Hz · Defaults — collector offline";
    public string RateHelp { get => _rateHelp; private set => Set(ref _rateHelp, value); }
    public double HistoryS { get => _historyS; set { value = Math.Round(Math.Clamp(value, 10, 600)); if (Set(ref _historyS, value) && !_applying) Change("graph.historyS", widget => widget.Graph.HistoryS = value); } }
    public double GraphHeight { get => _graphHeight; set { value = Math.Round(Math.Clamp(value, 10, 200)); if (Set(ref _graphHeight, value) && !_applying) Change("graph.height", widget => widget.Graph.Height = value); } }
    public ChoiceItem? GraphStyle { get => _graphStyle; set { if (Set(ref _graphStyle, value) && value is not null && !_applying) { string selected = value.Value; Change("graph.style", widget => widget.Graph.Style = selected); } } }
    public ChoiceItem? Monitor { get => _monitor; set { if (Set(ref _monitor, value) && value is not null && !_applying) { string selected = value.Value; Change("monitor", widget => widget.Monitor = selected); Raise(nameof(MonitorLabel)); Raise(nameof(Subtitle)); } } }
    public double X { get => _x; set { int selected = (int)Math.Round(value); if (Set(ref _x, selected) && !_applying) Change("x", widget => widget.X = selected); } }
    public double Y { get => _y; set { int selected = (int)Math.Round(value); if (Set(ref _y, selected) && !_applying) Change("y", widget => widget.Y = selected); } }
    public ChoiceItem? ZMode { get => _zMode; set { if (Set(ref _zMode, value) && value is not null && !_applying && Enum.TryParse(value.Value, out Halo.Shared.Config.ZMode selected)) Change("zMode", widget => widget.ZMode = selected); } }
    public bool ClickThrough { get => _clickThrough; set { if (Set(ref _clickThrough, value) && !_applying) Change("clickThrough", widget => widget.ClickThrough = value); } }
    public bool KeepOnScreen { get => _keepOnScreen; set { if (Set(ref _keepOnScreen, value) && !_applying) Change("keepOnScreen", widget => widget.KeepOnScreen = value); } }
    public bool Locked { get => _locked; set { if (Set(ref _locked, value) && !_applying) Change("locked", widget => widget.Locked = value); } }
    public double Opacity { get => _opacity; set { value = Math.Round(Math.Clamp(value, .1, 1), 2); if (Set(ref _opacity, value) && !_applying) Change("opacity", widget => widget.Opacity = value); } }
    public bool HideOnFullscreen { get => _hideOnFullscreen; set { if (Set(ref _hideOnFullscreen, value) && !_applying) Change("hideOnFullscreen", widget => widget.HideOnFullscreen = value); } }

    public bool UseGlobalFont { get => _useGlobalFont; set { if (Set(ref _useGlobalFont, value)) { Raise(nameof(IsFontOverrideEnabled)); if (!_applying) Change("appearance.fontFamily", widget => widget.Appearance.FontFamily = value ? null : FontFamily); } } }
    public bool IsFontOverrideEnabled => !UseGlobalFont;
    public string FontFamily { get => _fontFamily; set { value = string.IsNullOrWhiteSpace(value) ? "Trebuchet MS" : value.Trim(); if (Set(ref _fontFamily, value) && !_applying && !UseGlobalFont) Change("appearance.fontFamily", widget => widget.Appearance.FontFamily = value); } }
    public bool UseGlobalScale { get => _useGlobalScale; set { if (Set(ref _useGlobalScale, value)) { Raise(nameof(IsScaleOverrideEnabled)); if (!_applying) Change("appearance.scale", widget => widget.Appearance.Scale = value ? null : ScaleAuto ? ScaleValue.Auto : ScaleValue.Fixed(Scale)); } } }
    public bool IsScaleOverrideEnabled => !UseGlobalScale;
    public bool ScaleAuto { get => _scaleAuto; set { if (Set(ref _scaleAuto, value) && !_applying && !UseGlobalScale) { double scale = Scale; Change("appearance.scale", widget => widget.Appearance.Scale = value ? ScaleValue.Auto : ScaleValue.Fixed(scale)); } } }
    public double Scale { get => _scale; set { value = Math.Round(Math.Clamp(value, 1, 3.5), 2); if (Set(ref _scale, value) && !_applying && !UseGlobalScale && !ScaleAuto) Change("appearance.scale", widget => widget.Appearance.Scale = ScaleValue.Fixed(value)); } }
    public bool UseGlobalShowTitle { get => _useGlobalShowTitle; set { if (Set(ref _useGlobalShowTitle, value)) { Raise(nameof(IsShowTitleOverrideEnabled)); if (!_applying) Change("appearance.showTitle", widget => widget.Appearance.ShowTitle = value ? null : ShowTitle); } } }
    public bool IsShowTitleOverrideEnabled => !UseGlobalShowTitle;
    public bool ShowTitle { get => _showTitle; set { if (Set(ref _showTitle, value) && !_applying && !UseGlobalShowTitle) Change("appearance.showTitle", widget => widget.Appearance.ShowTitle = value); } }
    public bool UseGlobalWidth { get => _useGlobalWidth; set { if (Set(ref _useGlobalWidth, value)) { Raise(nameof(IsWidthOverrideEnabled)); if (!_applying) Change("appearance.width", widget => widget.Appearance.Width = value ? null : Width); } } }
    public bool IsWidthOverrideEnabled => !UseGlobalWidth;
    public double Width { get => _width; set { value = Math.Round(Math.Clamp(value, 120, 800)); if (Set(ref _width, value) && !_applying && !UseGlobalWidth) Change("appearance.width", widget => widget.Appearance.Width = value); } }
    public ChoiceItem? TempUnit { get => _tempUnit; set { if (Set(ref _tempUnit, value) && value is not null && !_applying) { string selected = value.Value; Change("appearance.tempUnit", widget => widget.Appearance.TempUnit = selected == "C" ? null : selected); } } }

    public string NetworkInterface { get => _networkInterface; set { value = string.IsNullOrWhiteSpace(value) ? "Best" : value.Trim(); if (Set(ref _networkInterface, value) && !_applying) _owner.ChangeSettings("collector.networkInterface", settings => settings.Collector.NetworkInterface = value); } }
    public bool ExternalIpEnabled { get => _externalIpEnabled; set { if (Set(ref _externalIpEnabled, value) && !_applying) _owner.ChangeSettings("collector.externalIp.enabled", settings => settings.Collector.ExternalIp.Enabled = value); } }
    public string ExternalIpUrl { get => _externalIpUrl; set { value = value?.Trim() ?? ""; if (Set(ref _externalIpUrl, value) && !_applying) _owner.ChangeSettings("collector.externalIp.url", settings => settings.Collector.ExternalIp.Url = value); } }
    public double ExternalIpRefresh { get => _externalIpRefresh; set { value = Math.Round(Math.Clamp(value, 1, 1440), 1); if (Set(ref _externalIpRefresh, value) && !_applying) _owner.ChangeSettings("collector.externalIp.refreshMinutes", settings => settings.Collector.ExternalIp.RefreshMinutes = value); } }
    public ChoiceItem? Transport { get => _transport; set { if (Set(ref _transport, value) && value is not null && !_applying) { string selected = value.Value; _owner.ChangeSettings("collector.presentMonTransport", settings => settings.Collector.PresentMonTransport = selected); } } }
    public ChoiceItem? Tap { get => _tap; set { if (Set(ref _tap, value) && value is not null && !_applying) { string selected = value.Value; _owner.ChangeSettings("collector.presentedTap", settings => settings.Collector.PresentedTap = selected); } } }
    public double FlushMs { get => _flushMs; set { int selected = (int)Math.Round(Math.Clamp(value, 0, 1000)); if (Set(ref _flushMs, selected) && !_applying) _owner.ChangeSettings("collector.presentMonEtwFlushMs", settings => settings.Collector.PresentMonEtwFlushMs = selected); } }
    public double LowsWindow { get => _lowsWindow; set { value = Math.Round(Math.Clamp(value, 10, 600)); if (Set(ref _lowsWindow, value) && !_applying) _owner.ChangeSettings("collector.frameLowsWindowS", settings => settings.Collector.FrameLowsWindowS = value); } }

    public WidgetItemViewModel(WidgetsPageViewModel owner, WidgetInstance model, HardwareSnapshot hardware)
    {
        _owner = owner;
        Id = model.Id;
        Type = model.Type;
        Panel = PanelCatalog.Find(Type) ?? throw new InvalidOperationException($"Unknown widget type {Type}");
        _hardware = hardware;
        ApplyFrom(model, new HashSet<string>(StringComparer.Ordinal));
    }

    public void ApplyFrom(WidgetInstance model, IReadOnlySet<string> dirty)
    {
        _applying = true;
        try
        {
            Apply("enabled", dirty, () => Enabled = model.Enabled);
            Apply("title", dirty, () => Title = model.Title ?? "");
            Apply("rateHz", dirty, () => { _rateHz = model.RateHz; Raise(nameof(RateHz)); });
            Apply("graph.historyS", dirty, () => HistoryS = model.Graph.HistoryS);
            Apply("graph.height", dirty, () => GraphHeight = model.Graph.Height);
            Apply("graph.style", dirty, () => GraphStyle = GraphStyles.FirstOrDefault(item => item.Value == model.Graph.Style) ?? GraphStyles[0]);
            BuildMonitorChoices(model.Monitor);
            Apply("monitor", dirty, () => Monitor = MonitorChoices.FirstOrDefault(item => item.Value.Equals(model.Monitor, StringComparison.OrdinalIgnoreCase)) ?? MonitorChoices[0]);
            Apply("x", dirty, () => X = model.X); Apply("y", dirty, () => Y = model.Y);
            Apply("zMode", dirty, () => ZMode = ZModes.First(item => item.Value == model.ZMode.ToString()));
            Apply("clickThrough", dirty, () => ClickThrough = model.ClickThrough);
            Apply("keepOnScreen", dirty, () => KeepOnScreen = model.KeepOnScreen);
            Apply("locked", dirty, () => Locked = model.Locked);
            Apply("opacity", dirty, () => Opacity = model.Opacity);
            Apply("hideOnFullscreen", dirty, () => HideOnFullscreen = model.HideOnFullscreen);

            if (!HasDirtySection("appearance", dirty)) ApplyAppearance(model);
            ApplyDataSource(_owner.Config.Settings, dirty);
            if (!HasDirtySection("options", dirty) && !HasDirtySection("metrics", dirty)) BuildDynamicEditors(model);
            RefreshRate(model);
            Raise(nameof(DisplayName)); Raise(nameof(Subtitle)); Raise(nameof(RateLabel));
        }
        finally { _applying = false; }
    }

    public void RefreshHardware(HardwareSnapshot hardware, WidgetInstance model)
    {
        _hardware = hardware;
        _applying = true;
        try { BuildDynamicEditors(model); RefreshRate(model); }
        finally { _applying = false; }
    }

    public void RefreshSettings(AppSettings settings, IReadOnlySet<string> dirty)
    {
        _applying = true;
        try
        {
            ApplyDataSource(settings, dirty);
            if (!dirty.Any(path => path.StartsWith("appearance.", StringComparison.Ordinal)))
                BuildAppearanceColors(_owner.ModelFor(Id));
        }
        finally { _applying = false; }
    }

    public void ChangeMetric(string key, string field, Action<MetricSetting> update)
        => _owner.ChangeWidget(Id, $"metrics.{key}.{field}", widget =>
        {
            if (!widget.Metrics.TryGetValue(key, out MetricSetting? metric)) widget.Metrics[key] = metric = new MetricSetting();
            update(metric);
            if (IsEmpty(metric)) widget.Metrics.Remove(key);
        });

    public void ChangeOption(string key, string? value, bool structural)
    {
        _owner.ChangeWidget(Id, $"options.{key}", widget =>
        {
            if (value is null) widget.Options.Remove(key); else widget.Options[key] = value;
        });
        if (structural)
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _applying = true;
                try { BuildDynamicEditors(_owner.ModelFor(Id)); RefreshRate(_owner.ModelFor(Id)); }
                finally { _applying = false; }
            });
        Raise(nameof(DisplayName)); Raise(nameof(Subtitle));
    }

    public void ChangeAppearanceColor(string token, string? value)
        => _owner.ChangeWidget(Id, $"appearance.colors.{token}", widget =>
        {
            if (value is null)
            {
                widget.Appearance.Colors?.Remove(token);
                if (widget.Appearance.Colors?.Count == 0) widget.Appearance.Colors = null;
            }
            else (widget.Appearance.Colors ??= new Dictionary<string, string>())[token] = value;
        });

    public void ResetRefreshAndGraphs()
    {
        double rate = Panel.DefaultRateHz;
        _applying = true;
        try { RateHz = rate; HistoryS = 40; GraphHeight = 25; GraphStyle = GraphStyles[0]; }
        finally { _applying = false; }
        Change("refreshAndGraphs", widget =>
        {
            widget.RateHz = rate;
            widget.Graph = new GraphSettings();
        });
    }

    public void ResetMetrics()
    {
        MetricRows.Clear();
        Options.Clear();
        Change("metrics", widget => widget.Metrics.Clear());
        Change("options", widget => widget.Options.Clear());
        BuildDynamicEditors(_owner.ModelFor(Id));
    }

    public void UseGlobalForAll()
    {
        Change("appearance", widget => widget.Appearance = new WidgetAppearance());
        WidgetInstance model = _owner.ModelFor(Id);
        model.Appearance = new WidgetAppearance();
        _applying = true;
        try { ApplyAppearance(model); }
        finally { _applying = false; }
    }

    public void ResetPlacement()
    {
        _applying = true;
        try
        {
            Monitor = MonitorChoices[0]; X = 0; Y = 0; ZMode = ZModes[0]; ClickThrough = false;
            KeepOnScreen = true; Locked = false; Opacity = 1; HideOnFullscreen = false;
        }
        finally { _applying = false; }
        Change("placement", widget =>
        {
            widget.Monitor = ""; widget.X = 0; widget.Y = 0; widget.ZMode = Halo.Shared.Config.ZMode.Desktop;
            widget.ClickThrough = false; widget.KeepOnScreen = true; widget.Locked = false;
            widget.Opacity = 1; widget.HideOnFullscreen = false;
        });
    }

    private void Change(string field, Action<WidgetInstance> apply) => _owner.ChangeWidget(Id, field, apply);

    private void Apply(string field, IReadOnlySet<string> dirty, Action apply)
    {
        string path = $"widgets.{Id}.{field}";
        if (!dirty.Any(item => item == "$" || item == path || item.StartsWith(path + ".", StringComparison.Ordinal) || path.StartsWith(item + ".", StringComparison.Ordinal)))
            apply();
    }

    private bool HasDirtySection(string section, IReadOnlySet<string> dirty)
    {
        string path = $"widgets.{Id}.{section}";
        return dirty.Any(item => item == "$" || item == path || item.StartsWith(path + ".", StringComparison.Ordinal) || path.StartsWith(item + ".", StringComparison.Ordinal));
    }

    private void ApplyAppearance(WidgetInstance model)
    {
        AppearanceSettings global = _owner.Config.Settings.Appearance;
        UseGlobalFont = model.Appearance.FontFamily is null;
        FontFamily = model.Appearance.FontFamily ?? global.FontFamily;
        UseGlobalScale = model.Appearance.Scale is null;
        ScaleAuto = (model.Appearance.Scale ?? global.Scale).IsAuto;
        Scale = (model.Appearance.Scale ?? global.Scale).Or(1.7);
        UseGlobalShowTitle = model.Appearance.ShowTitle is null;
        ShowTitle = model.Appearance.ShowTitle ?? true;
        UseGlobalWidth = model.Appearance.Width is null;
        Width = model.Appearance.Width ?? 206;
        string temp = model.Appearance.TempUnit ?? "C";
        TempUnit = TemperatureUnits.First(item => item.Value == temp);
        BuildAppearanceColors(model);
    }

    private void BuildAppearanceColors(WidgetInstance model)
    {
        AppearanceColors.Clear();
        foreach (string token in Panel.Tokens.Distinct(StringComparer.Ordinal))
        {
            bool useGlobal = model.Appearance.Colors?.ContainsKey(token) != true;
            string value = model.Appearance.Colors?.GetValueOrDefault(token)
                ?? _owner.Config.Settings.Appearance.Colors.GetValueOrDefault(token)
                ?? ThemeTokens.Default(token) ?? "#000000FF";
            AppearanceColors.Add(new WidgetColorViewModel(this, token, value, useGlobal));
        }
    }

    private void ApplyDataSource(AppSettings settings, IReadOnlySet<string>? dirty = null)
    {
        dirty ??= new HashSet<string>(StringComparer.Ordinal);
        if (!dirty.Contains("collector.networkInterface")) _networkInterface = settings.Collector.NetworkInterface;
        if (!dirty.Contains("collector.externalIp.enabled")) _externalIpEnabled = settings.Collector.ExternalIp.Enabled;
        if (!dirty.Contains("collector.externalIp.url")) _externalIpUrl = settings.Collector.ExternalIp.Url;
        if (!dirty.Contains("collector.externalIp.refreshMinutes")) _externalIpRefresh = settings.Collector.ExternalIp.RefreshMinutes;
        if (!dirty.Contains("collector.presentMonTransport")) _transport = TransportChoices.FirstOrDefault(item => item.Value == settings.Collector.PresentMonTransport) ?? TransportChoices[0];
        if (!dirty.Contains("collector.presentedTap")) _tap = TapChoices.FirstOrDefault(item => item.Value == settings.Collector.PresentedTap) ?? TapChoices[0];
        if (!dirty.Contains("collector.presentMonEtwFlushMs")) _flushMs = settings.Collector.PresentMonEtwFlushMs;
        if (!dirty.Contains("collector.frameLowsWindowS")) _lowsWindow = settings.Collector.FrameLowsWindowS;
        Raise(nameof(NetworkInterface)); Raise(nameof(ExternalIpEnabled)); Raise(nameof(ExternalIpUrl)); Raise(nameof(ExternalIpRefresh));
        Raise(nameof(Transport)); Raise(nameof(Tap)); Raise(nameof(FlushMs)); Raise(nameof(LowsWindow));
    }

    private void BuildDynamicEditors(WidgetInstance model)
    {
        Options.Clear();
        foreach (OptionSpec spec in Panel.Options)
            Options.Add(new OptionEditorViewModel(this, spec, Panel.OptionValue(model.Options, spec.Key), _hardware));

        MetricRows.Clear();
        foreach (MetricSpec spec in Panel.Metrics)
        {
            foreach ((string suffix, string displaySuffix, string repeat, string volume, bool placeholder) in RepeatValues(spec, model))
            {
                string key = suffix.Length == 0 ? spec.Key : $"{spec.Key}.{suffix}";
                string defaultLabel = spec.DefaultLabel.Replace("{n}", displaySuffix).Replace("{x}", displaySuffix);
                string resolvedRepeat = repeat;
                if (resolvedRepeat.Length == 0 && spec.MetricName.Contains("{n}", StringComparison.Ordinal))
                {
                    resolvedRepeat = Panel.OptionValue(model.Options, "cpuFanChannel");
                    if (resolvedRepeat.Length == 0)
                        resolvedRepeat = _hardware.Fans.FirstOrDefault(choice => choice.Label.Contains("CPU", StringComparison.OrdinalIgnoreCase))?.Value
                            ?? _hardware.Fans.FirstOrDefault()?.Value ?? "0";
                }
                string metricName = PanelCatalog.ResolveMetricName(spec.MetricName,
                    Panel.OptionValue(model.Options, "gpuIndex"), resolvedRepeat, volume,
                    Panel.OptionValue(model.Options, "stream"),
                    Panel.OptionValue(model.Options, "aggregate").Equals("true", StringComparison.OrdinalIgnoreCase));
                string name = suffix.Length == 0 ? spec.DefaultLabel.TrimEnd(':') : $"{spec.DefaultLabel.Replace("{n}", displaySuffix).Replace("{x}", displaySuffix).TrimEnd(':')}";
                if (placeholder) name = "Not currently detected";
                MetricRows.Add(new MetricRowViewModel(this, spec, key, metricName, name, defaultLabel, model.Metrics.GetValueOrDefault(key)));
            }
        }
    }

    private IEnumerable<(string Suffix, string Display, string Repeat, string Volume, bool Placeholder)> RepeatValues(MetricSpec spec, WidgetInstance model)
    {
        if (spec.Repeat == Repeat.None) return [("", "", "", "", false)];
        if (spec.Repeat == Repeat.PerCore)
        {
            int count = _hardware.LogicalCpuCount;
            string[] saved = SavedSuffixes(model, spec.Key).ToArray();
            bool placeholder = count == 0 && saved.Length == 0;
            if (count == 0) count = Math.Max(1, saved.Select(ParseNonNegative).DefaultIfEmpty(0).Max() + 1);
            return Enumerable.Range(0, Math.Min(count, 256)).Select(index => (index.ToString(), (index + 1).ToString(), index.ToString(), "", placeholder));
        }
        if (spec.Repeat == Repeat.PerRank)
        {
            // The bounds are the collector's: ProcessProvider publishes exactly 10 ranks per ranking.
            int count = int.TryParse(Panel.OptionValue(model.Options, "topN"), out int parsed)
                ? Math.Clamp(parsed, PanelCatalog.MinTopRows, PanelCatalog.MaxTopRows) : 5;
            return Enumerable.Range(0, count).Select(index => (index.ToString(), (index + 1).ToString(), index.ToString(), "", false));
        }
        if (spec.Repeat == Repeat.PerVolume)
        {
            (IReadOnlyList<string> values, bool placeholder) = SelectedValues(model, "volumes", _hardware.Volumes.Select(choice => choice.Value), spec.Key, "C");
            return values.Select(value => (value, value, "", value.ToLowerInvariant(), placeholder));
        }
        (IReadOnlyList<string> fans, bool fanPlaceholder) = SelectedValues(model, "channels", _hardware.Fans.Select(choice => choice.Value), spec.Key, "0");
        return fans.Select(value => (value, value, value, "", fanPlaceholder));
    }

    private static (IReadOnlyList<string> Values, bool Placeholder) SelectedValues(WidgetInstance model, string optionKey, IEnumerable<string> discovered, string metricKey, string fallback)
    {
        string option = model.Options.GetValueOrDefault(optionKey, "");
        var values = option.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        string[] found = discovered.ToArray();
        string[] saved = SavedSuffixes(model, metricKey).ToArray();
        if (values.Count == 0) values.AddRange(found);
        if (values.Count == 0) values.AddRange(saved);
        bool placeholder = values.Count == 0;
        if (values.Count == 0) values.Add(fallback);
        return (values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), placeholder);
    }

    private static IEnumerable<string> SavedSuffixes(WidgetInstance model, string key)
        => model.Metrics.Keys.Where(candidate => candidate.StartsWith(key + ".", StringComparison.Ordinal)).Select(candidate => candidate[(key.Length + 1)..]);
    private static int ParseNonNegative(string value) => int.TryParse(value, out int parsed) && parsed >= 0 ? parsed : 0;

    private void RefreshRate(WidgetInstance model)
    {
        if (Panel.EventDriven)
        {
            RateMaximum = PanelRates.EventDrivenFallbackHz;
            RateHelp = "Frame metrics repaint when new frames arrive, up to the monitor refresh rate. A 5 Hz tick remains as fallback.";
            _rateHz = 5;
            Raise(nameof(RateHz)); Raise(nameof(RateLabel)); Raise(nameof(Subtitle));
            return;
        }

        var rates = new List<(MetricRowViewModel Row, double Rate, bool Registered)>();
        foreach (MetricRowViewModel row in MetricRows.GroupBy(item => item.Name).Select(group => group.First()))
        {
            if (_hardware.Metrics.TryGetValue(row.MetricName, out MetricInfo info))
            {
                if (info.Semantics == MetricSemantics.Static) continue;
                rates.Add((row, info.NominalRateHz, true));
            }
            else rates.Add((row, Panel.DefaultRateHz, false));
        }
        RateMaximum = PanelRates.MaxHz(Panel, MetricRows.Select(row => row.MetricName), name =>
            _hardware.Metrics.TryGetValue(name, out MetricInfo info) && info.Semantics != MetricSemantics.Static
                ? info.NominalRateHz
                : 0);
        var help = rates.Select(item => item.Registered
            ? $"{item.Row.Name}: {item.Rate:0.###} Hz{(item.Rate < RateMaximum ? " — raising the widget higher redraws the same value" : "")}"
            : $"{item.Row.Name}: {item.Rate:0.###} Hz default (not registered now)").ToList();
        RateHelp = string.Join(Environment.NewLine, help);
        if (_rateHz > RateMaximum)
        {
            _rateHz = RateMaximum;
            Raise(nameof(RateHz));
        }
        Raise(nameof(RateRangeText)); Raise(nameof(RateLabel)); Raise(nameof(Subtitle));
    }

    private void BuildMonitorChoices(string saved)
    {
        MonitorChoices.Clear();
        MonitorChoices.Add(new ChoiceItem("", "Primary monitor"));
        foreach (MonitorList.Entry monitor in MonitorList.Get()) MonitorChoices.Add(new ChoiceItem(monitor.Device, monitor.Label));
        if (saved.Length > 0 && !MonitorChoices.Any(choice => choice.Value.Equals(saved, StringComparison.OrdinalIgnoreCase)))
            MonitorChoices.Add(new ChoiceItem(saved, saved + " · unavailable"));
    }

    private string FriendlyPanelName()
    {
        if (Type == "gpu")
        {
            string index = _owner.ModelFor(Id).Options.GetValueOrDefault("gpuIndex", "0");
            string label = _hardware.Gpus.FirstOrDefault(choice => choice.Value == index)?.Label ?? $"GPU {index}";
            return label;
        }
        if (Type == "fps")
        {
            string stream = _owner.ModelFor(Id).Options.GetValueOrDefault("stream", "displayed");
            return $"{Panel.DisplayName} — {stream}";
        }
        return Panel.DisplayName;
    }

    private static bool IsEmpty(MetricSetting metric)
        => metric.Show is null && metric.Label is null && metric.Graph is null && metric.Color is null &&
           metric.Warn is null && metric.Max is null;
}

public sealed class WidgetsPageViewModel : ObservableObject, IDisposable
{
    private readonly LiveConfigService _config;
    private readonly CollectorSession _collector = new();
    private readonly Dictionary<string, WidgetInstance> _models = new(StringComparer.Ordinal);
    private HardwareSnapshot _hardware = HardwareSnapshot.Offline;
    private WidgetItemViewModel? _selectedWidget;
    private string _collectorHint = "Collector not running — hardware choices use saved values and manifest defaults.";

    public LiveConfigService Config => _config;
    public ObservableCollection<WidgetItemViewModel> Widgets { get; } = [];
    public IReadOnlyList<string> FontFamilies { get; } = Fonts.SystemFontFamilies.Select(font => font.Source).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();

    /// <summary>Adapter names for the Data source picker: "Best" first, then every non-loopback
    /// adapter with the connected ones first. Enumerated once — the Data source tab is not a live
    /// view of the NIC list.
    /// <para>MERGE NOTE: ticket 01 landed this same enumeration as
    /// <c>Halo.Settings.Services.NetworkAdapters.List()</c>, but that file is on their branch and
    /// does not exist in this worktree, so calling it would not compile here. Delete this and point
    /// the property at theirs once the branches meet — nothing else has to change.</para></summary>
    public IReadOnlyList<string> NetworkAdapterChoices { get; } = BuildNetworkAdapterChoices();

    private static IReadOnlyList<string> BuildNetworkAdapterChoices()
    {
        var names = new List<string> { "Best" };
        try
        {
            // Offer exactly what the collector can honour. NetworkProvider.PickNic only ever accepts
            // an interface that is Up and neither Loopback nor Tunnel, matched on Name — so listing
            // anything else would let the user pick a name that silently matches nothing and leaves
            // the widget with no network metrics at all.
            var eligible = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .Where(nic => nic.NetworkInterfaceType
                    is not (System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                         or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel))
                .ToList();

            // .NET 10 also returns one interface per NDIS lightweight filter bound to an adapter,
            // named "<adapter>-<filter>-0000" — 35 entries on this machine against 3 real NICs.
            // They pass the collector's test, so they are not wrong, just unusable as a menu. Drop
            // any candidate whose name is another candidate's name plus a suffix; that identifies
            // the filter instances by their own naming rule rather than by a vendor blocklist.
            names.AddRange(eligible
                .Where(nic => !eligible.Any(parent => !ReferenceEquals(parent, nic)
                    && nic.Name.StartsWith(parent.Name + "-", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(GatewayCount)
                .ThenBy(nic => nic.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(nic => nic.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            // Enumeration is best-effort: the box stays editable and "Best" alone is a valid list.
        }
        return names;
    }

    /// <summary>The collector prefers the interface that has a default gateway, so surface those first.</summary>
    private static int GatewayCount(System.Net.NetworkInformation.NetworkInterface nic)
    {
        try { return nic.GetIPProperties().GatewayAddresses.Count; }
        catch { return 0; }
    }
    public WidgetItemViewModel? SelectedWidget { get => _selectedWidget; set => Set(ref _selectedWidget, value); }
    public bool CollectorOffline => !_hardware.Online;
    public string CollectorHint { get => _collectorHint; private set => Set(ref _collectorHint, value); }

    public WidgetsPageViewModel(LiveConfigService config)
    {
        _config = config;
        _config.ExternalChanged += Config_ExternalChanged;
        Reconcile(new HashSet<string>(StringComparer.Ordinal));
        PollCollector();
    }

    public void PollCollector()
    {
        HardwareSnapshot next;
        try { next = HardwareSnapshot.Capture(_collector); }
        catch { next = HardwareSnapshot.Offline; }
        if (next.Signature == _hardware.Signature) return;
        _hardware = next;
        CollectorHint = next.Online
            ? "Collector connected — hardware choices and Hz limits are live."
            : "Collector not running — hardware choices use saved values and manifest defaults.";
        Raise(nameof(CollectorOffline));
        foreach (WidgetItemViewModel row in Widgets) row.RefreshHardware(next, ModelFor(row.Id));
    }

    public void AddWidget(PanelType panel)
    {
        string id = UniqueId(panel.Id);
        var widget = new WidgetInstance
        {
            Id = id,
            Type = panel.Id,
            Enabled = true,
            RateHz = panel.EventDriven ? 5 : panel.DefaultRateHz,
        };
        foreach (OptionSpec option in panel.Options)
            if (option.Default.Length > 0) widget.Options[option.Key] = option.Default;
        if (panel.Id == "gpu" && _hardware.Gpus.Count > 0)
        {
            string unused = _hardware.Gpus.Select(item => item.Value)
                .FirstOrDefault(value => !Widgets.Any(row => row.Type == "gpu" && ModelFor(row.Id).Options.GetValueOrDefault("gpuIndex", "0") == value))
                ?? _hardware.Gpus[0].Value;
            widget.Options["gpuIndex"] = unused;
        }
        _models[id] = Clone(widget);
        Widgets.Add(new WidgetItemViewModel(this, widget, _hardware));
        SelectedWidget = Widgets[^1];
        _config.QueueWidgets($"widgets.{id}", config =>
        {
            if (config.Widgets.All(item => item.Id != id)) config.Widgets.Add(Clone(widget));
        }, flushImmediately: true);
    }

    public void Duplicate(WidgetItemViewModel source)
    {
        WidgetInstance clone = Clone(ModelFor(source.Id));
        clone.Id = UniqueId(source.Type);
        clone.Title = string.IsNullOrWhiteSpace(clone.Title) ? null : clone.Title + " copy";
        int index = Widgets.IndexOf(source) + 1;
        var row = new WidgetItemViewModel(this, clone, _hardware);
        _models[clone.Id] = Clone(clone);
        Widgets.Insert(index, row);
        SelectedWidget = row;
        _config.QueueWidgets($"widgets.{clone.Id}", config =>
        {
            int diskIndex = config.Widgets.FindIndex(item => item.Id == source.Id);
            config.Widgets.Insert(diskIndex < 0 ? config.Widgets.Count : diskIndex + 1, Clone(clone));
        }, flushImmediately: true);
    }

    public void Remove(WidgetItemViewModel row)
    {
        int index = Widgets.IndexOf(row);
        Widgets.Remove(row);
        _models.Remove(row.Id);
        SelectedWidget = Widgets.Count == 0 ? null : Widgets[Math.Clamp(index, 0, Widgets.Count - 1)];
        _config.QueueWidgets($"widgets.{row.Id}", config => config.Widgets.RemoveAll(item => item.Id == row.Id), flushImmediately: true);
    }

    public void Move(WidgetItemViewModel row, int targetIndex)
    {
        int from = Widgets.IndexOf(row);
        if (from < 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, Widgets.Count - 1);
        if (from == targetIndex) return;
        Widgets.Move(from, targetIndex);
        QueueOrder();
    }

    public void MoveBy(WidgetItemViewModel row, int delta) => Move(row, Widgets.IndexOf(row) + delta);

    internal WidgetInstance ModelFor(string id)
    {
        if (_models.TryGetValue(id, out WidgetInstance? model)) return Clone(model);
        throw new InvalidOperationException($"Widget {id} no longer exists.");
    }

    internal void ChangeWidget(string id, string field, Action<WidgetInstance> apply)
    {
        if (_models.TryGetValue(id, out WidgetInstance? local)) apply(local);
        _config.QueueWidgets($"widgets.{id}.{field}", config =>
        {
            WidgetInstance? widget = config.Widgets.FirstOrDefault(item => item.Id == id);
            if (widget is not null) apply(widget);
        });
    }

    internal void ChangeSettings(string path, Action<AppSettings> apply)
    {
        apply(_config.Settings);
        _config.QueueSettings(path, apply);
        var noDirtyPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (WidgetItemViewModel row in Widgets) row.RefreshSettings(_config.Settings, noDirtyPaths);
    }

    private void QueueOrder()
    {
        string[] ids = Widgets.Select(item => item.Id).ToArray();
        _config.QueueWidgets("widgets.order", config =>
        {
            var byId = config.Widgets.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var ordered = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            ordered.AddRange(config.Widgets.Where(item => !ids.Contains(item.Id, StringComparer.Ordinal)));
            config.Widgets = ordered;
        });
    }

    private void Config_ExternalChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (e.File == ConfigFileKind.Settings)
        {
            foreach (WidgetItemViewModel row in Widgets) row.RefreshSettings(_config.Settings, e.DirtyPaths);
        }
        else Reconcile(e.DirtyPaths);
    }

    private void Reconcile(IReadOnlySet<string> dirty)
    {
        string? selected = SelectedWidget?.Id;
        IReadOnlyList<WidgetInstance> disk = _config.Widgets.Widgets;
        for (int index = 0; index < disk.Count; index++)
        {
            WidgetInstance model = disk[index];
            WidgetItemViewModel? existing = Widgets.FirstOrDefault(item => item.Id == model.Id);
            if (existing is null)
            {
                _models[model.Id] = Clone(model);
                existing = new WidgetItemViewModel(this, _models[model.Id], _hardware);
                Widgets.Insert(Math.Min(index, Widgets.Count), existing);
            }
            else
            {
                WidgetInstance merged = MergeExternal(_models[model.Id], model, dirty);
                _models[model.Id] = merged;
                existing.ApplyFrom(merged, dirty);
                int current = Widgets.IndexOf(existing);
                if (!dirty.Contains("widgets.order") && current != index) Widgets.Move(current, index);
            }
        }
        foreach (WidgetItemViewModel row in Widgets.Where(row => disk.All(item => item.Id != row.Id)).ToArray())
        {
            if (!dirty.Any(path => path.StartsWith($"widgets.{row.Id}", StringComparison.Ordinal)))
            {
                Widgets.Remove(row);
                _models.Remove(row.Id);
            }
        }
        SelectedWidget = selected is null ? Widgets.FirstOrDefault() : Widgets.FirstOrDefault(item => item.Id == selected) ?? Widgets.FirstOrDefault();
    }

    private string UniqueId(string type)
    {
        string stem = type.Replace("-", "", StringComparison.Ordinal);
        if (Widgets.All(item => item.Id != stem)) return stem;
        for (int index = 2; ; index++)
        {
            string candidate = $"{stem}-{index}";
            if (Widgets.All(item => item.Id != candidate)) return candidate;
        }
    }

    private static WidgetInstance MergeExternal(WidgetInstance local, WidgetInstance disk, IReadOnlySet<string> dirty)
    {
        string prefix = $"widgets.{disk.Id}.";
        if (dirty.Contains("$") || dirty.Contains(prefix.TrimEnd('.'))) return Clone(local);
        WidgetInstance merged = Clone(disk);
        foreach (string path in dirty.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)))
            CopyDirtyPath(local, merged, path[prefix.Length..]);
        return merged;
    }

    private static void CopyDirtyPath(WidgetInstance source, WidgetInstance target, string field)
    {
        switch (field)
        {
            case "enabled": target.Enabled = source.Enabled; return;
            case "title": target.Title = source.Title; return;
            case "rateHz": target.RateHz = source.RateHz; return;
            case "monitor": target.Monitor = source.Monitor; return;
            case "x": target.X = source.X; return;
            case "y": target.Y = source.Y; return;
            case "zMode": target.ZMode = source.ZMode; return;
            case "clickThrough": target.ClickThrough = source.ClickThrough; return;
            case "keepOnScreen": target.KeepOnScreen = source.KeepOnScreen; return;
            case "locked": target.Locked = source.Locked; return;
            case "opacity": target.Opacity = source.Opacity; return;
            case "hideOnFullscreen": target.HideOnFullscreen = source.HideOnFullscreen; return;
            case "refreshAndGraphs": target.RateHz = source.RateHz; target.Graph = Clone(source).Graph; return;
            case "placement":
                target.Monitor = source.Monitor; target.X = source.X; target.Y = source.Y; target.ZMode = source.ZMode;
                target.ClickThrough = source.ClickThrough; target.KeepOnScreen = source.KeepOnScreen;
                target.Locked = source.Locked; target.Opacity = source.Opacity; target.HideOnFullscreen = source.HideOnFullscreen; return;
            case "metrics": target.Metrics = Clone(source).Metrics; return;
            case "appearance": target.Appearance = Clone(source).Appearance; return;
        }
        if (field.StartsWith("graph.", StringComparison.Ordinal))
        {
            if (field == "graph.historyS") target.Graph.HistoryS = source.Graph.HistoryS;
            else if (field == "graph.height") target.Graph.Height = source.Graph.Height;
            else if (field == "graph.style") target.Graph.Style = source.Graph.Style;
            return;
        }
        if (field.StartsWith("options.", StringComparison.Ordinal))
        {
            string key = field[8..];
            if (source.Options.TryGetValue(key, out string? value)) target.Options[key] = value; else target.Options.Remove(key);
            return;
        }
        if (field.StartsWith("metrics.", StringComparison.Ordinal))
        {
            string rest = field[8..];
            int split = rest.LastIndexOf('.');
            string key = split < 0 ? rest : rest[..split];
            if (source.Metrics.TryGetValue(key, out MetricSetting? metric)) target.Metrics[key] = CloneMetric(metric); else target.Metrics.Remove(key);
            return;
        }
        if (field.StartsWith("appearance.", StringComparison.Ordinal)) target.Appearance = Clone(source).Appearance;
    }

    private static MetricSetting CloneMetric(MetricSetting metric) => new()
    {
        Show = metric.Show, Label = metric.Label, Graph = metric.Graph,
        Color = metric.Color, Warn = metric.Warn?.ToArray(), Max = metric.Max,
    };

    private static WidgetInstance Clone(WidgetInstance source) => new()
    {
        Id = source.Id, Type = source.Type, Enabled = source.Enabled, Title = source.Title,
        Monitor = source.Monitor, X = source.X, Y = source.Y, ZMode = source.ZMode,
        ClickThrough = source.ClickThrough, KeepOnScreen = source.KeepOnScreen, Locked = source.Locked,
        Opacity = source.Opacity, HideOnFullscreen = source.HideOnFullscreen, RateHz = source.RateHz,
        Graph = new GraphSettings { HistoryS = source.Graph.HistoryS, Height = source.Graph.Height, Style = source.Graph.Style },
        Options = new Dictionary<string, string>(source.Options, StringComparer.Ordinal),
        Metrics = source.Metrics.ToDictionary(pair => pair.Key, pair => new MetricSetting
        {
            Show = pair.Value.Show, Label = pair.Value.Label, Graph = pair.Value.Graph,
            Color = pair.Value.Color, Warn = pair.Value.Warn?.ToArray(), Max = pair.Value.Max,
        }, StringComparer.Ordinal),
        Appearance = new WidgetAppearance
        {
            Colors = source.Appearance.Colors is null ? null : new Dictionary<string, string>(source.Appearance.Colors, StringComparer.Ordinal),
            FontFamily = source.Appearance.FontFamily, Scale = source.Appearance.Scale,
            ShowTitle = source.Appearance.ShowTitle, Width = source.Appearance.Width, TempUnit = source.Appearance.TempUnit,
        },
    };

    public void Dispose()
    {
        _config.ExternalChanged -= Config_ExternalChanged;
        _collector.Dispose();
    }
}
