namespace Halo.Metrics;

/// <summary>
/// The batteries-included client: everything a consumer needs to read the Halo collector
/// correctly, so nobody has to re-derive the reader rules.
///
/// Handles attach/retry, the collector-restart invalidation rule, liveness (heartbeat plus a
/// PID probe when the heartbeat goes cold), a name→index cache and frame-ring draining.
/// Call <see cref="Poll"/> once per update cycle, then read values.
///
/// Not thread-safe: use one session per consumer thread (the section itself is shared safely).
/// </summary>
public sealed class CollectorSession : IDisposable
{
    private readonly MetricsReader _reader = new();
    private readonly Dictionary<string, int> _indexByName = new(StringComparer.Ordinal);
    private readonly Action<string>? _log;
    private readonly FrameEntry[] _frameBuf;
    private int _frameCount;
    private ulong _frameCursor;
    private long _attachedStartQpc;

    /// <param name="log">Optional diagnostics sink (the package has no logger of its own).</param>
    /// <param name="frameBufferSize">Frames drained per <see cref="Poll"/>.</param>
    public CollectorSession(Action<string>? log = null, int frameBufferSize = 4096)
    {
        _log = log;
        _frameBuf = new FrameEntry[Math.Max(1, frameBufferSize)];
    }

    /// <summary>Seconds without a heartbeat before <see cref="Stale"/> goes true.</summary>
    public double StaleAfterSeconds { get; set; } = 5;

    /// <summary>Seconds without a heartbeat before the collector's PID is probed and, if it is
    /// gone, the section is released so a newly started collector can be picked up.</summary>
    public double DeadAfterSeconds { get; set; } = 30;

    public bool Attached { get; private set; }
    public double HeartbeatAgeSeconds => _reader.HeartbeatAgeSeconds;
    public int CollectorPid => _reader.CollectorPid;
    public string CollectorVersion => _reader.CollectorVersion;
    public long QpcFrequency => _reader.QpcFrequency;
    public int MetricCount => _reader.MetricCount;

    /// <summary>Raw reader, for consumers that want the section without this policy layer.</summary>
    public MetricsReader Reader => _reader;

    /// <summary>True when there is no live collector behind the numbers.</summary>
    public bool Stale => !Attached || HeartbeatAgeSeconds > StaleAfterSeconds;

    /// <summary>Frames appended since the previous <see cref="Poll"/> (chronological).</summary>
    public ReadOnlySpan<FrameEntry> NewFrames => _frameBuf.AsSpan(0, _frameCount);

    /// <summary>Call once per update cycle before reading values.</summary>
    public void Poll()
    {
        _frameCount = 0;

        if (!Attached)
        {
            Attached = _reader.TryAttach();
            if (!Attached) return;
            _indexByName.Clear();
            _attachedStartQpc = _reader.CollectorStartQpc;
            _frameCursor = 0;
            _log?.Invoke($"attached to {SharedMemoryLayout.SectionName} (pid {_reader.CollectorPid}, {_reader.MetricCount} metrics)");
        }

        // A restarted collector reuses the same named section (our handle keeps it alive) but
        // rebuilds the registry — cached name→index mappings become WRONG and silently mix
        // metrics across slots (observed 2026-07-19). Detect via the start QPC.
        if (_reader.CollectorStartQpc != _attachedStartQpc)
        {
            _log?.Invoke("collector restarted — invalidating metric index cache");
            Reset();
            return; // re-attach next poll
        }

        if (_reader.HeartbeatAgeSeconds > DeadAfterSeconds)
        {
            // Very stale: if the process is gone, detach so a new section can be picked up.
            try { System.Diagnostics.Process.GetProcessById(_reader.CollectorPid); }
            catch
            {
                _log?.Invoke("collector process gone — detaching");
                Reset();
                return;
            }
        }

        var (n, cursor) = _reader.ReadFrames(_frameCursor, _frameBuf);
        _frameCount = n;
        _frameCursor = cursor;
    }

    private void Reset()
    {
        _reader.Detach();
        _indexByName.Clear();
        _frameCursor = 0;
        _frameCount = 0;
        Attached = false;
    }

    /// <summary>Registry index for a metric name, or -1. Misses are not cached (a provider may
    /// register the metric later).</summary>
    public int IndexOf(string name)
    {
        if (_indexByName.TryGetValue(name, out int i)) return i;
        if (!Attached) return -1;
        i = _reader.ResolveIndex(name);
        if (i >= 0) _indexByName[name] = i;
        return i;
    }

    /// <summary>Value + validity. False when missing, never written, marked N/A, or older than
    /// <paramref name="maxAgeS"/>.</summary>
    public bool TryGet(string name, out double value, double maxAgeS = double.MaxValue)
    {
        value = 0;
        if (!Attached) return false;
        if (!_reader.TryRead(IndexOf(name), out value, out double age)) return false;
        return age <= maxAgeS;
    }

    public double Get(string name, double fallback = 0)
        => Attached && _reader.TryRead(IndexOf(name), out double v, out _) ? v : fallback;

    public string GetText(string name, string fallback = "")
        => Attached && _reader.TryReadString(IndexOf(name), out string s) ? s : fallback;

    public MetricInfo? Describe(string name)
    {
        int i = IndexOf(name);
        return i < 0 ? null : _reader.DescribeIndex(i);
    }

    /// <summary>Every registered metric, in registration order.</summary>
    public IReadOnlyList<MetricInfo> Metrics()
    {
        var list = new List<MetricInfo>();
        if (!Attached) return list;
        _reader.RefreshRegistry();
        for (int i = 0; i < _reader.MetricCount; i++)
            if (_reader.DescribeIndex(i) is { } m) list.Add(m);
        return list;
    }

    /// <summary>Provider health rows.</summary>
    public IReadOnlyList<ProviderInfo> Providers()
        => Attached ? _reader.Providers().ToList() : new List<ProviderInfo>();

    public void Dispose() => _reader.Dispose();
}
