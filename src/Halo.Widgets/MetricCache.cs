using Halo.Shared.Metrics;

namespace Halo.Widgets;

/// <summary>
/// Widget-side data layer: attaches to shared memory, resolves metric names once, exposes
/// values + staleness, keeps per-metric history rings for graphs (sampled at each graph's own
/// cadence) and a frame-ring consumer for frametime graphs.
/// </summary>
public sealed class MetricCache : IDisposable
{
    private readonly MetricsReader _reader = new();
    private readonly Dictionary<string, int> _idx = new();
    private ulong _frameCursor;
    public const int FrameBufferSize = 4096;
    private readonly FrameEntry[] _frameBuf = new FrameEntry[FrameBufferSize];
    private int _frameCount;

    public bool Attached { get; private set; }
    public double HeartbeatAge => _reader.HeartbeatAgeSeconds;
    public int CollectorPid => _reader.CollectorPid;

    private long _attachedStartQpc;

    /// <summary>Call once per master tick. Handles attach/detach on collector restart.</summary>
    public void Tick()
    {
        if (!Attached)
        {
            Attached = _reader.TryAttach();
            if (Attached)
            {
                _idx.Clear();
                _attachedStartQpc = _reader.CollectorStartQpc;
                _frameCursor = 0;
            }
            if (!Attached) return;
        }

        // A restarted collector reuses the same named section (we keep it alive via our
        // handle) but rebuilds the registry — cached name→index mappings become WRONG,
        // silently mixing metrics across slots (observed 2026-07-19). Detect via start QPC.
        if (_reader.CollectorStartQpc != _attachedStartQpc)
        {
            Halo.Shared.Log.Warn("collector restarted — invalidating metric index cache");
            _reader.Detach();
            _idx.Clear();
            _frameCursor = 0;
            _frameCount = 0;
            Attached = false;
            return; // re-attach next tick
        }
        if (_reader.HeartbeatAgeSeconds > 30)
        {
            // collector very stale — if pid gone, detach so a new section can be picked up
            try { System.Diagnostics.Process.GetProcessById(_reader.CollectorPid); }
            catch
            {
                _reader.Detach();
                Attached = false;
                return;
            }
        }
        // pull new frames
        var (n, cursor) = _reader.ReadFrames(_frameCursor, _frameBuf);
        _frameCount = n;
        _frameCursor = cursor;
    }

    public bool Stale => !Attached || HeartbeatAge > 5;

    private int Index(string name)
    {
        if (_idx.TryGetValue(name, out int i)) return i;
        i = _reader.ResolveIndex(name);
        if (i >= 0) _idx[name] = i; // don't cache misses: metric may register later
        return i;
    }

    public double Value(string name, double fallback = 0)
        => Attached ? _reader.ReadOr(Index(name), fallback) : fallback;

    /// <summary>Value + validity: false if missing/never-written/stale-marked.</summary>
    public bool TryValue(string name, out double value, double maxAgeS = double.MaxValue)
    {
        value = 0;
        if (!Attached) return false;
        if (!_reader.TryRead(Index(name), out value, out double age)) return false;
        return age <= maxAgeS;
    }

    public string Text(string name, string fallback = "")
        => Attached && _reader.TryReadString(Index(name), out string s) ? s : fallback;

    /// <summary>Frames that arrived since the previous Tick() (chronological).</summary>
    public ReadOnlySpan<FrameEntry> NewFrames => _frameBuf.AsSpan(0, _frameCount);

    public IReadOnlyList<(string Name, MetricType Type, MetricUnit Unit, float RateHz)> Describe()
    {
        var list = new List<(string, MetricType, MetricUnit, float)>();
        _reader.RefreshRegistry();
        for (int i = 0; i < _reader.MetricCount; i++)
        {
            var d = _reader.DescribeIndex(i);
            if (d != null) list.Add((d.Value.Name, d.Value.Type, d.Value.Unit, d.Value.RateHz));
        }
        return list;
    }

    public void Dispose() => _reader.Dispose();
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
