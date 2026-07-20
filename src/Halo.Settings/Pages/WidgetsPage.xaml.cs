using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Halo.Shared.Config;

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
    // Per-type graph-line toggle checkboxes built for the selected widget (option key + control).
    private readonly List<(string Key, CheckBox Box)> _graphChecks = new();

    /// <summary>Toggleable graph lines per widget type: option key + display label. Default is on;
    /// only a hidden line is persisted (key="false"), matching the panel-side default.</summary>
    private static (string Key, string Label)[] GraphLines(string type) => type switch
    {
        "cpu-ram" => new[]
        {
            ("graphCpuTemp", "CPU temperature"),
            ("graphCpuUsage", "CPU usage"),
            ("graphRamUsage", "RAM usage"),
        },
        "gpu" => new[]
        {
            ("graphGpuTemp", "GPU temperature"),
            ("graphGpuUsage", "GPU usage"),
            ("graphGpuMem", "VRAM usage"),
            ("graphGpuFan", "Fan speed"),
        },
        "drives" => new[]
        {
            ("graphDriveWrite", "Write history"),
            ("graphDriveRead", "Read history"),
        },
        _ => Array.Empty<(string, string)>(),
    };

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
        Id = w.Id, Type = w.Type, Enabled = w.Enabled, Monitor = w.Monitor,
        X = w.X, Y = w.Y, ZMode = w.ZMode, ClickThrough = w.ClickThrough,
        KeepOnScreen = w.KeepOnScreen, Locked = w.Locked, Opacity = w.Opacity,
        RateHz = w.RateHz, Options = new Dictionary<string, string>(w.Options),
    };

    private static string FriendlyName(WidgetInstance w) => w.Type switch
    {
        "clock" => "Clock",
        "power" => "Power draw",
        "drives" => "Drives",
        "cpu-ram" => "CPU & RAM",
        "fans" => "Fans",
        "network" => "Network",
        "topcpu" => "Top processes — CPU",
        "topram" => "Top processes — RAM",
        "gpu" => "GPU",
        "latency" => "Latency & DLSS",
        "fps" => w.Options.GetValueOrDefault("stream", "").ToLowerInvariant() switch
        {
            "displayed" => "FPS counter — displayed",
            "presented" => "FPS counter — presented",
            _ => "FPS counter",
        },
        _ => w.Type,
    };

    private static string OptionsHelp(string type) => type switch
    {
        "fps" => "stream=presented — live counter fed by the present tap (RTSS-like latency).\n" +
                 "stream=displayed — what actually reached the screen, fate-resolved (frame-gen aware).",
        "topcpu" or "topram" => "aggregate=true — sum same-name processes into one row.",
        "cpu-ram" or "gpu" or "drives" =>
            "Use Graph lines above to choose which lines appear on the graph. Extra options: key=value, one per line.",
        _ => "This panel has no options. (Format: key=value, one per line.)",
    };

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
        RateBox.Text = w.RateHz?.ToString(CultureInfo.InvariantCulture) ?? "";

        // Graph-line checkboxes (owned separately from the raw options box).
        GraphLinesPanel.Children.Clear();
        _graphChecks.Clear();
        var lines = GraphLines(w.Type);
        GraphLinesGroup.Visibility = lines.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (key, label) in lines)
        {
            var cb = new CheckBox
            {
                Content = label,
                Margin = new Thickness(0, 3, 0, 3),
                IsChecked = w.Options.GetValueOrDefault(key) != "false",
            };
            GraphLinesPanel.Children.Add(cb);
            _graphChecks.Add((key, cb));
        }

        // Raw options box shows everything except the graph-line keys owned by the checkboxes.
        var graphKeys = lines.Select(l => l.Key).ToHashSet();
        var sb = new StringBuilder();
        foreach (var kv in w.Options)
            if (!graphKeys.Contains(kv.Key)) sb.AppendLine($"{kv.Key}={kv.Value}");
        OptionsBox.Text = sb.ToString().TrimEnd('\r', '\n');
        OptionsHint.Text = OptionsHelp(w.Type);

        _loading = false;
    }

    private void ClearDetail()
    {
        NameText.Text = "";
        SubText.Text = "";
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
        if (MonitorCombo.SelectedIndex >= 0 && MonitorCombo.SelectedIndex < _monitorDevices.Count)
            _current.Monitor = _monitorDevices[MonitorCombo.SelectedIndex];
        if (int.TryParse(XBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out int x)) _current.X = x;
        if (int.TryParse(YBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out int y)) _current.Y = y;
        if (ZModeCombo.SelectedIndex >= 0) _current.ZMode = ZModes[ZModeCombo.SelectedIndex].Mode;
        _current.ClickThrough = ClickThroughCheck.IsChecked == true;
        _current.KeepOnScreen = KeepOnScreenCheck.IsChecked == true;
        _current.Locked = LockedCheck.IsChecked == true;
        _current.Opacity = Math.Round(Math.Clamp(OpacitySlider.Value, 0.1, 1.0), 2);
        _current.RateHz = double.TryParse(RateBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double r)
            ? r : (double?)null;
        _current.Options = ParseOptions(OptionsBox.Text);
        // Graph-line checkboxes win over any stray text key: persist only hidden lines.
        foreach (var (key, box) in _graphChecks)
        {
            if (box.IsChecked == false) _current.Options[key] = "false";
            else _current.Options.Remove(key);
        }
    }

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
    }

    private static bool OptionsEqual(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out string? v) || v != kv.Value) return false;
        return true;
    }
}
