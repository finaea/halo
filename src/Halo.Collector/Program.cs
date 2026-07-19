using Halo.Collector;
using Halo.Collector.Providers;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Shared.Metrics;

// --dump: attach as a reader and print all metrics once (diagnostics; works while another
// collector instance is running)
if (args.Contains("--dump"))
{
    using var reader = new MetricsReader();
    if (!reader.TryAttach())
    {
        Console.WriteLine("no Halo.Metrics.v1 section (collector not running?)");
        return 2;
    }
    Console.WriteLine($"collector pid={reader.CollectorPid} heartbeatAge={reader.HeartbeatAgeSeconds:0.00}s metrics={reader.MetricCount} frames={reader.FrameCursor}");
    for (int i = 0; i < reader.MetricCount; i++)
    {
        var d = reader.DescribeIndex(i);
        if (d == null) continue;
        var (type, unit, rate, name) = d.Value;
        if (type == MetricType.String)
        {
            reader.TryReadString(i, out string sv);
            Console.WriteLine($"{name,-32} \"{sv}\" ({unit}, {rate:0.##}Hz)");
        }
        else
        {
            bool ok = reader.TryRead(i, out double v, out double age);
            Console.WriteLine($"{name,-32} {(ok ? v.ToString("0.###") : "N/A"),12}  age={(ok ? age.ToString("0.00") : "-")}s ({unit}, {rate:0.##}Hz)");
        }
    }
    return 0;
}

// --pm-smoketest [pid]: verify the PresentMon SDK transport end-to-end (needs admin unless a
// PresentMon service is already running). Tracks the given pid (default: dwm, which presents
// every vblank) and prints consumed frame counts + data freshness for 5 s. Safe to run while
// a collector instance is up — it attaches to the same service.
if (args.Contains("--pm-smoketest"))
{
    string pmRoot = FindProjectRoot(AppContext.BaseDirectory);
    Log.Init(Path.Combine(pmRoot, "logs"), "pm-smoketest", alsoConsole: true);
    int pid = args.Where(a => int.TryParse(a, out _)).Select(int.Parse).FirstOrDefault();
    if (pid == 0) pid = System.Diagnostics.Process.GetProcessesByName("dwm").FirstOrDefault()?.Id ?? 0;
    if (pid == 0) { Console.WriteLine("no target pid"); return 3; }
    using var sdk = new PresentMonSdkSource();
    if (!sdk.Start(pmRoot, IsElevated(), 20)) { Console.WriteLine("sdk transport unavailable (see log above)"); return 3; }
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

// Halo.Collector — elevated data process (plan §4). Single instance.
using var singleInstance = new Mutex(true, "Local\\Halo.Collector.SingleInstance", out bool isNew);
if (!isNew)
{
    Console.WriteLine("Halo.Collector already running.");
    return 1;
}

string root = AppContext.BaseDirectory;
// project root = …\bin\Halo.Collector\ → two up; fall back to CWD for `dotnet run`
string projectRoot = FindProjectRoot(root);
Log.Init(Path.Combine(projectRoot, "logs"), "collector", alsoConsole: args.Contains("--console"));
Log.Info($"project root: {projectRoot}");
bool elevated = IsElevated();
Log.Info($"elevated: {elevated}");

var configStore = new ConfigStore(Path.Combine(projectRoot, "config"));
using var writer = new MetricsWriter();
var sink = new MetricSink(writer);

var host = new ProviderHost(sink);
var settings = configStore.Settings;

// Providers, each on its own cadence (plan §5 rate table)
host.Add(new BuiltinProvider(settings));                    // uptime, RAM, IPs, drive space: 1 Hz
host.Add(new CpuKernelProvider(), settings.DefaultRateHz);  // per-core/total CPU: cap 64 Hz
host.Add(new ProcessProvider(configStore));                 // top CPU/RAM lists: 1 Hz (cap 2)
host.Add(new DiskIoProvider(settings));                     // per-volume IO rates: default 10 Hz
host.Add(new NetworkProvider(settings));                    // net rates: default 10 Hz
host.Add(new NvmlProvider(), settings.DefaultRateHz);       // GPU: cap 20 Hz
host.Add(new LhmProvider(LhmProvider.Part.Cpu), settings.DefaultRateHz); // MSR: cap 20 Hz
host.Add(new LhmProvider(LhmProvider.Part.SuperIo, settings));           // fans/Vcore: 1 Hz (cap 2)
host.Add(new LhmProvider(LhmProvider.Part.Storage, settings)); // SMART temps: 1/30 s
host.Add(new LhmProvider(LhmProvider.Part.Gpu));            // NVAPI extras: voltage, fan RPM
host.Add(new PresentMonProvider(projectRoot, settings));    // frame data: event-driven
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
        case "reset-max": sink.ResetMax(parts.Length > 1 ? parts[1] : ""); break;
        case "reset-net": NetworkProvider.RequestTotalsReset(); break;
        case "ping": break;
        default: Log.Warn($"unknown command: {cmd}"); break;
    }
});

configStore.Changed += () =>
{
    Log.Info("config changed (hot-reload)");
    // Providers read live values from the shared ConfigStore-provided settings object where
    // they need to; structural changes (drive list) are picked up on their next poll.
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

static string FindProjectRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Halo.sln"))) return dir.FullName;
        dir = dir.Parent;
    }
    return new DirectoryInfo(start).FullName;
}

static bool IsElevated()
{
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    return new System.Security.Principal.WindowsPrincipal(identity)
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}
