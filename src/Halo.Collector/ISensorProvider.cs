using Halo.Shared.Metrics;

namespace Halo.Collector;

/// <summary>
/// The maintainability contract (plan §4): vendor swaps are one-module changes.
/// A provider owns a set of metrics and is polled on its own cadence by the ProviderHost.
/// </summary>
public interface ISensorProvider : IDisposable
{
    string Name { get; }

    /// <summary>Hard rate ceiling for this provider's sensor class (plan §5 table).</summary>
    double MaxRateHz { get; }

    /// <summary>Default poll rate (may be lower than the ceiling).</summary>
    double DefaultRateHz { get; }

    /// <summary>
    /// Initialise hardware access and register metrics via the sink.
    /// Returning false marks the provider unavailable (its metrics read N/A); the host
    /// retries Initialize with backoff so hot-plug / service-start recovers it.
    /// </summary>
    bool Initialize(MetricSink sink);

    /// <summary>One poll tick: read hardware, push values through the sink.</summary>
    void Poll(MetricSink sink);
}
