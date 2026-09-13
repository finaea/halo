using Halo.Metrics;

namespace Halo.Widgets;

/// <summary>
/// Widget-side data layer. The section plumbing — attach/retry, restart invalidation, liveness,
/// name→index caching, frame draining — lives in <see cref="CollectorSession"/> (the package any
/// third-party widget uses); what is left here is widget policy: the staleness gate the panels
/// read and the history rings the graphs draw.
/// </summary>
public sealed class MetricCache : IDisposable
{
    public const int FrameBufferSize = 4096;

    private readonly CollectorSession _session = new(Halo.Shared.Log.Info, FrameBufferSize);

    public bool Attached => _session.Attached;
    public double HeartbeatAge => _session.HeartbeatAgeSeconds;
    public int CollectorPid => _session.CollectorPid;
    public CollectorSession Session => _session;

    /// <summary>Call once per master tick.</summary>
    public void Tick() => _session.Poll();

    public bool Stale => _session.Stale;

    public double Value(string name, double fallback = 0) => _session.Get(name, fallback);

    /// <summary>Value + validity: false if missing/never-written/stale-marked.</summary>
    public bool TryValue(string name, out double value, double maxAgeS = double.MaxValue)
        => _session.TryGet(name, out value, maxAgeS);

    public string Text(string name, string fallback = "") => _session.GetText(name, fallback);

    /// <summary>Frames that arrived since the previous Tick() (chronological).</summary>
    public ReadOnlySpan<FrameEntry> NewFrames => _session.NewFrames;

    /// <summary>Nominal publish rate of a metric, or 0 when it is not registered. This is what
    /// bounds a widget's refresh slider honestly (rates plan R2).</summary>
    public double NominalRateHz(string name) => _session.Describe(name)?.NominalRateHz ?? 0;

    public IReadOnlyList<MetricInfo> Describe() => _session.Metrics();

    /// <summary>How many metrics the collector has registered. A growing count means new
    /// hardware appeared, which can raise a widget's refresh bound (rates plan R2).</summary>
    public int MetricCount => _session.MetricCount;

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Timestamped sample ring for graph series. Samples carry the QPC they were taken at, because
/// graph columns are time buckets, not sample slots: the visible span is <c>graph.historyS</c>
/// seconds whatever the widget's refresh rate happens to be (rates plan R4).
///
/// Frame-driven series store the frame's own QPC and are drawn one bar per sample instead.
/// </summary>
public sealed class SampleRing
{
    private long[] _qpc;
    private double[] _val;
    private int _count, _head;

    public SampleRing(int capacity)
    {
        int n = Math.Max(2, capacity);
        _qpc = new long[n];
        _val = new double[n];
    }

    public int Capacity => _val.Length;
    public int Count => _count;

    public void Add(long qpc, double v)
    {
        _qpc[_head] = qpc;
        _val[_head] = v;
        _head = (_head + 1) % _val.Length;
        if (_count < _val.Length) _count++;
    }

    /// <summary>i=0 oldest … Count-1 newest.</summary>
    public (long Qpc, double Value) this[int i]
    {
        get { int k = (_head - _count + i + 2 * _val.Length) % _val.Length; return (_qpc[k], _val[k]); }
    }

    public double ValueAt(int i) => _val[(_head - _count + i + 2 * _val.Length) % _val.Length];

    /// <summary>
    /// Grow or shrink without losing the newest samples — a rate or history change must not blank
    /// a graph that is already drawing (settings plan: applied in place, no window rebuild).
    /// </summary>
    public void Resize(int capacity)
    {
        int n = Math.Max(2, capacity);
        if (n == _val.Length) return;
        int keep = Math.Min(_count, n);
        var q = new long[n];
        var v = new double[n];
        for (int i = 0; i < keep; i++)
        {
            var s = this[_count - keep + i];
            q[i] = s.Qpc;
            v[i] = s.Value;
        }
        _qpc = q;
        _val = v;
        _count = keep;
        _head = keep % n;
    }

    public double Max()
    {
        double m = 0;
        for (int i = 0; i < _count; i++) { double v = ValueAt(i); if (v > m) m = v; }
        return m;
    }

    public void Clear() { _count = 0; _head = 0; }
}
