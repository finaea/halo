using System.Globalization;
using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelModels;

/// <summary>
/// The panel models (tech plan §3.4): per type, what the panel shows as an ordered list of
/// <see cref="Block"/>s, built on the same <see cref="PanelContext"/> helpers Rainformer uses
/// (<c>Shows</c>, <c>Label</c>, <c>Warn</c>, <c>Color</c>, <c>NaSample</c>). Every caption and hero
/// maps to a published metric. Rainformer stays hand-built and does not read these; every skin after
/// it does, so a metric added here shows up in all of them.
///
/// <para>Labels are the skin-neutral words a block-based skin shows ("LOAD", "VRAM"); the user's
/// own rename (<see cref="PanelContext.UserLabel"/>) always wins over them. Catalog defaults are
/// Rainformer's colon-suffixed row labels and are not used here.</para>
/// </summary>
public static class Models
{
    public static PanelModel? Build(string type, PanelContext ctx) => type switch
    {
        "cpu-ram" => Cpu(ctx),
        "gpu" => Gpu(ctx),
        "fps" => Fps(ctx),
        "latency" => Latency(ctx),
        "power" => Power(ctx),
        "drives" => Drives(ctx),
        "network" => Network(ctx),
        "fans" => Fans(ctx),
        "topcpu" => Top(ctx, byRam: false),
        "topram" => Top(ctx, byRam: true),
        "clock" => Clock(ctx),
        "companion" => Companion(),
        _ => null,
    };

    // ---- shared pieces ----

    private static string L(PanelContext c, string key, string fallback) => c.UserLabel(key) ?? fallback;

    /// <summary>A reading with its unit, or <see cref="Val.Na"/> (unit and all).</summary>
    public static Val Read(PanelContext c, string metric, Func<double, string> format, string unit = "")
        => c.Metrics.TryValue(metric, out double v) ? new Val(format(v), unit) : Val.Na;

    /// <summary>The step of <paramref name="v"/> on a threshold list — the state twin of
    /// <see cref="PanelData.WarnColor"/>, so the colour and the step can never disagree.</summary>
    public static WarnLevel Level(double v, IReadOnlyList<double> thresholds)
        => PanelData.WarnColor(v, thresholds) switch
        {
            "devWarn1" => WarnLevel.L1,
            "devWarn2" => WarnLevel.L2,
            "devWarn3" => WarnLevel.L3,
            "devWarn4" => WarnLevel.L4,
            _ => WarnLevel.L5,
        };

    private static WarnLevel LevelOf(PanelContext c, string metric, string key)
        => c.Metrics.TryValue(metric, out double v) ? Level(v, c.Warn(key)) : WarnLevel.None;

    private static WarnLevel Max(WarnLevel a, WarnLevel b) => a > b ? a : b;

    /// <summary>Integer with a thin-space thousands separator ("4 350"): wide numbers in a narrow
    /// condensed face read better grouped, and a thin space never wraps.</summary>
    public static string Grouped(double v)
        => Math.Round(v).ToString("#,0", Thin);

    private static readonly NumberFormatInfo Thin = new() { NumberGroupSeparator = " ", NumberGroupSizes = [3] };

    /// <summary>Three significant figures: 4.82, 38.6, 412.</summary>
    public static string Sig3(double v)
        => Math.Abs(v) < 10 ? ValueFormat.Fixed(v, 2) : Math.Abs(v) < 100 ? ValueFormat.Fixed(v, 1) : ValueFormat.Int0(v);

    /// <summary>A byte count in 1024 steps: ("1.74", "TB").</summary>
    public static (string Num, string Unit) Bytes(double bytes, bool bits = false, string suffix = "")
    {
        string[] units = bits ? ["bit", "kbit", "Mbit", "Gbit", "Tbit"] : ["B", "KB", "MB", "GB", "TB"];
        double v = bits ? bytes * 8 : bytes;
        int i = 0;
        while (Math.Abs(v) >= 1000 && i < units.Length - 1) { v /= 1024; i++; }
        return (i == 0 ? ValueFormat.Int0(v) : Sig3(v), units[i] + suffix);
    }

    private static Val BytesVal(PanelContext c, string metric, bool bits = false, string suffix = "")
    {
        if (!c.Metrics.TryValue(metric, out double v)) return Val.Na;
        var (n, u) = Bytes(v, bits, suffix);
        return new Val(n, u);
    }

    /// <summary>"used / total" in one unit, both or nothing — half a ratio is not a number.</summary>
    private static Val Budget(PanelContext c, string used, string total, double scale)
    {
        if (!c.Metrics.TryValue(used, out double u) || !c.Metrics.TryValue(total, out double t)) return Val.Na;
        var (tn, unit) = Bytes(t * scale);
        double div = Math.Pow(1024, Array.IndexOf(new[] { "B", "KB", "MB", "GB", "TB" }, unit));
        return new Val(Sig3(u * scale / div), "/ " + tn + " " + unit);
    }

    private static double Frac(PanelContext c, string pctMetric)
        => c.Metrics.TryValue(pctMetric, out double v) ? Math.Clamp(v / 100, 0, 1) : double.NaN;

    private static GraphEl LineGraph(PanelContext ctx) => new()
    {
        Start = GraphStart.Right,
        H = ctx.GraphHeight,
        HistoryS = ctx.GraphHistoryS,
        Style = ctx.GraphStyle,
    };

    /// <summary>
    /// Does a block-based skin draw this metric's graph line? An explicit per-metric setting wins,
    /// exactly as everywhere else; with none, only the panel's <paramref name="primary"/> line is
    /// drawn. The catalog turns on three overlaid lines for the CPU card, which suits Rainformer's
    /// 1 px strip but would bury a constellation graph's dots.
    /// </summary>
    private static bool Graphs(PanelContext c, string key, string primary)
        => c.Metric(key)?.Graph ?? key == primary;

    // ---- CPU / RAM ----

    private static PanelModel Cpu(PanelContext ctx)
    {
        string fan = MetricNames.FanRpm(PanelData.CpuFanChannel(ctx));
        string temp = MetricNames.CpuPackageTempC;
        var m = new PanelModel
        {
            Type = "cpu-ram",
            Title = c => c.TitleOr("CPU"),
            Sub = _ => "CENTRAL PROCESSING UNIT",
            State = c => LevelOf(c, temp, "temp"),
            Missing = c => c.Shows("temp") && c.Metrics.Has(temp) && !c.Metrics.TryValue(temp, out _) ? NoData.Sensor : NoData.None,
            MissingWhat = "TEMPERATURE",
        };
        m.Blocks.Add(TempHero(temp, "temp"));
        m.Blocks.Add(new StatBlock("usage", c => L(c, "usage", "LOAD"), c => Read(c, MetricNames.CpuTotalPct, v => ValueFormat.Fixed(v, 1), "%"))
        {
            Visible = c => c.Shows("usage"),
            Fraction = c => Frac(c, MetricNames.CpuTotalPct),
            BarWarn = c => PanelData.Over(c.Metrics.Value(MetricNames.CpuTotalPct), c.Warn("usage")),
            Token = c => c.Color("usage", "cpuUsage"),
        });
        m.Blocks.Add(new ChipsBlock(
        [
            new Chip("clock", c => L(c, "clock", "CLOCK"), c => Read(c, MetricNames.CpuClockMhz, Grouped, "MHz")) { Visible = c => c.Shows("clock") },
            new Chip("fan", c => L(c, "fan", "CPU FAN"), c => Read(c, fan, Grouped, "rpm")) { Visible = c => c.Shows("fan") },
        ]) { Visible = c => c.Shows("clock") || c.Shows("fan") });

        var (rows, _) = PanelData.CoreGrid(ctx);
        if (rows.Count > 0)
        {
            string columnsOpt = ctx.Option("coreColumns");
            int cores = rows.Count(r => !r.IsHeading);
            int columns = columnsOpt is "1" or "2" or "3" ? int.Parse(columnsOpt) : cores <= 8 ? 1 : cores <= 32 ? 2 : 3;
            m.Blocks.Add(new HeadingBlock(_ => "CORES")
            {
                Visible = c => c.Shows("cores"),
                Right = c => c.Metrics.TryValue(MetricNames.CpuLogicalCount, out double n) ? $"{ValueFormat.Int0(n)} threads" : "",
            });
            m.Blocks.Add(new GridBlock(rows.Select(r =>
            {
                int core = r.Logical;
                return r.IsHeading
                    ? new GridCell(r.Label, _ => Val.Absent, _ => 0, IsHeading: true)
                    : new GridCell(ShortCore(r.Label), c => Read(c, MetricNames.CpuCorePct(core), ValueFormat.Int0, "%"), c => Frac(c, MetricNames.CpuCorePct(core)));
            }).ToList(), columns) { Visible = c => c.Shows("cores") });
        }

        m.Blocks.Add(new HeadingBlock(_ => "MEMORY · BUDGET") { Visible = c => c.Shows("ram") });
        m.Blocks.Add(new StatBlock("ram", c => L(c, "ram", "RAM"), c => Budget(c, MetricNames.RamUsedGb, MetricNames.RamTotalGb, 1073741824))
        {
            Visible = c => c.Shows("ram"),
            Fraction = c => Frac(c, MetricNames.RamPct),
            BarWarn = c => PanelData.Over(c.Metrics.Value(MetricNames.RamPct), c.Warn("ram")),
            Token = c => c.Color("ram", "ramUsage"),
        });

        var graph = LineGraph(ctx);
        GraphSeries? primary = null;
        if (Graphs(ctx, "usage", "usage"))
            graph.Series.Add(primary = new GraphSeries { Color = ctx.Color("usage", "cpuUsage"), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.NaSample(MetricNames.CpuTotalPct) });
        if (Graphs(ctx, "temp", "usage"))
            graph.Series.Add(new GraphSeries { Color = ctx.Color("temp", "cpuTemp"), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.NaSample(temp) });
        if (Graphs(ctx, "ram", "usage"))
            graph.Series.Add(new GraphSeries { Color = ctx.Color("ram", "ramUsage"), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.NaSample(MetricNames.RamPct) });
        if (graph.Series.Count > 0) m.Blocks.Add(new GraphBlock(graph) { Primary = primary ?? graph.Series[0] });
        return m;
    }

    /// <summary>"Core 3:" → "C3" when the user named nothing: the grid cells are narrow.</summary>
    private static string ShortCore(string label)
        => label.StartsWith("Core ", StringComparison.Ordinal) && label.EndsWith(':') ? "C" + label[5..^1] : label;

    /// <summary>A temperature hero with its session max above it. Shown while the metric is
    /// registered — or the collector is down, when nothing is and the card still says N/A.</summary>
    private static HeroBlock TempHero(string metric, string key) => new(c => Read(c, metric, v => ValueFormat.Int0(c.Temp(v)), c.TempUnit))
    {
        Visible = c => c.Shows(key) && (c.Metrics.Has(metric) || c.Stale),
        CaptionLabel = _ => "MAX:",
        CaptionValue = c => Read(c, metric + MetricNames.MaxSuffix, v => ValueFormat.Int0(c.Temp(v))),
        Warn = c => LevelOf(c, metric, key),
    };

    // ---- GPU ----

    private static PanelModel Gpu(PanelContext ctx)
    {
        int gpu = ctx.OptionInt("gpuIndex", 0);
        string temp = MetricNames.GpuTempC(gpu), usage = MetricNames.GpuUsagePct(gpu), vramPct = MetricNames.GpuVramPct(gpu);
        string fanPct = MetricNames.GpuFanPct(gpu), fanRpm = MetricNames.GpuFanRpm(gpu);
        var m = new PanelModel
        {
            Type = "gpu",
            Title = c => c.TitleOr("GPU"),
            Sub = _ => "GRAPHICS PROCESSING UNIT",
            State = c => LevelOf(c, temp, "temp"),
            Missing = c => c.Shows("temp") && c.Metrics.Has(temp) && !c.Metrics.TryValue(temp, out _) ? NoData.Sensor : NoData.None,
            MissingWhat = "TEMPERATURE",
        };
        m.Blocks.Add(TempHero(temp, "temp"));
        m.Blocks.Add(new StatBlock("usage", c => L(c, "usage", "LOAD"), c => Read(c, usage, ValueFormat.Int0, "%"))
        {
            Visible = c => c.Shows("usage"),
            Fraction = c => Frac(c, usage),
            BarWarn = c => PanelData.Over(c.Metrics.Value(usage), c.Warn("usage")),
            Token = c => c.Color("usage", "gpuUsage"),
        });
        m.Blocks.Add(new StatBlock("vram", c => L(c, "vram", "VRAM"), c => Budget(c, MetricNames.GpuVramUsedMb(gpu), MetricNames.GpuVramTotalMb(gpu), 1048576))
        {
            Visible = c => c.Shows("vram"),
            Detail = _ => "budget",
            Fraction = c => Frac(c, vramPct),
            BarWarn = c => PanelData.Over(c.Metrics.Value(vramPct), c.Warn("vram")),
            Token = c => c.Color("vram", "gpuMemUsage"),
        });
        m.Blocks.Add(new StatBlock("fan", c => L(c, "fan", "FAN"), c =>
            {
                if (!c.Metrics.TryValue(fanPct, out double pct)) return Read(c, fanRpm, Grouped, "rpm");
                // 0 rpm is a real reading (fan stop); only an unreadable sensor drops the rpm half
                return new Val(ValueFormat.Int0(pct), c.Metrics.TryValue(fanRpm, out double rpm) ? $"% · {Grouped(rpm)} rpm" : "%");
            })
        {
            Visible = c => c.Shows("fan"),
            Glyph = RowGlyph.Propeller,
            Spin = c => SpinOf(c.Metrics.TryValue(fanRpm, out double r) ? r : -1, c.Metrics.Value(fanPct)),
            Detail = c => c.Metrics.Has(fanPct) || c.Metrics.Has(fanRpm) ? SpinWord(SpinOf(c.Metrics.TryValue(fanRpm, out double r) ? r : -1, c.Metrics.Value(fanPct))) : "",
            Token = c => c.Color("fan", "gpuFan"),
        });
        m.Blocks.Add(new ChipsBlock(
        [
            new Chip("clockCore", c => L(c, "clockCore", "CORE"), c => Read(c, MetricNames.GpuClockCoreMhz(gpu), Grouped, "MHz")) { Visible = c => c.Shows("clockCore") },
            new Chip("clockMem", c => L(c, "clockMem", "MEM"), c => Read(c, MetricNames.GpuClockMemMhz(gpu), Grouped, "MHz")) { Visible = c => c.Shows("clockMem") },
        ]) { Visible = c => c.Shows("clockCore") || c.Shows("clockMem") });

        var graph = LineGraph(ctx);
        GraphSeries? primary = null;
        foreach (var (key, metric, token) in new[] { ("usage", usage, "gpuUsage"), ("temp", temp, "gpuTemp"), ("vram", vramPct, "gpuMemUsage"), ("fan", fanPct, "gpuFan") })
            if (Graphs(ctx, key, "usage"))
            {
                var s = new GraphSeries { Color = ctx.Color(key, token), Ring = ctx.NewRing(), FixedMax = 100, Sample = c => c.NaSample(metric) };
                graph.Series.Add(s);
                if (key == "usage") primary = s;
            }
        if (graph.Series.Count > 0) m.Blocks.Add(new GraphBlock(graph) { Primary = primary ?? graph.Series[0] });
        return m;
    }

    /// <summary>A fan's state from its reading: 0 rpm is stopped (a real reading, not N/A), at or
    /// past 90 % duty is flat out. <paramref name="rpm"/> &lt; 0 = no rpm sensor.</summary>
    public static SpinState SpinOf(double rpm, double pct)
        => rpm == 0 || (rpm < 0 && pct <= 0) ? SpinState.Stopped : pct >= 90 ? SpinState.Full : SpinState.Cruising;

    public static string SpinWord(SpinState s) => s switch
    {
        SpinState.Stopped => "stopped",
        SpinState.Full => "full",
        _ => "cruising",
    };

    // ---- FPS ----

    /// <summary>No fresh presented-FPS sample = no 3D app (the same rule as Rainformer's panel).</summary>
    public static bool FpsIdle(PanelContext c) => !c.Metrics.TryValue(MetricNames.FpsPresented, out _, maxAgeS: 3);

    private static PanelModel Fps(PanelContext ctx)
    {
        bool presented = ctx.Option("stream") == "presented";
        string fps = presented ? MetricNames.FpsPresented : MetricNames.FpsDisplayed;
        string low1 = presented ? MetricNames.FpsLow1Presented : MetricNames.FpsLow1Displayed;
        string low01 = presented ? MetricNames.FpsLow01Presented : MetricNames.FpsLow01Displayed;
        string ft = presented ? MetricNames.FpsFrametimePresentedMs : MetricNames.FpsFrametimeDisplayedMs;
        string worst = presented ? MetricNames.FpsFrametimePresentedWorstMs : MetricNames.FpsFrametimeDisplayedWorstMs;
        string stream = presented ? "PRESENTED" : "DISPLAYED";

        Val Live(PanelContext c, string metric, Func<double, string> f, string unit = "")
            => FpsIdle(c) ? Val.Na : Read(c, metric, f, unit);

        var m = new PanelModel
        {
            Type = "fps",
            Title = c => c.TitleOr("FPS"),
            Sub = c => FpsIdle(c) ? stream + " · IDLE"
                : c.Metrics.Value(MetricNames.DlssFgPresent) > 0 ? stream + " · FRAME GEN ON"
                : c.Metrics.Value(MetricNames.DlssSrPresent) > 0 ? stream + " · DLSS"
                : stream,
            State = c => FpsIdle(c) || !c.Metrics.TryValue(fps, out double v) ? WarnLevel.None : FpsLevel(v, c.Warn("fps")),
            Missing = c => FpsIdle(c) ? NoData.NoApp : NoData.None,
            MissingWhat = "FRAMES",
            WarnTag = "LOW FRAMERATE",
            CritTag = "STUTTERING",
        };
        m.Blocks.Add(new HeroBlock(c => Live(c, fps, ValueFormat.Int0)) { Visible = c => c.Shows("fps") });
        m.Blocks.Add(new StatBlock("low1", c => L(c, "low1", "1% LOW"), c => Live(c, low1, ValueFormat.Int0)) { Visible = c => c.Shows("low1") });
        m.Blocks.Add(new StatBlock("low01", c => L(c, "low01", "0.1% LOW"), c => Live(c, low01, ValueFormat.Int0)) { Visible = c => c.Shows("low01") });
        m.Blocks.Add(new StatBlock("frametime", c => L(c, "frametime", "FRAMETIME"), c => Live(c, ft, v => ValueFormat.Fixed(v, 1), "ms")) { Visible = c => c.Shows("frametime") });
        m.Blocks.Add(new StatBlock("worst", c => L(c, "worst", "WORST · 1 s"), c => Live(c, worst, v => ValueFormat.Fixed(v, 1), "ms")) { Visible = c => c.Shows("worst") });
        m.Blocks.Add(new StatBlock("app", _ => "", c => FpsIdle(c) ? Val.Na : new Val(c.Metrics.Text(MetricNames.FpsAppName))) { Visible = c => c.Shows("app") });
        m.Blocks.Add(new StatBlock("dlss", _ => "DLSS", c =>
            !FpsIdle(c) && (c.Metrics.Value(MetricNames.DlssSrPresent) > 0 || c.Metrics.Value(MetricNames.DlssFgPresent) > 0 || c.Metrics.Value(MetricNames.DlssRrPresent) > 0)
                ? new Val(c.Metrics.Text(MetricNames.DlssVersion)) : Val.Na) { Visible = c => c.Shows("dlss") });
        // the 1 %-low frametime the graph marks with a dashed line: 1000 / low1 ms, absent with the low
        m.Blocks.Add(new StatBlock("low1ms", _ => "1% LOW", c =>
            !FpsIdle(c) && c.Metrics.TryValue(low1, out double l) && l > 0 ? new Val(ValueFormat.Fixed(1000 / l, 1), "ms") : Val.Na));

        // Frame graph: identical sampling to Rainformer's (FpsPanel) — the stream's frametime per
        // frame, its lane filter and its reset key. Only the look differs.
        m.Blocks.Add(new GraphBlock(new GraphEl
        {
            Start = GraphStart.Right,
            H = ctx.GraphHeight,
            VisibleWhen = c => c.Graphs("frametime"),
            FrameSample = f => presented ? f.FrametimeMs : f.DisplayedFtMs,
            FrameDisplayedOnly = !presented,
            FrameFilter = presented
                ? (c, f) => c.Metrics.Value(MetricNames.FpsTapActive) >= 1
                    ? (f.Flags & (uint)FrameFlags.Provisional) != 0
                    : (f.Flags & (uint)FrameFlags.Provisional) == 0
                : (c, f) => (f.Flags & (uint)FrameFlags.Provisional) == 0,
            ResetKey = presented
                ? c => AppPid(c) * 2 + (c.Metrics.Value(MetricNames.FpsTapActive) >= 1 ? 1 : 0)
                : AppPid,
            Series =
            {
                new GraphSeries { Color = ctx.Color("frametime", "gpuFan"), Ring = new SampleRing(188), Sample = _ => 0 },
            },
        }));
        return m;
    }

    private static long AppPid(PanelContext c) => (long)c.Metrics.Value(MetricNames.FpsAppPid);

    /// <summary>
    /// FPS reads the same four thresholds the other way round: the catalog's 30/60/90/120 colour a
    /// high framerate "hottest". As a state, below the second threshold is a low framerate (L4) and
    /// below the first is stuttering (L5); anything above is fine. No new numbers.
    /// </summary>
    public static WarnLevel FpsLevel(double fps, IReadOnlyList<double> t)
        => t.Count < 2 ? WarnLevel.None : fps < t[0] ? WarnLevel.L5 : fps < t[1] ? WarnLevel.L4 : WarnLevel.L1;

    // ---- Latency ----

    private static PanelModel Latency(PanelContext ctx)
    {
        Val Live(PanelContext c, string metric, Func<double, string> f, string unit)
            => FpsIdle(c) || !c.Metrics.TryValue(metric, out double v, maxAgeS: 3) ? Val.Na : new Val(f(v), unit);

        var m = new PanelModel
        {
            Type = "latency",
            Title = c => c.TitleOr("LATENCY"),
            Sub = _ => "PC · DISPLAY · INPUT",
            State = c => FpsIdle(c) || !c.Metrics.TryValue(MetricNames.LatencyPcMs, out double v, maxAgeS: 3) ? WarnLevel.None : Level(v, c.Warn("pclat")),
            Missing = c => FpsIdle(c) ? NoData.NoApp : NoData.None,
            MissingWhat = "LATENCY",
            WarnTag = "SLUGGISH",
            CritTag = "LAGGING",
        };
        m.Blocks.Add(new HeroBlock(c => Live(c, MetricNames.LatencyPcMs, v => ValueFormat.Fixed(v, 1), "ms"))
        {
            Visible = c => c.Shows("pclat"),
            CaptionLabel = c => L(c, "pclat", "PC LATENCY"),
            Warn = c => FpsIdle(c) || !c.Metrics.TryValue(MetricNames.LatencyPcMs, out double v, maxAgeS: 3) ? WarnLevel.None : Level(v, c.Warn("pclat")),
        });
        m.Blocks.Add(new ChipsBlock(
        [
            new Chip("queue", c => L(c, "queue", "QUEUE"), c => Live(c, MetricNames.LatencyQueueMs, v => ValueFormat.Fixed(v, 1), "ms")) { Visible = c => c.Shows("queue") },
            new Chip("render", c => L(c, "render", "RENDER"), c => Live(c, MetricNames.LatencyRenderMs, v => ValueFormat.Fixed(v, 1), "ms")) { Visible = c => c.Shows("render") },
            new Chip("display", c => L(c, "display", "DISPLAY"), c => Live(c, MetricNames.FpsDisplayLatencyMs, v => ValueFormat.Fixed(v, 1), "ms")) { Visible = c => c.Shows("display") },
        ]) { Stacked = true, Visible = c => c.Shows("queue") || c.Shows("render") || c.Shows("display") });
        // click and all-input are a rolling 20 s window (PresentMonProvider.InputLatencyWindowS)
        m.Blocks.Add(new HeadingBlock(_ => "INPUT") { Right = _ => "rolling 20 s", Visible = c => c.Shows("click") || c.Shows("input") });
        m.Blocks.Add(new StatPairBlock(
            new StatBlock("click", c => L(c, "click", "CLICK"), c => FpsIdle(c) ? Val.Na : Read(c, MetricNames.LatencyClickMs, ValueFormat.Int0, "ms")) { Visible = c => c.Shows("click") },
            new StatBlock("input", c => L(c, "input", "ALL INPUT"), c => FpsIdle(c) ? Val.Na : Read(c, MetricNames.LatencyAllInputMs, ValueFormat.Int0, "ms")) { Visible = c => c.Shows("input") })
        { Visible = c => c.Shows("click") || c.Shows("input") });
        m.Blocks.Add(new HeadingBlock(_ => "DLSS") { Visible = c => c.Shows("dlss") || c.Shows("model") || c.Shows("framegen") });
        m.Blocks.Add(new StatBlock("dlss", c => L(c, "dlss", "VERSION"), c => Text(c, MetricNames.DlssVersion)) { Visible = c => c.Shows("dlss") });
        m.Blocks.Add(new StatBlock("model", c => L(c, "model", "MODEL"), c => Text(c, MetricNames.DlssModel)) { Visible = c => c.Shows("model") });
        m.Blocks.Add(new StatBlock("framegen", c => L(c, "framegen", "FRAME GEN"), c => Live(c, MetricNames.RenderRateHz, ValueFormat.Int0, "Hz"))
        {
            Visible = c => c.Shows("framegen"),
            Detail = _ => "rendered",
        });
        var graph = LineGraph(ctx);
        graph.VisibleWhen = c => c.Graphs("pclat");
        graph.Series.Add(new GraphSeries
        {
            Color = ctx.Color("pclat", "histogram"),
            Ring = ctx.NewRing(),
            Sample = c => c.Metrics.TryValue(MetricNames.LatencyPcMs, out double v, maxAgeS: 3) ? v : double.NaN,
        });
        m.Blocks.Add(new GraphBlock(graph) { Primary = graph.Series[0] });
        return m;
    }

    private static Val Text(PanelContext c, string metric)
        => !FpsIdle(c) && c.Metrics.Text(metric) is { Length: > 0 } s ? new Val(s) : Val.Na;

    // ---- Power ----

    private static PanelModel Power(PanelContext ctx)
    {
        int gpu = ctx.OptionInt("gpuIndex", 0);
        string gpuVolt = MetricNames.GpuVoltageV(gpu), gpuPower = MetricNames.GpuPowerW(gpu);
        bool showMax = !ctx.Options.ContainsKey("showMax") || ctx.OptionBool("showMax");
        var rows = new (string Key, string Label, string Metric, Func<double, string> Format, string Unit)[]
        {
            ("cpuPower", "CPU POWER", MetricNames.CpuPackagePowerW, ValueFormat.Int0, "W"),
            ("gpuPower", "GPU POWER", gpuPower, ValueFormat.Int0, "W"),
            ("vcore", "VCORE", MetricNames.CpuVcoreV, v => ValueFormat.Fixed(v, 3), "V"),
            ("gpuVolt", "GPU VOLT", gpuVolt, v => ValueFormat.Fixed(v, 3), "V"),
        };
        var m = new PanelModel
        {
            Type = "power",
            Title = c => c.TitleOr("POWER"),
            Sub = _ => "ENERGY",
            State = c => rows.Aggregate(WarnLevel.None, (a, r) => Max(a, LevelOf(c, r.Metric, r.Key))),
            Missing = c => !c.Metrics.TryValue(MetricNames.CpuPackagePowerW, out _) && !c.Metrics.TryValue(gpuPower, out _) ? NoData.Sensor : NoData.None,
            MissingWhat = "POWER",
            WarnTag = "HIGH DRAW",
            CritTag = "OVERDRAWN",
        };
        // CPU + GPU, and N/A unless both read: there is no capacity metric, so no "/ 450 W"
        m.Blocks.Add(new HeroBlock(c => c.Metrics.TryValue(MetricNames.CpuPackagePowerW, out double a) && c.Metrics.TryValue(gpuPower, out double b)
                ? new Val(ValueFormat.Int0(a + b), "W") : Val.Na)
        {
            Kind = HeroKind.Pill,
            Note = "CPU\n+ GPU",
            Visible = c => c.Shows("cpuPower") || c.Shows("gpuPower"),
        });
        foreach (var r in rows)
        {
            var row = r;
            m.Blocks.Add(new StatBlock(row.Key, c => L(c, row.Key, row.Label), c => Read(c, row.Metric, row.Format, row.Unit))
            {
                Visible = c => c.Shows(row.Key),
                Max = showMax ? c => Read(c, row.Metric + MetricNames.MaxSuffix, row.Format) : null,
                Token = _ => "keyPower",
            });
        }
        return m;
    }

    // ---- Drives ----

    private static PanelModel Drives(PanelContext ctx)
    {
        var letters = PanelData.SelectedVolumes(ctx);
        var m = new PanelModel
        {
            Type = "drives",
            Title = c => c.TitleOr("DRIVES"),
            Sub = _ => letters.Count == 1 ? "HARBOR · 1 VOLUME" : $"HARBOR · {letters.Count} VOLUMES",
            State = c => letters.Aggregate(WarnLevel.None, (a, d) => Max(a, LevelOf(c, MetricNames.DriveTempC(d), $"temp.{d}"))),
            WarnTag = "RUNNING WARM",
        };
        foreach (char letter in letters)
        {
            char d = letter;
            m.Blocks.Add(new VolumeBlock(d)
            {
                Label = c => c.Metrics.Text(MetricNames.DriveLabel(d)) is { Length: > 0 } s ? s.ToUpperInvariant() : $"DRIVE {d}",
                Used = c => PanelData.ShowsFree(c, d)
                    ? (c.Metrics.TryValue(MetricNames.DriveTotalB(d), out double t) && c.Metrics.TryValue(MetricNames.DriveUsedB(d), out double u) ? Free(t - u) : Val.Na)
                    : Budget(c, MetricNames.DriveUsedB(d), MetricNames.DriveTotalB(d), 1),
                Total = c => BytesVal(c, MetricNames.DriveTotalB(d)),
                Fraction = c => c.Metrics.TryValue(MetricNames.DriveTotalB(d), out double t) && t > 0 && c.Metrics.TryValue(MetricNames.DriveUsedB(d), out double u) ? Math.Clamp(u / t, 0, 1) : double.NaN,
                Temp = c => c.Shows($"temp.{d}") && c.Shows("temp") ? Read(c, MetricNames.DriveTempC(d), v => ValueFormat.Int0(c.Temp(v)), c.TempUnit) : Val.Absent,
                TempWarn = c => LevelOf(c, MetricNames.DriveTempC(d), $"temp.{d}"),
                Read = c => c.Shows("read") ? BytesVal(c, MetricNames.DriveReadBps(d), suffix: "/s") : Val.Absent,
                Write = c => c.Shows("write") ? BytesVal(c, MetricNames.DriveWriteBps(d), suffix: "/s") : Val.Absent,
                BarWarn = c =>
                {
                    double t = c.Metrics.Value(MetricNames.DriveTotalB(d));
                    double pct = t > 0 ? c.Metrics.Value(MetricNames.DriveUsedB(d)) * 100 / t : 0;
                    var w = c.Warn("used");
                    return w.Length > 0 && pct >= w[^1];
                },
                Visible = c => c.Shows("used") || c.Shows("total"),
            });
        }
        return m;
    }

    private static Val Free(double bytes)
    {
        var (n, u) = Bytes(bytes);
        return new Val(n, u + " free");
    }

    // ---- Network ----

    private static PanelModel Network(PanelContext ctx)
    {
        static bool Bits(PanelContext c) => c.Option("units") == "bits";
        var m = new PanelModel
        {
            Type = "network",
            Title = c => c.TitleOr("NETWORK"),
            Sub = _ => "COMMS",
            Missing = c => c.Metrics.Has(MetricNames.NetDownBps) && !c.Metrics.TryValue(MetricNames.NetDownBps, out _) ? NoData.Sensor : NoData.None,
            MissingWhat = "NETWORK",
        };
        m.Blocks.Add(new TrafficBlock(
            new StatBlock("down", c => L(c, "down", "DOWN"), c => BytesVal(c, MetricNames.NetDownBps, Bits(c), "/s")) { Visible = c => c.Shows("down") },
            new StatBlock("up", c => L(c, "up", "UP"), c => BytesVal(c, MetricNames.NetUpBps, Bits(c), "/s")) { Visible = c => c.Shows("up") })
        { Visible = c => c.Shows("down") || c.Shows("up") });

        var graph = LineGraph(ctx);
        GraphSeries? primary = null;
        // one line by default: each series autoscales to its own peak, so two lines on one plot
        // would draw a trickle of upload as tall as the download beside it
        if (Graphs(ctx, "down", "down"))
            graph.Series.Add(primary = new GraphSeries { Color = ctx.Color("down", "netDown"), Ring = ctx.NewRing(), Sample = c => c.NaSample(MetricNames.NetDownBps) });
        if (Graphs(ctx, "up", "down"))
            graph.Series.Add(new GraphSeries { Color = ctx.Color("up", "netUp"), Ring = ctx.NewRing(), Sample = c => c.NaSample(MetricNames.NetUpBps) });
        if (graph.Series.Count > 0) m.Blocks.Add(new GraphBlock(graph) { Primary = primary ?? graph.Series[0] });

        m.Blocks.Add(new StatPairBlock(
            new StatBlock("peak", c => L(c, "peak", "PEAK"), c => BytesVal(c, MetricNames.NetDownBps + MetricNames.MaxSuffix, Bits(c), "/s")) { Visible = c => c.Shows("peak") },
            new StatBlock("sum", c => L(c, "sum", "SUM"), c => BytesVal(c, MetricNames.NetDownTotalB, Bits(c))) { Visible = c => c.Shows("sum") })
        { Visible = c => c.Shows("peak") || c.Shows("sum") });
        m.Blocks.Add(new StatPairBlock(
            new StatBlock("ipInternal", c => L(c, "ipInternal", "LAN"), c => Ip(c, MetricNames.NetIpInternal)) { Visible = c => c.Shows("ipInternal"), Plain = true },
            new StatBlock("ipExternal", c => L(c, "ipExternal", "WAN"), c => Ip(c, MetricNames.NetIpExternal)) { Visible = c => c.Shows("ipExternal"), Plain = true })
        { Visible = c => c.Shows("ipInternal") || c.Shows("ipExternal") });
        return m;
    }

    private static Val Ip(PanelContext c, string metric)
        => c.Metrics.Text(metric) is { Length: > 0 } s && s != "N/A" ? new Val(s) : Val.Na;

    // ---- Fans ----

    private static PanelModel Fans(PanelContext ctx)
    {
        var channels = PanelData.SelectedChannels(ctx);
        var m = new PanelModel
        {
            Type = "fans",
            Title = c => c.TitleOr("FANS"),
            Sub = _ => channels.Count == 1 ? "PROPULSION · 1 CHANNEL" : $"PROPULSION · {channels.Count} CHANNELS",
            Missing = c => c.Stale || channels.Count == 0 || channels.Any(ch => c.Metrics.TryValue(MetricNames.FanRpm(ch), out _)) ? NoData.None : NoData.Sensor,
            MissingWhat = "FAN SENSORS",
        };
        foreach (int channel in channels)
        {
            int ch = channel;
            string key = $"rpm.{ch}", rpm = MetricNames.FanRpm(ch);
            m.Blocks.Add(new StatBlock(key, c =>
                {
                    if (c.UserLabel(key) is { Length: > 0 } custom) return custom.Replace("{n}", ch.ToString());
                    return c.Metrics.Text(MetricNames.FanName(ch)) is { Length: > 0 } n ? n.ToUpperInvariant() : $"FAN {ch}";
                }, c => Read(c, rpm, Grouped, "rpm"))
            {
                Visible = c => c.Shows(key),
                Glyph = RowGlyph.Propeller,
                Spin = c => SpinOf(c.Metrics.TryValue(rpm, out double r) ? r : -1, PanelData.FanPercent(c, ch, key)),
                Detail = c => c.Metrics.TryValue(rpm, out double r) ? SpinWord(SpinOf(r, PanelData.FanPercent(c, ch, key))) : "",
                Fraction = c => c.Metrics.TryValue(rpm, out _) ? PanelData.FanPercent(c, ch, key) / 100 : double.NaN,
                BarWarn = c => PanelData.Over(PanelData.FanPercent(c, ch, key), c.Warn(key)),
                Token = c => c.Color(key, "keyFans"),
            });
        }
        return m;
    }

    // ---- Top processes ----

    private static PanelModel Top(PanelContext ctx, bool byRam)
    {
        int n = Math.Clamp(ctx.OptionInt("topN", 5), 1, 10);
        static bool Agg(PanelContext c) => c.OptionBool("aggregate");
        Val Pct(PanelContext c, string metric) => Read(c, metric, v => ValueFormat.Fixed(v, 1), "%");
        var m = new PanelModel
        {
            Type = byRam ? "topram" : "topcpu",
            Title = c => c.TitleOr(byRam ? "TOP RAM" : "TOP CPU"),
            Sub = _ => "FLEET ROSTER · RECEIPT",
        };
        var rows = Enumerable.Range(0, n).Select(rank => byRam
            ? new ListRow(rank + 1,
                c => c.Metrics.Text(MetricNames.TopRamName(rank, Agg(c))) is { Length: > 0 } s ? s : "---",
                c => BytesVal(c, MetricNames.TopRamB(rank, Agg(c))),
                c => Pct(c, MetricNames.TopRamCpuPct(rank, Agg(c))),
                c => Exceeds(c.Metrics.Value(MetricNames.TopRamB(rank, Agg(c))), c.Warn("ram")))
            : new ListRow(rank + 1,
                c => c.Metrics.Text(MetricNames.TopCpuName(rank, Agg(c))) is { Length: > 0 } s ? s : "---",
                c => Pct(c, MetricNames.TopCpuPct(rank, Agg(c))),
                c => BytesVal(c, MetricNames.TopCpuRamB(rank, Agg(c))),
                c => Exceeds(c.Metrics.Value(MetricNames.TopCpuPct(rank, Agg(c))), c.Warn("cpu")))).ToList();
        m.Blocks.Add(new ListBlock(rows)
        {
            PrimaryHeading = byRam ? "RAM" : "CPU",
            SecondaryHeading = byRam ? "CPU" : "RAM",
            Count = c => c.Shows("count") ? Read(c, MetricNames.ProcCount, ValueFormat.Int0) : Val.Absent,
        });
        return m;
    }

    private static bool Exceeds(double v, IReadOnlyList<double> thresholds) => thresholds.Count > 0 && v >= thresholds[^1];

    // ---- Clock ----

    private static PanelModel Clock(PanelContext ctx)
    {
        var m = new PanelModel { Type = "clock", Title = c => c.TitleOr("CLOCK"), Sub = _ => "" };
        m.Blocks.Add(new StatBlock("uptime", c => L(c, "uptime", "UPTIME"), c => Read(c, MetricNames.SysUptimeS, Uptime)) { Visible = c => c.Shows("uptime") });
        return m;
    }

    // ---- Companion ----

    /// <summary>The skin-neutral companion: the uptime, and the mood as its sub-label. A skin with a
    /// host (Azur Archive) swaps in its own title and draws the rest by hand.</summary>
    private static PanelModel Companion()
    {
        var m = new PanelModel { Type = "companion", Title = c => c.TitleOr("COMPANION"), Sub = c => c.Mood.Status };
        m.Blocks.Add(new StatBlock("uptime", c => L(c, "uptime", "UPTIME"), c => Read(c, MetricNames.SysUptimeS, Uptime)) { Visible = c => c.Shows("uptime") });
        return m;
    }

    /// <summary>"3d 04h" — days and hours; minutes are noise on a clock card.</summary>
    public static string Uptime(double seconds)
    {
        long s = (long)seconds;
        return s >= 86400 ? $"{s / 86400}d {s % 86400 / 3600:00}h" : $"{s / 3600}h {s % 3600 / 60:00}m";
    }
}
