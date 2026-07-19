using System.ComponentModel;
using System.Windows.Media;

namespace Halo.Settings;

/// <summary>Editable key/value row used by the fan grids (both values stored as text).</summary>
public sealed class StringPair
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>One theme color token with live-updating swatch preview.</summary>
public sealed class ColorRow : INotifyPropertyChanged
{
    public string Token { get; }
    public string Desc { get; }

    private int _r, _g, _b, _a;

    public ColorRow(string token, int r, int g, int b, int a, string desc = "")
    {
        Token = token;
        Desc = desc;
        _r = Clamp(r); _g = Clamp(g); _b = Clamp(b); _a = Clamp(a);
    }

    public int R { get => _r; set { _r = Clamp(value); On(nameof(R)); On(nameof(Swatch)); } }
    public int G { get => _g; set { _g = Clamp(value); On(nameof(G)); On(nameof(Swatch)); } }
    public int B { get => _b; set { _b = Clamp(value); On(nameof(B)); On(nameof(Swatch)); } }
    public int A { get => _a; set { _a = Clamp(value); On(nameof(A)); On(nameof(Swatch)); } }

    public Brush Swatch => new SolidColorBrush(Color.FromArgb((byte)_a, (byte)_r, (byte)_g, (byte)_b));

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>One live metric row (static columns fixed; Value/Age refresh every 2 s).</summary>
public sealed class MetricRow : INotifyPropertyChanged
{
    public int Index { get; }
    public string Name { get; }
    public string Type { get; }
    public string Unit { get; }
    public string Rate { get; }
    public bool IsString { get; }

    public MetricRow(int index, string name, string type, string unit, string rate, bool isString)
    {
        Index = index; Name = name; Type = type; Unit = unit; Rate = rate; IsString = isString;
    }

    private string _value = "";
    public string Value { get => _value; set { if (_value != value) { _value = value; On(nameof(Value)); } } }

    private string _age = "";
    public string Age { get => _age; set { if (_age != value) { _age = value; On(nameof(Age)); } } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
