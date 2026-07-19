using System.Text.Json;
using Vortice.Mathematics;

namespace Halo.Widgets;

/// <summary>
/// Visual tokens extracted from the Rainformer skin (@Resources\Variables.inc, light set — the
/// values the skins actually reference; reference copy in the user's Rainmeter Skins folder).
/// Overridable via config\theme.json; values are facts extracted from the user's own config,
/// no GPL code (plan §9.2).
/// </summary>
public sealed class Theme
{
    public double Scale = 1.7;
    public string FontFamily = "Trebuchet MS";
    public double TextSizePt = 8;
    public double TitleSizePt = 9;

    // panel geometry (logical units, pre-scale)
    public double BgOffset = 5;
    public double BgShapeW = 196;
    public double BgWidth = 206;          // BgShapeW + 2*BgOffset
    public double AbsMargin = 3;
    public double TopMargin = 28;
    public double BottomMargin = 3;
    public double RowSpacing = 1;
    public double CornerRadius = 4;
    public double StrokeWidth = 0;
    public double TitleZoneH = 21;        // top rounded band height

    // derived
    public double ContentMargin => AbsMargin + BgOffset - 1;            // 7
    public double RightAlign => BgWidth - (AbsMargin + BgOffset) - 1;   // 197
    public double ContentWidth => BgWidth - (AbsMargin + BgOffset) * 2; // 190
    public double CenterAlign => BgWidth / 2;                           // 103
    public double TopMarginFormula => TopMargin + BgOffset - 1;         // 32

    public Dictionary<string, Color4> Colors = new()
    {
        ["title"] = C(0, 0, 0, 255),
        ["activeTitle"] = C(255, 128, 0, 255),
        ["text"] = C(0, 0, 0, 205),
        ["text2"] = C(60, 65, 62, 205),
        ["bar"] = C(93, 141, 172, 255),
        ["histogram"] = C(176, 196, 222, 250),
        ["netDown"] = C(51, 153, 255, 205),
        ["netUp"] = C(51, 255, 0, 205),
        ["red"] = C(204, 0, 0, 255),
        ["redText"] = C(204, 0, 0, 205),          // colorRed with colorTextAlpha (TopCPU warn)
        ["emptyBar"] = C(255, 255, 255, 25),
        ["bgTop"] = C(163, 178, 230, 180),
        ["bgBody"] = C(230, 230, 230, 180),
        ["stroke"] = C(96, 138, 203, 100),
        ["solidLabel"] = C(248, 248, 248, 255),
        ["inactiveButton"] = C(120, 120, 120, 255),
        ["barWarn"] = C(220, 20, 60, 255),
        ["cpuTemp"] = C(204, 0, 0, 255),
        ["cpuUsage"] = C(176, 196, 222, 255),
        ["ramUsage"] = C(102, 204, 0, 255),
        ["gpuTemp"] = C(204, 0, 0, 255),
        ["gpuUsage"] = C(176, 196, 222, 255),
        ["gpuMemUsage"] = C(102, 204, 0, 255),
        ["gpuFan"] = C(0, 191, 255, 255),
        ["maxValue"] = C(178, 190, 181, 205),     // MaxTempColor (Power panel gray max column)
        ["maxLabelGray"] = C(120, 120, 120, 255), // Power panel "Max:" hardcoded gray
        ["devWarn1"] = C(47, 186, 255, 255),
        ["devWarn2"] = C(255, 255, 36, 255),
        ["devWarn3"] = C(255, 143, 30, 255),
        ["devWarn4"] = C(255, 0, 0, 255),
        ["devWarn5"] = C(204, 0, 0, 255),
        ["horizLine"] = C(80, 80, 80, 255),
        ["staleBadge"] = C(255, 80, 80, 220),
    };

    public Color4 Color(string token) => Colors.TryGetValue(token, out var c) ? c : new Color4(1, 0, 1, 1);

    private static Color4 C(byte r, byte g, byte b, byte a) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>Device-independent px per pt (Rainmeter FontSize is points; px = pt·96/72).</summary>
    public float FontPx(double pt) => (float)(pt * 96.0 / 72.0);

    /// <summary>Load overrides from config\theme.json if present (partial: colors/scale/font).</summary>
    public static Theme Load(string configDir)
    {
        var t = new Theme();
        string path = Path.Combine(configDir, "theme.json");
        if (!File.Exists(path)) return t;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("scale", out var s)) t.Scale = s.GetDouble();
            if (root.TryGetProperty("fontFamily", out var f)) t.FontFamily = f.GetString() ?? t.FontFamily;
            if (root.TryGetProperty("textSizePt", out var ts)) t.TextSizePt = ts.GetDouble();
            if (root.TryGetProperty("colors", out var colors))
            {
                foreach (var p in colors.EnumerateObject())
                {
                    var arr = p.Value;
                    if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() >= 3)
                    {
                        byte r = (byte)arr[0].GetInt32(), g = (byte)arr[1].GetInt32(), b = (byte)arr[2].GetInt32();
                        byte a = arr.GetArrayLength() >= 4 ? (byte)arr[3].GetInt32() : (byte)255;
                        t.Colors[p.Name] = C(r, g, b, a);
                    }
                }
            }
        }
        catch (Exception ex) { Halo.Shared.Log.Warn($"theme.json parse: {ex.Message}"); }
        return t;
    }
}
