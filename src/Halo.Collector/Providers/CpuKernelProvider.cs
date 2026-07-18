using System.Runtime.InteropServices;
using Halo.Shared.Metrics;

namespace Halo.Collector.Providers;

/// <summary>
/// Per-core + total CPU usage from NtQuerySystemInformation(SystemProcessorPerformanceInformation).
/// Counters advance on the ~15.6 ms kernel tick, so rates beyond 64 Hz are duplicate reads
/// (plan §5 hard cap).
/// </summary>
public sealed unsafe class CpuKernelProvider : ISensorProvider
{
    public string Name => "cpu-kernel";
    public double MaxRateHz => 64;
    public double DefaultRateHz => 10;

    private int _coreCount;
    private long[] _prevIdle = [];
    private long[] _prevBusy = []; // kernel+user (kernel includes idle; subtracted)

    public bool Initialize(MetricSink sink)
    {
        _coreCount = Environment.ProcessorCount;
        _prevIdle = new long[_coreCount];
        _prevBusy = new long[_coreCount];

        sink.Register(MetricNames.CpuTotalPct, MetricType.Double, MetricUnit.Percent, Name, MaxRateHz);
        for (int i = 0; i < _coreCount; i++)
            sink.Register(MetricNames.CpuCorePct(i), MetricType.Double, MetricUnit.Percent, Name, MaxRateHz);

        Poll(sink); // prime deltas
        return true;
    }

    public void Poll(MetricSink sink)
    {
        int entrySize = sizeof(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION);
        int bufSize = entrySize * _coreCount;
        byte* buf = stackalloc byte[bufSize];
        int status = NtQuerySystemInformation(8 /*SystemProcessorPerformanceInformation*/, buf, (uint)bufSize, out uint returned);
        if (status != 0) throw new InvalidOperationException($"NtQuerySystemInformation failed 0x{status:X8}");

        int n = Math.Min(_coreCount, (int)(returned / entrySize));
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

    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime, KernelTime, UserTime, DpcTime, InterruptTime;
        public uint InterruptCount;
        private uint _pad;
    }

    [DllImport("ntdll")]
    private static extern int NtQuerySystemInformation(int infoClass, void* info, uint size, out uint returned);
}
