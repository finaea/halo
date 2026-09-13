using System.Collections.Concurrent;
using Halo.Metrics;

namespace Halo.Collector;

/// <summary>
/// Provider-facing facade over MetricsWriter: name-based set with cached slot indexes,
/// automatic session-max tracking ("name.max" metrics) and reset support (plan §6).
///
/// Providers register each metric with its <b>nominal rate</b> — the real cadence of that
/// number, which is not always the provider's poll rate (fps.app.name is refreshed once a
/// second inside a 40 Hz drain; net.ip.external every few minutes). Consumers read that rate
/// out of the section to bound their own refresh sliders honestly (rates plan R2/R3).
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

    public int Register(string name, MetricType type, MetricUnit unit, string provider, double nominalRateHz,
        MetricSemantics semantics = MetricSemantics.Latest, MetricFlags flags = MetricFlags.None, int windowMs = 0)
    {
        // One rule for Static across every provider: a metric written once at discovery has no
        // cadence, so its nominal rate is 0 and a consumer's refresh slider ignores it. Anything
        // re-read on every poll is Latest (or IntervalAvg/RollingWindow) at the real poll rate.
        if (semantics == MetricSemantics.Static) nominalRateHz = 0;
        int idx = Writer.Register(new MetricDescriptor(name, type, unit, provider, nominalRateHz, semantics, flags, windowMs));
        _indexByName[name] = idx;
        return idx;
    }

    /// <summary>Register a double metric together with its ".max" session-extremum companion.</summary>
    public void RegisterWithMax(string name, MetricUnit unit, string provider, double nominalRateHz,
        MetricSemantics semantics = MetricSemantics.Latest, MetricFlags flags = MetricFlags.None, int windowMs = 0)
    {
        int idx = Register(name, MetricType.Double, unit, provider, nominalRateHz, semantics, flags | MetricFlags.HasMaxCompanion, windowMs);
        _ = idx;
        int maxIdx = Register(name + MetricNames.MaxSuffix, MetricType.Double, unit, provider, nominalRateHz,
            MetricSemantics.RunningMax, flags | MetricFlags.IsMaxCompanion);
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

    public bool IsRegistered(string name) => _indexByName.ContainsKey(name);

    /// <summary>
    /// Read back a metric another provider published, for Calc metrics that combine sources
    /// (latency.pc.ms adds PresentMon's display latency to PCL's queue and render times).
    /// False when the metric is not registered yet, was never written, is N/A, or is older than
    /// <paramref name="maxAgeS"/> — so a Calc never quietly keeps summing a frozen component.
    /// </summary>
    public bool TryGet(string name, out double value, double maxAgeS = double.MaxValue)
    {
        value = 0;
        if (!_indexByName.TryGetValue(name, out int idx)) return false;
        if (!Writer.TryRead(idx, out value, out double age)) return false;
        if (age > maxAgeS) { value = 0; return false; }
        return true;
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

    // ---- provider health (published in the section's provider table) ----

    public int RegisterProvider(string name, bool needsElevation) => Writer.RegisterProvider(name, needsElevation);

    public void SetProviderState(int index, ProviderState state, double rateHz, string lastError = "")
        => Writer.SetProviderState(index, state, rateHz, lastError);

    public void SetProviderPoll(int index, long qpc, double elapsedMs)
        => Writer.SetProviderPoll(index, qpc, elapsedMs);
}
