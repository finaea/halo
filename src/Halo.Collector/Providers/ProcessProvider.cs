using System.Diagnostics;
using System.Runtime.InteropServices;
using Halo.Metrics;
using Halo.Shared.Config;

namespace Halo.Collector.Providers;

/// <summary>
/// Process snapshot via one NtQuerySystemInformation(SystemProcessInformation) call:
/// process count + top-N by CPU and by RAM (plan §5: ~ms per snapshot, cap 2 Hz).
/// Both rankings (per instance and same-name aggregated) are always published; each Top widget
/// picks one, so its toggle costs nothing here.
/// </summary>
public sealed unsafe class ProcessProvider : ISensorProvider
{
    public string Name => "process";
    public double MaxRateHz => 2;
    public double DefaultRateHz => 1;

    /// <summary>Ranks published per ranking. Widgets show between 1 and this many rows; the
    /// collector always publishes the full set so changing a widget's row count is instant
    /// and costs the collector nothing (v1 published 5 and had a config key that nothing read).</summary>
    private const int Ranks = 10;

    private byte[] _buffer = new byte[1 << 20];
    private readonly Dictionary<ulong, long> _prevCpuTime = new();
    private readonly Dictionary<ulong, long> _currCpuTime = new();
    private long _prevQpc;

    // perf-counter style display names for a few special processes
    private static readonly Dictionary<string, string> Renames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MemCompression"] = "Memory Compression",
        [""] = "System Idle",
    };

    public bool Initialize(MetricSink sink)
    {
        sink.Register(MetricNames.ProcCount, MetricType.Double, MetricUnit.Count, Name, DefaultRateHz);
        for (int i = 0; i < Ranks; i++)
        {
            foreach (bool agg in (bool[])[false, true])
            {
                sink.Register(MetricNames.TopCpuName(i, agg), MetricType.String, MetricUnit.Text, Name, DefaultRateHz);
                // CPU% is the process's share of the interval between two snapshots.
                sink.Register(MetricNames.TopCpuPct(i, agg), MetricType.Double, MetricUnit.Percent, Name, DefaultRateHz, MetricSemantics.IntervalAvg);
                sink.Register(MetricNames.TopCpuRamB(i, agg), MetricType.Double, MetricUnit.Bytes, Name, DefaultRateHz);
                sink.Register(MetricNames.TopRamName(i, agg), MetricType.String, MetricUnit.Text, Name, DefaultRateHz);
                sink.Register(MetricNames.TopRamB(i, agg), MetricType.Double, MetricUnit.Bytes, Name, DefaultRateHz);
                sink.Register(MetricNames.TopRamCpuPct(i, agg), MetricType.Double, MetricUnit.Percent, Name, DefaultRateHz, MetricSemantics.IntervalAvg);
            }
        }
        _prevQpc = Stopwatch.GetTimestamp();
        Poll(sink); // prime deltas
        return true;
    }

    private record struct Proc(string Name, ulong Pid, long WorkingSet, double CpuPct);

    public void Poll(MetricSink sink)
    {
        // fetch snapshot (grow buffer on STATUS_INFO_LENGTH_MISMATCH)
        uint returned = 0;
        int status;
        while (true)
        {
            fixed (byte* p = _buffer)
                status = NtQuerySystemInformation(5 /*SystemProcessInformation*/, p, (uint)_buffer.Length, out returned);
            if (status == unchecked((int)0xC0000004)) { _buffer = new byte[_buffer.Length * 2]; continue; }
            if (status != 0) throw new InvalidOperationException($"NtQuerySystemInformation(process) 0x{status:X8}");
            break;
        }

        long nowQpc = Stopwatch.GetTimestamp();
        double wallSeconds = (double)(nowQpc - _prevQpc) / Stopwatch.Frequency;
        _prevQpc = nowQpc;
        // 100ns CPU-time units consumed per second of wall per 1 core = 1e7; % of whole machine:
        double denom = wallSeconds * 1e7 * Environment.ProcessorCount;

        var procs = new List<Proc>(512);
        int count = 0;
        _currCpuTime.Clear();

        fixed (byte* basePtr = _buffer)
        {
            byte* p = basePtr;
            while (true)
            {
                uint next = *(uint*)p;
                ulong pid = *(ulong*)(p + 0x50);
                long user = *(long*)(p + 0x28);
                long kernel = *(long*)(p + 0x30);
                // private working set (parity with UsageMonitor Alias=RAM = "Working Set - Private";
                // full working set at 0x90 double-counts shared pages)
                long ws = *(long*)(p + 0x08);

                // ImageName UNICODE_STRING at 0x38: Length(2) MaxLength(2) pad(4) Buffer(8)
                ushort nameLen = *(ushort*)(p + 0x38);
                char* nameBuf = *(char**)(p + 0x40);
                string name = nameLen > 0 && nameBuf != null ? new string(nameBuf, 0, nameLen / 2) : "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                if (Renames.TryGetValue(name, out var pretty)) name = pretty;

                if (pid != 0) // skip Idle for count parity with perf counters ("Processes" counter excludes Idle/Total)
                {
                    count++;
                    long cpu = user + kernel;
                    _currCpuTime[pid] = cpu;
                    double cpuPct = 0;
                    if (denom > 0 && _prevCpuTime.TryGetValue(pid, out long prev) && cpu >= prev)
                        cpuPct = (cpu - prev) / denom * 100.0;
                    procs.Add(new Proc(name, pid, ws, cpuPct));
                }

                if (next == 0) break;
                p += next;
                if (p < basePtr || p >= basePtr + returned) break; // corrupt chain guard
            }
        }

        _prevCpuTime.Clear();
        foreach (var kv in _currCpuTime) _prevCpuTime[kv.Key] = kv.Value;

        sink.Set(MetricNames.ProcCount, count);

        // Publish BOTH rankings; each Top widget picks per its own "aggregate" option.
        var aggregated = procs
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Proc(g.Key, 0, g.Sum(x => x.WorkingSet), g.Sum(x => x.CpuPct)))
            .ToList();

        PublishRanking(sink, procs, agg: false);
        PublishRanking(sink, aggregated, agg: true);
    }

    private void PublishRanking(MetricSink sink, List<Proc> ranked, bool agg)
    {
        var topCpu = ranked.OrderByDescending(x => x.CpuPct).Take(Ranks).ToList();
        var topRam = ranked.OrderByDescending(x => x.WorkingSet).Take(Ranks).ToList();
        for (int i = 0; i < Ranks; i++)
        {
            if (i < topCpu.Count)
            {
                sink.SetString(MetricNames.TopCpuName(i, agg), topCpu[i].Name);
                sink.Set(MetricNames.TopCpuPct(i, agg), topCpu[i].CpuPct);
                sink.Set(MetricNames.TopCpuRamB(i, agg), topCpu[i].WorkingSet);
            }
            if (i < topRam.Count)
            {
                sink.SetString(MetricNames.TopRamName(i, agg), topRam[i].Name);
                sink.Set(MetricNames.TopRamB(i, agg), topRam[i].WorkingSet);
                sink.Set(MetricNames.TopRamCpuPct(i, agg), topRam[i].CpuPct);
            }
        }
    }

    public void Dispose() { }

    [DllImport("ntdll")]
    private static extern int NtQuerySystemInformation(int infoClass, void* info, uint size, out uint returned);
}
