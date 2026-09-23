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

// --pm-smoketest [pid]: verify the PresentMon SDK transport end-to-end (attach only: needs a
// PresentMon service already running, an installed one or a collector's child — it never spawns
// one). Tracks the given pid (default: dwm, which presents every vblank) and prints consumed
// frame counts + data freshness for 5 s. Safe to run while a collector instance is up — it
// attaches to the same service.
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

// Crash handlers first: this is a WinExe, so the runtime's default "print the unhandled
// exception to stderr" writes to a console that does not exist. Without these a fault anywhere
// below leaves no trace at all — which is exactly what happened on 2026-09-17.
// ProcessDiagnostics, not Diagnostics: a bare `Diagnostics` binds to the System.Diagnostics namespace here.
ProcessDiagnostics.InstallCrashHandlers();
Log.Init("collector", alsoConsole: args.Contains("--console"));
// Opens this instance's session record AND classifies every previous one, so a start that
// follows a crash says so instead of looking like an ordinary boot.
SessionLog.Begin("collector");
ProcessDiagnostics.LogEnvironment("collector");

var lifecycle = Log.For("lifecycle");
bool elevated = Elevation.IsElevated;

// Startup breadcrumbs. Every step below can fail or hang, and until now none of them said so:
// on 2026-09-17 pid 26784 logged three lines ending at "elevated: False" and vanished somewhere
// between here and the first provider, leaving nothing to name the step. The phase is written
// synchronously to the session record, so it survives a hard kill and an uncatchable native
// fault — the two cases a queued log line never survives.
SessionLog.SetPhase("config-store");
var configStore = new ConfigStore(Paths.ConfigDir);

// settings.json > diagnostics > logLevel. Applied here rather than before ConfigStore because
// ConfigStore's own constructor logs, so Log.Init has to come first — which means the very
// earliest lines are always at the default level. HALO_LOG_LEVEL is the way to catch those.
ApplyConfiguredLogLevel();

SessionLog.SetPhase("metrics-writer");
using var writer = new MetricsWriter(AppVersion.Current, Log.For("metrics").Warn);
var sink = new MetricSink(writer);

SessionLog.SetPhase("provider-host");
var host = new ProviderHost(sink);

// Providers, each on its own fixed cadence (rates plan R1: engineering constants, not settings).
// Registered through AddProvider so the phase names whichever one the process died in: the
// construction is trivial, but RegisterProvider writes to shared memory and Start spawns a
// thread, and either can fault.
AddProvider(new BuiltinProvider(configStore, elevated)); // uptime, RAM, IPs, drive space, sys.*: 1 Hz
AddProvider(new CpuKernelProvider());                    // per-core/total CPU: cap 64 Hz
AddProvider(new ProcessProvider());                      // top CPU/RAM lists: 1 Hz (cap 2)
AddProvider(new DiskIoProvider());                       // per-volume IO rates
AddProvider(new NetworkProvider(configStore));           // net rates
AddProvider(new NvmlProvider());                         // GPU: cap 20 Hz
AddProvider(new LhmProvider(LhmProvider.Part.Cpu));      // MSR: 5 Hz
AddProvider(new LhmProvider(LhmProvider.Part.SuperIo));  // fans/Vcore: 1 Hz (cap 2)
AddProvider(new LhmProvider(LhmProvider.Part.Storage));  // SMART temps: every 10 s
AddProvider(new LhmProvider(LhmProvider.Part.Gpu));      // NVAPI extras: voltage, fan RPM
AddProvider(new PresentMonProvider(configStore));        // frame data: event-driven
// NVIDIA PCL Stats ETW consumer: true Reflex PC latency + rendered (pre-FG) rate.
// Explicit provider GUID from NVIDIA's reference pclstats.h TRACELOGGING_DEFINE_PROVIDER
// (NOT the name-hash — the header declares it literally). Enabling the provider is the whole
// mechanism; the game self-pings, so no window-message broadcast from us.
AddProvider(new PclStatsProvider(
    providerName: "PCLStatsTraceLoggingProvider",
    providerGuidOverride: new Guid(0x0d216f06, 0x82a6, 0x4d49, 0xbc, 0x4f, 0x8f, 0x38, 0xae, 0x56, 0xef, 0xab)));

SessionLog.SetPhase("mark-ready");
writer.MarkReady();

// Declared before the command server so "quit" can set it. Disposal runs in reverse, so the
// server stops listening before the event it signals goes away.
using var stop = new ManualResetEventSlim(false);

// Which of the three stop triggers actually fired. All three used to funnel into one
// "collector shutting down" line, so a log could not tell a deliberate `quit` from a
// Stop-Process — and on 2026-09-17 five starts in one day left no way to ask.
string stopReason = "";
object stopReasonGate = new();

SessionLog.SetPhase("command-server");
using var commands = new CommandServer(cmd =>
{
    var parts = cmd.Split(' ', 2, StringSplitOptions.TrimEntries);
    switch (parts[0])
    {
        case ControlPipe.ResetMax: sink.ResetMax(parts.Length > 1 ? parts[1] : ""); break;
        case ControlPipe.ResetNet: NetworkProvider.RequestTotalsReset(); break;
        case ControlPipe.ReloadConfig: configStore.Reload(); lifecycle.Info("config reloaded on request"); break;
        case ControlPipe.Rescan:
            // Re-runs Initialize on every provider that enumerates hardware there (GPUs, fans,
            // volumes, CPU topology), on each provider's own thread. Providers that own an ETW
            // session or a counter baseline are skipped — see ISensorProvider.RescanReinitialises.
            int rescanned = host.Rescan();
            lifecycle.Info(rescanned < 0
                ? "rescan requested, ignored (one just ran)"
                : $"rescan requested: re-enumerating {rescanned} provider(s)");
            break;
        case ControlPipe.Ping: break;
        case ControlPipe.Quit: RequestStop("quit requested via control pipe"); break;
        default: Log.Warn($"unknown command: {cmd}"); break;
    }
});

configStore.Changed += () =>
{
    lifecycle.Info("config changed (hot-reload)");
    ApplyConfiguredLogLevel();
    // Providers hold the ConfigStore (not a settings snapshot) and read config.Settings live,
    // so value changes take effect immediately. Hardware sets are discovered, not configured,
    // and are reconciled by each provider on its next poll.
};

Console.CancelKeyPress += (_, e) => { e.Cancel = true; RequestStop("console Ctrl-C"); };
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    RequestStop("ProcessExit (logoff, shutdown, or a plain kill)", external: true);

SessionLog.SetPhase("running");
lifecycle.Info("collector running");

// ---- heartbeat + provider status loop ----------------------------------------------------
// Ticks at 1 Hz. It carries two jobs beyond the heartbeat: noticing that it did not get to run
// on time, and reporting provider status without burying the log in repeats.
const int TickMs = 1000;
// A tick later than this did not merely drift — the loop was blocked.
const long LateTickMs = 2 * TickMs;
// Ticks before the one full Info snapshot. Long enough for the first two init backoffs
// (1 s then 5 s) to resolve, so the snapshot shows settled state rather than a race.
const int SettleTicks = 15;
const int FullBlockTicks = 60;

var loopClock = System.Diagnostics.Stopwatch.StartNew();
long lastTickMs = 0;
int statusCounter = 0;
bool settledSnapshotLogged = false;
// Last Info-reported state per provider, so only movement is worth a line.
var lastStatus = new Dictionary<string, (bool Available, double RateHz)>();

while (!stop.Wait(TickMs))
{
    long nowMs = loopClock.ElapsedMilliseconds;
    long gap = nowMs - lastTickMs;
    lastTickMs = nowMs;
    // Be clear about what this can and cannot prove: it only ever fires on a stall the loop came
    // back from. A stall the process dies inside produces no line here at all — the last
    // SessionLog phase and the "no clean exit" report on the next start are the only evidence of
    // that one.
    if (gap > LateTickMs)
    {
        lifecycle.Warn($"heartbeat tick was {gap - TickMs} ms late (gap {gap} ms, period {TickMs} ms) — "
            + "the loop was blocked and has recovered; a stall this process dies inside cannot appear here");
    }

    writer.Heartbeat();
    statusCounter++;

    var status = host.Status();

    // Info lane: only what moved. The 60 s full block was 5,988 of collector-20260917.log's
    // 6,510 lines (92%) and said the same thing every time, which is how a genuine warning
    // goes unread.
    foreach (var (name, available, rate, ms) in status)
    {
        bool known = lastStatus.TryGetValue(name, out var prev);
        lastStatus[name] = (available, rate);
        // First sight is not a change: ProviderHost already logs each provider's init, and the
        // settled snapshot below gives the whole picture once.
        if (!known || (prev.Available == available && prev.RateHz.Equals(rate))) continue;
        lifecycle.Info($"status change: {name} available={available} rate={rate:0.##}Hz lastPoll={ms:0.00}ms");
    }

    // One full picture at Info, so a log opened cold still answers "what did this machine have".
    if (!settledSnapshotLogged && statusCounter >= SettleTicks)
    {
        settledSnapshotLogged = true;
        foreach (var (name, available, rate, ms) in status)
            lifecycle.Info($"status: {name} available={available} rate={rate:0.##}Hz lastPoll={ms:0.00}ms");
    }

    // Debug lane: the old 60 s block, for when someone turns logLevel up to watch a provider.
    if (statusCounter % FullBlockTicks == 0)
        foreach (var (name, available, rate, ms) in status)
            lifecycle.Debug($"status: {name} available={available} rate={rate:0.##}Hz lastPoll={ms:0.00}ms");
}

string reason = stopReason.Length > 0 ? stopReason : "stop signalled with no recorded trigger";
SessionLog.SetPhase($"shutdown: disposing providers ({reason})");
host.Dispose();
// End before Shutdown, and only after Dispose: a fault inside provider teardown then leaves the
// record still "running" at the shutdown phase rather than a clean exit that never happened.
SessionLog.End(reason);
Log.Shutdown(reason);
return 0;

void AddProvider(ISensorProvider provider)
{
    SessionLog.SetPhase($"provider-register: {provider.Name}");
    host.Add(provider);
}

// First trigger wins: Ctrl-C is immediately followed by ProcessExit, and the second, vaguer one
// must not overwrite the answer. The breadcrumb is durable because ProcessExit may never give the
// loop another turn to reach the shutdown path at all.
//
// `external` marks a stop this process cannot finish gracefully — logoff, shutdown, a kill — and
// only that case records a stop intent, so the next start reads it as an expected termination
// rather than inventing a crash. The `quit` and Ctrl-C paths deliberately do NOT: they reach the
// shutdown path below on their own, and a stop intent on them would relabel a genuine fault
// during provider teardown as "stopped on purpose".
void RequestStop(string why, bool external = false)
{
    lock (stopReasonGate)
    {
        if (stopReason.Length == 0)
        {
            stopReason = why;
            Log.Durable(LogLevel.Info, $"stop requested: {why}", "lifecycle");
            if (external) SessionLog.SetStopIntent(why);
        }
    }
    // ProcessExit fires after Main has returned, by which point `stop` has been disposed - so a
    // clean exit arrives here with nothing left to signal. Swallowing that hides no fault: the
    // loop is already gone, which is exactly what this call was asking for. It matters more now
    // that crash handlers are installed, because an ObjectDisposedException escaping a
    // ProcessExit handler would be written up as an unhandled exception on every normal
    // shutdown - a false crash report in the one file meant to make real ones legible.
    try { stop.Set(); }
    catch (ObjectDisposedException) { }
}

void ApplyConfiguredLogLevel()
{
    string? configured = configStore.Settings.Diagnostics.LogLevel;
    if (Log.TryParseLevel(configured, out LogLevel level)) Log.SetLevel(level);
    else if (!string.IsNullOrWhiteSpace(configured))
        Log.Warn($"diagnostics.logLevel '{configured}' is not a level Halo knows "
            + $"(debug|info|warn|error) — staying at {Log.Level}");
}
