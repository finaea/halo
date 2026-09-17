using System.Net.NetworkInformation;
using Halo.Metrics;
using Halo.Shared;
using Halo.Shared.Config;

namespace Halo.Collector.Providers;

/// <summary>
/// Network throughput from interface octet counters (delta / dt), plus session totals and
/// peaks. "Best" interface = the operational non-virtual interface carrying the default route.
///
/// The octet counters update far faster than the poll rate (measured 2026-09-13: sampling
/// GetIPStatistics().BytesReceived every 100 ms under a sustained download moved on every
/// sample, and still resolved ~90-byte ambient traffic once idle), so the poll rate sets the
/// resolution — each published value is a real average over one poll period, and short bursts
/// are under-reported in proportion to how long that period is.
/// </summary>
/// <remarks>
/// Takes the <see cref="ConfigStore"/>, not a settings snapshot: <c>Reload()</c> allocates a new
/// settings object, so a captured one stops seeing edits after the first hot-reload — the UI
/// said "live" and the adapter choice never moved (assessment §4.3).
/// </remarks>
public sealed class NetworkProvider(ConfigStore config) : ISensorProvider
{
    private static readonly ComponentLog Log2 = Log.For("network");

    public string Name => "network";
    public double MaxRateHz => 64;
    public double DefaultRateHz => CollectorRates.Network;

    private static volatile bool _resetRequested;
    public static void RequestTotalsReset() => _resetRequested = true;

    private NetworkInterface? _nic;
    private long _prevRx = -1, _prevTx = -1;
    private long _startRx, _startTx;
    private long _resetBaseRx, _resetBaseTx;
    private System.Diagnostics.Stopwatch _sw = new();
    private DateTime _nextNicRefresh = DateTime.MinValue;

    public bool Initialize(MetricSink sink)
    {
        sink.RegisterWithMax(MetricNames.NetDownBps, MetricUnit.BytesPerSecond, Name, DefaultRateHz, MetricSemantics.IntervalAvg);
        sink.RegisterWithMax(MetricNames.NetUpBps, MetricUnit.BytesPerSecond, Name, DefaultRateHz, MetricSemantics.IntervalAvg);
        sink.Register(MetricNames.NetDownTotalB, MetricType.Double, MetricUnit.Bytes, Name, DefaultRateHz, MetricSemantics.Cumulative);
        sink.Register(MetricNames.NetUpTotalB, MetricType.Double, MetricUnit.Bytes, Name, DefaultRateHz, MetricSemantics.Cumulative);
        PickNic();
        return true;
    }

    private void PickNic()
    {
        _nextNicRefresh = DateTime.UtcNow.AddSeconds(30);
        NetworkInterface? best = null;
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces();
            string prefer = config.Settings.Collector.NetworkInterface;
            foreach (var ni in all)
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (prefer != "Best" && !string.Equals(ni.Name, prefer, StringComparison.OrdinalIgnoreCase)) continue;
                // prefer the one with a default gateway
                bool hasGw = ni.GetIPProperties().GatewayAddresses.Count > 0;
                if (best == null || (hasGw && best.GetIPProperties().GatewayAddresses.Count == 0))
                    best = ni;
            }
        }
        catch (Exception ex) { Log2.Warn($"nic enumeration: {ex.Message}"); }

        if (best?.Id != _nic?.Id)
        {
            _nic = best;
            _prevRx = _prevTx = -1;
            Log2.Info($"interface: {best?.Name ?? "none"}");
        }
        else if (best != null)
        {
            _nic = best; // refresh the object; stats objects are snapshots
        }
    }

    /// <summary>Throughput is a reading off an interface: with no interface, or one whose counters
    /// we cannot read, there is no rate — not a rate of zero. The session totals are left alone;
    /// they are a running count and their last value stays true.</summary>
    private static void NoRates(MetricSink sink)
    {
        sink.MarkStale(MetricNames.NetDownBps);
        sink.MarkStale(MetricNames.NetUpBps);
    }

    public void Poll(MetricSink sink)
    {
        if (DateTime.UtcNow >= _nextNicRefresh || _nic == null) PickNic();
        if (_nic == null) { NoRates(sink); return; }

        IPInterfaceStatistics stats;
        try { stats = _nic.GetIPStatistics(); }
        catch { _nic = null; NoRates(sink); return; }

        long rx = stats.BytesReceived, tx = stats.BytesSent;
        double dt = _sw.IsRunning ? _sw.Elapsed.TotalSeconds : 0;
        _sw.Restart();

        if (_resetRequested)
        {
            _resetRequested = false;
            _resetBaseRx = rx - _startRx; // fold current session into the base
            _resetBaseTx = tx - _startTx;
            _startRx = rx; _startTx = tx;
            _resetBaseRx = 0; _resetBaseTx = 0;
            sink.ResetMax("net.");
        }

        if (_prevRx >= 0 && dt > 0.001)
        {
            double down = Math.Max(0, rx - _prevRx) / dt;
            double up = Math.Max(0, tx - _prevTx) / dt;
            sink.Set(MetricNames.NetDownBps, down);
            sink.Set(MetricNames.NetUpBps, up);
        }
        else
        {
            _startRx = rx; _startTx = tx; // first sample of this nic = session base
            NoRates(sink);                // a rate needs two samples; there is none yet
        }
        _prevRx = rx; _prevTx = tx;

        sink.Set(MetricNames.NetDownTotalB, Math.Max(0, rx - _startRx));
        sink.Set(MetricNames.NetUpTotalB, Math.Max(0, tx - _startTx));
    }

    public void Dispose() { }
}
