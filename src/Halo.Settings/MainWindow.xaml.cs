using System.Windows.Controls;
using System.IO;
using Halo.Settings.Pages;
using Halo.Settings.Services;
using Halo.Shared;
using Wpf.Ui.Controls;

namespace Halo.Settings;

public partial class MainWindow : FluentWindow
{
    private readonly LiveConfigService _config;
    private readonly bool _firstRun;
    private readonly Dictionary<string, ISettingsPage> _pages = new(StringComparer.Ordinal);
    private ISettingsPage? _current;

    public MainWindow()
    {
        _firstRun = !File.Exists(Path.Combine(Paths.ConfigDir, "widgets.json"));
        InitializeComponent();
        _config = new LiveConfigService(Paths.ConfigDir);
        _config.StatusChanged += Config_StatusChanged;
        ConfigPathText.Text = Paths.ConfigDir;
        ConfigPathText.ToolTip = Paths.ConfigDir;
        Navigation.SelectedIndex = _firstRun ? 1 : 0;
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
                "system" => new SystemCheckPage(_config, _firstRun, OpenWidgets),
                "widgets" => new WidgetsPage(_config),
                "about" => new AboutPage(),
                _ => new GeneralPage(_config),
            };
            _pages[key] = page;
        }
        PageHost.Content = page;
        _current = page;
        page.OnEnter();
        if (page is ISearchableSettingsPage searchable) searchable.ApplyFilter(SearchBox.Text);
    }

    private void OpenWidgets()
    {
        if (Navigation.SelectedIndex == 2) ShowPage("widgets");
        else Navigation.SelectedIndex = 2;
        if (_pages.TryGetValue("widgets", out ISettingsPage? page) && page is WidgetsPage widgets)
            widgets.ShowReadyBanner();
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
