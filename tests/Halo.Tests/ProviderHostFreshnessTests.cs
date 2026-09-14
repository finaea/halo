using Halo.Collector;
using Halo.Metrics;
using Xunit;

namespace Halo.Tests;

/// <summary>
/// Ticket 01, finding 4a — the provider host stales a failed provider's readings.
///
/// Drives the real <see cref="ProviderHost"/> with a fake provider rather than testing a copy of
/// the logic. Same private-section rule as <see cref="MetricsSectionTests"/>: every writer is
/// pointed away from the live collector's section.
/// </summary>
public class ProviderHostFreshnessTests
{
    private static string Section([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => $@"Local\Halo.Tests.{Environment.ProcessId}.{caller}";

    /// <summary>A provider we can break on demand. Each instance owns its own provider name and
    /// metric family, so one test can never be satisfied by another's leftovers.</summary>
    private sealed class Fake(string name, double rateHz) : ISensorProvider
    {
        public enum Mode { Ok, Throw, Hang }

        public volatile Mode State = Mode.Ok;
        public string Name => name;
        public double MaxRateHz => 64;
        public double DefaultRateHz => rateHz;
        public bool RescanReinitialises => false;

        public string Temp => $"{name}.temp.c";
        public string Power => $"{name}.power.w";
        public string Count => $"{name}.count";

        public bool Initialize(MetricSink sink)
        {
            sink.Register(Temp, MetricType.Double, MetricUnit.Celsius, Name, DefaultRateHz);
            sink.RegisterWithMax(Power, MetricUnit.Watts, Name, DefaultRateHz);
            sink.Register(Count, MetricType.Double, MetricUnit.Count, Name, 0, MetricSemantics.Static);
            sink.Set(Count, 3);
            return true;
        }

        public void Poll(MetricSink sink)
        {
            switch (State)
            {
                case Mode.Throw: throw new InvalidOperationException("fake poll failure");
                case Mode.Hang: Thread.Sleep(Timeout.Infinite); break;
            }
            sink.Set(Temp, 48);
            sink.Set(Power, 120);
        }

        public void Dispose() { }
    }

    private static bool WaitFor(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    [Fact]
    public void MarkProviderStale_clears_readings_but_spares_max_and_static()
    {
        using var writer = new MetricsWriter("test", null, Section());
        var sink = new MetricSink(writer);
        var p = new Fake("fakeA", 20);
        p.Initialize(sink);
        sink.Set(p.Temp, 48);
        sink.Set(p.Power, 120);

        sink.MarkProviderStale(p.Name);

        Assert.False(sink.TryGet(p.Temp, out _));

        // A session peak really was observed, and staling one is a one-way door: MetricSink.Set
        // only republishes a max when a later sample beats it, so a staled max whose base then
        // reads lower would be N/A for the rest of the session. Only ResetMax clears these.
        Assert.True(sink.TryGet(p.Power + MetricNames.MaxSuffix, out double max));
        Assert.Equal(120, max, 9);

        // Static metrics are facts about the machine, not readings — and having no cadence they
        // are not in the provider's slot list at all.
        Assert.True(sink.TryGet(p.Count, out double count));
        Assert.Equal(3, count, 9);
    }

    [Fact]
    public void Rule1_a_throwing_poll_stales_the_provider_and_recovery_republishes()
    {
        using var writer = new MetricsWriter("test", null, Section());
        var sink = new MetricSink(writer);
        var p = new Fake("fakeB", 20);

        using var host = new ProviderHost(sink);
        host.Add(p);
        Assert.True(WaitFor(() => sink.TryGet(p.Temp, out double v) && Math.Abs(v - 48) < 1e-9, 5000),
            "provider never published while healthy");

        p.State = Fake.Mode.Throw;
        Assert.True(WaitFor(() => !sink.TryGet(p.Temp, out _), 5000),
            "a throwing poll left its last reading looking live");
        Assert.True(sink.TryGet(p.Power + MetricNames.MaxSuffix, out _), "rule 1 must not touch .max");

        p.State = Fake.Mode.Ok;
        Assert.True(WaitFor(() => sink.TryGet(p.Temp, out _), 5000),
            "a recovered provider never republished");
    }

    /// <summary>
    /// Rule 2 — the backstop for a provider wedged inside a native call that never returns. It has
    /// no catch block to reach, so nothing on its own thread can report it.
    ///
    /// <b>This test really does take ~10 s</b>, because the bound it verifies is 10 s and the point
    /// is that it does *not* fire early — a bound that trips on an ordinary hiccup would blank
    /// working sensors. Filter it out of a fast loop with <c>--filter Speed!=Slow</c>.
    /// </summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Rule2_a_hung_provider_goes_NA_at_the_bound_and_not_before()
    {
        using var writer = new MetricsWriter("test", null, Section());
        var sink = new MetricSink(writer);
        var p = new Fake("fakeC", 2);   // bound = max(10 s, 10/2 = 5 s) = the 10 s floor

        using var host = new ProviderHost(sink);
        host.Add(p);
        Assert.True(WaitFor(() => sink.TryGet(p.Temp, out _), 5000), "provider never published");

        p.State = Fake.Mode.Hang;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool staled = WaitFor(() => !sink.TryGet(p.Temp, out _), 25000);
        double took = sw.Elapsed.TotalSeconds;

        Assert.True(staled, $"a wedged provider still read as live after {took:0.#} s");
        Assert.InRange(took, 9, 14);
        Assert.True(sink.TryGet(p.Power + MetricNames.MaxSuffix, out _), "rule 2 must not touch .max");
        // The wedged thread is a background thread and dies with the test process.
    }
}
