using Halo.Metrics;

namespace Halo.Widgets;

/// <summary>
/// What a panel (and its window) reads, and nothing more. <see cref="MetricCache"/> is the only
/// implementation a running Halo ever uses; the render harness swaps in
/// <see cref="Harness.FixtureMetrics"/> so a panel can be drawn with no collector behind it.
///
/// Kept inside Halo.Widgets on purpose. <c>Halo.Metrics</c> is a published third-party contract
/// (CLAUDE.md), and an interface shaped by what Halo's own panels happen to call is not something
/// to make permanent there.
/// </summary>
public interface IMetricSource
{
    /// <summary>True when there is no live collector behind the numbers.</summary>
    bool Stale { get; }

    double Value(string name, double fallback = 0);

    /// <summary>Value + validity: false if missing/never-written/stale-marked.</summary>
    bool TryValue(string name, out double value, double maxAgeS = double.MaxValue);

    /// <summary>Does the collector publish this metric at all? True even when its current value is
    /// N/A — see <see cref="MetricCache.Has"/> for why the panels need the difference.</summary>
    bool Has(string name);

    string Text(string name, string fallback = "");

    /// <summary>Frames that arrived since the previous tick (chronological).</summary>
    ReadOnlySpan<FrameEntry> NewFrames { get; }

    IReadOnlyList<MetricInfo> Describe();
}
