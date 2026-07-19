using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Halo.Shared.Config;

namespace Halo.Settings.Pages;

public partial class WidgetsPage : UserControl, ISettingsPage
{
    private readonly ConfigStore _store;
    private readonly ObservableCollection<WidgetInstance> _widgets = new();
    // Disk state as of page load, per widget id. Save applies only fields that differ from
    // this, so it can't undo drag positions / context-menu toggles the widgets process
    // wrote to disk while the page was open.
    private readonly Dictionary<string, WidgetInstance> _pristine = new();
    private WidgetInstance? _current;
    private bool _loading;

    public WidgetsPage(ConfigStore store)
    {
        _store = store;
        InitializeComponent();
        WidgetList.ItemsSource = _widgets;
        ZModeCombo.ItemsSource = Enum.GetValues(typeof(ZMode));
    }

    public void OnEnter()
    {
        LoadFromStore(0);
        Status.Text = "";
    }

    private void LoadFromStore(int selectIndex)
    {
        _current = null;
        _store.Reload();
        _widgets.Clear();
        _pristine.Clear();
        foreach (var w in _store.Widgets.Widgets)
        {
            _widgets.Add(w);
            _pristine[w.Id] = Clone(w);
        }
        if (_widgets.Count > 0) WidgetList.SelectedIndex = Math.Clamp(selectIndex, 0, _widgets.Count - 1);
        else ClearDetail();
    }

    private static WidgetInstance Clone(WidgetInstance w) => new()
    {
        Id = w.Id, Type = w.Type, Enabled = w.Enabled, Monitor = w.Monitor,
        X = w.X, Y = w.Y, ZMode = w.ZMode, ClickThrough = w.ClickThrough,
        KeepOnScreen = w.KeepOnScreen, Locked = w.Locked, Opacity = w.Opacity,
        RateHz = w.RateHz, Options = new Dictionary<string, string>(w.Options),
    };

    public void OnLeave() { }

    private void WidgetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FlushCurrent();
        _current = WidgetList.SelectedItem as WidgetInstance;
        LoadDetail(_current);
    }

    private void LoadDetail(WidgetInstance? w)
    {
        _loading = true;
        Detail.IsEnabled = w != null;
        if (w == null) { ClearDetail(); _loading = false; return; }

        IdText.Text = w.Id;
        TypeText.Text = w.Type;
        EnabledCheck.IsChecked = w.Enabled;
        MonitorBox.Text = w.Monitor;
        XBox.Text = w.X.ToString(CultureInfo.InvariantCulture);
        YBox.Text = w.Y.ToString(CultureInfo.InvariantCulture);
        ZModeCombo.SelectedItem = w.ZMode;
        ClickThroughCheck.IsChecked = w.ClickThrough;
        KeepOnScreenCheck.IsChecked = w.KeepOnScreen;
        LockedCheck.IsChecked = w.Locked;
        OpacityBox.Text = w.Opacity.ToString(CultureInfo.InvariantCulture);
        RateBox.Text = w.RateHz?.ToString(CultureInfo.InvariantCulture) ?? "";

        var sb = new StringBuilder();
        foreach (var kv in w.Options) sb.AppendLine($"{kv.Key}={kv.Value}");
        OptionsBox.Text = sb.ToString().TrimEnd('\r', '\n');

        _loading = false;
    }

    private void ClearDetail()
    {
        IdText.Text = TypeText.Text = "";
        MonitorBox.Text = XBox.Text = YBox.Text = OpacityBox.Text = RateBox.Text = "";
        OptionsBox.Text = "";
        EnabledCheck.IsChecked = ClickThroughCheck.IsChecked =
            KeepOnScreenCheck.IsChecked = LockedCheck.IsChecked = false;
        ZModeCombo.SelectedItem = null;
        Detail.IsEnabled = false;
    }

    private void FlushCurrent()
    {
        if (_current == null || _loading) return;
        _current.Enabled = EnabledCheck.IsChecked == true;
        _current.Monitor = MonitorBox.Text.Trim();
        if (int.TryParse(XBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out int x)) _current.X = x;
        if (int.TryParse(YBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out int y)) _current.Y = y;
        if (ZModeCombo.SelectedItem is ZMode z) _current.ZMode = z;
        _current.ClickThrough = ClickThroughCheck.IsChecked == true;
        _current.KeepOnScreen = KeepOnScreenCheck.IsChecked == true;
        _current.Locked = LockedCheck.IsChecked == true;
        if (double.TryParse(OpacityBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double op))
            _current.Opacity = Math.Clamp(op, 0.1, 1.0);
        _current.RateHz = double.TryParse(RateBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double r)
            ? r : (double?)null;
        _current.Options = ParseOptions(OptionsBox.Text);
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
        foreach (var edited in _widgets)
        {
            var target = disk.Find(w => w.Id == edited.Id);
            if (target == null) { disk.Add(edited); continue; }
            ApplyEdits(target, _pristine.GetValueOrDefault(edited.Id) ?? target, edited);
        }

        try
        {
            _store.SaveWidgets();
            Status.Text = $"Saved widgets.json at {DateTime.Now:HH:mm:ss}.";
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
