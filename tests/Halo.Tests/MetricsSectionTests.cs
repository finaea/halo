using Halo.Metrics;
using Xunit;

namespace Halo.Tests;

/// <summary>
/// Ticket 01, finding 5 — a string metric honours the staleness marker.
///
/// <para><b>Every writer here is pointed at a private section</b> through the third constructor
/// argument. Constructing a <see cref="MetricsWriter"/> clears the whole section, so a test that
/// used the default would wipe a running collector's metrics — no window, no process name, nothing
/// to notice. Shadowing <c>SharedMemoryLayout</c> does not help once Halo.Metrics is a
/// ProjectReference: the name is a <c>const</c>, baked into Halo.Metrics.dll at its own compile
/// time (measured 2026-09-14). The name has to be passed in.</para>
///
/// <para>That constructor is <c>internal</c>, reachable only because Halo.Metrics grants
/// <c>InternalsVisibleTo("Halo.Tests")</c> — the package's public surface is a published
/// third-party contract and stays frozen. <b>So this project must keep the assembly name
/// Halo.Tests</b>; renaming it breaks this file with a compile error, which is the intended
/// loud failure. <see cref="NativeSectionTests"/> has no such dependency — it drives
/// <c>NativeSection</c> directly, whose section name has always been an ordinary parameter.</para>
/// </summary>
public class MetricsSectionTests
{
    /// <summary>Unique per test, so xUnit's parallel runner cannot make two tests share a section.</summary>
    private static string Section([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => $@"Local\Halo.Tests.{Environment.ProcessId}.{caller}";

    [Fact]
    public void MarkStale_on_a_string_metric_is_visible_to_the_reader()
    {
        string section = Section();
        using var writer = new MetricsWriter("test", null, section);
        int ip = writer.Register(new MetricDescriptor(
            "test.ip.external", MetricType.String, MetricUnit.Text, "test", 1));
        writer.SetString(ip, "203.0.113.7");
        writer.MarkReady();

        using var reader = new MetricsReader(section);
        Assert.True(reader.TryAttach());
        int i = reader.ResolveIndex("test.ip.external");

        Assert.True(reader.TryReadString(i, out string before));
        Assert.Equal("203.0.113.7", before);

        // The bug: TryReadString used to return the seqlock payload without ever looking at the
        // value slot's timestamp, which is the only thing MarkStale touches. A metric the collector
        // had marked N/A kept reading back as live for the rest of the session.
        writer.MarkStale(ip);
        Assert.False(reader.TryReadString(i, out _));

        // Staling is not a one-way door.
        writer.SetString(ip, "198.51.100.42");
        Assert.True(reader.TryReadString(i, out string after));
        Assert.Equal("198.51.100.42", after);
    }

    [Fact]
    public void Staled_string_falls_back_to_the_callers_default_through_CollectorSession()
    {
        // This is the surface the widgets actually call (MetricCache.Text -> GetText), and the
        // privacy case that made finding 5 matter: external IP lookup turned off must not leave
        // the user's public IP on screen.
        string section = Section();
        using var writer = new MetricsWriter("test", null, section);
        int ip = writer.Register(new MetricDescriptor(
            "test.ip.external", MetricType.String, MetricUnit.Text, "test", 1));
        writer.SetString(ip, "203.0.113.7");
        writer.MarkReady();

        using var session = new CollectorSession(null, 4096, section);
        session.Poll();
        Assert.Equal("203.0.113.7", session.GetText("test.ip.external", "N/A"));

        writer.MarkStale(ip);
        Assert.Equal("N/A", session.GetText("test.ip.external", "N/A"));
    }

    [Fact]
    public void Never_written_metrics_read_as_absent()
    {
        string section = Section();
        using var writer = new MetricsWriter("test", null, section);
        writer.Register(new MetricDescriptor("test.name", MetricType.String, MetricUnit.Text, "test", 1));
        writer.Register(new MetricDescriptor("test.temp.c", MetricType.Double, MetricUnit.Celsius, "test", 1));
        writer.MarkReady();

        using var reader = new MetricsReader(section);
        Assert.True(reader.TryAttach());
        Assert.False(reader.TryReadString(reader.ResolveIndex("test.name"), out _));
        Assert.False(reader.TryRead(reader.ResolveIndex("test.temp.c"), out _, out _));
    }

    [Fact]
    public void Double_round_trips_and_stales()
    {
        string section = Section();
        using var writer = new MetricsWriter("test", null, section);
        int i = writer.Register(new MetricDescriptor("test.temp.c", MetricType.Double, MetricUnit.Celsius, "test", 1));
        writer.Set(i, 48.5);
        writer.MarkReady();

        using var reader = new MetricsReader(section);
        Assert.True(reader.TryAttach());
        Assert.True(reader.TryRead(reader.ResolveIndex("test.temp.c"), out double v, out double age));
        Assert.Equal(48.5, v, 9);
        Assert.InRange(age, 0, 5);

        writer.MarkStale(i);
        Assert.False(reader.TryRead(reader.ResolveIndex("test.temp.c"), out _, out _));
    }
}
