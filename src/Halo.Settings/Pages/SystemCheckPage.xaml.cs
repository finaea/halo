using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Halo.Settings.Services;
using Halo.Settings.ViewModels;
using Halo.Shared;

namespace Halo.Settings.Pages;

public partial class SystemCheckPage : UserControl, ISettingsPage, IDisposable
{
    private readonly SystemCheckViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    private readonly Action _openWidgets;
    private readonly bool _completeFirstRunOnLeave;
    private bool _firstRunCompleted;
    private bool _disposed;

    public SystemCheckPage(LiveConfigService config, bool firstRun, Action openWidgets)
    {
        _viewModel = new SystemCheckViewModel(config, firstRun);
        _openWidgets = openWidgets;
        _completeFirstRunOnLeave = firstRun;
        InitializeComponent();
        DataContext = _viewModel;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            async (_, _) => await _viewModel.RefreshAsync(), Dispatcher);
        _timer.Stop();
    }

    public void OnEnter()
    {
        _viewModel.RefreshLoggingFromCurrent();
        _timer.Start();
        _ = _viewModel.RefreshAsync();
    }

    public void OnLeave()
    {
        _timer.Stop();
        CompleteFirstRun();
    }


    private async void Retry_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshAsync();
    private async void InstallPawnIo_Click(object sender, RoutedEventArgs e) => await _viewModel.InstallPawnIoAsync();
    private async void RepairAutostart_Click(object sender, RoutedEventArgs e) => await _viewModel.RepairAutostartAsync();
    private async void Rescan_Click(object sender, RoutedEventArgs e) => await _viewModel.RescanAsync();
    private async void ArrangeWidgets_Click(object sender, RoutedEventArgs e) => await _viewModel.ArrangeWidgetsAsync();

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
        => OpenFolder(Paths.LogsDir, "logs folder", createIfMissing: true);

    private void OpenInstallFolder_Click(object sender, RoutedEventArgs e)
        => OpenFolder(Paths.AppRoot, "install folder", createIfMissing: false);

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
        string title = _viewModel.HasExistingLayout ? "Regenerate default layout" : "Generate default layout";
        string effect = _viewModel.HasExistingLayout
            ? "This will replace your current widgets."
            : "This will create widgets.json.";
        MessageBoxResult result = MessageBox.Show(
            $"{plan.Summary}\n\n{effect}",
            title, MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);
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

    private void CompleteFirstRun()
    {
        if (!_completeFirstRunOnLeave || _firstRunCompleted) return;
        if (FirstRunState.Complete(out string? error))
            _firstRunCompleted = true;
        else
            _viewModel.ActionStatus = $"Could not finish first-run setup: {error}";
    }

    private void OpenFolder(string path, string displayName, bool createIfMissing)
    {
        try
        {
            if (createIfMissing) Directory.CreateDirectory(path);
            if (!Directory.Exists(path))
            {
                _viewModel.ActionStatus = $"The {displayName} does not exist yet.";
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _viewModel.ActionStatus = $"Could not open the {displayName}: {ex.Message}";
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
