using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Halo.Shared.Config;
using Halo.Shared.Panels;

namespace Halo.Settings.Pages;

public partial class WidgetsPage : UserControl, ISettingsPage
{
    private static readonly (ZMode Mode, string Label)[] ZModes =
    {
        (ZMode.Desktop, "On desktop — under all windows, survives Win+D"),
        (ZMode.Normal, "Normal window"),
        (ZMode.Topmost, "Always on top"),
    };

    /// <summary>List row: friendly name + enable checkbox writing straight to the instance.</summary>
    public sealed class WidgetRow(WidgetInstance w)
    {
        public WidgetInstance W { get; } = w;
        public string Name => FriendlyName(W);
        public string Sub => $"{W.Type} · {W.Id}";
        public bool Enabled { get => W.Enabled; set => W.Enabled = value; }
    }

    private readonly ConfigStore _store;
    private readonly ObservableCollection<WidgetRow> _rows = new();
    // Disk state as of page load, per widget id. Save applies only fields that differ from
    // this, so it can't undo drag positions / context-menu toggles the widgets process
    // wrote to disk while the page was open.
    private readonly Dictionary<string, WidgetInstance> _pristine = new();
    private List<MonitorList.Entry> _monitors = new();
    private readonly List<string> _monitorDevices = new();
    private WidgetInstance? _current;
    private bool _loading;
    // Per-type graph toggles built for the selected widget (metric key + control).
    private readonly List<(string Key, CheckBox Box)> _graphChecks = new();

    /// <summary>Graph lines this panel type offers, straight from the catalog — no per-type
    /// switch to keep in sync any more (settings plan S1).</summary>
    private static (string Key, string Label)[] GraphLines(string type)
        => PanelCatalog.Find(type)?.Metrics
            .Where(m => m.Graphable)
            .Select(m => (m.Key, m.DefaultLabel.TrimEnd(':')))
            .ToArray() ?? [];

    public WidgetsPage(ConfigStore store)
    {
        _store = store;
        InitializeComponent();
        WidgetList.ItemsSource = _rows;
        foreach (var z in ZModes) ZModeCombo.Items.Add(z.Label);
    }

    public void OnEnter()
    {
        _monitors = MonitorList.Get();
        LoadFromStore(0);
        Status.Text = "";
    }

    public void OnLeave() { }

    private void LoadFromStore(int selectIndex)
    {
        _current = null;
        _store.Reload();
        _rows.Clear();
        _pristine.Clear();
        foreach (var w in _store.Widgets.Widgets)
        {
            _rows.Add(new WidgetRow(w));
            _pristine[w.Id] = Clone(w);
        }
        if (_rows.Count > 0) WidgetList.SelectedIndex = Math.Clamp(selectIndex, 0, _rows.Count - 1);
        else ClearDetail();
    }

    private static WidgetInstance Clone(WidgetInstance w) => new()
    {
        Id = w.Id, Type = w.Type, Enabled = w.Enabled, Title = w.Title, Monitor = w.Monitor,
        X = w.X, Y = w.Y, ZMode = w.ZMode, ClickThrough = w.ClickThrough,
        KeepOnScreen = w.KeepOnScreen, Locked = w.Locked, Opacity = w.Opacity,
        HideOnFullscreen = w.HideOnFullscreen, RateHz = w.RateHz,
        Options = new Dictionary<string, string>(w.Options),
        // deep copy: a shared MetricSetting instance would make the "what changed" diff blind
        Metrics = w.Metrics.ToDictionary(kv => kv.Key, kv => new MetricSetting
        {
            Show = kv.Value.Show,
            Label = kv.Value.Label,
            Graph = kv.Value.Graph,
            Color = kv.Value.Color,
            Warn = kv.Value.Warn?.ToArray(),
            Max = kv.Value.Max,
        }),
    };

    private static string FriendlyName(WidgetInstance w)
    {
        string baseName = PanelCatalog.Find(w.Type)?.DisplayName ?? w.Type;
        if (w.Type != "fps") return baseName;
        return w.Options.GetValueOrDefault("stream", "").ToLowerInvariant() switch
        {
            "displayed" => baseName + " — displayed",
            "presented" => baseName + " — presented",
            _ => baseName,
        };
    }

    /// <summary>Help text for the raw options box: generated from the catalog's OptionSpecs.</summary>
    private static string OptionsHelp(string type)
    {
        var panel = PanelCatalog.Find(type);
        if (panel == null || panel.Options.Count == 0)
            return "This panel has no options. (Format: key=value, one per line.)";
        var sb = new StringBuilder();
        foreach (var o in panel.Options)
        {
            string range = o.Choices is { Length: > 0 } ? string.Join(" | ", o.Choices) : o.Range ?? o.Kind.ToString().ToLowerInvariant();
            sb.AppendLine($"{o.Key} ({range}, default \"{o.Default}\") — {o.Help.Replace("\n", " ")}");
        }
        return sb.ToString().TrimEnd();
    }

    private void WidgetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FlushCurrent();
        _current = (WidgetList.SelectedItem as WidgetRow)?.W;
        LoadDetail(_current);
    }

    private void LoadDetail(WidgetInstance? w)
    {
        _loading = true;
        Detail.IsEnabled = w != null;
        if (w == null) { ClearDetail(); _loading = false; return; }

        NameText.Text = FriendlyName(w);
        SubText.Text = $"{w.Type} · {w.Id}";
        TitleBox.Text = w.Title ?? "";

        MonitorCombo.Items.Clear();
        _monitorDevices.Clear();
        MonitorCombo.Items.Add("Automatic — first monitor");
        _monitorDevices.Add("");
        foreach (var m in _monitors)
        {
            MonitorCombo.Items.Add(m.Label);
            _monitorDevices.Add(m.Device);
        }
        int mi = _monitorDevices.FindIndex(d => d.Equals(w.Monitor, StringComparison.OrdinalIgnoreCase));
        if (mi < 0)
        {
            MonitorCombo.Items.Add($"{w.Monitor} (not connected)");
            _monitorDevices.Add(w.Monitor);
            mi = _monitorDevices.Count - 1;
        }
        MonitorCombo.SelectedIndex = mi;

        XBox.Text = w.X.ToString(CultureInfo.InvariantCulture);
        YBox.Text = w.Y.ToString(CultureInfo.InvariantCulture);
        ZModeCombo.SelectedIndex = Math.Max(0, Array.FindIndex(ZModes, z => z.Mode == w.ZMode));
        ClickThroughCheck.IsChecked = w.ClickThrough;
        KeepOnScreenCheck.IsChecked = w.KeepOnScreen;
        LockedCheck.IsChecked = w.Locked;
        OpacitySlider.Value = Math.Clamp(w.Opacity, 0.1, 1.0);
        RateBox.Text = w.RateHz.ToString(CultureInfo.InvariantCulture);

        // Graph toggles now live in the per-metric settings, not in the options dictionary.
        GraphLinesPanel.Children.Clear();
        _graphChecks.Clear();
        var lines = GraphLines(w.Type);
        GraphLinesGroup.Visibility = lines.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var panelType = PanelCatalog.Find(w.Type);
        foreach (var (key, label) in lines)
        {
            var cb = new CheckBox
            {
                Content = label,
                Margin = new Thickness(0, 3, 0, 3),
                IsChecked = w.Metrics.GetValueOrDefault(key)?.Graph
                            ?? panelType?.Metric(key)?.GraphDefaultOn ?? true,
            };
            GraphLinesPanel.Children.Add(cb);
            _graphChecks.Add((key, cb));
        }

        var sb = new StringBuilder();
        foreach (var kv in w.Options) sb.AppendLine($"{kv.Key}={kv.Value}");
        OptionsBox.Text = sb.ToString().TrimEnd('\r', '\n');
        OptionsHint.Text = OptionsHelp(w.Type);

        _loading = false;
    }

    private void ClearDetail()
    {
        NameText.Text = "";
        SubText.Text = "";
        TitleBox.Text = "";
        MonitorCombo.Items.Clear();
        _monitorDevices.Clear();
        XBox.Text = YBox.Text = RateBox.Text = "";
        OptionsBox.Text = OptionsHint.Text = "";
        GraphLinesPanel.Children.Clear();
        _graphChecks.Clear();
        GraphLinesGroup.Visibility = Visibility.Collapsed;
        ClickThroughCheck.IsChecked = KeepOnScreenCheck.IsChecked = LockedCheck.IsChecked = false;
        ZModeCombo.SelectedItem = null;
        Detail.IsEnabled = false;
    }

    private void FlushCurrent()
    {
        if (_current == null || _loading) return;
        _current.Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? null : TitleBox.Text.Trim();
        if (MonitorCombo.SelectedIndex >= 0 && MonitorCombo.SelectedIndex < _monitorDevices.Count)
            _current.Monitor = _monitorDevices[MonitorCombo.SelectedIndex];
        if (int.TryParse(XBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out int x)) _current.X = x;
        if (int.TryParse(YBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out int y)) _current.Y = y;
        if (ZModeCombo.SelectedIndex >= 0) _current.ZMode = ZModes[ZModeCombo.SelectedIndex].Mode;
        _current.ClickThrough = ClickThroughCheck.IsChecked == true;
        _current.KeepOnScreen = KeepOnScreenCheck.IsChecked == true;
        _current.Locked = LockedCheck.IsChecked == true;
        _current.Opacity = Math.Round(Math.Clamp(OpacitySlider.Value, 0.1, 1.0), 2);
        if (double.TryParse(RateBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double r))
            _current.RateHz = Math.Clamp(r, 0.5, 10);
        _current.Options = ParseOptions(OptionsBox.Text);

        // Only a hidden line is persisted; a line left at its catalog default stays out of the file.
        var panelType = PanelCatalog.Find(_current.Type);
        foreach (var (key, box) in _graphChecks)
        {
            bool on = box.IsChecked == true;
            bool def = panelType?.Metric(key)?.GraphDefaultOn ?? true;
            if (on == def)
            {
                if (_current.Metrics.TryGetValue(key, out var existing))
                {
                    existing.Graph = null;
                    if (IsEmpty(existing)) _current.Metrics.Remove(key);
                }
            }
            else
            {
                if (!_current.Metrics.TryGetValue(key, out var m)) _current.Metrics[key] = m = new MetricSetting();
                m.Graph = on;
            }
        }
    }

    private static bool IsEmpty(MetricSetting m)
        => m.Show == null && m.Label == null && m.Graph == null && m.Color == null && m.Warn == null && m.Max == null;

    private static Dictionary<string, string> ParseOptions(string text)
    {
        var d = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) d[line] = "";
            else d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        FlushCurrent();
        int sel = WidgetList.SelectedIndex;

        _store.Reload();
        var disk = _store.Widgets.Widgets;
        foreach (var row in _rows)
        {
            var edited = row.W;
            var target = disk.Find(w => w.Id == edited.Id);
            if (target == null) { disk.Add(edited); continue; }
            ApplyEdits(target, _pristine.GetValueOrDefault(edited.Id) ?? target, edited);
        }

        try
        {
            _store.SaveWidgets();
            Status.Text = $"Saved widgets.json at {DateTime.Now:HH:mm:ss} — widgets update live.";
        }
        catch (Exception ex) { Status.Text = "Save failed: " + ex.Message; return; }
        LoadFromStore(sel);
    }

    /// <summary>Copies onto <paramref name="target"/> only the fields the user changed on this page.</summary>
    private static void ApplyEdits(WidgetInstance target, WidgetInstance was, WidgetInstance now)
    {
        if (now.Enabled != was.Enabled) target.Enabled = now.Enabled;
        if (now.Title != was.Title) target.Title = now.Title;
        if (now.Monitor != was.Monitor) target.Monitor = now.Monitor;
        if (now.X != was.X) target.X = now.X;
        if (now.Y != was.Y) target.Y = now.Y;
        if (now.ZMode != was.ZMode) target.ZMode = now.ZMode;
        if (now.ClickThrough != was.ClickThrough) target.ClickThrough = now.ClickThrough;
        if (now.KeepOnScreen != was.KeepOnScreen) target.KeepOnScreen = now.KeepOnScreen;
        if (now.Locked != was.Locked) target.Locked = now.Locked;
        if (now.Opacity != was.Opacity) target.Opacity = now.Opacity;
        if (now.RateHz != was.RateHz) target.RateHz = now.RateHz;
        if (!OptionsEqual(now.Options, was.Options)) target.Options = now.Options;
        if (!MetricsEqual(now.Metrics, was.Metrics)) target.Metrics = now.Metrics;
    }

    private static bool OptionsEqual(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out string? v) || v != kv.Value) return false;
        return true;
    }

    private static bool MetricsEqual(Dictionary<string, MetricSetting> a, Dictionary<string, MetricSetting> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var other)) return false;
            var m = kv.Value;
            if (m.Show != other.Show || m.Label != other.Label || m.Graph != other.Graph
                || m.Color != other.Color || m.Max != other.Max) return false;
            if ((m.Warn == null) != (other.Warn == null)) return false;
            if (m.Warn != null && other.Warn != null && !m.Warn.SequenceEqual(other.Warn)) return false;
        }
        return true;
    }
}
