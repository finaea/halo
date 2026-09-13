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

    /// <summary>The cadence this provider is polled at. A fixed engineering constant, not a
    /// user setting: nobody outside this codebase can judge what a sensor class can take
    /// (rates plan R1).</summary>
    double DefaultRateHz { get; }

    /// <summary>True when this provider's data needs an elevated collector. Published in the
    /// section's provider table so the System check can explain an empty panel.</summary>
    bool NeedsElevation => false;

    /// <summary>Short reason code (see Halo.Metrics.ProviderError) explaining the last failed
    /// Initialize; null while the provider is healthy.</summary>
    string? UnavailableReason => null;

    /// <summary>
    /// Initialise hardware access and register metrics via the sink.
    /// Returning false marks the provider unavailable (its metrics read N/A); the host
    /// retries Initialize with backoff so hot-plug / service-start recovers it.
    /// </summary>
    bool Initialize(MetricSink sink);

    /// <summary>One poll tick: read hardware, push values through the sink.</summary>
    void Poll(MetricSink sink);
}
