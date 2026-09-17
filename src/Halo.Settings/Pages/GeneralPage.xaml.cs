using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Halo.Settings.Services;
using Halo.Settings.ViewModels;
using Halo.Shared;
using Halo.Shared.Config;
using Microsoft.Win32;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace Halo.Settings.Pages;

public partial class GeneralPage : UserControl, ISettingsPage, IDisposable
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

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string name }) _viewModel.ApplyPreset(name);
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GlobalColorViewModel row }) row.IsPickerOpen = !row.IsPickerOpen;
    }

    private void ResetAppearance_Click(object sender, RoutedEventArgs e) => _viewModel.ResetAppearance();

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
            ElevatedOutcome outcome = await AutostartManager.RunElevatedAsync(enable);
            if (outcome.ExitCode is null)
                ShowInfo("Action cancelled", "Windows left the autostart tasks unchanged.", InfoBarSeverity.Informational);
            else if (outcome.ExitCode != 0)
                // Outcome.Detail is what the elevated child wrote to its own log: its stderr cannot
                // be piped back through "runas", so an exit code was all this dialog used to have.
                ShowInfo("Autostart change failed",
                    outcome.Detail.Length > 0
                        ? $"Halo.Settings exited with code {outcome.ExitCode}. {outcome.Detail}"
                        : $"Halo.Settings exited with code {outcome.ExitCode}.",
                    InfoBarSeverity.Error);
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
        bool payloadsPresent = AutostartManager.HasTaskPayloads;
        AutostartSwitch.IsEnabled = payloadsPresent;
        RepairAutostartButton.IsEnabled = payloadsPresent && !status.Healthy;
        AutostartSwitch.ToolTip = payloadsPresent ? null : AutostartManager.MissingPayloadMessage;
        // Same label as the System check page, from the same property: this button sits directly
        // under the autostart toggle, so leaving it reading "Repair" while the switch reads "off"
        // is the worst version of the mismatch — the switch says nothing is wrong, the button says
        // something is.
        RepairAutostartButton.Content = status.ActionLabel;
        RepairAutostartButton.ToolTip = !payloadsPresent ? AutostartManager.MissingPayloadMessage
            : status.Off
                ? "Registers both scheduled tasks so Halo starts at logon, and asks Windows for administrator permission."
                : "Recreates both scheduled tasks and asks Windows for administrator permission.";
        if (!payloadsPresent) AutostartStatusText.Text = "Autostart can be changed from an installed or published Halo folder.";
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
        target.Arrange = source.Arrange;
    }

    public void Dispose()
    {
        _config.StatusChanged -= Config_StatusChanged;
        _viewModel.Dispose();
    }
}
