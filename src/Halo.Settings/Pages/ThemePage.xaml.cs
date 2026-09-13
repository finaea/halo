using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Halo.Shared.Config;
using Halo.Shared.Panels;

namespace Halo.Settings.Pages;

/// <summary>
/// Global colour tokens. In schema v2 these live in settings.json under appearance.colors as
/// #RRGGBBAA strings — theme.json is gone — and the token list comes from
/// <see cref="ThemeTokens"/> in Halo.Shared, so Settings and the renderer can no longer drift
/// apart (settings plan S1/S4). Only tokens the user actually changed are written back.
/// </summary>
public partial class ThemePage : UserControl, ISettingsPage
{
    private readonly ConfigStore _store;
    private readonly ObservableCollection<ColorRow> _rows = new();

    public ThemePage(ConfigStore store)
    {
        _store = store;
        InitializeComponent();
        ColorGrid.ItemsSource = _rows;
    }

    public void OnEnter()
    {
        _store.Reload();
        var colors = _store.Settings.Appearance.Colors;
        Status.Text = "";

        _rows.Clear();
        foreach (var (token, defaultHex, desc) in ThemeTokens.Defaults)
        {
            string hex = colors.TryGetValue(token, out string? custom) && custom.Length > 0 ? custom : defaultHex;
            if (!ThemeTokens.TryParse(hex, out byte r, out byte g, out byte b, out byte a))
                ThemeTokens.TryParse(defaultHex, out r, out g, out b, out a);
            _rows.Add(new ColorRow(token, r, g, b, a, desc));
        }
    }

    public void OnLeave() { }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var colors = _store.Settings.Appearance.Colors;
        foreach (var row in _rows)
        {
            string hex = ThemeTokens.ToHex((byte)row.R, (byte)row.G, (byte)row.B, (byte)row.A);
            string? def = ThemeTokens.Default(row.Token);
            // keep the file to real overrides; a token back at its default is just noise
            if (string.Equals(hex, def, StringComparison.OrdinalIgnoreCase)) colors.Remove(row.Token);
            else colors[row.Token] = hex;
        }

        try
        {
            _store.SaveSettings();
            Status.Text = $"Saved at {DateTime.Now:HH:mm:ss} — widgets recolour live.";
        }
        catch (Exception ex) { Status.Text = "Save failed: " + ex.Message; }
    }
}
