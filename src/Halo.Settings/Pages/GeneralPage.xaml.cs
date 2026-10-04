using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private readonly ProfileService _profiles;
    private bool _autostartBusy;
    private bool _profileBusy;
    private bool _refreshingProfiles;

    public GeneralPage(LiveConfigService config, ProfileService profiles)
    {
        _config = config;
        _profiles = profiles;
        _viewModel = new GeneralViewModel(config);
        InitializeComponent();
        DataContext = _viewModel;
        _config.StatusChanged += Config_StatusChanged;
    }

    public void OnEnter()
    {
        _viewModel.RefreshFromCurrent();
        RefreshProfiles();
        _ = RefreshAutostartAsync();
    }

    public void OnLeave() { }

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

    public sealed record ProfileItem(string Id, string Name, bool Active)
    {
        public string Label => Active ? $"{Name}  (active)" : Name;
    }

    /// <summary>Rebuild the picker from the folder. The selection is always the active profile.</summary>
    private void RefreshProfiles()
    {
        _refreshingProfiles = true;
        try
        {
            string? active = _profiles.ActiveId;
            List<ProfileItem> items = _profiles.Store.List().Select(e => new ProfileItem(e.Id, e.Name, e.Id == active)).ToList();
            ProfileBox.ItemsSource = items;
            ProfileBox.SelectedItem = items.FirstOrDefault(i => i.Active);
            DeleteProfileButton.IsEnabled = items.Count > 1;
            DeleteProfileButton.ToolTip = items.Count > 1 ? "Deletes a profile other than the active one." : "The last profile cannot be deleted.";
        }
        finally { _refreshingProfiles = false; }
    }

    private List<ProfileItem> ProfileItems => (ProfileBox.ItemsSource as IEnumerable<ProfileItem>)?.ToList() ?? [];

    private ProfileItem? ActiveItem => ProfileItems.FirstOrDefault(i => i.Active);

    private async Task RunProfileActionAsync(Func<Task<string>> action)
    {
        if (_profileBusy) return;
        _profileBusy = true;
        IsEnabled = false;
        try { ShowInfo(await action(), "", InfoBarSeverity.Success); }
        catch (Exception ex) { ShowInfo("Profile action failed", ex.Message, InfoBarSeverity.Error); }
        finally
        {
            IsEnabled = true;
            _profileBusy = false;
            RefreshProfiles();
            _viewModel.RefreshFromCurrent();
        }
    }

    private async void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingProfiles || ProfileBox.SelectedItem is not ProfileItem { Active: false } target) return;
        await RunProfileActionAsync(async () =>
        {
            await _profiles.SelectAsync(target.Id);
            return $"Switched to {target.Name}";
        });
    }

    private async void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveItem is not { } item) return;
        string? name = NamePrompt.Ask(Window.GetWindow(this), "Rename profile", "A new name for this profile:", item.Name, "Rename");
        if (name == null || name == item.Name) return;
        await RunProfileActionAsync(() => Task.FromResult($"Renamed to {_profiles.Store.Rename(item.Id, name)}"));
    }

    private async void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveItem is not { } item) return;
        await RunProfileActionAsync(async () =>
        {
            string id = await _profiles.DuplicateAsync(item.Id);
            return $"Copied as {_profiles.Store.Load(id)?.Name}";
        });
    }

    private async void SaveAsNewProfile_Click(object sender, RoutedEventArgs e)
    {
        string? name = NamePrompt.Ask(Window.GetWindow(this), "Save as new profile",
            "The widgets and look on screen now become a new profile, which then becomes the active one.",
            _profiles.Store.UniqueName((ActiveItem?.Name ?? ProfileStore.DefaultName) + " copy"), "Save");
        if (name == null) return;
        await RunProfileActionAsync(async () =>
        {
            string id = await _profiles.SaveAsNewAsync(name);
            return $"Saved as {_profiles.Store.Load(id)?.Name}";
        });
    }

    private async void NewDefaultProfile_Click(object sender, RoutedEventArgs e)
    {
        DefaultLayoutPlan plan = await Task.Run(ProfileService.CreateDefaultLayout);
        string? name = NamePrompt.Ask(Window.GetWindow(this), "New default profile",
            $"A new profile with Halo's default look. {plan.Message} The current profile is kept as it is now.",
            _profiles.Store.UniqueName("New profile"), "Create");
        if (name == null) return;
        await RunProfileActionAsync(async () =>
        {
            string id = await _profiles.NewDefaultAsync(name, plan);
            return $"Created and switched to {_profiles.Store.Load(id)?.Name}";
        });
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        List<ProfileItem> others = ProfileItems.Where(i => !i.Active).ToList();
        if (others.Count == 0) { ShowInfo("Nothing to delete", "The last profile cannot be deleted.", InfoBarSeverity.Informational); return; }
        // The active profile cannot be deleted, so the picker's own selection is never the target:
        // the others are offered in a list of their own.
        ProfileItem? target = NamePrompt.Choose(Window.GetWindow(this), "Delete profile",
            "The profile to delete. The active one cannot be deleted, so it is not in this list.", others, nameof(ProfileItem.Name), "Next");
        if (target == null) return;
        if (System.Windows.MessageBox.Show($"Delete the profile \"{target.Name}\"? This cannot be undone.", "Delete profile",
                System.Windows.MessageBoxButton.OKCancel, MessageBoxImage.Warning, System.Windows.MessageBoxResult.Cancel)
            != System.Windows.MessageBoxResult.OK) return;
        await RunProfileActionAsync(() =>
        {
            _profiles.Store.Delete(target.Id, _profiles.ActiveId);
            return Task.FromResult($"Deleted {target.Name}");
        });
    }

    private async void ResetProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveItem is not { } item) return;
        DefaultLayoutPlan plan = await Task.Run(ProfileService.CreateDefaultLayout);
        if (System.Windows.MessageBox.Show(
                $"This gives \"{item.Name}\" Halo's default look and replaces its widgets with a default set. {plan.Message} Collector and logging settings stay as they are. This cannot be undone.",
                "Reset to default", System.Windows.MessageBoxButton.OKCancel, MessageBoxImage.Warning, System.Windows.MessageBoxResult.Cancel)
            != System.Windows.MessageBoxResult.OK) return;
        await RunProfileActionAsync(async () =>
        {
            await _profiles.ResetActiveAsync(plan);
            return $"{item.Name} was reset to default";
        });
    }

    private async void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveItem is not { } item) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Halo profile",
            Filter = $"Halo profile (*{ProfileStore.Extension})|*{ProfileStore.Extension}",
            DefaultExt = ProfileStore.Extension,
            AddExtension = true,
            FileName = string.Concat(item.Name.Split(Path.GetInvalidFileNameChars())),
        };
        if (dialog.ShowDialog() != true) return;
        await RunProfileActionAsync(async () =>
        {
            await _profiles.ExportAsync(item.Id, dialog.FileName);
            return $"Exported to {dialog.FileName}";
        });
    }

    private async void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Halo profile",
            Filter = $"Halo profiles (*{ProfileStore.Extension};*{ProfileStore.LegacyExtension})|*{ProfileStore.Extension};*{ProfileStore.LegacyExtension}",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        await RunProfileActionAsync(() =>
        {
            string id = _profiles.Store.Import(dialog.FileName, MonitorList.Get().Select(m => m.Device));
            return Task.FromResult($"Imported as {_profiles.Store.Load(id)?.Name}. Select it in the list to use it.");
        });
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

    public void Dispose()
    {
        _config.StatusChanged -= Config_StatusChanged;
        _viewModel.Dispose();
    }
}
