using System.Windows.Media;
using Halo.Shared.Panels;

namespace Halo.Settings.ViewModels;

public sealed class GlobalColorViewModel : ObservableObject
{
    private readonly Action<string, string> _changed;
    private bool _applying;
    private Color _color;
    private bool _isPickerOpen;

    public string Token { get; }
    public string Label { get; }
    public string Description { get; }
    public string Path => $"appearance.colors.{Token}";

    public Color Color
    {
        get => _color;
        set
        {
            if (!Set(ref _color, value)) return;
            Raise(nameof(Hex));
            Raise(nameof(Swatch));
            if (!_applying) _changed(Token, Hex);
        }
    }

    public string Hex
    {
        get => ThemeTokens.ToHex(_color.R, _color.G, _color.B, _color.A);
        set
        {
            if (!ThemeTokens.TryParse(value, out byte r, out byte g, out byte b, out byte a))
            {
                Raise(nameof(Hex));
                return;
            }
            Color = Color.FromArgb(a, r, g, b);
        }
    }

    public Brush Swatch => new SolidColorBrush(Color);

    public bool IsPickerOpen
    {
        get => _isPickerOpen;
        set => Set(ref _isPickerOpen, value);
    }

    public GlobalColorViewModel(string token, string label, string description, string hex, Action<string, string> changed)
    {
        Token = token;
        Label = label;
        Description = description;
        _changed = changed;
        _color = Parse(hex);
    }

    public void ApplyExternal(string hex)
    {
        _applying = true;
        try { Color = Parse(hex); }
        finally { _applying = false; }
    }

    private static Color Parse(string hex)
    {
        if (!ThemeTokens.TryParse(hex, out byte r, out byte g, out byte b, out byte a))
            return Colors.Transparent;
        return Color.FromArgb(a, r, g, b);
    }
}
