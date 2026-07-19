namespace Halo.Shared.Metrics;

/// <summary>
/// Well-known metric names. The registry in shared memory is dynamic (providers register
/// what they find), but these constants keep collector and widgets in sync for the ~85
/// inventory metrics. Session-max variants use the ".max" suffix.
/// </summary>
public static class MetricNames
{
    public const string MaxSuffix = ".max";

    // CPU (LHM MSR + kernel)
    public const string CpuTotalPct = "cpu.total.pct";
    public static string CpuCorePct(int logicalIndex) => $"cpu.core.{logicalIndex}.pct";
    public const string CpuPackageTempC = "cpu.package.temp.c";
    public const string CpuPackagePowerW = "cpu.package.power.w";
    public const string CpuVcoreV = "cpu.vcore.v";
    public const string CpuClockMhz = "cpu.clock.mhz";
    public const string CpuName = "cpu.name";

    // RAM
    public const string RamUsedGb = "ram.used.gb";
    public const string RamTotalGb = "ram.total.gb";
    public const string RamPct = "ram.pct";

    // GPU (NVML/NVAPI via LHM)
    public const string GpuTempC = "gpu.temp.c";
    public const string GpuUsagePct = "gpu.usage.pct";
    public const string GpuVramUsedMb = "gpu.vram.used.mb";
    public const string GpuVramTotalMb = "gpu.vram.total.mb";
    public const string GpuVramPct = "gpu.vram.pct";
    public const string GpuFanRpm = "gpu.fan.rpm";
    public const string GpuFanPct = "gpu.fan.pct";
    public const string GpuClockCoreMhz = "gpu.clock.core.mhz";
    public const string GpuClockMemMhz = "gpu.clock.mem.mhz";
    public const string GpuVoltageV = "gpu.voltage.v";
    public const string GpuPowerW = "gpu.power.w";
    public const string GpuName = "gpu.name";

    // Case/CPU fans (LHM SuperIO NCT6687D: CPU, Pump, System 1..6)
    public static string FanRpm(int channel) => $"fan.{channel}.rpm";      // channel 0..7
    public static string FanPct(int channel) => $"fan.{channel}.pct";
    public static string FanName(int channel) => $"fan.{channel}.name";
    public const string CpuFanRpm = "fan.0.rpm"; // channel 0 = CPU fan header

    // Drives (per volume letter)
    public static string DriveTempC(char letter) => $"drive.{char.ToLowerInvariant(letter)}.temp.c";
    public static string DriveUsedB(char letter) => $"drive.{char.ToLowerInvariant(letter)}.used.b";
    public static string DriveTotalB(char letter) => $"drive.{char.ToLowerInvariant(letter)}.total.b";
    public static string DriveReadBps(char letter) => $"drive.{char.ToLowerInvariant(letter)}.read.bps";
    public static string DriveWriteBps(char letter) => $"drive.{char.ToLowerInvariant(letter)}.write.bps";
    public static string DriveActivityPct(char letter) => $"drive.{char.ToLowerInvariant(letter)}.activity.pct";
    public static string DriveLabel(char letter) => $"drive.{char.ToLowerInvariant(letter)}.label";

    // Network
    public const string NetDownBps = "net.down.bps";
    public const string NetUpBps = "net.up.bps";
    public const string NetDownTotalB = "net.down.total.b";
    public const string NetUpTotalB = "net.up.total.b";
    public const string NetIpInternal = "net.ip.internal";
    public const string NetIpExternal = "net.ip.external";

    // Processes. Two rankings are published side by side (rank 0..4):
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

    // FPS / frame pipeline (PresentMon)
    public const string FpsPresented = "fps.presented";
    public const string FpsDisplayed = "fps.displayed";
    public const string FpsFrametimeMs = "fps.frametime.ms";             // avg present-to-present over window
    public const string FpsFrametimeWorstMs = "fps.frametime.worst.ms";  // worst in last widget tick window
    public const string FpsLow1Presented = "fps.low1.presented";
    public const string FpsLow01Presented = "fps.low01.presented";
    public const string FpsLow1Displayed = "fps.low1.displayed";
    public const string FpsLow01Displayed = "fps.low01.displayed";
    public const string FpsFgRatio = "fps.fgratio";                      // displayed:presented
    public const string FpsRefreshHz = "fps.refresh.hz";                 // active monitor refresh
    public const string FpsAppName = "fps.app.name";
    public const string FpsAppPid = "fps.app.pid";
    public const string LatencyClickMs = "latency.click.ms";             // Click-to-Photon
    public const string LatencyAllInputMs = "latency.allinput.ms";       // All-Input-to-Photon
    public const string LatencyPclMs = "latency.pcl.ms";                 // true marker-based PC Latency (Reflex PCL Stats ETW consumer)
    public const string RenderRateHz = "render.rate.hz";                 // game-rendered (pre-frame-gen) rate from PCL simulation markers
    public const string FpsDisplayLatencyMs = "fps.displaylatency.ms";   // present→displayed (P2D) from PresentMon MsUntilDisplayed

    // DLSS / NGX module inspection
    public const string DlssSrPresent = "dlss.sr.present";
    public const string DlssFgPresent = "dlss.fg.present";
    public const string DlssRrPresent = "dlss.rr.present";
    public const string DlssVersion = "dlss.version";
    public const string DlssModel = "dlss.model";        // "Transformer/CNN · override/game DLL"

    // System
    public const string SysUptimeS = "sys.uptime.s";
}
