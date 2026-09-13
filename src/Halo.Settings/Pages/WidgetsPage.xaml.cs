using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Halo.Settings.Services;
using Halo.Settings.ViewModels;
using Halo.Shared.Panels;

namespace Halo.Settings.Pages;

public partial class WidgetsPage : UserControl, ISettingsPage, IDisposable
{
    /// <summary>Where the user last put the splitter. Halo has no window/UI-state store, so this
    /// is remembered for the life of the process only and resets on restart.</summary>
    private static double _listColumnWidth = 236;

    private readonly WidgetsPageViewModel _viewModel;
    private readonly DispatcherTimer _collectorTimer;
    private Point _dragStart;

    public WidgetsPage(LiveConfigService config)
    {
        InitializeComponent();
        ListColumn.Width = new GridLength(_listColumnWidth);
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

    private void ListSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        => _listColumnWidth = ListColumn.ActualWidth;

    private void WidgetList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragStart = e.GetPosition(WidgetList);

    private void WidgetList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _viewModel.SelectedWidget is null) return;
        Point current = e.GetPosition(WidgetList);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(WidgetList, _viewModel.SelectedWidget, DragDropEffects.Move);
        HideDropLine();
    }

    private void WidgetList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(WidgetItemViewModel)) is WidgetItemViewModel)
        {
            e.Effects = DragDropEffects.Move;
            ShowDropLine(DropGap(e.GetPosition(WidgetList)).Y);
        }
        else
        {
            e.Effects = DragDropEffects.None;
            HideDropLine();
        }
        e.Handled = true;
    }

    private void WidgetList_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also bubbles up from each row as the pointer crosses between them, which would
        // make the line blink; only clear it when the pointer has really left the list.
        Point pointer = e.GetPosition(WidgetList);
        if (pointer.X < 0 || pointer.Y < 0 || pointer.X > WidgetList.ActualWidth || pointer.Y > WidgetList.ActualHeight)
            HideDropLine();
    }

    private void WidgetList_Drop(object sender, DragEventArgs e)
    {
        HideDropLine();
        if (e.Data.GetData(typeof(WidgetItemViewModel)) is not WidgetItemViewModel source) return;
        int from = _viewModel.Widgets.IndexOf(source);
        if (from < 0) return;
        int gap = ModelIndexOfGap(DropGap(e.GetPosition(WidgetList)).Index);
        // The gap is where the row goes *between*; Move wants the index it ends up *at*, which is
        // one lower whenever the row is travelling down the list past its own slot.
        _viewModel.Move(source, gap > from ? gap - 1 : gap);
        WidgetList.SelectedItem = source;
    }

    /// <summary>The gap the pointer is hovering: the view index the dragged row would land in front
    /// of, and that gap's y in <c>WidgetList</c> coordinates. Rows split at their own midpoint, so
    /// the lower half of a row means "after it".</summary>
    private (int Index, double Y) DropGap(Point pointer)
    {
        double lastEdge = 0;
        for (int index = 0; index < WidgetList.Items.Count; index++)
        {
            if (WidgetList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container ||
                !container.IsVisible) continue;
            double top = container.TranslatePoint(new Point(0, 0), WidgetList).Y;
            lastEdge = top + container.ActualHeight;
            if (pointer.Y < top + container.ActualHeight / 2) return (index, top);
            if (pointer.Y < lastEdge) return (index + 1, lastEdge);
        }
        return (WidgetList.Items.Count, lastEdge);
    }

    /// <summary>Translate a gap in the (possibly filtered) list view into an index into Widgets.</summary>
    private int ModelIndexOfGap(int viewIndex)
        => viewIndex < WidgetList.Items.Count && WidgetList.Items[viewIndex] is WidgetItemViewModel next
            ? _viewModel.Widgets.IndexOf(next)
            : _viewModel.Widgets.Count;

    private void ShowDropLine(double y)
    {
        Canvas.SetTop(DropLine, Math.Max(0, y - 1));
        DropLine.Visibility = Visibility.Visible;
    }

    private void HideDropLine() => DropLine.Visibility = Visibility.Collapsed;

    private void WidgetList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0 || _viewModel.SelectedWidget is not { } row) return;
        if (e.Key == Key.Up) { _viewModel.MoveBy(row, -1); e.Handled = true; }
        else if (e.Key == Key.Down) { _viewModel.MoveBy(row, 1); e.Handled = true; }
    }

    public void Dispose()
    {
        _collectorTimer.Stop();
        _viewModel.Dispose();
    }
}
