using Halo.Collector;
using Halo.Metrics;

namespace Halo.Tests;

/// <summary>
/// <see cref="FrameStats"/> is pure arithmetic over a queue of frames — no section, no ETW, no
/// hardware — so the one thing every fps readout in Halo depends on can be pinned exactly.
///
/// What these pin is the correction landed on 2026-09-14: the headline rate used to be
/// <c>frame count ÷ (newest − oldest)</c>, and k frames bound only k−1 gaps, so a locked 60 fps
/// capture published 61 next to a FRAMETIME row that said 16.7 ms. The rate now comes from the
/// intervals themselves, which is exact and agrees with the frametime row by construction.
/// </summary>
public class FrameStatsTests
{
    private static readonly long Qpf = System.Diagnostics.Stopwatch.Frequency;

    /// <summary>A steady stream: <paramref name="seconds"/> of frames at exactly
    /// <paramref name="hz"/>, every one displayed, starting one interval in (the first frame of a
    /// capture has no previous present to measure against, same as the real pipeline).</summary>
    private static FrameStats Steady(double hz, double seconds, out long lastQpc)
    {
        var s = new FrameStats(60);
        float ftMs = (float)(1000.0 / hz);
        long step = (long)(Qpf / hz);
        long q = Qpf * 1000;                 // arbitrary non-zero epoch
        int frames = (int)(seconds * hz);
        lastQpc = q;
        for (int i = 0; i < frames; i++)
        {
            q += step;
            s.Add(new FrameEntry
            {
                Qpc = q,
                FrametimeMs = ftMs,
                DisplayedFtMs = ftMs,
                Flags = (uint)FrameFlags.Displayed,
                Pid = 1234,
            });
            lastQpc = q;
        }
        return s;
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void SteadyRate_PresentedFps_IsTheRate_NotRatePlusOne(double hz)
    {
        var stats = Steady(hz, seconds: 3, out long now);

        var r = stats.Consume(now);

        // the old span-based form returned hz + 1 here, every time (precision 3 is far tighter
        // than the +1 it is guarding against, and leaves room for the float frametimes)
        Assert.Equal(hz, r.FpsPresented, precision: 3);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void SteadyRate_DisplayedFps_IsTheRate(double hz)
    {
        var stats = Steady(hz, seconds: 3, out long now);

        var r = stats.Consume(now);

        Assert.Equal(hz, r.FpsDisplayed, precision: 3);
    }

    /// <summary>The cross-check that made finding 1 visible on a running machine: the panel drew
    /// FPS and FRAMETIME side by side and they disagreed. 1000 ÷ frametime must be the FPS.</summary>
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void FpsAndFrametime_AgreeWithEachOther(double hz)
    {
        var stats = Steady(hz, seconds: 3, out long now);

        var r = stats.Consume(now);

        Assert.Equal(r.FpsPresented, 1000.0 / r.AvgFrametimeMs, precision: 6);
        Assert.Equal(r.FpsPresented, 1000.0 / r.AvgFrametimeShortMs, precision: 6);
    }

    /// <summary>One frame carries no interval, so there is no rate to report. The old code hit
    /// the <c>Math.Max(0.1, span)</c> floor and published a hard 10 fps whatever the real rate —
    /// which is what the panel flashed for a moment after every Clear().</summary>
    [Fact]
    public void OneSample_ReportsNoRate_RatherThanTheTenFpsFloorArtifact()
    {
        var stats = new FrameStats(60);
        long q = Qpf * 1000;
        stats.Add(new FrameEntry
        {
            Qpc = q,
            FrametimeMs = 4.1667f,   // a 240 fps frame: the floor would still have said 10
            DisplayedFtMs = 4.1667f,
            Flags = (uint)FrameFlags.Displayed,
        });

        var r = stats.Consume(q);

        Assert.Equal(0, r.FpsPresented);
        Assert.Equal(0, r.FpsDisplayed);
        Assert.Equal(1, r.SampleCount);
    }

    [Fact]
    public void NoSamples_ReportsNothing()
    {
        var r = new FrameStats(60).Consume(Qpf * 1000);

        Assert.Equal(0, r.FpsPresented);
        Assert.Equal(0, r.FpsDisplayed);
        Assert.Equal(0, r.SampleCount);
    }

    [Fact]
    public void Clear_DropsTheWindow()
    {
        var stats = Steady(60, seconds: 3, out long now);
        Assert.True(stats.Consume(now).FpsPresented > 0);

        stats.Clear();

        Assert.Equal(0, stats.Consume(now).SampleCount);
    }

    /// <summary>Frames that never reached the screen carry no flip-to-flip interval, so they must
    /// not count toward the displayed rate — half-displayed at 60 fps is 30 displayed fps, and the
    /// old span-based form got this doubly wrong (subset count over the full window's span).</summary>
    [Fact]
    public void DisplayedFps_CountsOnlyFramesThatReachedTheScreen()
    {
        var stats = new FrameStats(60);
        long step = Qpf / 60;
        long q = Qpf * 1000;
        for (int i = 0; i < 180; i++)
        {
            q += step;
            bool shown = i % 2 == 0;         // every other frame makes it to the screen
            stats.Add(new FrameEntry
            {
                Qpc = q,
                FrametimeMs = 1000f / 60,
                DisplayedFtMs = shown ? 1000f / 30 : 0,
                Flags = shown ? (uint)FrameFlags.Displayed : (uint)FrameFlags.Dropped,
            });
        }

        var r = stats.Consume(q);

        Assert.Equal(60, r.FpsPresented, precision: 3);
        Assert.Equal(30, r.FpsDisplayed, precision: 3);
    }

    /// <summary>
    /// A frame can be flagged <c>Displayed</c> while carrying <c>DisplayedFtMs == 0</c>: the flag
    /// is set from <i>either</i> display-side value
    /// (<c>PresentMonSdkSource.cs:292</c> — <c>dispLat &gt; 0 || dispFt &gt; 0</c>) while the value
    /// comes from <c>dispFt</c> alone (<c>:302</c>, NaN → 0). So `dispN1` really can count fewer
    /// frames than are flagged displayed.
    ///
    /// That loss must not move the rate, and this is the property that makes the mean-of-intervals
    /// form safe where the old count ÷ span form was not: dropping a sample drops one from the
    /// count AND its interval from the sum, so the quotient is unchanged. The old form divided a
    /// shrinking count by a fixed ~1 s span and read low in direct proportion.
    /// </summary>
    [Theory]
    [InlineData(1)]    // every frame reports a flip-to-flip interval
    [InlineData(5)]    // 20% report none
    [InlineData(2)]    // 50% report none
    public void DisplayedFps_IsUnbiased_WhenSomeDisplayedFramesReportNoInterval(int keepEveryNth)
    {
        var stats = new FrameStats(60);
        long step = Qpf / 60;
        long q = Qpf * 1000;
        for (int i = 0; i < 180; i++)
        {
            q += step;
            // every frame reached the screen; only some carry a usable msBetweenDisplayChange
            bool hasInterval = i % keepEveryNth == 0;
            stats.Add(new FrameEntry
            {
                Qpc = q,
                FrametimeMs = 1000f / 60,
                DisplayedFtMs = hasInterval ? 1000f / 60 : 0,
                Flags = (uint)FrameFlags.Displayed,
            });
        }

        var r = stats.Consume(q);

        Assert.Equal(60, r.FpsDisplayed, precision: 3);
        Assert.Equal(60, r.FpsPresented, precision: 3);
    }

    /// <summary>The headline window is the newest 1 s of frames, anchored to the newest frame's
    /// timestamp (2026-07-19). A rate change must be fully reflected one second later, and must
    /// not be dragged by the older half of the 60 s lows window.</summary>
    [Fact]
    public void HeadlineWindow_IsTheNewestSecond_NotTheWholeWindow()
    {
        var stats = new FrameStats(60);
        long q = Qpf * 1000;
        // 5 s at 30 fps …
        for (int i = 0; i < 150; i++)
        {
            q += Qpf / 30;
            stats.Add(new FrameEntry { Qpc = q, FrametimeMs = 1000f / 30, DisplayedFtMs = 1000f / 30, Flags = (uint)FrameFlags.Displayed });
        }
        // … then 2 s at 120, so the newest second is entirely inside the new rate
        for (int i = 0; i < 240; i++)
        {
            q += Qpf / 120;
            stats.Add(new FrameEntry { Qpc = q, FrametimeMs = 1000f / 120, DisplayedFtMs = 1000f / 120, Flags = (uint)FrameFlags.Displayed });
        }

        var r = stats.Consume(q);

        Assert.Equal(120, r.FpsPresented, precision: 3);
        Assert.Equal(390, r.SampleCount);   // the 60 s lows window still holds everything
    }

    /// <summary>WORST is the longest single frame in the headline second, so a hitch stays
    /// readable for a full second while the mean glides back (2026-07-19 semantics).</summary>
    [Fact]
    public void WorstFrametime_IsTheHitch_WhileTheMeanBarelyMoves()
    {
        var stats = new FrameStats(60);
        long q = Qpf * 1000;
        for (int i = 0; i < 120; i++)
        {
            bool hitch = i == 60;
            float ft = hitch ? 50f : 1000f / 60;
            q += (long)(ft * Qpf / 1000);
            stats.Add(new FrameEntry { Qpc = q, FrametimeMs = ft, DisplayedFtMs = ft, Flags = (uint)FrameFlags.Displayed });
        }

        var r = stats.Consume(q);

        Assert.Equal(50, r.WorstFrametimeMs, precision: 3);
        Assert.InRange(r.AvgFrametimeMs, 16, 18);
    }

    /// <summary>The 1% / 0.1% lows work off frametimes and were never affected by finding 1 —
    /// this is the regression guard that says so.</summary>
    [Fact]
    public void Lows_AreDrivenByTheWorstFrametimes_NotTheHeadlineRate()
    {
        var stats = new FrameStats(60);
        long q = Qpf * 1000;
        for (int i = 0; i < 1000; i++)
        {
            float ft = i % 100 == 0 ? 40f : 1000f / 60;   // 1 in 100 frames is a 25 fps frame
            q += (long)(ft * Qpf / 1000);
            stats.Add(new FrameEntry { Qpc = q, FrametimeMs = ft, DisplayedFtMs = ft, Flags = (uint)FrameFlags.Displayed });
        }

        var r = stats.Consume(q);

        Assert.Equal(25, r.Low1Presented, precision: 3);     // 1000 / 40
        Assert.Equal(25, r.Low01Presented, precision: 3);
    }

    /// <summary>Frame-gen ratio is displayed frames ÷ app (non-generated) frames over the whole
    /// window: one generated frame per rendered one reads 2×.</summary>
    [Fact]
    public void FgRatio_IsDisplayedOverAppFrames()
    {
        var stats = new FrameStats(60);
        long q = Qpf * 1000;
        for (int i = 0; i < 240; i++)
        {
            bool generated = i % 2 == 1;
            q += Qpf / 120;
            stats.Add(new FrameEntry
            {
                Qpc = q,
                FrametimeMs = 1000f / 120,
                DisplayedFtMs = 1000f / 120,
                Flags = (uint)(FrameFlags.Displayed | (generated ? FrameFlags.Generated : FrameFlags.AppFrame)),
            });
        }

        var r = stats.Consume(q);

        Assert.Equal(2, r.FgRatio, precision: 6);
    }
}
