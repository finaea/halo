using System.Collections.Concurrent;
using Halo.Shared.Metrics;

namespace Halo.Collector;

/// <summary>
/// Provider-facing facade over MetricsWriter: name-based set with cached slot indexes,
/// automatic session-max tracking ("name.max" metrics) and reset support (plan §6).
/// </summary>
public sealed class MetricSink(MetricsWriter writer)
{
    private readonly ConcurrentDictionary<string, int> _indexByName = new();
    private readonly ConcurrentDictionary<string, MaxState> _maxByName = new();

    private sealed class MaxState
    {
        public int Index;
        public double Value = double.MinValue;
    }

    public MetricsWriter Writer { get; } = writer;

    public int Register(string name, MetricType type, MetricUnit unit, string provider, double maxRateHz)
    {
        int idx = Writer.Register(new MetricDescriptor(name, type, unit, provider, (float)maxRateHz));
        _indexByName[name] = idx;
        return idx;
    }

    /// <summary>Register a double metric together with its ".max" session-extremum companion.</summary>
    public void RegisterWithMax(string name, MetricUnit unit, string provider, double maxRateHz)
    {
        Register(name, MetricType.Double, unit, provider, maxRateHz);
        int maxIdx = Register(name + MetricNames.MaxSuffix, MetricType.Double, unit, provider, maxRateHz);
        _maxByName[name] = new MaxState { Index = maxIdx };
    }

    public void Set(string name, double value)
    {
        if (!_indexByName.TryGetValue(name, out int idx)) return;
        Writer.Set(idx, value);
        if (_maxByName.TryGetValue(name, out var max) && value > max.Value)
        {
            max.Value = value;
            Writer.Set(max.Index, value);
        }
    }

    public void SetString(string name, string value)
    {
        if (_indexByName.TryGetValue(name, out int idx)) Writer.SetString(idx, value);
    }

    public void MarkStale(string name)
    {
        if (_indexByName.TryGetValue(name, out int idx)) Writer.MarkStale(idx);
    }

    public void MarkAllStale(string prefix)
    {
        foreach (var (name, idx) in _indexByName)
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                Writer.MarkStale(idx);
    }

    public void SetEffectiveRate(string name, double hz)
    {
        if (_indexByName.TryGetValue(name, out int idx)) Writer.SetEffectiveRate(idx, (float)hz);
    }

    /// <summary>Reset session maxima whose base-metric name starts with prefix ("" = all).</summary>
    public void ResetMax(string prefix)
    {
        foreach (var (name, max) in _maxByName)
        {
            if (prefix.Length != 0 && !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            max.Value = double.MinValue;
            Writer.MarkStale(max.Index);
        }
    }
}
