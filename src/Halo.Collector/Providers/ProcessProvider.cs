using System.Diagnostics;
using System.Runtime.InteropServices;
using Halo.Shared.Config;
using Halo.Shared.Metrics;

namespace Halo.Collector.Providers;

/// <summary>
/// Process snapshot via one NtQuerySystemInformation(SystemProcessInformation) call:
/// process count + top-N by CPU and by RAM (plan §5: ~ms per snapshot, cap 2 Hz).
/// </summary>
public sealed unsafe class ProcessProvider(GeneralSettings settings) : ISensorProvider
{
    public string Name => "process";
    public double MaxRateHz => 2;
    public double DefaultRateHz => 1;

    private byte[] _buffer = new byte[1 << 20];
    private readonly Dictionary<ulong, long> _prevCpuTime = new();
    private readonly Dictionary<ulong, long> _currCpuTime = new();
    private long _prevQpc;
    private int _topN;

    // perf-counter style display names for a few special processes
    private static readonly Dictionary<string, string> Renames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MemCompression"] = "Memory Compression",
        [""] = "System Idle",
    };

    public bool Initialize(MetricSink sink)
    {
        _topN = Math.Clamp(settings.TopProcessCount, 1, 5);
        sink.Register(MetricNames.ProcCount, MetricType.Double, MetricUnit.Count, Name, MaxRateHz);
        for (int i = 0; i < 5; i++)
        {
            sink.Register(MetricNames.TopCpuName(i), MetricType.String, MetricUnit.Text, Name, MaxRateHz);
            sink.Register(MetricNames.TopCpuPct(i), MetricType.Double, MetricUnit.Percent, Name, MaxRateHz);
            sink.Register(MetricNames.TopCpuRamB(i), MetricType.Double, MetricUnit.Bytes, Name, MaxRateHz);
            sink.Register(MetricNames.TopRamName(i), MetricType.String, MetricUnit.Text, Name, MaxRateHz);
            sink.Register(MetricNames.TopRamB(i), MetricType.Double, MetricUnit.Bytes, Name, MaxRateHz);
            sink.Register(MetricNames.TopRamCpuPct(i), MetricType.Double, MetricUnit.Percent, Name, MaxRateHz);
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
                long ws = *(long*)(p + 0x90);

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

        // aggregate same-name processes like UsageMonitor does? Rainmeter UsageMonitor sums
        // per-instance; the screenshot shows single processes (svchost 1.1% is the max single
        // instance under perf-counter naming). We aggregate by name — matches Task Manager
        // grouping and reads better; per-instance duplicates in a top list are noise.
        var byName = procs
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Proc(g.Key, 0, g.Sum(x => x.WorkingSet), g.Sum(x => x.CpuPct)))
            .ToList();

        var topCpu = byName.OrderByDescending(x => x.CpuPct).Take(5).ToList();
        var topRam = byName.OrderByDescending(x => x.WorkingSet).Take(5).ToList();

        for (int i = 0; i < 5; i++)
        {
            if (i < topCpu.Count)
            {
                sink.SetString(MetricNames.TopCpuName(i), topCpu[i].Name);
                sink.Set(MetricNames.TopCpuPct(i), topCpu[i].CpuPct);
                sink.Set(MetricNames.TopCpuRamB(i), topCpu[i].WorkingSet);
            }
            if (i < topRam.Count)
            {
                sink.SetString(MetricNames.TopRamName(i), topRam[i].Name);
                sink.Set(MetricNames.TopRamB(i), topRam[i].WorkingSet);
                sink.Set(MetricNames.TopRamCpuPct(i), topRam[i].CpuPct);
            }
        }
    }

    public void Dispose() { }

    [DllImport("ntdll")]
    private static extern int NtQuerySystemInformation(int infoClass, void* info, uint size, out uint returned);
}
