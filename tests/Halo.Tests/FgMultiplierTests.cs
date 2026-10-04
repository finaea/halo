using Halo.Collector.Providers;

namespace Halo.Tests;

/// <summary>
/// fps.fg.multiplier moved out of the Rainformer widget into the collector so both skins (and any
/// reader of the section) show one number. These pin the arithmetic it carried over — displayed ÷
/// rendered, the 0.25–8 clamp — and the fallback to PresentMon's sim-pacing ratio.
/// </summary>
public class FgMultiplierTests
{
    [Fact]
    public void DisplayedOverRendered_LiveCyberpunkReading()
    {
        // 2026-10-04 15:30:14, dynamic FG generating: 193.3 displayed over 105.3 rendered
        double? m = PclStatsProvider.FgMultiplier(193.3, 105.3, fgRatioFallback: 1.946);
        Assert.NotNull(m);
        Assert.Equal(1.836, m!.Value, precision: 3);
    }

    [Fact]
    public void NoFrameGeneration_ReadsAboutOne()
        => Assert.Equal(1.0, PclStatsProvider.FgMultiplier(117.0, 117.0, null)!.Value, precision: 6);

    [Theory]
    [InlineData(1000.0, 10.0, 8.0)]
    [InlineData(5.0, 100.0, 0.25)]
    public void Clamped(double displayed, double rendered, double expected)
        => Assert.Equal(expected, PclStatsProvider.FgMultiplier(displayed, rendered, null)!.Value, precision: 6);

    [Theory]
    [InlineData(0.0, 0.0)]    // no reading at all
    [InlineData(144.0, 0.0)]  // no Reflex markers
    [InlineData(144.0, 1.0)]  // a render rate this low is noise, not a rate
    public void WithoutAUsableRenderedRate_FallsBackToFgRatio(double displayed, double rendered)
        => Assert.Equal(1.95, PclStatsProvider.FgMultiplier(displayed, rendered, 1.95)!.Value, precision: 6);

    [Fact]
    public void NeitherSource_IsNoReading()
    {
        Assert.Null(PclStatsProvider.FgMultiplier(0, 0, null));
        Assert.Null(PclStatsProvider.FgMultiplier(0, 0, 0));
    }
}
