using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Halo.Settings.Services;
using Halo.Settings.ViewModels;
using Halo.Shared;
using Halo.Shared.Config;
using Microsoft.Win32;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace Halo.Settings.Pages;

public partial class GeneralPage : UserControl, ISettingsPage, ISearchableSettingsPage, IDisposable
{
    private readonly LiveConfigService _config;
    private readonly GeneralViewModel _viewModel;
    private bool _autostartBusy;

    public GeneralPage(LiveConfigService config)
    {
        _config = config;
        _viewModel = new GeneralViewModel(config);
        InitializeComponent();
        DataContext = _viewModel;
        _config.StatusChanged += Config_StatusChanged;
    }

    public void OnEnter()
    {
        _viewModel.RefreshFromCurrent();
        _ = RefreshAutostartAsync();
    }

    public void OnLeave() { }

    public void ApplyFilter(string query)
    {
        string filter = query.Trim();
        bool empty = filter.Length == 0;
        FrameworkElement[] desktopRows = [AutostartRow, LockRow, SnapRow, RepairAutostartButton];
        FrameworkElement[] appearanceRows = [PresetRow, ScaleRow, FontRow, CornerRow, ColorsRow];
        FrameworkElement[] frameRows = [TransportRow, TapRow, FlushRow, LowsRow];
        FrameworkElement[] networkRows = [AdapterRow, ExternalIpRow, IpUrlRow, IpRefreshRow];

        bool desktop = ApplyRows(desktopRows, filter, empty);
        bool appearance = ApplyRows(appearanceRows, filter, empty);
        bool frame = ApplyRows(frameRows, filter, empty);
        bool network = ApplyRows(networkRows, filter, empty);
        bool files = empty || Matches(FilesCard, filter);

        ICollectionView colors = CollectionViewSource.GetDefaultView(_viewModel.Colors);
        colors.Filter = empty ? null : item => item is GlobalColorViewModel row &&
            ($"{row.Token} {row.Label} {row.Description} color colour palette".Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        ColorsRow.Visibility = empty || !colors.IsEmpty || Matches(ColorsRow, filter)
            ? Visibility.Visible
            : Visibility.Collapsed;
        appearance = appearanceRows.Any(row => row.Visibility == Visibility.Visible);

        DesktopCard.Visibility = desktop ? Visibility.Visible : Visibility.Collapsed;
        AppearanceCard.Visibility = appearance ? Visibility.Visible : Visibility.Collapsed;
        FrameCard.Visibility = frame ? Visibility.Visible : Visibility.Collapsed;
        NetworkCard.Visibility = network ? Visibility.Visible : Visibility.Collapsed;
        FilesCard.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        NoSearchResults.Visibility = desktop || appearance || frame || network || files ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool ApplyRows(IEnumerable<FrameworkElement> rows, string filter, bool empty)
    {
        bool any = false;
        foreach (FrameworkElement row in rows)
        {
            bool visible = empty || Matches(row, filter);
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            any |= visible;
        }
        return any;
    }

    private static bool Matches(FrameworkElement element, string query)
        => element.Tag?.ToString()?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name }) _viewModel.ApplyPreset(name);
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GlobalColorViewModel row }) row.IsPickerOpen = !row.IsPickerOpen;
    }

    private void ResetAppearance_Click(object sender, RoutedEventArgs e) => _viewModel.ResetAppearance();
    private void ResetCollector_Click(object sender, RoutedEventArgs e) => _viewModel.ResetFrameData();
    private void ResetNetwork_Click(object sender, RoutedEventArgs e) => _viewModel.ResetNetwork();

    private async void AutostartSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_autostartBusy) return;
        bool enable = AutostartSwitch.IsChecked == true;
        await ChangeAutostartAsync(enable);
    }

    private async void RepairAutostart_Click(object sender, RoutedEventArgs e)
        => await ChangeAutostartAsync(enable: true);

    private async Task ChangeAutostartAsync(bool enable)
    {
        _autostartBusy = true;
        AutostartSwitch.IsEnabled = false;
        RepairAutostartButton.IsEnabled = false;
        AutostartStatusText.Text = enable ? "Waiting for administrator permission…" : "Removing scheduled tasks…";
        try
        {
            int? exitCode = await AutostartManager.RunElevatedAsync(enable);
            if (exitCode is null)
                ShowInfo("Action cancelled", "Windows left the autostart tasks unchanged.", InfoBarSeverity.Informational);
            else if (exitCode != 0)
                ShowInfo("Autostart change failed", $"Halo.Settings exited with code {exitCode}.", InfoBarSeverity.Error);
        }
        finally
        {
            _autostartBusy = false;
            await RefreshAutostartAsync();
        }
    }

    private async Task RefreshAutostartAsync()
    {
        if (_autostartBusy) return;
        _autostartBusy = true;
        AutostartSwitch.IsEnabled = false;
        RepairAutostartButton.IsEnabled = false;
        AutostartStatus status = await Task.Run(AutostartManager.GetStatus);
        AutostartStatusText.Text = status.Summary;
        AutostartSwitch.IsChecked = status.Enabled;
        AutostartSwitch.IsEnabled = true;
        RepairAutostartButton.IsEnabled = !status.Healthy;
        _autostartBusy = false;
    }

    private async void ExportLayout_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Halo layout",
            Filter = "Halo layout (*.halo-layout)|*.halo-layout",
            DefaultExt = ".halo-layout",
            AddExtension = true,
            FileName = $"Halo-layout-{DateTime.Now:yyyy-MM-dd}",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _config.FlushAllAsync();
            await using var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            await WriteEntryAsync(archive, "settings.json", _config.Settings, ConfigJsonContext.Default.AppSettings);
            await WriteEntryAsync(archive, "widgets.json", _config.Widgets, ConfigJsonContext.Default.WidgetsConfig);
            ShowInfo("Layout exported", dialog.FileName, InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowInfo("Export failed", ex.Message, InfoBarSeverity.Error); }
    }

    private async void ImportLayout_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Halo layout",
            Filter = "Halo layout (*.halo-layout)|*.halo-layout",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        System.Windows.MessageBoxResult confirmation = System.Windows.MessageBox.Show(
            "This replaces settings.json and widgets.json with the selected Halo layout.",
            "Import Halo layout", System.Windows.MessageBoxButton.OKCancel, MessageBoxImage.Warning, System.Windows.MessageBoxResult.Cancel);
        if (confirmation != System.Windows.MessageBoxResult.OK) return;
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(dialog.FileName);
            AppSettings settings = await ReadEntryAsync(archive, "settings.json", ConfigJsonContext.Default.AppSettings);
            WidgetsConfig widgets = await ReadEntryAsync(archive, "widgets.json", ConfigJsonContext.Default.WidgetsConfig);
            _config.QueueSettings("$", target => CopySettings(settings, target), flushImmediately: true);
            _config.QueueWidgets("$", target => CopyWidgets(widgets, target), flushImmediately: true);
            await _config.FlushAllAsync();
            _viewModel.RefreshFromCurrent();
            ShowInfo("Layout imported", "settings.json and widgets.json were updated.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowInfo("Import failed", ex.Message, InfoBarSeverity.Error); }
    }

    private void ResetEverything_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(
            "This resets settings.json and widgets.json to Halo defaults. This cannot be undone.",
            "Reset everything", System.Windows.MessageBoxButton.OKCancel, MessageBoxImage.Warning, System.Windows.MessageBoxResult.Cancel);
        if (result != System.Windows.MessageBoxResult.OK) return;
        _viewModel.ResetEverything();
        ShowInfo("Defaults queued", "Resetting both settings.json and widgets.json.", InfoBarSeverity.Informational);
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Paths.DataDir) { UseShellExecute = true }); }
        catch (Exception ex) { ShowInfo("Could not open data folder", ex.Message, InfoBarSeverity.Error); }
    }

    private void Config_StatusChanged(object? sender, ConfigWriteStatus e)
    {
        if (e.IsError) ShowInfo("Configuration error", e.Message, InfoBarSeverity.Error);
        else if (PageInfoBar.IsOpen && PageInfoBar.Title == "Configuration error") PageInfoBar.IsOpen = false;
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        PageInfoBar.Title = title;
        PageInfoBar.Message = message;
        PageInfoBar.Severity = severity;
        PageInfoBar.IsOpen = true;
    }

    private static async Task<T> ReadEntryAsync<T>(ZipArchive archive, string name,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : class
    {
        ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidDataException($"The layout does not contain {name}.");
        await using Stream stream = entry.Open();
        return await JsonSerializer.DeserializeAsync(stream, typeInfo) ?? throw new InvalidDataException($"{name} is empty.");
    }

    private static async Task WriteEntryAsync<T>(ZipArchive archive, string name, T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using Stream stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, typeInfo);
    }

    private static void CopySettings(AppSettings source, AppSettings target)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.LockAll = source.LockAll;
        target.Snap = source.Snap;
        target.Appearance = source.Appearance;
        target.Collector = source.Collector;
    }

    private static void CopyWidgets(WidgetsConfig source, WidgetsConfig target)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.Widgets = source.Widgets;
    }

    public void Dispose()
    {
        _config.StatusChanged -= Config_StatusChanged;
        _viewModel.Dispose();
    }
}
