using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Halo.Metrics;
using Halo.Shared;
using Halo.Shared.Config;

namespace Halo.Settings.Pages;

public partial class GeneralPage : UserControl, ISettingsPage
{
    // "console" is gone with schema v2: the capture app was never shipped and the setting was dead.
    private static readonly (string Value, string Label)[] Transports =
    {
        ("auto", "Auto — bundled PresentMon service"),
        ("sdk", "Service + SDK only"),
    };

    private static readonly (string Value, string Label)[] TapModes =
    {
        ("auto", "Auto — live tap when supported"),
        ("off", "Off — capture transport only"),
    };

    private readonly ConfigStore _store;

    public GeneralPage(ConfigStore store)
    {
        _store = store;
        InitializeComponent();

        foreach (var t in Transports) TransportCombo.Items.Add(t.Label);
        foreach (var t in TapModes) TapCombo.Items.Add(t.Label);
        foreach (var f in Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s))
            FontCombo.Items.Add(f);
    }

    public void OnEnter()
    {
        _store.Reload();
        var s = _store.Settings;

        AutoScaleCheck.IsChecked = s.Appearance.Scale.IsAuto;
        ScaleSlider.Value = Math.Clamp(s.Appearance.Scale.Or(1.7), ScaleSlider.Minimum, ScaleSlider.Maximum);
        ScaleSlider.IsEnabled = !s.Appearance.Scale.IsAuto;
        FontCombo.Text = s.Appearance.FontFamily;
        TextSizeBox.Text = s.Appearance.TextSizePt.ToString(CultureInfo.InvariantCulture);
        CornerRadiusBox.Text = s.Appearance.CornerRadius.ToString(CultureInfo.InvariantCulture);
        LockAllCheck.IsChecked = s.LockAll;
        SnapCheck.IsChecked = s.Snap;

        FrameLowsBox.Text = s.Collector.FrameLowsWindowS.ToString(CultureInfo.InvariantCulture);
        EtwFlushBox.Text = s.Collector.PresentMonEtwFlushMs.ToString(CultureInfo.InvariantCulture);
        TransportCombo.SelectedIndex = IndexOf(Transports, s.Collector.PresentMonTransport);
        TapCombo.SelectedIndex = IndexOf(TapModes, s.Collector.PresentedTap);

        ExternalIpCheck.IsChecked = s.Collector.ExternalIp.Enabled;
        IpUrlBox.Text = s.Collector.ExternalIp.Url;
        IpRefreshBox.Text = s.Collector.ExternalIp.RefreshMinutes.ToString(CultureInfo.InvariantCulture);
        LoadNetworkAdapters(s.Collector.NetworkInterface);

        Status.Text = "";
    }

    public void OnLeave() { }

    private void AutoScale_Changed(object sender, RoutedEventArgs e)
        => ScaleSlider.IsEnabled = AutoScaleCheck.IsChecked != true;

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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _store.Settings;

        s.Appearance.Scale = AutoScaleCheck.IsChecked == true
            ? ScaleValue.Auto
            : ScaleValue.Fixed(Math.Round(ScaleSlider.Value, 2));
        s.Appearance.FontFamily = string.IsNullOrWhiteSpace(FontCombo.Text) ? s.Appearance.FontFamily : FontCombo.Text.Trim();
        s.Appearance.TextSizePt = Clamp(ParseD(TextSizeBox.Text, s.Appearance.TextSizePt), 5, 24);
        s.Appearance.CornerRadius = Clamp(ParseD(CornerRadiusBox.Text, s.Appearance.CornerRadius), 0, 20);
        s.LockAll = LockAllCheck.IsChecked == true;
        s.Snap = SnapCheck.IsChecked == true;

        s.Collector.FrameLowsWindowS = ParseD(FrameLowsBox.Text, s.Collector.FrameLowsWindowS);
        s.Collector.PresentMonEtwFlushMs = (int)Clamp(ParseD(EtwFlushBox.Text, s.Collector.PresentMonEtwFlushMs), 0, 1000);
        if (TransportCombo.SelectedIndex >= 0) s.Collector.PresentMonTransport = Transports[TransportCombo.SelectedIndex].Value;
        if (TapCombo.SelectedIndex >= 0) s.Collector.PresentedTap = TapModes[TapCombo.SelectedIndex].Value;
        s.Collector.NetworkInterface = string.IsNullOrWhiteSpace(NetIfCombo.Text) ? "Best" : NetIfCombo.Text.Trim();
        s.Collector.ExternalIp.Enabled = ExternalIpCheck.IsChecked == true;
        s.Collector.ExternalIp.Url = IpUrlBox.Text.Trim();
        s.Collector.ExternalIp.RefreshMinutes = ParseD(IpRefreshBox.Text, s.Collector.ExternalIp.RefreshMinutes);

        try
        {
            _store.SaveSettings();
            Status.Text = $"Saved at {DateTime.Now:HH:mm:ss}. Widgets and collector pick changes up live.";
        }
        catch (Exception ex) { Status.Text = "Save failed: " + ex.Message; }
    }

    private void ResetMax_Click(object sender, RoutedEventArgs e)
        => Status.Text = ControlPipe.Send(ControlPipe.ResetMax)
            ? "Sent reset-max to collector."
            : "Collector not running (reset-max not delivered).";

    private void ResetNet_Click(object sender, RoutedEventArgs e)
        => Status.Text = ControlPipe.Send(ControlPipe.ResetNet)
            ? "Sent reset-net to collector."
            : "Collector not running (reset-net not delivered).";

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Paths.DataDir) { UseShellExecute = true }); }
        catch (Exception ex) { Status.Text = "Could not open " + Paths.DataDir + ": " + ex.Message; }
    }

    private static double ParseD(string t, double fallback)
        => double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : fallback;

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
