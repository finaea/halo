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
        _current = null;
        _store.Reload();
        _widgets.Clear();
        foreach (var w in _store.Widgets.Widgets) _widgets.Add(w);
        if (_widgets.Count > 0) WidgetList.SelectedIndex = 0;
        else ClearDetail();
        Status.Text = "";
    }

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
        _store.Widgets.Widgets = _widgets.ToList();
        try
        {
            _store.SaveWidgets();
            Status.Text = $"Saved widgets.json at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex) { Status.Text = "Save failed: " + ex.Message; }
    }
}
