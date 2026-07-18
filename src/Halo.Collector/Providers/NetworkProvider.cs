using System.Net.NetworkInformation;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Shared.Metrics;

namespace Halo.Collector.Providers;

/// <summary>
/// Network throughput from interface octet counters (delta / dt), plus session totals and
/// peaks. "Best" interface = the operational non-virtual interface carrying the default route.
/// </summary>
public sealed class NetworkProvider(GeneralSettings settings) : ISensorProvider
{
    public string Name => "network";
    public double MaxRateHz => 64;
    public double DefaultRateHz => 10;

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
        sink.RegisterWithMax(MetricNames.NetDownBps, MetricUnit.BytesPerSecond, Name, MaxRateHz);
        sink.RegisterWithMax(MetricNames.NetUpBps, MetricUnit.BytesPerSecond, Name, MaxRateHz);
        sink.Register(MetricNames.NetDownTotalB, MetricType.Double, MetricUnit.Bytes, Name, MaxRateHz);
        sink.Register(MetricNames.NetUpTotalB, MetricType.Double, MetricUnit.Bytes, Name, MaxRateHz);
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
            string prefer = settings.NetworkInterface;
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
        catch (Exception ex) { Log.Warn($"nic enumeration: {ex.Message}"); }

        if (best?.Id != _nic?.Id)
        {
            _nic = best;
            _prevRx = _prevTx = -1;
            Log.Info($"network interface: {best?.Name ?? "none"}");
        }
        else if (best != null)
        {
            _nic = best; // refresh the object; stats objects are snapshots
        }
    }

    public void Poll(MetricSink sink)
    {
        if (DateTime.UtcNow >= _nextNicRefresh || _nic == null) PickNic();
        if (_nic == null) return;

        IPInterfaceStatistics stats;
        try { stats = _nic.GetIPStatistics(); }
        catch { _nic = null; return; }

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
        }
        _prevRx = rx; _prevTx = tx;

        sink.Set(MetricNames.NetDownTotalB, Math.Max(0, rx - _startRx));
        sink.Set(MetricNames.NetUpTotalB, Math.Max(0, tx - _startTx));
    }

    public void Dispose() { }
}
