using System.Runtime.InteropServices;
using Halo.Metrics;
using Halo.Shared;

namespace Halo.Collector.Providers;

/// <summary>
/// Per-core + total CPU usage from NtQuerySystemInformation(SystemProcessorPerformanceInformation).
/// Counters advance on the ~15.6 ms kernel tick, so rates beyond 64 Hz are duplicate reads
/// (plan §5 hard cap).
///
/// Also publishes the machine's CPU topology (<c>cpu.logical.count</c>,
/// <c>cpu.core.&lt;i&gt;.class</c>, <c>cpu.core.&lt;i&gt;.physical</c>) so a widget can lay out
/// P-cores and E-cores without knowing which chip it is running on (hardware plan H3).
/// </summary>
public sealed unsafe class CpuKernelProvider : ISensorProvider
{
    public string Name => "cpu-kernel";
    public double MaxRateHz => 64;
    public double DefaultRateHz => CollectorRates.CpuKernel;

    /// <summary>Initialize reads the processor topology, so `rescan` re-reads it.</summary>
    public bool RescanReinitialises => true;

    private int _coreCount;
    private int _groupCount = 1;
    private long[] _prevIdle = [];
    private long[] _prevBusy = []; // kernel+user (kernel includes idle; subtracted)
    private bool _shortReadLogged;

    public bool Initialize(MetricSink sink)
    {
        var topology = CpuTopology.Read();
        _coreCount = Math.Max(CpuTopology.LogicalCount, Environment.ProcessorCount);
        _groupCount = CpuTopology.GroupCount;
        _prevIdle = new long[_coreCount];
        _prevBusy = new long[_coreCount];
        _shortReadLogged = false;

        // Usage is the busy fraction between two reads, i.e. an average over the poll period.
        sink.Register(MetricNames.CpuTotalPct, MetricType.Double, MetricUnit.Percent, Name, DefaultRateHz, MetricSemantics.IntervalAvg);
        sink.Register(MetricNames.CpuLogicalCount, MetricType.Double, MetricUnit.Count, Name, 0, MetricSemantics.Static);
        for (int i = 0; i < _coreCount; i++)
            sink.Register(MetricNames.CpuCorePct(i), MetricType.Double, MetricUnit.Percent, Name, DefaultRateHz, MetricSemantics.IntervalAvg);
        sink.Set(MetricNames.CpuLogicalCount, _coreCount);

        PublishTopology(sink, topology);

        Poll(sink); // prime deltas
        return true;
    }

    /// <summary>
    /// One class + physical-core id per logical CPU. Static: written at discovery, never again.
    /// Skipped entirely when the OS call failed — a widget then sees no class metrics and falls
    /// back to a flat core list, which is better than publishing a guess.
    /// </summary>
    private void PublishTopology(MetricSink sink, IReadOnlyList<CpuTopology.Logical> topology)
    {
        if (topology.Count == 0)
        {
            Log.Warn("cpu topology unavailable — cpu.core.<i>.class/.physical not published");
            return;
        }
        foreach (var l in topology)
        {
            if (l.Index >= _coreCount) continue;
            sink.Register(MetricNames.CpuCoreClass(l.Index), MetricType.Double, MetricUnit.None, Name, 0, MetricSemantics.Static);
            sink.Register(MetricNames.CpuCorePhysical(l.Index), MetricType.Double, MetricUnit.Count, Name, 0, MetricSemantics.Static);
            sink.Set(MetricNames.CpuCoreClass(l.Index), l.Class);
            sink.Set(MetricNames.CpuCorePhysical(l.Index), l.PhysicalCore);
        }
        int perf = topology.Count(l => l.Class == 0);
        Log.Info($"cpu topology: {topology.Count} logical / {topology.Select(l => l.PhysicalCore).Distinct().Count()} physical" +
                 $" across {_groupCount} group(s); {perf} logical on performance cores, {topology.Count - perf} on efficiency cores");
    }

    public void Poll(MetricSink sink)
    {
        int entrySize = sizeof(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION);
        int bufSize = entrySize * _coreCount;
        byte* buf = stackalloc byte[bufSize];
        new Span<byte>(buf, bufSize).Clear();

        int n = _groupCount > 1 ? QueryAllGroups(buf, entrySize) : QuerySingleGroup(buf, bufSize, entrySize);

        if (n < _coreCount && !_shortReadLogged)
        {
            _shortReadLogged = true;
            Log.Warn($"cpu-kernel: the kernel returned {n} of {_coreCount} logical CPUs — cpu.core.{n}..{_coreCount - 1}.pct stay N/A");
        }

        double totalBusyDelta = 0, totalDelta = 0;
        for (int i = 0; i < n; i++)
        {
            var p = (SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION*)(buf + i * entrySize);
            long idle = p->IdleTime;
            long busy = p->KernelTime + p->UserTime - idle; // KernelTime includes idle
            long idleDelta = idle - _prevIdle[i];
            long busyDelta = busy - _prevBusy[i];
            _prevIdle[i] = idle;
            _prevBusy[i] = busy;

            long span = idleDelta + busyDelta;
            double pct = span > 0 ? 100.0 * busyDelta / span : 0;
            sink.Set(MetricNames.CpuCorePct(i), Math.Clamp(pct, 0, 100));
            totalBusyDelta += busyDelta;
            totalDelta += span;
        }
        sink.Set(MetricNames.CpuTotalPct, totalDelta > 0 ? Math.Clamp(100.0 * totalBusyDelta / totalDelta, 0, 100) : 0);
    }

    /// <summary>The plain call: one array covering the caller's processor group.</summary>
    private int QuerySingleGroup(byte* buf, int bufSize, int entrySize)
    {
        int status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, buf, (uint)bufSize, out uint returned);
        if (status != 0) throw new InvalidOperationException($"NtQuerySystemInformation failed 0x{status:X8}");
        return Math.Min(_coreCount, (int)(returned / entrySize));
    }

    /// <summary>
    /// Above 64 logical CPUs the plain call only ever describes one processor group, so ask per
    /// group: NtQuerySystemInformationEx takes the group number as its input buffer and answers
    /// with that group's processors. Entries land at the group's global base so the index a
    /// metric name carries keeps matching <see cref="CpuTopology"/>'s numbering.
    /// </summary>
    private int QueryAllGroups(byte* buf, int entrySize)
    {
        int filled = 0;
        for (ushort g = 0; g < _groupCount; g++)
        {
            int remaining = _coreCount - filled;
            if (remaining <= 0) break;
            ushort group = g;
            int status = NtQuerySystemInformationEx(SystemProcessorPerformanceInformation,
                &group, sizeof(ushort), buf + filled * entrySize, (uint)(remaining * entrySize), out uint returned);
            if (status != 0)
                throw new InvalidOperationException($"NtQuerySystemInformationEx(group {g}) failed 0x{status:X8}");
            filled += (int)(returned / entrySize);
        }
        return Math.Min(_coreCount, filled);
    }

    public void Dispose() { }

    private const int SystemProcessorPerformanceInformation = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime, KernelTime, UserTime, DpcTime, InterruptTime;
        public uint InterruptCount;
        private uint _pad;
    }

    [DllImport("ntdll")]
    private static extern int NtQuerySystemInformation(int infoClass, void* info, uint size, out uint returned);

    // NTSTATUS NtQuerySystemInformationEx(SYSTEM_INFORMATION_CLASS, PVOID InputBuffer,
    //   ULONG InputBufferLength, PVOID SystemInformation, ULONG SystemInformationLength,
    //   PULONG ReturnLength) — the input buffer carries the processor group number (USHORT).
    [DllImport("ntdll")]
    private static extern int NtQuerySystemInformationEx(int infoClass, void* inputBuffer, uint inputBufferLength,
        void* info, uint size, out uint returned);
}
