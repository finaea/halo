using Halo.Collector;
using Halo.Collector.Providers;
using Halo.Metrics;
using Halo.Shared;
using Halo.Shared.Config;

// --dump [--json]: attach as a reader and print every metric once (diagnostics; works while
// another collector instance is running, and is the reference implementation of the reader rules)
if (args.Contains("--dump"))
{
    using var session = new CollectorSession();
    session.Poll();
    if (!session.Attached)
    {
        Console.WriteLine($"no {SharedMemoryLayout.SectionName} section (collector not running?)");
        return 2;
    }
    if (args.Contains("--json")) Dump.Json(session);
    else Dump.Table(session);
    return 0;
}

// --migrate-config [<dir>] [--to <dir>]: convert a v1 config folder into schema v2. Default
// source and target are this machine's data folder; the collector also migrates in place on
// startup when it finds v1 files there.
if (args.Contains("--migrate-config"))
{
    int i = Array.IndexOf(args, "--migrate-config");
    string source = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[i + 1] : Paths.ConfigDir;
    int t = Array.IndexOf(args, "--to");
    string target = t >= 0 && t + 1 < args.Length ? args[t + 1] : Paths.ConfigDir;
    Log.Init("migrate", alsoConsole: true);
    try
    {
        var r = ConfigMigrator.Migrate(source, target, Log.Info);
        Console.WriteLine(r.Detail);
        Log.Flush();
        return r.Migrated ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"migration failed: {ex.Message}");
        Log.Flush();
        return 1;
    }
}

// --pm-smoketest [pid]: verify the PresentMon SDK transport end-to-end (needs admin unless a
// PresentMon service is already running). Tracks the given pid (default: dwm, which presents
// every vblank) and prints consumed frame counts + data freshness for 5 s. Safe to run while
// a collector instance is up — it attaches to the same service.
if (args.Contains("--pm-smoketest"))
{
    Log.Init("pm-smoketest", alsoConsole: true);
    int pid = args.Where(a => int.TryParse(a, out _)).Select(int.Parse).FirstOrDefault();
    if (pid == 0) pid = System.Diagnostics.Process.GetProcessesByName("dwm").FirstOrDefault()?.Id ?? 0;
    if (pid == 0) { Console.WriteLine("no target pid"); return 3; }
    using var sdk = new PresentMonSdkSource();
    if (!sdk.Start(Paths.PresentMonDir, Elevation.IsElevated, 0, ownService: false)) { Console.WriteLine("sdk transport unavailable (see log above)"); return 3; }
    Console.WriteLine($"sdk up ({sdk.Detail}), tracking pid {pid}");
    sdk.OnTargetChanged(0, pid);
    if (!sdk.Tracking) { Console.WriteLine("tracking failed"); return 3; }
    var frames = new List<PresentMonSdkSource.FrameSample>();
    for (int s = 1; s <= 5; s++)
    {
        Thread.Sleep(1000);
        frames.Clear();
        sdk.Drain(pid, frames);
        string detail = "";
        if (frames.Count > 0)
        {
            var last = frames[^1].Entry;
            double ageMs = (System.Diagnostics.Stopwatch.GetTimestamp() - last.Qpc) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            detail = $" | newest: ft={last.FrametimeMs:0.00}ms dispFt={last.DisplayedFtMs:0.00}ms age={ageMs:0}ms flags=0x{last.Flags:x}";
        }
        Console.WriteLine($"t+{s}s: {frames.Count} frames{detail}");
    }
    sdk.OnTargetChanged(pid, 0);
    return 0;
}

// --tap-smoketest <pid>: verify the door-1 present tap end-to-end (needs admin). Prints
// presents/second and the age of the newest one for a DXGI or D3D9 app.
if (args.Contains("--tap-smoketest"))
{
    Log.Init("tap-smoketest", alsoConsole: true);
    if (!Elevation.IsElevated) { Console.WriteLine("needs admin (owns an ETW session)"); return 3; }
    int tapPid = args.Where(a => int.TryParse(a, out _)).Select(int.Parse).FirstOrDefault();
    if (tapPid == 0) { Console.WriteLine("usage: Halo.Collector.exe --tap-smoketest <pid of a presenting app>"); return 3; }
    using var tap = new PresentTap();
    int tapCount = 0;
    long tapLastQpc = 0;
    float tapLastFt = 0;
    tap.Observer = e => { Interlocked.Increment(ref tapCount); Volatile.Write(ref tapLastQpc, e.Qpc); tapLastFt = e.FrametimeMs; };
    // private session name so a running collector's "HaloTap" session is never hijacked
    if (!tap.Start(null, 60, 5, sessionName: "HaloTapTest")) { Console.WriteLine("tap failed (see log above)"); return 3; }
    tap.SetTarget(tapPid);
    Console.WriteLine($"tap up, watching pid {tapPid}");
    for (int s = 1; s <= 5; s++)
    {
        Thread.Sleep(1000);
        int c = Interlocked.Exchange(ref tapCount, 0);
        long lq = Volatile.Read(ref tapLastQpc);
        string detail = lq != 0
            ? $" | last ft={tapLastFt:0.00}ms age={(System.Diagnostics.Stopwatch.GetTimestamp() - lq) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0}ms"
            : "";
        Console.WriteLine($"t+{s}s: {c} presents{detail}");
    }
    return 0;
}

// Halo.Collector — elevated data process (plan §4). Single instance.
// Versioned with the section: a v1 and a v2 collector write different sections and can coexist
// while a machine is being upgraded, but only one of each may run.
using var singleInstance = new Mutex(true, "Local\\Halo.Collector.SingleInstance.v2", out bool isNew);
if (!isNew)
{
    Console.WriteLine("Halo.Collector already running.");
    return 1;
}

Log.Init("collector", alsoConsole: args.Contains("--console"));
Log.Info($"halo {AppVersion.Current} · app root: {Paths.AppRoot}");
Log.Info($"data: {Paths.DataDir}{(Paths.IsPortable ? " (portable)" : "")}");
bool elevated = Elevation.IsElevated;
Log.Info($"elevated: {elevated}");

var configStore = new ConfigStore(Paths.ConfigDir);
using var writer = new MetricsWriter(AppVersion.Current, Log.Warn);
var sink = new MetricSink(writer);

var host = new ProviderHost(sink);

// Providers, each on its own fixed cadence (rates plan R1: engineering constants, not settings)
host.Add(new BuiltinProvider(configStore, elevated)); // uptime, RAM, IPs, drive space, sys.*: 1 Hz
host.Add(new CpuKernelProvider());                    // per-core/total CPU: cap 64 Hz
host.Add(new ProcessProvider());                      // top CPU/RAM lists: 1 Hz (cap 2)
host.Add(new DiskIoProvider());                       // per-volume IO rates
host.Add(new NetworkProvider(configStore));           // net rates
host.Add(new NvmlProvider());                         // GPU: cap 20 Hz
host.Add(new LhmProvider(LhmProvider.Part.Cpu));      // MSR: 5 Hz
host.Add(new LhmProvider(LhmProvider.Part.SuperIo));  // fans/Vcore: 1 Hz (cap 2)
host.Add(new LhmProvider(LhmProvider.Part.Storage));  // SMART temps: 1/30 s
host.Add(new LhmProvider(LhmProvider.Part.Gpu));      // NVAPI extras: voltage, fan RPM
host.Add(new PresentMonProvider(configStore));        // frame data: event-driven
// NVIDIA PCL Stats ETW consumer: true Reflex PC latency + rendered (pre-FG) rate.
// Explicit provider GUID from NVIDIA's reference pclstats.h TRACELOGGING_DEFINE_PROVIDER
// (NOT the name-hash — the header declares it literally). Enabling the provider is the whole
// mechanism; the game self-pings, so no window-message broadcast from us.
host.Add(new PclStatsProvider(
    providerName: "PCLStatsTraceLoggingProvider",
    providerGuidOverride: new Guid(0x0d216f06, 0x82a6, 0x4d49, 0xbc, 0x4f, 0x8f, 0x38, 0xae, 0x56, 0xef, 0xab)));

writer.MarkReady();

using var commands = new CommandServer(cmd =>
{
    var parts = cmd.Split(' ', 2, StringSplitOptions.TrimEntries);
    switch (parts[0])
    {
        case ControlPipe.ResetMax: sink.ResetMax(parts.Length > 1 ? parts[1] : ""); break;
        case ControlPipe.ResetNet: NetworkProvider.RequestTotalsReset(); break;
        case ControlPipe.ReloadConfig: configStore.Reload(); Log.Info("config reloaded on request"); break;
        case ControlPipe.Rescan:
            // Re-runs Initialize on every provider that enumerates hardware there (GPUs, fans,
            // volumes, CPU topology), on each provider's own thread. Providers that own an ETW
            // session or a counter baseline are skipped — see ISensorProvider.RescanReinitialises.
            int rescanned = host.Rescan();
            Log.Info(rescanned < 0
                ? "rescan requested, ignored (one just ran)"
                : $"rescan requested: re-enumerating {rescanned} provider(s)");
            break;
        case ControlPipe.Ping: break;
        default: Log.Warn($"unknown command: {cmd}"); break;
    }
});

configStore.Changed += () =>
{
    Log.Info("config changed (hot-reload)");
    // Providers hold the ConfigStore (not a settings snapshot) and read config.Settings live,
    // so value changes take effect immediately. Hardware sets are discovered, not configured,
    // and are reconciled by each provider on its next poll.
};

Log.Info("collector running");
using var stop = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Set();

// Heartbeat + provider status loop
int statusCounter = 0;
while (!stop.Wait(1000))
{
    writer.Heartbeat();
    if (++statusCounter % 60 == 0)
    {
        foreach (var (name, available, rate, ms) in host.Status())
            Log.Info($"status: {name} available={available} rate={rate:0.##}Hz lastPoll={ms:0.00}ms");
    }
}

Log.Info("collector shutting down");
host.Dispose();
Log.Flush();
return 0;
