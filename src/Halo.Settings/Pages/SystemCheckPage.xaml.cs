using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Halo.Settings.Services;
using Halo.Settings.ViewModels;

namespace Halo.Settings.Pages;

public partial class SystemCheckPage : UserControl, ISettingsPage, ISearchableSettingsPage, IDisposable
{
    private readonly SystemCheckViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    private readonly Action _openWidgets;
    private bool _disposed;

    public SystemCheckPage(LiveConfigService config, bool firstRun, Action openWidgets)
    {
        _viewModel = new SystemCheckViewModel(config, firstRun);
        _openWidgets = openWidgets;
        InitializeComponent();
        DataContext = _viewModel;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            async (_, _) => await _viewModel.RefreshAsync(), Dispatcher);
        _timer.Stop();
    }

    public void OnEnter()
    {
        _timer.Start();
        _ = _viewModel.RefreshAsync();
    }

    public void OnLeave() => _timer.Stop();

    public void ApplyFilter(string query)
    {
        string filter = query.Trim();
        FrameworkElement[] sections = [SummarySection, ActionsSection, HardwareSection, ProvidersSection, ReadinessSection];
        bool any = false;
        foreach (FrameworkElement section in sections)
        {
            bool visible = filter.Length == 0 || section.Tag?.ToString()?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true;
            section.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            any |= visible;
        }
        NoSearchResults.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshAsync();
    private async void InstallPawnIo_Click(object sender, RoutedEventArgs e) => await _viewModel.InstallPawnIoAsync();
    private async void RepairAutostart_Click(object sender, RoutedEventArgs e) => await _viewModel.RepairAutostartAsync();
    private async void Rescan_Click(object sender, RoutedEventArgs e) => await _viewModel.RescanAsync();

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.BuildHardwareReport());
            _viewModel.ActionStatus = "Hardware report copied to the clipboard.";
        }
        catch (Exception ex) { _viewModel.ActionStatus = $"Could not copy the report: {ex.Message}"; }
    }

    private async void GenerateLayout_Click(object sender, RoutedEventArgs e)
    {
        LayoutPlan plan = _viewModel.CreateLayoutPlan();
        MessageBoxResult result = MessageBox.Show(
            $"{plan.Summary}\n\nThis replaces widgets.json.",
            "Generate default layout", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK) return;
        try
        {
            await _viewModel.SaveLayoutAsync(plan);
            _openWidgets();
        }
        catch (Exception ex)
        {
            _viewModel.ActionStatus = $"Could not create the layout: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _viewModel.Dispose();
    }
}
