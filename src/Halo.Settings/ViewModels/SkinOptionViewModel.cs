using System.Collections.ObjectModel;
using System.Globalization;
using Halo.Shared.Panels;

namespace Halo.Settings.ViewModels;

/// <summary>
/// One of a skin's own options (its <c>SkinInfo.Options</c>), generated from the spec so a new skin
/// needs no XAML. Used twice: on the Appearance page, where a value equal to the inherited one is
/// stored as "no override"; and per widget, with a "Use global" box like every other widget
/// override. <paramref name="save"/> gets the value to store, or null to store nothing.
/// </summary>
public sealed class SkinOptionViewModel : ObservableObject
{
    private readonly OptionSpec _spec;
    private readonly Action<string, string?> _save;
    private readonly bool _canInherit;
    private string _inherited;
    private bool _applying;
    private bool _useGlobal;
    private double _numberValue;
    private bool _boolValue;
    private ChoiceItem? _selectedChoice;

    public string Key => _spec.Key;
    public string Label => _spec.Label;
    public string Help => _spec.Help;
    public bool IsNumber => _spec.Kind is OptionKind.Int or OptionKind.Double;
    public bool IsBoolean => _spec.Kind == OptionKind.Bool;
    public bool IsChoice => _spec.Kind == OptionKind.Enum;
    public double Minimum { get; }
    public double Maximum { get; }
    public int Decimals => _spec.Kind == OptionKind.Int ? 0 : 1;
    public ObservableCollection<ChoiceItem> Choices { get; } = [];

    /// <summary>Only the per-widget editor shows the "Use global" box.</summary>
    public bool CanInherit => _canInherit;
    public bool IsEnabled => !UseGlobal;
    public string InheritedHint => $"Inherited: {Display(_inherited)}";

    public bool UseGlobal
    {
        get => _useGlobal;
        set
        {
            if (!Set(ref _useGlobal, value)) return;
            Raise(nameof(IsEnabled));
            if (_applying) return;
            if (value) Show(_inherited);
            _save(Key, value ? null : Current);
        }
    }

    public double NumberValue
    {
        get => _numberValue;
        set
        {
            value = Math.Round(Math.Clamp(value, Minimum, Maximum), Decimals);
            if (Set(ref _numberValue, value)) Changed();
        }
    }

    public bool BoolValue
    {
        get => _boolValue;
        set { if (Set(ref _boolValue, value)) Changed(); }
    }

    public ChoiceItem? SelectedChoice
    {
        get => _selectedChoice;
        set { if (Set(ref _selectedChoice, value) && value is not null) Changed(); }
    }

    public SkinOptionViewModel(OptionSpec spec, Action<string, string?> save, bool canInherit)
    {
        _spec = spec;
        _save = save;
        _canInherit = canInherit;
        _inherited = spec.Default;
        (Minimum, Maximum) = OptionEditorViewModel.ParseRange(spec.Range);
        foreach (string choice in spec.Choices ?? []) Choices.Add(new ChoiceItem(choice, choice));
    }

    /// <summary>Show <paramref name="own"/> (this level's stored value, null = none) over
    /// <paramref name="inherited"/> (what applies without it), writing nothing.</summary>
    public void Load(string? own, string inherited)
    {
        _applying = true;
        try
        {
            _inherited = inherited;
            // Without an inherit box there is nothing to untick, so the row must never sit in the
            // inheriting state: Changed() ignores edits while it does.
            UseGlobal = _canInherit && own is null;
            Show(own ?? inherited);
            Raise(nameof(InheritedHint));
        }
        finally { _applying = false; }
    }

    private string Current => IsBoolean ? (BoolValue ? "true" : "false")
        : IsChoice ? SelectedChoice?.Value ?? _spec.Default
        : NumberValue.ToString(CultureInfo.InvariantCulture);

    private void Changed()
    {
        if (_applying || UseGlobal) return;
        // Without an inherit box, a value equal to what is inherited is no override at all, so
        // picking the preset's own value leaves a clean "preset, no tweaks" config behind.
        _save(Key, !_canInherit && Current == Normalise(_inherited) ? null : Current);
    }

    private void Show(string value)
    {
        bool applying = _applying;
        _applying = true;
        try
        {
            NumberValue = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : Minimum;
            BoolValue = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            SelectedChoice = Choices.FirstOrDefault(c => c.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? Choices.FirstOrDefault();
        }
        finally { _applying = applying; }
    }

    /// <summary>The inherited value as <see cref="Current"/> would spell it ("4" vs "4.0").</summary>
    private string Normalise(string value) => IsNumber && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
        ? Math.Round(d, Decimals).ToString(CultureInfo.InvariantCulture) : value;

    private string Display(string value) => IsBoolean ? (value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "on" : "off") : value;
}
