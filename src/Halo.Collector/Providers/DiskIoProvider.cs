using System.Runtime.InteropServices;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Shared.Metrics;

namespace Halo.Collector.Providers;

/// <summary>
/// Per-volume read/write B/s + activity %, via PDH LogicalDisk counters
/// (english names, locale-safe). Counters tick at ~1 s kernel granularity internally,
/// PDH computes rates between our collects.
/// </summary>
public sealed class DiskIoProvider(GeneralSettings settings) : ISensorProvider
{
    public string Name => "disk-io";
    public double MaxRateHz => 64;
    public double DefaultRateHz => 10;

    private nint _query;
    private readonly List<(char Letter, nint Read, nint Write, nint Busy)> _counters = new();
    private bool _primed;

    public bool Initialize(MetricSink sink)
    {
        if (PdhOpenQueryW(null, 0, out _query) != 0) return false;

        _counters.Clear();
        foreach (string s in settings.DriveLetters)
        {
            char c = char.ToUpperInvariant(s[0]);
            nint r = Add($"\\LogicalDisk({c}:)\\Disk Read Bytes/sec");
            nint w = Add($"\\LogicalDisk({c}:)\\Disk Write Bytes/sec");
            nint b = Add($"\\LogicalDisk({c}:)\\% Disk Time");
            if (r == 0 && w == 0) continue;
            _counters.Add((c, r, w, b));
            sink.Register(MetricNames.DriveReadBps(c), MetricType.Double, MetricUnit.BytesPerSecond, Name, MaxRateHz);
            sink.Register(MetricNames.DriveWriteBps(c), MetricType.Double, MetricUnit.BytesPerSecond, Name, MaxRateHz);
            sink.Register(MetricNames.DriveActivityPct(c), MetricType.Double, MetricUnit.Percent, Name, MaxRateHz);
        }
        _primed = false;
        return _counters.Count > 0;
    }

    private nint Add(string path)
    {
        return PdhAddEnglishCounterW(_query, path, 0, out nint counter) == 0 ? counter : 0;
    }

    public void Poll(MetricSink sink)
    {
        int status = PdhCollectQueryData(_query);
        if (status != 0) throw new InvalidOperationException($"PdhCollectQueryData 0x{status:X8}");
        if (!_primed) { _primed = true; return; } // rates need two collections

        foreach (var (c, r, w, b) in _counters)
        {
            if (r != 0) sink.Set(MetricNames.DriveReadBps(c), Value(r));
            if (w != 0) sink.Set(MetricNames.DriveWriteBps(c), Value(w));
            if (b != 0) sink.Set(MetricNames.DriveActivityPct(c), Math.Clamp(Value(b), 0, 100));
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
