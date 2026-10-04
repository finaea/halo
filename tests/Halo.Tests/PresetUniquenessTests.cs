using Halo.Shared.Panels;
using Halo.Shared.Skins;
using Xunit.Abstractions;

namespace Halo.Tests;

/// <summary>
/// Keeps the Rainformer presets visibly apart. Each preset is reduced to its two surfaces
/// (bgTop, bgBody) composited over mid grey, converted to CIE Lab (sRGB, D65), and a pair's
/// distance is sqrt(dTop² + dBody²) with CIE76 ΔE per surface. The floor of 12 sits below the
/// closest shipped pair (rainformer-dark vs high-contrast ≈ 15.0; closest palette pair
/// sakura-dusk vs sunset-horizon ≈ 17.3) with margin, so it trips on a near-duplicate rather
/// than on today's catalogue.
/// </summary>
public sealed class PresetUniquenessTests(ITestOutputHelper output)
{
    private const double Floor = 12.0;

    private static IReadOnlyList<PresetSpec> Presets => SkinCatalog.Rainformer.Presets;

    private static (double L, double a, double b) SurfaceLab(string hex)
    {
        Assert.True(ThemeTokens.TryParse(hex, out byte r, out byte g, out byte b, out byte al), $"cannot parse {hex}");
        double a = al / 255.0, bg = 0x80 / 255.0;
        return ToLab(r / 255.0 * a + bg * (1 - a), g / 255.0 * a + bg * (1 - a), b / 255.0 * a + bg * (1 - a));
    }

    private static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static (double L, double a, double b) ToLab(double sr, double sg, double sb)
    {
        double r = Lin(sr), g = Lin(sg), b = Lin(sb);
        double x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;
        double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        double z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static double DeltaE((double L, double a, double b) p, (double L, double a, double b) q)
        => Math.Sqrt(Math.Pow(p.L - q.L, 2) + Math.Pow(p.a - q.a, 2) + Math.Pow(p.b - q.b, 2));

    private static double Distance(IReadOnlyDictionary<string, string> x, IReadOnlyDictionary<string, string> y)
    {
        double top = DeltaE(SurfaceLab(x["bgTop"]), SurfaceLab(y["bgTop"]));
        double body = DeltaE(SurfaceLab(x["bgBody"]), SurfaceLab(y["bgBody"]));
        return Math.Sqrt(top * top + body * body);
    }

    [Fact]
    public void EveryPairOfRainformerPresets_IsVisiblyApart()
    {
        var failures = new List<string>();
        double min = double.MaxValue;
        string closest = "";
        for (int i = 0; i < Presets.Count; i++)
            for (int j = i + 1; j < Presets.Count; j++)
            {
                double d = Distance(Presets[i].Colors, Presets[j].Colors);
                if (d < min) { min = d; closest = $"{Presets[i].Id} vs {Presets[j].Id}"; }
                if (d < Floor) failures.Add($"{Presets[i].Id} vs {Presets[j].Id} = {d:F1} (needs >= {Floor:F1})");
            }
        output.WriteLine($"closest pair: {closest} = {min:F2}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void ANudgedCopyOfAPreset_FallsBelowTheFloor()
    {
        var src = Presets.First(p => p.Id == "sakura-dusk");
        var nudged = new Dictionary<string, string>(src.Colors)
        {
            ["bgTop"] = Nudge(src.Colors["bgTop"], 3),
            ["bgBody"] = Nudge(src.Colors["bgBody"], -3),
        };

        Assert.True(Distance(src.Colors, nudged) < Floor, $"nudged copy still {Distance(src.Colors, nudged):F1} away");
    }

    private static string Nudge(string hex, int steps)
    {
        Assert.True(ThemeTokens.TryParse(hex, out byte r, out byte g, out byte b, out byte a));
        static int C(int v) => Math.Clamp(v, 0, 255);
        return $"#{C(r + steps):X2}{C(g + steps):X2}{C(b + steps):X2}{a:X2}";
    }
}
