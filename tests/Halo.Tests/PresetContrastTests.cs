using Halo.Shared.Panels;
using Halo.Shared.Skins;
using Xunit.Abstractions;

namespace Halo.Tests;

/// <summary>
/// The contrast gate for every skin preset (preset research §4.3, tech plan §8). WCAG 2.x ratio;
/// the surface token is composited over a wallpaper (black / mid grey / white) and the foreground
/// over that surface. A <see cref="PresetSpec.ContrastExempt"/> preset is the parity baseline and
/// is skipped.
/// </summary>
public sealed class PresetContrastTests(ITestOutputHelper output)
{
    private static readonly (string Name, double Grey)[] Wallpapers =
        [("black", 0), ("grey", 0x80 / 255.0), ("white", 1)];

    private record Check(string Token, string Surface, string Wallpaper, double Min);

    private static IEnumerable<Check> Checks(SkinInfo skin)
    {
        foreach (var pair in skin.ContrastPairs ?? [])
            foreach (var (w, _) in Wallpapers.Where(w => pair.AllWallpapers || w.Name == "grey"))
                yield return new(pair.Foreground, pair.Surface, w, pair.MinRatio);
    }

    public static IEnumerable<object[]> Presets()
        => SkinCatalog.All.SelectMany(s => s.Presets.Where(p => !p.ContrastExempt).Select(p => new object[] { s.Id, p.Id }));

    private static (double r, double g, double b) Rgb(string hex, out double a)
    {
        Assert.True(ThemeTokens.TryParse(hex, out byte r, out byte g, out byte b, out byte al), $"cannot parse {hex}");
        a = al / 255.0;
        return (r / 255.0, g / 255.0, b / 255.0);
    }

    private static (double r, double g, double b) Over((double r, double g, double b) fg, double a, (double r, double g, double b) bg)
        => (fg.r * a + bg.r * (1 - a), fg.g * a + bg.g * (1 - a), fg.b * a + bg.b * (1 - a));

    private static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double Lum((double r, double g, double b) c) => 0.2126 * Lin(c.r) + 0.7152 * Lin(c.g) + 0.0722 * Lin(c.b);

    private static double Ratio(PresetSpec p, Check c)
    {
        double grey = Wallpapers.First(w => w.Name == c.Wallpaper).Grey;
        var surfaceRgb = Rgb(p.Colors[c.Surface], out double sa);
        var surface = Over(surfaceRgb, sa, (grey, grey, grey));
        var fgRgb = Rgb(p.Colors[c.Token], out double fa);
        var fg = Over(fgRgb, fa, surface);
        double l1 = Lum(fg), l2 = Lum(surface);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static PresetSpec Find(string skinId, string presetId)
        => SkinCatalog.Find(skinId)!.Presets.First(p => p.Id == presetId);

    [Theory]
    [MemberData(nameof(Presets))]
    public void Preset_meets_contrast_tiers(string skinId, string presetId)
    {
        var p = Find(skinId, presetId);
        var failures = new List<string>();
        foreach (var pair in SkinCatalog.Find(skinId)!.ContrastPairs ?? [])
            foreach (var t in new[] { pair.Foreground, pair.Surface })
                Assert.True(p.Colors.ContainsKey(t), $"{presetId}: contrast pair names unknown token '{t}'");
        foreach (var c in Checks(SkinCatalog.Find(skinId)!))
        {
            double r = Ratio(p, c);
            if (r + 1e-9 < c.Min)
                failures.Add($"{presetId}: {c.Token} on {c.Surface} over {c.Wallpaper} = {r:F2} (needs {c.Min:F1})");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Print_min_contrast_per_preset()
    {
        foreach (var skin in SkinCatalog.All)
            foreach (var p in skin.Presets.Where(p => !p.ContrastExempt))
            {
                var mins = Checks(skin).GroupBy(c => (c.Min, c.Wallpaper == "grey" ? "grey" : "any"))
                    .OrderByDescending(g => g.Key.Min)
                    .Select(g => { var worst = g.MinBy(c => Ratio(p, c))!; return $"min{g.Key.Min:F1}/{g.Key.Item2}={Ratio(p, worst):F2} ({worst.Token}/{worst.Wallpaper})"; });
                output.WriteLine($"{p.Id,-18} {string.Join("  ", mins)}");
            }
    }

    [Fact]
    public void Every_preset_defines_every_core_token_with_a_parsable_value()
    {
        foreach (var skin in SkinCatalog.All)
            foreach (var p in skin.Presets)
            {
                foreach (var t in skin.Tokens.Where(t => t.Core))
                {
                    Assert.True(p.Colors.TryGetValue(t.Token, out var hex), $"{p.Id} is missing core token {t.Token}");
                    Assert.True(ThemeTokens.TryParse(hex, out _, out _, out _, out _), $"{p.Id}.{t.Token} = '{hex}' does not parse");
                }
            }
    }

    [Fact]
    public void Preset_ids_are_unique_per_skin_and_the_first_is_the_default()
    {
        foreach (var skin in SkinCatalog.All)
        {
            Assert.Equal(skin.Presets.Count, skin.Presets.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.Same(skin.Presets[0], skin.DefaultPreset);
        }
    }

    [Fact]
    public void Each_skin_has_exactly_one_high_contrast_preset()
    {
        foreach (var skin in SkinCatalog.All)
            Assert.Equal(1, skin.Presets.Count(p => p.HighContrast));
    }
}
