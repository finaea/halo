using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Halo.Metrics;
using Halo.Shared;

namespace Halo.Settings.Pages;

public partial class AboutPage : UserControl, ISettingsPage, IDisposable
{
    private const string ProjectUrl = "https://github.com/finaea/halo";
    private const string RainformerUrl = "https://www.deviantart.com/pul53dr1v3r/art/Rainformer-3-1-HWiNFO-Edition-Rainmeter-789616481";
    private readonly CollectorSession _session = new();
    private readonly string _noticesPath = Path.Combine(Paths.AppRoot, "THIRD-PARTY-NOTICES.md");

    public AboutPage()
    {
        InitializeComponent();
        SettingsVersionText.Text = AppVersion.Current;
        NoticesButton.IsEnabled = File.Exists(_noticesPath);
        NoticesButton.ToolTip = NoticesButton.IsEnabled ? _noticesPath : "THIRD-PARTY-NOTICES.md is not present in this build.";
    }

    public void OnEnter()
    {
        _session.Poll();
        CollectorVersionText.Text = _session.Attached && !_session.Stale
            ? _session.GetText(MetricNames.SysCollectorVersion, _session.CollectorVersion)
            : "Not connected";
    }

    public void OnLeave() { }

    private void CopyVersion_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"Halo Settings {AppVersion.Current}\nHalo Collector {CollectorVersionText.Text}\nMetrics protocol v2");
            LinkStatusText.Text = "Version information copied to the clipboard.";
        }
        catch (Exception ex) { LinkStatusText.Text = $"Could not copy version information: {ex.Message}"; }
    }

    private void Project_Click(object sender, RoutedEventArgs e) => Open(ProjectUrl);
    private void Rainformer_Click(object sender, RoutedEventArgs e) => Open(RainformerUrl);
    private void Notices_Click(object sender, RoutedEventArgs e) => Open(_noticesPath);
    private void DataFolder_Click(object sender, RoutedEventArgs e) => Open(Paths.DataDir);

    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { LinkStatusText.Text = $"Could not open the link: {ex.Message}"; }
    }

    public void Dispose() => _session.Dispose();
}
