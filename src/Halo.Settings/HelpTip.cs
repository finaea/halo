using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Halo.Settings;

/// <summary>
/// Small circled "?" badge that explains a setting. Hover (or click) shows the text.
/// Usage: &lt;local:HelpTip Tip="What this setting does."/&gt;
/// </summary>
public sealed class HelpTip : Border
{
    public static readonly DependencyProperty TipProperty = DependencyProperty.Register(
        nameof(Tip), typeof(string), typeof(HelpTip),
        new PropertyMetadata("", (d, _) => ((HelpTip)d).RebuildToolTip()));

    public string Tip { get => (string)GetValue(TipProperty); set => SetValue(TipProperty, value); }

    public HelpTip()
    {
        Width = 15;
        Height = 15;
        CornerRadius = new CornerRadius(7.5);
        Margin = new Thickness(7, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Background = new SolidColorBrush(Color.FromRgb(0x41, 0x41, 0x48));
        Cursor = Cursors.Help;
        SnapsToDevicePixels = true;
        Child = new TextBlock
        {
            Text = "?",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xD4)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        };
        ToolTipService.SetInitialShowDelay(this, 120);
        ToolTipService.SetShowDuration(this, 120000);
        MouseLeftButtonUp += (_, _) => { if (ToolTip is ToolTip t) t.IsOpen = !t.IsOpen; };
    }

    private void RebuildToolTip()
    {
        ToolTip = new ToolTip
        {
            Content = new TextBlock { Text = Tip, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
        };
    }
}
