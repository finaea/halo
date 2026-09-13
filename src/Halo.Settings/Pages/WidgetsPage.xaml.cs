using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Halo.Settings.Services;
using Halo.Settings.ViewModels;
using Halo.Shared.Panels;

namespace Halo.Settings.Pages;

public partial class WidgetsPage : UserControl, ISettingsPage, ISearchableSettingsPage, IDisposable
{
    private readonly WidgetsPageViewModel _viewModel;
    private readonly DispatcherTimer _collectorTimer;
    private Point _dragStart;
    private string _filter = "";

    public WidgetsPage(LiveConfigService config)
    {
        InitializeComponent();
        _viewModel = new WidgetsPageViewModel(config);
        DataContext = _viewModel;
        _collectorTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => _viewModel.PollCollector(), Dispatcher);
    }

    public void OnEnter()
    {
        _viewModel.PollCollector();
        _collectorTimer.Start();
    }

    public void OnLeave() => _collectorTimer.Stop();

    public void ShowReadyBanner() => ReadyBanner.Visibility = Visibility.Visible;

    public void ApplyFilter(string query)
    {
        _filter = query.Trim();
        ICollectionView view = CollectionViewSource.GetDefaultView(_viewModel.Widgets);
        view.Filter = item => item is WidgetItemViewModel widget &&
            (_filter.Length == 0 || widget.DisplayName.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) ||
             widget.Panel.DisplayName.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) ||
             widget.Type.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
             widget.Options.Any(option => option.Label.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)) ||
             widget.MetricRows.Any(metric => metric.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)));
        view.Refresh();
        if (_viewModel.SelectedWidget is not null && !view.Contains(_viewModel.SelectedWidget))
            _viewModel.SelectedWidget = view.Cast<WidgetItemViewModel>().FirstOrDefault();
    }

    private void AddWidget_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (PanelType panel in PanelCatalog.All)
        {
            var item = new MenuItem { Header = panel.DisplayName, Tag = panel };
            item.Click += (_, _) => _viewModel.AddWidget(panel);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = AddWidgetButton;
        menu.IsOpen = true;
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedWidget is { } row) _viewModel.Duplicate(row);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedWidget is not { } row) return;
        MessageBoxResult result = MessageBox.Show(Window.GetWindow(this),
            $"Remove ‘{row.DisplayName}’ from the desktop layout?", "Remove widget",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (result == MessageBoxResult.OK) _viewModel.Remove(row);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedWidget is { } row) _viewModel.MoveBy(row, -1);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedWidget is { } row) _viewModel.MoveBy(row, 1);
    }

    private void ResetRefresh_Click(object sender, RoutedEventArgs e) => _viewModel.SelectedWidget?.ResetRefreshAndGraphs();
    private void ResetMetrics_Click(object sender, RoutedEventArgs e) => _viewModel.SelectedWidget?.ResetMetrics();
    private void UseGlobal_Click(object sender, RoutedEventArgs e) => _viewModel.SelectedWidget?.UseGlobalForAll();
    private void ResetPlacement_Click(object sender, RoutedEventArgs e) => _viewModel.SelectedWidget?.ResetPlacement();

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: var context }) return;
        if (context is MetricRowViewModel metric) metric.IsPickerOpen = true;
        else if (context is WidgetColorViewModel color) color.IsPickerOpen = true;
    }

    private void WidgetList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragStart = e.GetPosition(WidgetList);

    private void WidgetList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _viewModel.SelectedWidget is null) return;
        Point current = e.GetPosition(WidgetList);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(WidgetList, _viewModel.SelectedWidget, DragDropEffects.Move);
    }

    private void WidgetList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(WidgetItemViewModel)) is not WidgetItemViewModel source) return;
        DependencyObject? origin = e.OriginalSource as DependencyObject;
        ListBoxItem? targetItem = FindAncestor<ListBoxItem>(origin);
        int target = targetItem is null ? _viewModel.Widgets.Count - 1 : WidgetList.ItemContainerGenerator.IndexFromContainer(targetItem);
        _viewModel.Move(source, target);
        WidgetList.SelectedItem = source;
    }

    private void WidgetList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0 || _viewModel.SelectedWidget is not { } row) return;
        if (e.Key == Key.Up) { _viewModel.MoveBy(row, -1); e.Handled = true; }
        else if (e.Key == Key.Down) { _viewModel.MoveBy(row, 1); e.Handled = true; }
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    public void Dispose()
    {
        _collectorTimer.Stop();
        _viewModel.Dispose();
    }
}
