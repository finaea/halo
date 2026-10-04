using System.Windows;
using System.Windows.Controls;
using Halo.Settings.Services;
using Halo.Settings.ViewModels;

namespace Halo.Settings.Pages;

public partial class AppearancePage : UserControl, ISettingsPage, IDisposable
{
    private readonly AppearanceViewModel _viewModel;

    public AppearancePage(LiveConfigService config)
    {
        _viewModel = new AppearanceViewModel(config);
        InitializeComponent();
        DataContext = _viewModel;
    }

    public void OnEnter() => _viewModel.Refresh();

    public void OnLeave() { }

    private void Skin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SkinCardViewModel card }) _viewModel.SelectSkin(card.Id);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PresetChipViewModel chip })
        {
            _viewModel.SelectPreset(chip.Id);
            // Clicking the active chip changes nothing, but a ToggleButton unchecks itself on click;
            // re-assert the bound state so the chip never reads as off.
            chip.IsSelected = false;
            chip.IsSelected = true;
        }
    }

    private void ResetToPreset_Click(object sender, RoutedEventArgs e) => _viewModel.ResetToPreset();

    private void ResetAppearance_Click(object sender, RoutedEventArgs e) => _viewModel.ResetAppearance();

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlobalColorViewModel row }) row.IsPickerOpen = !row.IsPickerOpen;
    }

    private void ResetColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlobalColorViewModel row }) row.ResetToPreset();
    }

    public void Dispose() => _viewModel.Dispose();
}
