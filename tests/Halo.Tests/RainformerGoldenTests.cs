using System.Runtime.CompilerServices;
using Halo.Shared;
using Halo.Widgets;
using Halo.Widgets.Harness;
using Halo.Widgets.Skins;

namespace Halo.Tests;

/// <summary>
/// Pixel goldens for the Rainformer skin: every panel type under every built-in fixture, rendered
/// off-screen through WARP and compared with the PNG committed under
/// <c>tests\Halo.Tests\goldens\rainformer</c>. They exist so a skin refactor that moves one pixel
/// of the original look fails loudly instead of drifting.
///
/// <para>The goldens are read from the source tree (<see cref="CallerFilePathAttribute"/>) rather
/// than copied to bin, so 55 PNGs do not ride along in every build output. WARP is the software
/// rasteriser, so the render does not depend on the machine's GPU or driver.</para>
///
/// <para>Regenerate after an intended visual change with <c>HALO_UPDATE_GOLDENS=1 dotnet test
/// --filter RainformerGoldenTests</c>, then review the PNG diff before committing.</para>
///
/// <para>In the log collection because <see cref="Dx"/> writes breadcrumbs and <c>Log</c> is one
/// process-wide set of counters that parallel log tests would corrupt.</para>
/// </summary>
[Collection(LogTestCollection.Name)]
public sealed class RainformerGoldenTests
{
    private static readonly Lazy<Dx> SharedDx = new(() => new Dx(Paths.FontsDir, warp: true));

    /// <summary>Per-channel slack that only absorbs the PNG premultiply round-trip.</summary>
    private const int ChannelTolerance = 8;

    private static string GoldenDir([CallerFilePath] string src = "") =>
        Path.Combine(Path.GetDirectoryName(src)!, "goldens", "rainformer");

    public static IEnumerable<object[]> Cases() =>
        from type in PanelFactory.KnownTypes
        from fixture in FixtureMetrics.BuiltIn
        select new object[] { type, fixture };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Render_matches_golden(string type, string fixture)
    {
        var actual = PanelRenderer.Render(SharedDx.Value, new RenderRequest(type, fixture));
        string path = Path.Combine(GoldenDir(), $"{type}-{fixture}.png");

        if (Environment.GetEnvironmentVariable("HALO_UPDATE_GOLDENS") == "1")
        {
            actual.SavePng(path);
            return;
        }

        Assert.True(File.Exists(path), $"missing golden {path} (generate with HALO_UPDATE_GOLDENS=1)");
        var golden = PanelImage.DecodePng(File.ReadAllBytes(path));

        string? failure = Compare(golden, actual);
        if (failure == null) return;

        string outPath = Path.Combine(AppContext.BaseDirectory, "golden-failures", $"{type}-{fixture}.png");
        actual.SavePng(outPath);
        Assert.Fail($"{type}/{fixture}: {failure}. Actual render: {outPath}; golden: {path}");
    }

    private static string? Compare(PanelImage golden, PanelImage actual)
    {
        if (golden.Width != actual.Width || golden.Height != actual.Height)
            return $"size {actual.Width}x{actual.Height} != golden {golden.Width}x{golden.Height}";

        int diffs = 0, firstX = -1, firstY = -1;
        for (int i = 0; i < actual.Pixels.Length; i += 4)
        {
            bool differs = false;
            for (int c = 0; c < 4; c++)
                if (Math.Abs(actual.Pixels[i + c] - golden.Pixels[i + c]) > ChannelTolerance) { differs = true; break; }
            if (!differs) continue;
            if (diffs++ == 0) { firstX = i / 4 % actual.Width; firstY = i / 4 / actual.Width; }
        }
        return diffs == 0 ? null : $"{diffs} pixels differ, first at ({firstX},{firstY})";
    }
}
