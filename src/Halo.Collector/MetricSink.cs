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
    /// <summary>Registry slots each provider owns, with the nominal rate each one declared, so
    /// the host can republish live rates — and stale the lot when the provider dies — without a
    /// second name→index map.</summary>
    private readonly ConcurrentDictionary<string, List<Slot>> _slotsByProvider =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Slot(int Index, double NominalHz, bool IsMax);

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
        // Two providers may register the same name (NVML and LHM both feed gpu.<i>.fan.rpm); the
        // first one owns the descriptor, so only the first one accounts for its live rate — else
        // the two would fight over effectiveRateHz at their different cadences.
        bool firstRegistrant = !_indexByName.ContainsKey(name);
        int idx = Writer.Register(new MetricDescriptor(name, type, unit, provider, nominalRateHz, semantics, flags, windowMs));
        _indexByName[name] = idx;
        if (firstRegistrant && provider.Length != 0 && nominalRateHz > 0)
        {
            var slots = _slotsByProvider.GetOrAdd(provider, _ => new List<Slot>());
            lock (slots) slots.Add(new Slot(idx, nominalRateHz, flags.HasFlag(MetricFlags.IsMaxCompanion)));
        }
        return idx;
    }

    /// <summary>
    /// Republish the live rate of every metric a provider owns. <paramref name="scale"/> is that
    /// provider's measured poll rate divided by its configured one, so a metric that publishes at
    /// a sub-cadence (the frame lows at 2 Hz inside a 40 Hz drain) scales with the provider
    /// instead of being overwritten by the poll rate. Static metrics have no cadence and are
    /// left out of the list entirely.
    /// </summary>
    public void SetEffectiveRateScale(string provider, double scale)
    {
        if (!_slotsByProvider.TryGetValue(provider, out var slots)) return;
        lock (slots)
            foreach (var s in slots)
                Writer.SetEffectiveRate(s.Index, (float)(s.NominalHz * scale));
    }

    /// <summary>
    /// Mark every reading a provider owns N/A. <see cref="ProviderHost"/> calls this when a
    /// provider fails or stops polling: without it, the last value a dead provider wrote keeps a
    /// valid timestamp and reads as live forever.
    ///
    /// Two families are deliberately left out. <b>Static</b> metrics are facts about the machine
    /// written once at discovery, not readings, and having no cadence they are not in this list at
    /// all. <b>.max companions</b> are session extrema — the peak really was observed, and staling
    /// one is unrecoverable anyway, because <see cref="Set"/> only republishes a max when a later
    /// sample beats it. Only <see cref="ResetMax"/> clears those.
    /// </summary>
    public void MarkProviderStale(string provider)
    {
        if (!_slotsByProvider.TryGetValue(provider, out var slots)) return;
        lock (slots)
            foreach (var s in slots)
                if (!s.IsMax) Writer.MarkStale(s.Index);
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
