using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace Halo.Settings.Pages;

public partial class ThemePage : UserControl, ISettingsPage
{
    // Ordered token defaults — a hardcoded copy of Halo.Widgets/Theme.cs (extracted Rainformer
    // values). We don't reference Halo.Widgets (it pulls in Vortice); the tokens are plain data.
    private static readonly (string Token, int R, int G, int B, int A)[] Defaults =
    {
        ("title", 0, 0, 0, 255),
        ("activeTitle", 255, 128, 0, 255),
        ("text", 0, 0, 0, 205),
        ("text2", 60, 65, 62, 205),
        ("bar", 93, 141, 172, 255),
        ("histogram", 176, 196, 222, 250),
        ("netDown", 51, 153, 255, 205),
        ("netUp", 51, 255, 0, 205),
        ("red", 204, 0, 0, 255),
        ("redText", 204, 0, 0, 205),
        ("emptyBar", 255, 255, 255, 25),
        ("bgTop", 163, 178, 230, 180),
        ("bgBody", 230, 230, 230, 180),
        ("stroke", 96, 138, 203, 100),
        ("solidLabel", 248, 248, 248, 255),
        ("inactiveButton", 120, 120, 120, 255),
        ("barWarn", 220, 20, 60, 255),
        ("cpuTemp", 204, 0, 0, 255),
        ("cpuUsage", 176, 196, 222, 255),
        ("ramUsage", 102, 204, 0, 255),
        ("gpuTemp", 204, 0, 0, 255),
        ("gpuUsage", 176, 196, 222, 255),
        ("gpuMemUsage", 102, 204, 0, 255),
        ("gpuFan", 0, 191, 255, 255),
        ("maxValue", 178, 190, 181, 205),
        ("maxLabelGray", 120, 120, 120, 255),
        ("devWarn1", 47, 186, 255, 255),
        ("devWarn2", 255, 255, 36, 255),
        ("devWarn3", 255, 143, 30, 255),
        ("devWarn4", 255, 0, 0, 255),
        ("devWarn5", 204, 0, 0, 255),
        ("horizLine", 80, 80, 80, 255),
        ("staleBadge", 255, 80, 80, 220),
    };

    private const double DefaultScale = 1.7;

    private static readonly Dictionary<string, string> TokenDesc = new()
    {
        ["title"] = "Panel title text",
        ["activeTitle"] = "Highlighted / active title text",
        ["text"] = "Primary text",
        ["text2"] = "Secondary text",
        ["bar"] = "Bar fill",
        ["histogram"] = "Graph line & fill",
        ["netDown"] = "Network download",
        ["netUp"] = "Network upload",
        ["red"] = "Alert accent",
        ["redText"] = "Alert text",
        ["emptyBar"] = "Bar background (empty part)",
        ["bgTop"] = "Panel header background",
        ["bgBody"] = "Panel body background",
        ["stroke"] = "Panel border",
        ["solidLabel"] = "Solid label text",
        ["inactiveButton"] = "Inactive button glyphs",
        ["barWarn"] = "Bar color when warning",
        ["cpuTemp"] = "CPU temperature graph",
        ["cpuUsage"] = "CPU usage graph",
        ["ramUsage"] = "RAM usage graph",
        ["gpuTemp"] = "GPU temperature graph",
        ["gpuUsage"] = "GPU usage graph",
        ["gpuMemUsage"] = "GPU memory graph",
        ["gpuFan"] = "GPU fan graph",
        ["maxValue"] = "Session-max readouts",
        ["maxLabelGray"] = "Session-max labels",
        ["devWarn1"] = "Staged warning 1 (coolest)",
        ["devWarn2"] = "Staged warning 2",
        ["devWarn3"] = "Staged warning 3",
        ["devWarn4"] = "Staged warning 4",
        ["devWarn5"] = "Staged warning 5 (critical)",
        ["horizLine"] = "Separator lines",
        ["staleBadge"] = "Stale-data badge",
    };

    private readonly ObservableCollection<ColorRow> _rows = new();

    public ThemePage()
    {
        InitializeComponent();
        ColorGrid.ItemsSource = _rows;
    }

    private static string ThemePath => Path.Combine(ProjectPaths.ConfigDir, "theme.json");

    public void OnEnter()
    {
        double scale = DefaultScale;
        var overrides = new Dictionary<string, int[]>();
        Status.Text = "";

        try
        {
            if (File.Exists(ThemePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ThemePath));
                var root = doc.RootElement;
                if (root.TryGetProperty("scale", out var s) && s.ValueKind == JsonValueKind.Number)
                    scale = s.GetDouble();
                if (root.TryGetProperty("colors", out var colors) && colors.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in colors.EnumerateObject())
                    {
                        var a = p.Value;
                        if (a.ValueKind == JsonValueKind.Array && a.GetArrayLength() >= 3)
                        {
                            int al = a.GetArrayLength() >= 4 ? a[3].GetInt32() : 255;
                            overrides[p.Name] = new[] { a[0].GetInt32(), a[1].GetInt32(), a[2].GetInt32(), al };
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Status.Text = "theme.json parse warning (showing defaults): " + ex.Message; }

        ScaleBox.Text = scale.ToString(CultureInfo.InvariantCulture);
        _rows.Clear();
        foreach (var d in Defaults)
        {
            var v = overrides.TryGetValue(d.Token, out var o) ? o : new[] { d.R, d.G, d.B, d.A };
            _rows.Add(new ColorRow(d.Token, v[0], v[1], v[2], v[3], TokenDesc.GetValueOrDefault(d.Token, "")));
        }
        // Preserve any custom tokens present in the file that we don't know about.
        foreach (var kv in overrides)
            if (!Defaults.Any(x => x.Token == kv.Key))
                _rows.Add(new ColorRow(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
    }

    public void OnLeave() { }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        double scale = double.TryParse(ScaleBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double sc)
            ? sc : DefaultScale;

        var root = new JsonObject { ["scale"] = scale };
        var colors = new JsonObject();
        foreach (var r in _rows)
            colors[r.Token] = new JsonArray(r.R, r.G, r.B, r.A);
        root["colors"] = colors;

        try
        {
            Directory.CreateDirectory(ProjectPaths.ConfigDir);
            File.WriteAllText(ThemePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Status.Text = $"Saved theme.json at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex) { Status.Text = "Save failed: " + ex.Message; }
    }
}
