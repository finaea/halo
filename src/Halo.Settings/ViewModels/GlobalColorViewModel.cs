using System.Windows.Media;
using Halo.Shared.Panels;

namespace Halo.Settings.ViewModels;

/// <summary>One global colour on the Appearance page: the effective value (preset + tweak) and,
/// beside it, the preset's own value as a ghost, so a tweak can be seen and undone row by row.</summary>
public sealed class GlobalColorViewModel : ObservableObject
{
    private readonly Action<string, string> _changed;
    private bool _applying;
    private Color _color;
    private string _presetHex = "#00000000";
    private bool _isPickerOpen;

    public string Token { get; }
    public string Label { get; }
    public string Description { get; }

    public Color Color
    {
        get => _color;
        set
        {
            if (!Set(ref _color, value)) return;
            Raise(nameof(Hex));
            Raise(nameof(Swatch));
            Raise(nameof(IsTweaked));
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

    /// <summary>What the active preset says for this token.</summary>
    public string PresetHex
    {
        get => _presetHex;
        set
        {
            if (!Set(ref _presetHex, value)) return;
            Raise(nameof(PresetSwatch));
            Raise(nameof(IsTweaked));
        }
    }

    public Brush PresetSwatch => new SolidColorBrush(Parse(PresetHex));
    public bool IsTweaked => !string.Equals(Hex, PresetHex, StringComparison.OrdinalIgnoreCase);

    public bool IsPickerOpen
    {
        get => _isPickerOpen;
        set => Set(ref _isPickerOpen, value);
    }

    public GlobalColorViewModel(string token, string label, string description, Action<string, string> changed)
    {
        Token = token;
        Label = label;
        Description = description;
        _changed = changed;
    }

    /// <summary>Show a value without writing it back.</summary>
    public void ApplyExternal(string hex, string presetHex)
    {
        _applying = true;
        try
        {
            PresetHex = presetHex;
            Color = Parse(hex);
        }
        finally { _applying = false; }
    }

    /// <summary>Undo this row's tweak: back to the preset value, written like any edit.</summary>
    public void ResetToPreset() => Hex = PresetHex;

    private static Color Parse(string hex)
    {
        if (!ThemeTokens.TryParse(hex, out byte r, out byte g, out byte b, out byte a))
            return Colors.Transparent;
        return Color.FromArgb(a, r, g, b);
    }
}

/// <summary>Colours of one <c>TokenSpec.Group</c> (Surfaces, Text, Data, Warnings, a skin's extras).</summary>
public sealed record ColorGroupViewModel<T>(string Name, IReadOnlyList<T> Rows);
