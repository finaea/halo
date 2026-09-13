using System.Windows.Controls;
using Halo.Settings.Pages;
using Halo.Settings.Services;
using Halo.Shared;
using Wpf.Ui.Controls;

namespace Halo.Settings;

public partial class MainWindow : FluentWindow
{
    private readonly LiveConfigService _config;
    private readonly Dictionary<string, ISettingsPage> _pages = new(StringComparer.Ordinal);
    private ISettingsPage? _current;

    public MainWindow()
    {
        InitializeComponent();
        _config = new LiveConfigService(Paths.ConfigDir);
        _config.StatusChanged += Config_StatusChanged;
        ConfigPathText.Text = Paths.ConfigDir;
        ConfigPathText.ToolTip = Paths.ConfigDir;
        Navigation.SelectedIndex = 0;
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is not ListBoxItem { Tag: string key }) return;
        ShowPage(key);
    }

    public void ShowPage(string key)
    {
        _current?.OnLeave();
        if (!_pages.TryGetValue(key, out ISettingsPage? page))
        {
            page = key switch
            {
                "general" => new GeneralPage(_config),
                "system" => new PlaceholderPage("System check", "Collector, provider, driver and autostart checks will appear here."),
                "widgets" => new PlaceholderPage("Widgets", "Add, reorder and customise widgets here."),
                "about" => new PlaceholderPage("About", "Halo version, credits and third-party notices."),
                _ => new GeneralPage(_config),
            };
            _pages[key] = page;
        }
        PageHost.Content = page;
        _current = page;
        page.OnEnter();
        if (page is ISearchableSettingsPage searchable) searchable.ApplyFilter(SearchBox.Text);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_current is ISearchableSettingsPage searchable) searchable.ApplyFilter(SearchBox.Text);
    }

    private void Config_StatusChanged(object? sender, ConfigWriteStatus e)
    {
        SaveStatusText.Text = e.IsError ? "⚠ " + e.Message : e.IsSaving ? e.Message : "✓ " + e.Message;
        SaveStatusText.Foreground = (System.Windows.Media.Brush)FindResource(e.IsError ? "HaloDanger" : "HaloSuccess");
    }

    protected override void OnClosed(EventArgs e)
    {
        _current?.OnLeave();
        foreach (ISettingsPage page in _pages.Values)
            (page as IDisposable)?.Dispose();
        _config.StatusChanged -= Config_StatusChanged;
        _config.Dispose();
        base.OnClosed(e);
    }
}
