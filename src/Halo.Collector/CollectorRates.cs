namespace Halo.Collector;

/// <summary>
/// Every provider cadence in one place (rates plan R1). These are engineering constants, not
/// user settings: nobody outside this codebase can judge what a sensor class tolerates, and the
/// host computes each provider's period once at start-up anyway (<see cref="ProviderHost"/>), so
/// a hot-reloadable rate would not take effect without a restart.
///
/// The numbers come from the cost measurement in <c>docs\perf-usage-breakdown.md</c>: the five
/// raises below add ~0.8 core-points in total. <c>LhmCpu</c> stays at 5 Hz because the MSR sweep
/// overruns its period on 2.78 % of polls at 10 Hz.
/// </summary>
internal static class CollectorRates
{
    /// <summary>Kernel processor-time counters. They advance on the ~15.6 ms tick, so 64 Hz is
    /// the point beyond which every read is a duplicate.</summary>
    public const double CpuKernel = 10;

    /// <summary>Interface octet counters; the poll rate is the resolution (bursts are averaged
    /// over one period), so 10 Hz is what makes a 100 ms spike visible.</summary>
    public const double Network = 10;

    /// <summary>PDH LogicalDisk byte counters — same reasoning as <see cref="Network"/>.</summary>
    public const double DiskIo = 10;

    /// <summary>NVML: ~0.2–1 ms per call. Utilisation is windowed by the driver, so the panel
    /// gains freshness on temp/clock/power rather than on load.</summary>
    public const double Nvml = 10;

    /// <summary>Uptime, RAM, IPs, drive space, sys.* — none of it moves faster than this.</summary>
    public const double Builtin = 1;

    /// <summary>One NtQuerySystemInformation snapshot of every process; the most expensive
    /// cheap-looking call in the collector (~6 ms).</summary>
    public const double Process = 1;

    /// <summary>LHM MSR sweep. 5, not 10 — see the class summary.</summary>
    public const double LhmCpu = 5;

    /// <summary>SuperIO port I/O behind the ISA mutex: fans and Vcore do not move faster.</summary>
    public const double LhmSuperIo = 1;

    /// <summary>SMART/NVMe temperature sweep: ~32 ms per drive, so once every 10 s (plan R1
    /// raised this from every 30 s — drive temps lag a workload by tens of seconds).</summary>
    public const double LhmStorage = 1.0 / 10;

    /// <summary>NVAPI extras (GPU voltage, fan rpm) and the only source for AMD/Intel GPUs.
    /// The worst-value poll in the collector at ~78 ms; 1 Hz is as often as it earns.</summary>
    public const double LhmGpu = 1;

    /// <summary>Frame-data drain. Event-driven in practice; this is the drain cadence.</summary>
    public const double PresentMon = 40;

    /// <summary>PCL Stats marker aggregation window flush.</summary>
    public const double PclStats = 5;
}
