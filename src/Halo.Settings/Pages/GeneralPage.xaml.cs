using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Halo.Shared;
using Halo.Shared.Config;

namespace Halo.Settings.Pages;

public partial class GeneralPage : UserControl, ISettingsPage
{
    private readonly ConfigStore _store;
    private readonly ObservableCollection<StringPair> _fanNames = new();
    private readonly ObservableCollection<StringPair> _fanMaxRpm = new();

    public GeneralPage(ConfigStore store)
    {
        _store = store;
        InitializeComponent();
        FanNamesGrid.ItemsSource = _fanNames;
        FanMaxRpmGrid.ItemsSource = _fanMaxRpm;
    }

    public void OnEnter()
    {
        _store.Reload();
        var s = _store.Settings;
        DefaultRateBox.Text = s.DefaultRateHz.ToString(CultureInfo.InvariantCulture);
        ScaleSlider.Value = s.Scale;
        FontBox.Text = s.FontFamily;
        LockAllCheck.IsChecked = s.LockAll;
        FrameLowsBox.Text = s.FrameLowsWindowS.ToString(CultureInfo.InvariantCulture);
        GraphHistoryBox.Text = s.GraphHistoryS.ToString(CultureInfo.InvariantCulture);
        IpUrlBox.Text = s.ExternalIpUrl;
        IpRefreshBox.Text = s.ExternalIpRefreshMinutes.ToString(CultureInfo.InvariantCulture);
        DrivesBox.Text = string.Join(", ", s.DriveLetters);
        NetIfBox.Text = s.NetworkInterface;
        TopProcBox.Text = s.TopProcessCount.ToString(CultureInfo.InvariantCulture);

        _fanNames.Clear();
        foreach (var kv in s.FanNames) _fanNames.Add(new StringPair { Key = kv.Key, Value = kv.Value });
        _fanMaxRpm.Clear();
        foreach (var kv in s.FanMaxRpm)
            _fanMaxRpm.Add(new StringPair { Key = kv.Key, Value = kv.Value.ToString(CultureInfo.InvariantCulture) });

        Status.Text = "";
    }

    public void OnLeave() { }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _store.Settings;
        s.DefaultRateHz = Clamp(ParseD(DefaultRateBox.Text, s.DefaultRateHz), 1, 100);
        s.Scale = Math.Round(ScaleSlider.Value, 2);
        s.FontFamily = string.IsNullOrWhiteSpace(FontBox.Text) ? s.FontFamily : FontBox.Text.Trim();
        s.LockAll = LockAllCheck.IsChecked == true;
        s.FrameLowsWindowS = ParseD(FrameLowsBox.Text, s.FrameLowsWindowS);
        s.GraphHistoryS = ParseD(GraphHistoryBox.Text, s.GraphHistoryS);
        s.ExternalIpUrl = IpUrlBox.Text.Trim();
        s.ExternalIpRefreshMinutes = ParseD(IpRefreshBox.Text, s.ExternalIpRefreshMinutes);
        s.DriveLetters = DrivesBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        s.NetworkInterface = string.IsNullOrWhiteSpace(NetIfBox.Text) ? "Best" : NetIfBox.Text.Trim();
        s.TopProcessCount = (int)Clamp(ParseD(TopProcBox.Text, s.TopProcessCount), 0, 100);

        var names = new Dictionary<string, string>();
        foreach (var p in _fanNames)
            if (!string.IsNullOrWhiteSpace(p.Key)) names[p.Key.Trim()] = (p.Value ?? "").Trim();
        s.FanNames = names;

        var maxRpm = new Dictionary<string, double>();
        foreach (var p in _fanMaxRpm)
            if (!string.IsNullOrWhiteSpace(p.Key) &&
                double.TryParse(p.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                maxRpm[p.Key.Trim()] = d;
        s.FanMaxRpm = maxRpm;

        try
        {
            _store.SaveSettings();
            Status.Text = $"Saved settings.json at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex) { Status.Text = "Save failed: " + ex.Message; }
    }

    private void ResetMax_Click(object sender, RoutedEventArgs e)
        => Status.Text = ControlPipe.Send("reset-max")
            ? "Sent reset-max to collector."
            : "Collector not running (reset-max not delivered).";

    private void ResetNet_Click(object sender, RoutedEventArgs e)
        => Status.Text = ControlPipe.Send("reset-net")
            ? "Sent reset-net to collector."
            : "Collector not running (reset-net not delivered).";

    private static double ParseD(string t, double fallback)
        => double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : fallback;

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
