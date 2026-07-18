using Halo.Shared.Metrics;

namespace Halo.Collector;

/// <summary>
/// Rolling-window frame statistics (plan §6): avg FPS presented/displayed, true 1%/0.1% lows
/// over a defined window (default 60 s), worst frametime since last Consume(), frame-gen ratio.
/// Single-threaded: owned by the PresentMon pump thread; results are published via MetricSink
/// (whose writes are lock-free).
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
    private double _worstSinceConsume;
    private long _qpcFreq = System.Diagnostics.Stopwatch.Frequency;
    private double _windowSeconds = windowSeconds;

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
        if (f.FrametimeMs > _worstSinceConsume) _worstSinceConsume = f.FrametimeMs;
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
        _worstSinceConsume = 0;
    }

    public readonly record struct Result(
        double FpsPresented, double FpsDisplayed,
        double AvgFrametimeMs, double WorstFrametimeMs,
        double Low1Presented, double Low01Presented,
        double Low1Displayed, double Low01Displayed,
        double FgRatio, int SampleCount);

    /// <summary>Compute stats over the current window; resets the worst-frametime accumulator.</summary>
    public Result Consume(long nowQpc)
    {
        Trim(nowQpc);
        int n = _window.Count;
        if (n == 0)
        {
            _worstSinceConsume = 0;
            return default;
        }

        // effective window span: from oldest sample to now (avoids inflated FPS during ramp-up)
        double spanS = Math.Max(0.001, (double)(nowQpc - _window.Peek().Qpc) / _qpcFreq);

        int displayedCount = 0, appCount = 0;
        double ftSum = 0;
        var presentedFts = new List<float>(n);
        var displayedFts = new List<float>(n);
        foreach (var s in _window)
        {
            presentedFts.Add(s.PresentedFtMs);
            ftSum += s.PresentedFtMs;
            if (s.Displayed)
            {
                displayedCount++;
                if (s.DisplayedFtMs > 0) displayedFts.Add(s.DisplayedFtMs);
            }
            if (!s.Generated) appCount++;
        }

        double fpsPresented = n / spanS;
        double fpsDisplayed = displayedCount / spanS;
        double worst = _worstSinceConsume;
        _worstSinceConsume = 0;

        return new Result(
            FpsPresented: fpsPresented,
            FpsDisplayed: fpsDisplayed,
            AvgFrametimeMs: ftSum / n,
            WorstFrametimeMs: worst,
            Low1Presented: LowFps(presentedFts, 0.01),
            Low01Presented: LowFps(presentedFts, 0.001),
            Low1Displayed: LowFps(displayedFts, 0.01),
            Low01Displayed: LowFps(displayedFts, 0.001),
            FgRatio: appCount > 0 ? (double)displayedCount / appCount : 0,
            SampleCount: n);
    }

    /// <summary>x% low FPS = 1000 / mean of the worst x% frametimes (CapFrameX-style).</summary>
    private static double LowFps(List<float> ftMs, double fraction)
    {
        int n = ftMs.Count;
        if (n < 16) return 0; // not enough data to be meaningful
        int k = Math.Max(1, (int)(n * fraction));
        // partial selection: k largest
        ftMs.Sort();
        double sum = 0;
        for (int i = n - k; i < n; i++) sum += ftMs[i];
        double meanWorst = sum / k;
        return meanWorst > 0 ? 1000.0 / meanWorst : 0;
    }
}
