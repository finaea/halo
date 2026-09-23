using Halo.Metrics;

namespace Halo.Collector;

/// <summary>
/// Rolling-window frame statistics (plan §6): avg FPS presented/displayed, true 1%/0.1% lows
/// over a defined window (default 60 s), worst frametime over the newest 1 s, frame-gen ratio.
/// Not thread-safe: each owner (PresentMonProvider, PresentTap) serialises access under its own
/// lock; results are published via MetricSink (whose writes are lock-free).
/// </summary>
public sealed class FrameStats(double windowSeconds)
{
    private struct Sample
    {
        public long Qpc;
        public float PresentedFtMs;   // present-to-present
        public float DisplayedFtMs;   // display-to-display (0 = not displayed)
        public bool Displayed;
        public bool Generated;
    }

    private readonly Queue<Sample> _window = new(16384);
    private long _lastQpc;
    private long _qpcFreq = System.Diagnostics.Stopwatch.Frequency;
    private double _windowSeconds = windowSeconds;
    private long _nextLowsQpc;                       // lows recompute cadence (2 Hz): the full-window
    private double _low1P, _low01P, _low1D, _low01D; // sort is the only expensive part of Consume

    public void SetWindow(double seconds) => _windowSeconds = seconds;

    public void Add(in FrameEntry f)
    {
        var s = new Sample
        {
            Qpc = f.Qpc,
            PresentedFtMs = f.FrametimeMs,
            DisplayedFtMs = f.DisplayedFtMs,
            Displayed = (f.Flags & (uint)FrameFlags.Displayed) != 0,
            Generated = (f.Flags & (uint)FrameFlags.Generated) != 0,
        };
        _window.Enqueue(s);
        if (f.Qpc > _lastQpc) _lastQpc = f.Qpc;
        Trim(f.Qpc);
    }

    private void Trim(long nowQpc)
    {
        long cutoff = nowQpc - (long)(_windowSeconds * _qpcFreq);
        while (_window.Count > 0 && _window.Peek().Qpc < cutoff)
            _window.Dequeue();
    }

    public void Clear()
    {
        _window.Clear();
        _lastQpc = 0;
        _nextLowsQpc = 0;
        _low1P = _low01P = _low1D = _low01D = 0;
    }

    public readonly record struct Result(
        double FpsPresented, double FpsDisplayed,
        double AvgFrametimeMs, double WorstFrametimeMs,
        double AvgFrametimeShortMs,                       // presented mean over the newest 100 ms
        double AvgDisplayedFtMs, double WorstDisplayedFtMs, // flip-to-flip: 100 ms mean / 1 s max
        double Low1Presented, double Low01Presented,
        double Low1Displayed, double Low01Displayed,
        double FgRatio, int SampleCount);

    /// <summary>
    /// Window semantics (refined 2026-07-19 on user request):
    ///  - headline FPS / FRAMETIME / WORST: rolling 1 s ANCHORED TO THE NEWEST FRAME's
    ///    timestamp — the old console transport's stdout arrived in ~1 s bursts, so
    ///    wall-clock anchoring made most polls see an empty "last second" (WORST flickered 0).
    ///    WORST = the longest single frame in that second, so a hitch stays readable for a
    ///    full second.
    ///  - FRAMETIME means (both streams): rolling 100 ms — live-feeling readouts; WORST stays
    ///    the 1 s max so hitches remain readable for a full second.
    ///  - 1% / 0.1% lows and FG ratio: the full rolling window (default 60 s, configurable).
    ///    Lows are recomputed at 2 Hz and cached between (they move slowly; the sort dominates
    ///    Consume's cost, which otherwise runs at the provider poll rate — 40 Hz).
    /// </summary>
    public Result Consume(long nowQpc)
    {
        if (_window.Count == 0 || _lastQpc == 0) return default;
        long dataNow = _lastQpc;
        Trim(dataNow);
        int n = _window.Count;
        if (n == 0) return default;

        long headlineCutoff = dataNow - _qpcFreq;      // newest 1 s of frames
        long shortCutoff = dataNow - _qpcFreq / 10;    // newest 100 ms (live FRAMETIME readout)

        bool lowsDue = dataNow >= _nextLowsQpc;
        if (lowsDue) _nextLowsQpc = dataNow + _qpcFreq / 2;

        int displayedCount = 0, appCount = 0;
        int n1 = 0, dispN1 = 0, nShort = 0, dispNShort = 0;
        double worst1 = 0, ftSum1 = 0, ftSumShort = 0, dispSum1 = 0, dispSumShort = 0, dispWorst1 = 0;
        List<float>? presentedFts = lowsDue ? new List<float>(n) : null;
        List<float>? displayedFts = lowsDue ? new List<float>(n) : null;
        foreach (var s in _window)
        {
            presentedFts?.Add(s.PresentedFtMs);
            if (s.Displayed)
            {
                displayedCount++;
                if (s.DisplayedFtMs > 0) displayedFts?.Add(s.DisplayedFtMs);
            }
            if (!s.Generated) appCount++;

            if (s.Qpc >= headlineCutoff)
            {
                n1++;
                ftSum1 += s.PresentedFtMs;
                if (s.PresentedFtMs > worst1) worst1 = s.PresentedFtMs;
                if (s.Displayed)
                {
                    if (s.DisplayedFtMs > 0) { dispN1++; dispSum1 += s.DisplayedFtMs; }
                    if (s.DisplayedFtMs > dispWorst1) dispWorst1 = s.DisplayedFtMs;
                }
                if (s.Qpc >= shortCutoff)
                {
                    nShort++;
                    ftSumShort += s.PresentedFtMs;
                    if (s.Displayed && s.DisplayedFtMs > 0) { dispNShort++; dispSumShort += s.DisplayedFtMs; }
                }
            }
        }

        // Rate comes from the intervals already summed, NOT from the span of the window's
        // frames: k frames bound only k-1 gaps, so count ÷ (newest-oldest) read a flat +1 fps
        // (60 showed 61) and disagreed with the FRAMETIME row right beside it. Each sample
        // carries its own present-to-present interval, so k frames really do carry k intervals
        // and mean-frametime → rate is exact. Under two samples there is no interval at all:
        // report 0 (N/A) rather than the old Math.Max(0.1, span) floor's hard 10 fps.
        double fpsPresented = Rate(n1, ftSum1);
        double fpsDisplayed = Rate(dispN1, dispSum1);

        if (lowsDue)
        {
            _low1P = LowFps(presentedFts!, 0.01);
            _low01P = LowFps(presentedFts!, 0.001);
            _low1D = LowFps(displayedFts!, 0.01);
            _low01D = LowFps(displayedFts!, 0.001);
        }

        return new Result(
            FpsPresented: fpsPresented,
            FpsDisplayed: fpsDisplayed,
            AvgFrametimeMs: n1 > 0 ? ftSum1 / n1 : 0,
            WorstFrametimeMs: worst1,
            AvgFrametimeShortMs: nShort > 0 ? ftSumShort / nShort : 0,
            AvgDisplayedFtMs: dispNShort > 0 ? dispSumShort / dispNShort : 0,
            WorstDisplayedFtMs: dispWorst1,
            Low1Presented: _low1P,
            Low01Presented: _low01P,
            Low1Displayed: _low1D,
            Low01Displayed: _low01D,
            FgRatio: appCount > 0 ? (double)displayedCount / appCount : 0,
            SampleCount: n);
    }

    /// <summary>Frames per second from <paramref name="count"/> frametime intervals totalling
    /// <paramref name="sumMs"/>. 0 = not enough data to name a rate (needs two samples).</summary>
    private static double Rate(int count, double sumMs)
        => count >= 2 && sumMs > 0 ? count * 1000.0 / sumMs : 0;

    /// <summary>x% low FPS = 1000 / mean of the worst x% frametimes (CapFrameX-style).</summary>
    private static double LowFps(List<float> ftMs, double fraction)
    {
        int n = ftMs.Count;
        if (n < 16) return 0; // not enough data to be meaningful
        int k = Math.Max(1, (int)(n * fraction));
        // k largest: full sort, then the top k
        ftMs.Sort();
        double sum = 0;
        for (int i = n - k; i < n; i++) sum += ftMs[i];
        double meanWorst = sum / k;
        return meanWorst > 0 ? 1000.0 / meanWorst : 0;
    }
}
