using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Halo.Settings;

/// <summary>A small modal that asks for one name (profile rename, save as new, new default).
/// Built in code: it is a text box and two buttons, and needs no XAML of its own.</summary>
public static class NamePrompt
{
    /// <returns>The trimmed name, or null when cancelled or left blank.</returns>
    public static string? Ask(Window? owner, string title, string message, string initial, string confirm = "OK")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 16), MinWidth = 320 };
        System.Windows.Automation.AutomationProperties.SetName(box, "Profile name");
        Window window = Build(owner, title, message, box, confirm, out Button ok);
        box.TextChanged += (_, _) => ok.IsEnabled = !string.IsNullOrWhiteSpace(box.Text);
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); Keyboard.Focus(box); };
        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }

    /// <returns>The chosen item, or null when cancelled.</returns>
    public static T? Choose<T>(Window? owner, string title, string message, IReadOnlyList<T> items, string displayMember, string confirm)
        where T : class
    {
        var box = new ComboBox { ItemsSource = items, DisplayMemberPath = displayMember, SelectedIndex = 0, Margin = new Thickness(0, 10, 0, 16), MinWidth = 320 };
        System.Windows.Automation.AutomationProperties.SetName(box, "Profile");
        Window window = Build(owner, title, message, box, confirm, out _);
        return window.ShowDialog() == true ? box.SelectedItem as T : null;
    }

    private static Window Build(Window? owner, string title, string message, Control input, string confirm, out Button ok)
    {
        ok = new Button { Content = confirm, IsDefault = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        var window = new Window
        {
            Title = title,
            Owner = owner,
            Content = new StackPanel { Margin = new Thickness(20), Children = { text, input, buttons } },
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
        };
        if (Application.Current?.TryFindResource("HaloPageBackground") is System.Windows.Media.Brush background) window.Background = background;
        ok.Click += (_, _) => window.DialogResult = true;
        return window;
    }
}
