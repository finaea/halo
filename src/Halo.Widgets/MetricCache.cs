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

    public void Dispose() => _session.Dispose();
}

/// <summary>Fixed-capacity sample ring for graph series (one value per sample tick).</summary>
public sealed class HistoryRing(int capacity)
{
    private readonly double[] _data = new double[Math.Max(2, capacity)];
    private int _count, _head;

    public int Capacity => _data.Length;
    public int Count => _count;

    public void Add(double v)
    {
        _data[_head] = v;
        _head = (_head + 1) % _data.Length;
        if (_count < _data.Length) _count++;
    }

    /// <summary>i=0 oldest … Count-1 newest.</summary>
    public double this[int i] => _data[(_head - _count + i + 2 * _data.Length) % _data.Length];

    public double Max()
    {
        double m = 0;
        for (int i = 0; i < _count; i++) { double v = this[i]; if (v > m) m = v; }
        return m;
    }

    public void Clear() { _count = 0; _head = 0; }
}
