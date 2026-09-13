using System.Windows;
using System.Windows.Controls;
using Halo.Settings.Pages;
using Halo.Shared.Config;

namespace Halo.Settings;

public partial class MainWindow : Window
{
    private readonly ConfigStore _store;
    private readonly Dictionary<int, ISettingsPage> _pages = new();
    private ISettingsPage? _current;

    public MainWindow()
    {
        InitializeComponent();
        // Settings app never watches; the collector/widgets own hot-reload. We just read + write.
        _store = new ConfigStore(Halo.Shared.Paths.ConfigDir, watch: false);
        ConfigPathText.Text = Halo.Shared.Paths.ConfigDir;
        Nav.SelectedIndex = 0; // triggers first navigation
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = Nav.SelectedIndex;
        if (i < 0) return;

        _current?.OnLeave();

        if (!_pages.TryGetValue(i, out ISettingsPage? page))
        {
            page = i switch
            {
                0 => new GeneralPage(_store),
                1 => new WidgetsPage(_store),
                2 => new ThemePage(_store),
                3 => new MetricsPage(),
                4 => new AutostartPage(),
                _ => new GeneralPage(_store),
            };
            _pages[i] = page;
        }

        Host.Content = page;
        _current = page;
        page.OnEnter();
    }

    protected override void OnClosed(EventArgs e)
    {
        _current?.OnLeave();
        foreach (var p in _pages.Values) (p as IDisposable)?.Dispose();
        _store.Dispose();
        base.OnClosed(e);
    }
}
