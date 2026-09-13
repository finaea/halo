namespace Halo.Metrics;

/// <summary>
/// Well-known metric names. The registry in shared memory is dynamic (providers register what
/// they find) and fully self-describing, so a consumer never needs this file — it is a
/// convenience for in-repo code and for anyone who prefers constants to string literals.
///
/// Indexed families (<c>gpu.&lt;i&gt;.*</c>, <c>cpu.core.&lt;i&gt;.*</c>, <c>fan.&lt;n&gt;.*</c>,
/// <c>drive.&lt;x&gt;.*</c>) are discovered at runtime: read <c>gpu.count</c> / <c>cpu.logical.count</c> /
/// <c>fan.count</c>, or just enumerate the registry. Session maxima use the ".max" suffix.
/// </summary>
public static class MetricNames
{
    public const string MaxSuffix = ".max";

    // ---- CPU (LHM MSR + kernel) ----
    public const string CpuTotalPct = "cpu.total.pct";
    public const string CpuLogicalCount = "cpu.logical.count";
    public static string CpuCorePct(int logicalIndex) => $"cpu.core.{logicalIndex}.pct";
    /// <summary>0 = performance core, 1 = efficiency core (all 0 on non-hybrid parts).</summary>
    public static string CpuCoreClass(int logicalIndex) => $"cpu.core.{logicalIndex}.class";
    /// <summary>Index of the physical core this logical CPU belongs to (SMT siblings share it).</summary>
    public static string CpuCorePhysical(int logicalIndex) => $"cpu.core.{logicalIndex}.physical";
    public const string CpuPackageTempC = "cpu.package.temp.c";
    public const string CpuPackagePowerW = "cpu.package.power.w";
    public const string CpuVcoreV = "cpu.vcore.v";
    public const string CpuClockMhz = "cpu.clock.mhz";
    public const string CpuName = "cpu.name";

    // ---- RAM ----
    public const string RamUsedGb = "ram.used.gb";
    public const string RamTotalGb = "ram.total.gb";
    public const string RamPct = "ram.pct";

    // ---- GPUs (NVML first, then LHM AMD/Intel devices; index is stable per boot) ----
    public const string GpuCount = "gpu.count";
    public static string GpuName(int i) => $"gpu.{i}.name";
    /// <summary>"nvidia" | "amd" | "intel".</summary>
    public static string GpuVendor(int i) => $"gpu.{i}.vendor";
    public static string GpuTempC(int i) => $"gpu.{i}.temp.c";
    public static string GpuUsagePct(int i) => $"gpu.{i}.usage.pct";
    public static string GpuVramUsedMb(int i) => $"gpu.{i}.vram.used.mb";
    public static string GpuVramTotalMb(int i) => $"gpu.{i}.vram.total.mb";
    public static string GpuVramPct(int i) => $"gpu.{i}.vram.pct";
    public static string GpuFanRpm(int i) => $"gpu.{i}.fan.rpm";
    public static string GpuFanPct(int i) => $"gpu.{i}.fan.pct";
    public static string GpuClockCoreMhz(int i) => $"gpu.{i}.clock.core.mhz";
    public static string GpuClockMemMhz(int i) => $"gpu.{i}.clock.mem.mhz";
    public static string GpuVoltageV(int i) => $"gpu.{i}.voltage.v";
    public static string GpuPowerW(int i) => $"gpu.{i}.power.w";

    // ---- Case/CPU fans (LHM SuperIO; channel order = LHM identifier order) ----
    public const string FanCount = "fan.count";
    public static string FanRpm(int channel) => $"fan.{channel}.rpm";
    public static string FanName(int channel) => $"fan.{channel}.name";
    /// <summary>PWM duty reported by the SuperIO chip, when it exposes a paired Control sensor.
    /// Absent on boards that don't; widgets fall back to rpm ÷ max.</summary>
    public static string FanControlPct(int channel) => $"fan.{channel}.control.pct";

    // ---- Drives (per volume letter; the set is discovered, not configured) ----
    public static string DriveTempC(char letter) => $"drive.{char.ToLowerInvariant(letter)}.temp.c";
    public static string DriveUsedB(char letter) => $"drive.{char.ToLowerInvariant(letter)}.used.b";
    public static string DriveTotalB(char letter) => $"drive.{char.ToLowerInvariant(letter)}.total.b";
    public static string DriveReadBps(char letter) => $"drive.{char.ToLowerInvariant(letter)}.read.bps";
    public static string DriveWriteBps(char letter) => $"drive.{char.ToLowerInvariant(letter)}.write.bps";
    public static string DriveLabel(char letter) => $"drive.{char.ToLowerInvariant(letter)}.label";

    // ---- Network ----
    public const string NetDownBps = "net.down.bps";
    public const string NetUpBps = "net.up.bps";
    public const string NetDownTotalB = "net.down.total.b";
    public const string NetUpTotalB = "net.up.total.b";
    public const string NetIpInternal = "net.ip.internal";
    public const string NetIpExternal = "net.ip.external";

    // ---- Processes. Two rankings are published side by side (rank 0..9):
    //  agg=false — each process instance is its own row (Rainformer/UsageMonitor parity)
    //  agg=true  — same-name processes summed (Task Manager style); widgets pick per instance
    public const string ProcCount = "proc.count";
    public static string TopCpuName(int rank, bool agg = false) => $"proc.topcpu.{Mode(agg)}{rank}.name";
    public static string TopCpuPct(int rank, bool agg = false) => $"proc.topcpu.{Mode(agg)}{rank}.cpu.pct";
    public static string TopCpuRamB(int rank, bool agg = false) => $"proc.topcpu.{Mode(agg)}{rank}.ram.b";
    public static string TopRamName(int rank, bool agg = false) => $"proc.topram.{Mode(agg)}{rank}.name";
    public static string TopRamB(int rank, bool agg = false) => $"proc.topram.{Mode(agg)}{rank}.ram.b";
    public static string TopRamCpuPct(int rank, bool agg = false) => $"proc.topram.{Mode(agg)}{rank}.cpu.pct";
    private static string Mode(bool agg) => agg ? "agg." : "";

    // ---- FPS / frame pipeline (PresentMon) ----
    public const string FpsPresented = "fps.presented";
    public const string FpsDisplayed = "fps.displayed";
    // per-stream frametimes: the presented pair is tap-sourced when the door-1 tap is active
    // (100 ms rolling mean, live) and resolved-lane otherwise; the displayed pair is always
    // resolved-lane, flip-to-flip based (what the screen actually did)
    public const string FpsFrametimePresentedMs = "fps.frametime.presented.ms";
    public const string FpsFrametimePresentedWorstMs = "fps.frametime.presented.worst.ms";
    public const string FpsFrametimeDisplayedMs = "fps.frametime.displayed.ms";
    public const string FpsFrametimeDisplayedWorstMs = "fps.frametime.displayed.worst.ms";
    public const string FpsTapActive = "fps.tap.active";                 // 1 = presented lane is the door-1 tap
    public const string FpsLow1Presented = "fps.low1.presented";
    public const string FpsLow01Presented = "fps.low01.presented";
    public const string FpsLow1Displayed = "fps.low1.displayed";
    public const string FpsLow01Displayed = "fps.low01.displayed";
    public const string FpsFgRatio = "fps.fgratio";                      // displayed:presented
    public const string FpsRefreshHz = "fps.refresh.hz";                 // active monitor refresh
    public const string FpsAppName = "fps.app.name";
    public const string LatencyClickMs = "latency.click.ms";             // Click-to-Photon
    public const string LatencyAllInputMs = "latency.allinput.ms";       // All-Input-to-Photon
    public const string LatencyQueueMs = "latency.queue.ms";             // input queue wait: PCLStatsInput post → ping consume (I2FS, ②a)
    public const string LatencyRenderMs = "latency.render.ms";           // ping consume → present (render pipeline, FS2P)
    public const string RenderRateHz = "render.rate.hz";                 // game-rendered (pre-frame-gen) rate from PCL simulation markers
    public const string FpsDisplayLatencyMs = "fps.displaylatency.ms";   // present→displayed (P2D) from PresentMon MsUntilDisplayed

    // ---- DLSS / NGX module inspection ----
    public const string DlssSrPresent = "dlss.sr.present";
    public const string DlssFgPresent = "dlss.fg.present";
    public const string DlssRrPresent = "dlss.rr.present";
    public const string DlssVersion = "dlss.version";
    public const string DlssModel = "dlss.model";        // "Transformer/CNN · override/game DLL"

    // ---- System / capabilities (what the System check page reads) ----
    public const string SysUptimeS = "sys.uptime.s";
    /// <summary>1 when the collector runs elevated; 0 means the elevation-only providers are N/A.</summary>
    public const string SysElevated = "sys.elevated";
    /// <summary>1 when the PawnIO driver is installed (LHM needs it for CPU temp/power/fans).</summary>
    public const string SysPawnIoInstalled = "sys.pawnio.installed";
    public const string SysPawnIoVersion = "sys.pawnio.version";
    public const string SysOsBuild = "sys.os.build";
    public const string SysCollectorVersion = "sys.collector.version";
}
