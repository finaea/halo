using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Halo.Shared;
using Halo.Shared.Config;

namespace Halo.Settings.Pages;

public partial class GeneralPage : UserControl, ISettingsPage
{
    private static readonly (string Value, string Label)[] Transports =
    {
        ("auto", "Auto — SDK, console fallback"),
        ("sdk", "Service + SDK (lowest latency)"),
        ("console", "Console capture app"),
    };

    private static readonly (string Value, string Label)[] TapModes =
    {
        ("auto", "Auto — live tap when supported"),
        ("off", "Off — capture transport only"),
    };

    private readonly ConfigStore _store;
    private readonly ObservableCollection<StringPair> _fanNames = new();
    private readonly ObservableCollection<StringPair> _fanMaxRpm = new();

    public GeneralPage(ConfigStore store)
    {
        _store = store;
        InitializeComponent();
        FanNamesGrid.ItemsSource = _fanNames;
        FanMaxRpmGrid.ItemsSource = _fanMaxRpm;

        foreach (var t in Transports) TransportCombo.Items.Add(t.Label);
        foreach (var t in TapModes) TapCombo.Items.Add(t.Label);
        foreach (var f in Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s))
            FontCombo.Items.Add(f);
    }

    public void OnEnter()
    {
        _store.Reload();
        var s = _store.Settings;
        DefaultRateBox.Text = s.DefaultRateHz.ToString(CultureInfo.InvariantCulture);
        ScaleSlider.Value = _loadedScale = LoadThemeScale();
        FontCombo.Text = s.FontFamily;
        LockAllCheck.IsChecked = s.LockAll;
        FrameLowsBox.Text = s.FrameLowsWindowS.ToString(CultureInfo.InvariantCulture);
        GraphHistoryBox.Text = s.GraphHistoryS.ToString(CultureInfo.InvariantCulture);
        EtwFlushBox.Text = s.PresentMonEtwFlushMs.ToString(CultureInfo.InvariantCulture);
        TransportCombo.SelectedIndex = IndexOf(Transports, s.PresentMonTransport);
        TapCombo.SelectedIndex = IndexOf(TapModes, s.PresentedTap);
        IpUrlBox.Text = s.ExternalIpUrl;
        IpRefreshBox.Text = s.ExternalIpRefreshMinutes.ToString(CultureInfo.InvariantCulture);
        TopProcBox.Text = s.TopProcessCount.ToString(CultureInfo.InvariantCulture);

        LoadNetworkAdapters(s.NetworkInterface);
        LoadDriveChecks(s.DriveLetters);

        _fanNames.Clear();
        foreach (var kv in s.FanNames) _fanNames.Add(new StringPair { Key = kv.Key, Value = kv.Value });
        _fanMaxRpm.Clear();
        foreach (var kv in s.FanMaxRpm)
            _fanMaxRpm.Add(new StringPair { Key = kv.Key, Value = kv.Value.ToString(CultureInfo.InvariantCulture) });

        Status.Text = "";
    }

    public void OnLeave() { }

    private static int IndexOf((string Value, string Label)[] set, string value)
    {
        for (int i = 0; i < set.Length; i++)
            if (set[i].Value.Equals(value, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private void LoadNetworkAdapters(string current)
    {
        NetIfCombo.Items.Clear();
        NetIfCombo.Items.Add("Best");
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                         .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
                         .ThenBy(n => n.Name))
                NetIfCombo.Items.Add(ni.Name);
        }
        catch { /* adapter enumeration is best-effort; the editable box still works */ }
        NetIfCombo.Text = string.IsNullOrWhiteSpace(current) ? "Best" : current;
    }

    private void LoadDriveChecks(List<string> configured)
    {
        DrivesPanel.Children.Clear();
        var present = DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => d.Name[..1].ToUpperInvariant());
        var known = new SortedSet<string>(present, StringComparer.OrdinalIgnoreCase);
        foreach (var c in configured) known.Add(c.ToUpperInvariant());

        foreach (var letter in known)
        {
            bool absent = !Directory.Exists(letter + ":\\");
            DrivesPanel.Children.Add(new CheckBox
            {
                Content = absent ? $"{letter}: (absent)" : letter + ":",
                Tag = letter,
                IsChecked = configured.Contains(letter, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 3, 16, 3),
            });
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _store.Settings;
        s.DefaultRateHz = Clamp(ParseD(DefaultRateBox.Text, s.DefaultRateHz), 1, 100);
        s.FontFamily = string.IsNullOrWhiteSpace(FontCombo.Text) ? s.FontFamily : FontCombo.Text.Trim();
        s.LockAll = LockAllCheck.IsChecked == true;
        s.FrameLowsWindowS = ParseD(FrameLowsBox.Text, s.FrameLowsWindowS);
        s.GraphHistoryS = ParseD(GraphHistoryBox.Text, s.GraphHistoryS);
        s.PresentMonEtwFlushMs = (int)Clamp(ParseD(EtwFlushBox.Text, s.PresentMonEtwFlushMs), 0, 1000);
        if (TransportCombo.SelectedIndex >= 0) s.PresentMonTransport = Transports[TransportCombo.SelectedIndex].Value;
        if (TapCombo.SelectedIndex >= 0) s.PresentedTap = TapModes[TapCombo.SelectedIndex].Value;
        s.ExternalIpUrl = IpUrlBox.Text.Trim();
        s.ExternalIpRefreshMinutes = ParseD(IpRefreshBox.Text, s.ExternalIpRefreshMinutes);
        s.DriveLetters = DrivesPanel.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (string)c.Tag)
            .ToList();
        s.NetworkInterface = string.IsNullOrWhiteSpace(NetIfCombo.Text) ? "Best" : NetIfCombo.Text.Trim();
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
            double scale = Math.Round(ScaleSlider.Value, 2);
            if (Math.Abs(scale - _loadedScale) > 0.001) { SaveThemeScale(scale); _loadedScale = scale; }
            Status.Text = $"Saved at {DateTime.Now:HH:mm:ss}. Widgets and collector pick changes up live.";
        }
        catch (Exception ex) { Status.Text = "Save failed: " + ex.Message; }
    }

    // Widget scale lives in theme.json (the renderer reads Theme.Scale only); this page
    // edits just the "scale" key and leaves the color tokens untouched.
    private double _loadedScale = 1.7;

    private static string ThemePath => Path.Combine(ProjectPaths.ConfigDir, "theme.json");

    private static double LoadThemeScale()
    {
        try
        {
            if (File.Exists(ThemePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ThemePath));
                if (doc.RootElement.TryGetProperty("scale", out var s) && s.ValueKind == JsonValueKind.Number)
                    return s.GetDouble();
            }
        }
        catch { /* fall through to default */ }
        return 1.7;
    }

    private static void SaveThemeScale(double scale)
    {
        JsonObject root;
        try
        {
            root = File.Exists(ThemePath)
                ? JsonNode.Parse(File.ReadAllText(ThemePath)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch { root = new JsonObject(); }
        root["scale"] = scale;
        File.WriteAllText(ThemePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
