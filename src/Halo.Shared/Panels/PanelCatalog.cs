using Halo.Metrics;

namespace Halo.Shared.Panels;

/// <summary>How a metric row repeats inside a panel.</summary>
public enum Repeat
{
    None,
    /// <summary>One row per logical CPU ({n}).</summary>
    PerCore,
    /// <summary>One block per selected volume ({x} = drive letter).</summary>
    PerVolume,
    /// <summary>One row per selected fan channel ({n}).</summary>
    PerFan,
    /// <summary>One row per ranking position ({n} = 0-based rank).</summary>
    PerRank,
}

public enum OptionKind { Bool, Int, Double, Enum, Text, List }

/// <summary>
/// One metric a panel can show. <see cref="MetricName"/> is a template: <c>{gpu}</c> is the
/// widget's GPU index, <c>{n}</c> the repeat index (core / fan channel / rank), <c>{x}</c> the
/// drive letter and <c>{agg}</c> the process-ranking mode.
/// </summary>
public sealed record MetricSpec(
    string Key,
    string DefaultLabel,
    string MetricName,
    MetricUnit Unit,
    bool Graphable = false,
    bool GraphDefaultOn = false,
    string ColorToken = "text",
    double[]? WarnDefaults = null,
    Repeat Repeat = Repeat.None,
    bool DefaultShow = true,
    // NeedsElevation: reads N/A unless the collector is elevated — the System check explains why.
    bool NeedsElevation = false);

/// <summary>
/// A per-widget knob. <see cref="Structural"/> options change which rows exist, so the widget
/// window is rebuilt when they change; everything else is applied in place.
/// </summary>
public sealed record OptionSpec(
    string Key,
    OptionKind Kind,
    string Default,
    string Label,
    string Help,
    bool Structural = false,
    string? Range = null,
    string[]? Choices = null);

/// <summary>One instantiable widget type, described once for the renderer and the Settings UI.</summary>
public sealed record PanelType(
    string Id,
    string DisplayName,
    IReadOnlyList<MetricSpec> Metrics,
    IReadOnlyList<OptionSpec> Options,
    IReadOnlyList<string> Tokens,
    double DefaultRateHz = 5,
    // EventDriven: fed by the frame ring, repainted on the frames-ready event, and not offered
    // a refresh-rate slider (rates plan R2).
    bool EventDriven = false,
    // Requires: hardware this panel needs, for the System check's per-widget verdict.
    string? Requires = null)
{
    public MetricSpec? Metric(string key) => Metrics.FirstOrDefault(m => m.Key == key);
    public OptionSpec? Option(string key) => Options.FirstOrDefault(o => o.Key == key);

    /// <summary>Option value for a widget, falling back to the catalog default.</summary>
    public string OptionValue(IReadOnlyDictionary<string, string> options, string key)
        => options.TryGetValue(key, out string? v) && v.Length > 0 ? v : Option(key)?.Default ?? "";
}

/// <summary>
/// The single description of every panel type: what it shows, what it can be told to do, and
/// which theme tokens it paints with. The renderer builds panels from it and the Settings app
/// generates its controls from it, so adding a metric or an option is one edit, not three
/// (settings plan S1).
/// </summary>
public static class PanelCatalog
{
    // Tokens every panel uses for the card itself.
    private static readonly string[] Frame = ["bgTop", "bgBody", "title", "text", "text2", "staleBadge"];
    private static readonly double[] Warn75 = [75];
    private static readonly double[] CpuTempWarn = [50, 60, 70, 80];
    private static readonly double[] GpuTempWarn = [45, 55, 65, 75];
    private static readonly double[] DriveTempWarn = [35, 45, 55, 65];

    public static IReadOnlyList<PanelType> All { get; } = BuildAll();

    public static PanelType? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public static bool IsKnownType(string id) => Find(id) != null;

    private static IReadOnlyList<PanelType> BuildAll() =>
    [
        new PanelType("clock", "Clock",
            Metrics:
            [
                new MetricSpec("uptime", "Uptime", MetricNames.SysUptimeS, MetricUnit.Seconds, ColorToken: "text2"),
            ],
            Options: [],
            Tokens: [.. Frame, "solidLabel"],
            DefaultRateHz: 5),

        new PanelType("cpu-ram", "CPU / RAM",
            Metrics:
            [
                new MetricSpec("temp", "Temp", MetricNames.CpuPackageTempC, MetricUnit.Celsius,
                    Graphable: true, GraphDefaultOn: true, ColorToken: "cpuTemp", WarnDefaults: CpuTempWarn, NeedsElevation: true),
                new MetricSpec("usage", "CPU:", MetricNames.CpuTotalPct, MetricUnit.Percent,
                    Graphable: true, GraphDefaultOn: true, ColorToken: "cpuUsage", WarnDefaults: Warn75),
                new MetricSpec("cores", "Core {n}:", "cpu.core.{n}.pct", MetricUnit.Percent,
                    ColorToken: "bar", Repeat: Repeat.PerCore),
                new MetricSpec("clock", "Clock:", MetricNames.CpuClockMhz, MetricUnit.Megahertz,
                    ColorToken: "text2", NeedsElevation: true),
                new MetricSpec("fan", "FAN:", "fan.{n}.rpm", MetricUnit.Rpm, ColorToken: "text2", NeedsElevation: true),
                new MetricSpec("ram", "RAM:", MetricNames.RamPct, MetricUnit.Percent,
                    Graphable: true, GraphDefaultOn: true, ColorToken: "ramUsage", WarnDefaults: Warn75),
            ],
            Options:
            [
                new OptionSpec("coreView", OptionKind.Enum, "auto", "Core rows",
                    "How per-core usage is laid out. Auto picks threads up to 48, physical cores above.",
                    Structural: true, Choices: ["auto", "thread", "core", "hidden"]),
                new OptionSpec("coreColumns", OptionKind.Enum, "auto", "Core columns",
                    "Columns of core rows. Auto: 1 up to 16 threads, 2 up to 32, 3 up to 48.",
                    Structural: true, Choices: ["auto", "1", "2", "3"]),
                new OptionSpec("cpuFanChannel", OptionKind.Int, "", "CPU fan channel",
                    "Which discovered fan channel is the CPU fan. Empty = first channel whose name contains \"CPU\".",
                    Structural: true),
            ],
            Tokens: [.. Frame, "cpuTemp", "cpuUsage", "ramUsage", "bar", "barWarn", "emptyBar", "solidLabel",
                     "devWarn1", "devWarn2", "devWarn3", "devWarn4", "devWarn5"],
            Requires: "LibreHardwareMonitor + PawnIO for temperature, power and fan RPM"),

        new PanelType("gpu", "GPU",
            Metrics:
            [
                new MetricSpec("temp", "Temp", "gpu.{gpu}.temp.c", MetricUnit.Celsius,
                    Graphable: true, GraphDefaultOn: true, ColorToken: "gpuTemp", WarnDefaults: GpuTempWarn),
                new MetricSpec("usage", "GPU:", "gpu.{gpu}.usage.pct", MetricUnit.Percent,
                    Graphable: true, GraphDefaultOn: true, ColorToken: "gpuUsage", WarnDefaults: Warn75),
                new MetricSpec("vram", "MEM:", "gpu.{gpu}.vram.pct", MetricUnit.Percent,
                    Graphable: true, GraphDefaultOn: true, ColorToken: "gpuMemUsage", WarnDefaults: Warn75),
                new MetricSpec("fan", "FAN:", "gpu.{gpu}.fan.pct", MetricUnit.Percent,
                    Graphable: true, GraphDefaultOn: true, ColorToken: "gpuFan", WarnDefaults: Warn75),
                new MetricSpec("clockCore", "CORE:", "gpu.{gpu}.clock.core.mhz", MetricUnit.Megahertz, ColorToken: "text2"),
                new MetricSpec("clockMem", "MEM:", "gpu.{gpu}.clock.mem.mhz", MetricUnit.Megahertz, ColorToken: "text2"),
            ],
            Options:
            [
                new OptionSpec("gpuIndex", OptionKind.Int, "0", "GPU",
                    "Which discovered GPU this widget shows (gpu.count says how many there are).",
                    Structural: true),
            ],
            Tokens: [.. Frame, "gpuTemp", "gpuUsage", "gpuMemUsage", "gpuFan", "barWarn", "emptyBar", "solidLabel",
                     "devWarn1", "devWarn2", "devWarn3", "devWarn4", "devWarn5"],
            Requires: "NVIDIA driver (NVML) for the full set; AMD/Intel cards show what LHM exposes"),

        new PanelType("fps", "FPS counter",
            Metrics:
            [
                new MetricSpec("app", "App", MetricNames.FpsAppName, MetricUnit.Text, ColorToken: "text2"),
                new MetricSpec("fps", "Framerate:", "fps.{stream}", MetricUnit.Fps,
                    ColorToken: "gpuUsage", WarnDefaults: [30, 60, 90, 120]),
                new MetricSpec("low1", "1p LOW:", "fps.low1.{stream}", MetricUnit.Fps, ColorToken: "text2"),
                new MetricSpec("low01", "0.1p:", "fps.low01.{stream}", MetricUnit.Fps, ColorToken: "text2"),
                new MetricSpec("frametime", "FRAMETIME:", "fps.frametime.{stream}.ms", MetricUnit.Milliseconds,
                    Graphable: true, GraphDefaultOn: true, ColorToken: "gpuFan"),
                new MetricSpec("worst", "WORST:", "fps.frametime.{stream}.worst.ms", MetricUnit.Milliseconds, ColorToken: "text2"),
                new MetricSpec("dlss", "DLSS", MetricNames.DlssVersion, MetricUnit.Text, ColorToken: "text2"),
            ],
            Options:
            [
                new OptionSpec("stream", OptionKind.Enum, "displayed", "Stream",
                    "presented — what the game handed to Windows (live, RTSS-like).\n"
                    + "displayed — what actually reached the screen, frame-generation aware.",
                    Structural: true, Choices: ["presented", "displayed"]),
            ],
            Tokens: [.. Frame, "gpuUsage", "gpuFan", "barWarn", "emptyBar", "solidLabel", "inactiveButton",
                     "devWarn1", "devWarn2", "devWarn3", "devWarn4", "devWarn5"],
            EventDriven: true,
            Requires: "PresentMon (elevated) for frame data"),

        new PanelType("latency", "Latency / DLSS",
            Metrics:
            [
                new MetricSpec("pclat", "PC LAT:", MetricNames.LatencyRenderMs, MetricUnit.Milliseconds,
                    Graphable: true, GraphDefaultOn: true, ColorToken: "histogram", WarnDefaults: [20, 35, 50, 70]),
                new MetricSpec("queue", "QUEUE", MetricNames.LatencyQueueMs, MetricUnit.Milliseconds, ColorToken: "text2"),
                new MetricSpec("render", "REND", MetricNames.LatencyRenderMs, MetricUnit.Milliseconds, ColorToken: "text2"),
                new MetricSpec("display", "DISP", MetricNames.FpsDisplayLatencyMs, MetricUnit.Milliseconds, ColorToken: "text2"),
                new MetricSpec("click", "CLICK", MetricNames.LatencyClickMs, MetricUnit.Milliseconds, ColorToken: "text2"),
                new MetricSpec("input", "INPUT", MetricNames.LatencyAllInputMs, MetricUnit.Milliseconds, ColorToken: "text2"),
                new MetricSpec("dlss", "DLSS:", MetricNames.DlssVersion, MetricUnit.Text, ColorToken: "text2"),
                new MetricSpec("model", "MODEL:", MetricNames.DlssModel, MetricUnit.Text, ColorToken: "text2"),
                new MetricSpec("framegen", "FRAME GEN:", MetricNames.RenderRateHz, MetricUnit.Hertz, ColorToken: "text2"),
            ],
            Options: [],
            Tokens: [.. Frame, "histogram", "activeTitle", "emptyBar", "solidLabel", "inactiveButton",
                     "devWarn1", "devWarn2", "devWarn3", "devWarn4", "devWarn5"],
            Requires: "NVIDIA Reflex (PCL Stats) markers for PC latency"),

        new PanelType("power", "Power",
            Metrics:
            [
                new MetricSpec("vcore", "VCORE", MetricNames.CpuVcoreV, MetricUnit.Volts,
                    WarnDefaults: [1.1, 1.3, 1.4, 1.5], NeedsElevation: true),
                new MetricSpec("gpuVolt", "GPU VOLT", "gpu.{gpu}.voltage.v", MetricUnit.Volts,
                    WarnDefaults: [0.85, 0.95, 1.0, 1.05]),
                new MetricSpec("cpuPower", "CPU POWER", MetricNames.CpuPackagePowerW, MetricUnit.Watts,
                    WarnDefaults: [50, 100, 150, 200], NeedsElevation: true),
                new MetricSpec("gpuPower", "GPU POWER", "gpu.{gpu}.power.w", MetricUnit.Watts,
                    WarnDefaults: [50, 150, 200, 250]),
            ],
            Options:
            [
                new OptionSpec("gpuIndex", OptionKind.Int, "0", "GPU",
                    "Which discovered GPU the GPU rows read.", Structural: true),
                new OptionSpec("showMax", OptionKind.Bool, "true", "Show session maxima",
                    "The gray \"Max: …\" column, latched until reset from the context menu."),
            ],
            Tokens: [.. Frame, "maxLabelGray", "devWarn1", "devWarn2", "devWarn3", "devWarn4", "devWarn5"],
            Requires: "PawnIO for Vcore and CPU package power"),

        new PanelType("drives", "Drives",
            Metrics:
            [
                new MetricSpec("label", "({x}:)", "drive.{x}.label", MetricUnit.Text, Repeat: Repeat.PerVolume),
                new MetricSpec("temp", "Temp", "drive.{x}.temp.c", MetricUnit.Celsius,
                    WarnDefaults: DriveTempWarn, Repeat: Repeat.PerVolume, NeedsElevation: true),
                new MetricSpec("used", "Used:", "drive.{x}.used.b", MetricUnit.Bytes,
                    WarnDefaults: Warn75, Repeat: Repeat.PerVolume),
                new MetricSpec("total", "Total:", "drive.{x}.total.b", MetricUnit.Bytes, Repeat: Repeat.PerVolume),
                new MetricSpec("write", "Write", "drive.{x}.write.bps", MetricUnit.BytesPerSecond,
                    Graphable: true, GraphDefaultOn: true, ColorToken: "histogram", Repeat: Repeat.PerVolume),
                new MetricSpec("read", "Read", "drive.{x}.read.bps", MetricUnit.BytesPerSecond,
                    Graphable: true, GraphDefaultOn: true, ColorToken: "histogram", Repeat: Repeat.PerVolume),
            ],
            Options:
            [
                new OptionSpec("volumes", OptionKind.List, "", "Volumes",
                    "Drive letters to show, comma separated. Empty = every fixed volume found.",
                    Structural: true),
                new OptionSpec("freeMode", OptionKind.List, "", "Show free space for",
                    "Drive letters that show \"Free:\" instead of \"Used:\", comma separated."),
            ],
            Tokens: [.. Frame, "bar", "barWarn", "histogram", "emptyBar", "red", "inactiveButton",
                     "devWarn1", "devWarn2", "devWarn3", "devWarn4", "devWarn5"],
            Requires: "PawnIO / SMART for drive temperatures"),

        new PanelType("network", "Network",
            Metrics:
            [
                new MetricSpec("ipExternal", "External IP:", MetricNames.NetIpExternal, MetricUnit.Text),
                new MetricSpec("ipInternal", "Internal IP:", MetricNames.NetIpInternal, MetricUnit.Text),
                new MetricSpec("down", "Down", MetricNames.NetDownBps, MetricUnit.BytesPerSecond,
                    Graphable: true, GraphDefaultOn: true, ColorToken: "netDown"),
                new MetricSpec("up", "Up", MetricNames.NetUpBps, MetricUnit.BytesPerSecond,
                    Graphable: true, GraphDefaultOn: true, ColorToken: "netUp"),
                new MetricSpec("peak", "Peak", MetricNames.NetDownBps + MetricNames.MaxSuffix,
                    MetricUnit.BytesPerSecond, ColorToken: "text2"),
                new MetricSpec("sum", "Sum", MetricNames.NetDownTotalB, MetricUnit.Bytes, ColorToken: "text2"),
            ],
            Options:
            [
                new OptionSpec("units", OptionKind.Enum, "bytes", "Units",
                    "Bytes per second (B/s) or bits per second (bit/s).", Choices: ["bytes", "bits"]),
            ],
            Tokens: [.. Frame, "netDown", "netUp", "emptyBar", "solidLabel", "inactiveButton"]),

        new PanelType("fans", "Fans",
            Metrics:
            [
                new MetricSpec("rpm", "Fan {n}", "fan.{n}.rpm", MetricUnit.Rpm,
                    ColorToken: "bar", WarnDefaults: Warn75, Repeat: Repeat.PerFan, NeedsElevation: true),
            ],
            Options:
            [
                new OptionSpec("channels", OptionKind.List, "", "Fan channels",
                    "Channel numbers to show, comma separated. Empty = every channel that is spinning.",
                    Structural: true),
            ],
            Tokens: [.. Frame, "bar", "barWarn", "emptyBar"],
            DefaultRateHz: 1,
            Requires: "PawnIO + a SuperIO chip LibreHardwareMonitor knows"),

        new PanelType("topcpu", "Top processes — CPU",
            Metrics:
            [
                new MetricSpec("name", "Process", "proc.topcpu.{agg}{n}.name", MetricUnit.Text, Repeat: Repeat.PerRank),
                new MetricSpec("cpu", "CPU", "proc.topcpu.{agg}{n}.cpu.pct", MetricUnit.Percent,
                    WarnDefaults: [20], Repeat: Repeat.PerRank),
                new MetricSpec("ram", "RAM", "proc.topcpu.{agg}{n}.ram.b", MetricUnit.Bytes,
                    ColorToken: "text2", Repeat: Repeat.PerRank),
                new MetricSpec("count", "Processes", MetricNames.ProcCount, MetricUnit.Count, ColorToken: "text2"),
            ],
            Options: TopProcOptions(),
            Tokens: [.. Frame, "redText"],
            DefaultRateHz: 1),

        new PanelType("topram", "Top processes — RAM",
            Metrics:
            [
                new MetricSpec("name", "Process", "proc.topram.{agg}{n}.name", MetricUnit.Text, Repeat: Repeat.PerRank),
                new MetricSpec("ram", "RAM", "proc.topram.{agg}{n}.ram.b", MetricUnit.Bytes,
                    WarnDefaults: [524288000], Repeat: Repeat.PerRank),
                new MetricSpec("cpu", "CPU", "proc.topram.{agg}{n}.cpu.pct", MetricUnit.Percent,
                    ColorToken: "text2", Repeat: Repeat.PerRank),
            ],
            Options: TopProcOptions(),
            Tokens: [.. Frame, "redText"],
            DefaultRateHz: 1),
    ];

    private static OptionSpec[] TopProcOptions() =>
    [
        new OptionSpec("topN", OptionKind.Int, "5", "Rows",
            "How many processes to list (1–10).", Structural: true, Range: "1..10"),
        new OptionSpec("aggregate", OptionKind.Bool, "false", "Sum same-name processes",
            "Task Manager style: all chrome.exe instances become one row.", Structural: true),
    ];

    /// <summary>Resolve a MetricSpec's template against a widget's options and repeat index.</summary>
    public static string ResolveMetricName(string template, string gpuIndex = "0", string repeatIndex = "",
        string volume = "", string stream = "", bool aggregate = false)
        => template
            .Replace("{gpu}", gpuIndex)
            .Replace("{n}", repeatIndex)
            .Replace("{x}", volume)
            .Replace("{stream}", stream)
            .Replace("{agg}", aggregate ? "agg." : "");
}
