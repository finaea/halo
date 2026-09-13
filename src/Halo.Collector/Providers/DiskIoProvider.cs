using System.Runtime.InteropServices;
using Halo.Metrics;
using Halo.Shared;

namespace Halo.Collector.Providers;

/// <summary>
/// Per-volume read/write B/s, via PDH LogicalDisk counters
/// (english names, locale-safe). PDH computes rates between our collects, so each published
/// value is a real average over exactly one poll period.
///
/// The raw byte counters are updated per I/O completion, NOT on a ~1 s tick (an earlier comment
/// here claimed the latter — measured wrong 2026-09-13: sampling the raw accumulator every
/// 100 ms under sustained writes moved on every single sample, and even caught lone 16 KB
/// background writes while otherwise idle). So the poll rate really does set the resolution:
/// short bursts are averaged across the poll period and under-reported at a lower rate.
/// </summary>
public sealed class DiskIoProvider : ISensorProvider
{
    public string Name => "disk-io";
    public double MaxRateHz => 64;
    public double DefaultRateHz => 5;

    private nint _query;
    private readonly List<(char Letter, nint Read, nint Write)> _counters = new();
    private bool _primed;
    private string _activeLetters = "";

    public bool Initialize(MetricSink sink) => BuildCounters(sink);

    private bool BuildCounters(MetricSink sink)
    {
        if (_query != 0) { PdhCloseQuery(_query); _query = 0; }
        if (PdhOpenQueryW(null, 0, out _query) != 0) return false;

        _counters.Clear();
        foreach (char c in Volumes.Local())
        {
            nint r = Add($"\\LogicalDisk({c}:)\\Disk Read Bytes/sec");
            nint w = Add($"\\LogicalDisk({c}:)\\Disk Write Bytes/sec");
            if (r == 0 && w == 0) continue;
            _counters.Add((c, r, w));
            // PDH computes the rate between our two collects, so the value is a real average
            // over exactly one poll period — not an instantaneous reading.
            sink.Register(MetricNames.DriveReadBps(c), MetricType.Double, MetricUnit.BytesPerSecond, Name,
                DefaultRateHz, MetricSemantics.IntervalAvg);
            sink.Register(MetricNames.DriveWriteBps(c), MetricType.Double, MetricUnit.BytesPerSecond, Name,
                DefaultRateHz, MetricSemantics.IntervalAvg);
        }
        _primed = false;
        // Commit the change-detector only when we actually bound counters — otherwise a volume
        // that isn't mounted yet would be recorded as "handled" and never retried.
        if (_counters.Count > 0) _activeLetters = Volumes.Key(_counters.Select(c => c.Letter));
        return _counters.Count > 0;
    }

    private nint Add(string path)
    {
        return PdhAddEnglishCounterW(_query, path, 0, out nint counter) == 0 ? counter : 0;
    }

    public void Poll(MetricSink sink)
    {
        // Hot-plug: rebuild the PDH query when the set of volumes changes so a newly-attached
        // drive starts publishing read/write without a restart.
        string live = Volumes.Key(Volumes.Local());
        if (live != _activeLetters)
        {
            Log.Info($"disk-io: volumes changed ({_activeLetters} -> {live}) — rebuilding counters");
            if (!BuildCounters(sink)) return;   // no valid counters yet; try again next poll
        }

        int status = PdhCollectQueryData(_query);
        if (status != 0) throw new InvalidOperationException($"PdhCollectQueryData 0x{status:X8}");
        if (!_primed) { _primed = true; return; } // rates need two collections

        foreach (var (c, r, w) in _counters)
        {
            if (r != 0) sink.Set(MetricNames.DriveReadBps(c), Value(r));
            if (w != 0) sink.Set(MetricNames.DriveWriteBps(c), Value(w));
        }
    }

    private static double Value(nint counter)
    {
        var v = new PDH_FMT_COUNTERVALUE();
        return PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out _, ref v) == 0 && v.CStatus == 0 ? v.doubleValue : 0;
    }

    public void Dispose()
    {
        if (_query != 0) { PdhCloseQuery(_query); _query = 0; }
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200;

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }

    [DllImport("pdh", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? szDataSource, nuint dwUserData, out nint phQuery);

    [DllImport("pdh", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(nint hQuery, string szFullCounterPath, nuint dwUserData, out nint phCounter);

    [DllImport("pdh")]
    private static extern int PdhCollectQueryData(nint hQuery);

    [DllImport("pdh")]
    private static extern int PdhGetFormattedCounterValue(nint hCounter, uint dwFormat, out uint lpdwType, ref PDH_FMT_COUNTERVALUE pValue);

    [DllImport("pdh")]
    private static extern int PdhCloseQuery(nint hQuery);
}
