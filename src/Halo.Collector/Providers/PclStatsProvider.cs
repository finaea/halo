using System.Diagnostics;
using System.Runtime.InteropServices;
using Halo.Shared;
using Halo.Metrics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Halo.Collector.Providers;

/// <summary>
/// Consumes NVIDIA "PCL Stats" latency markers directly from ETW — the same instrumentation
/// FrameView and the NVIDIA overlay read — giving true marker-based PC Latency and the
/// game-rendered (pre-frame-gen) rate, with NO NVIDIA App and no game-specific integration
/// beyond the Reflex the game already ships (open MIT spec: github.com/NVIDIA/PCLStats).
///
/// Protocol: the game only emits markers while a latency tool is "pinging" it — we broadcast
/// the registered PCLSTATS ping window message on a timer, and enable the PCLStats TraceLogging
/// provider on our own real-time ETW session. Markers carry a monotonic FrameID; PC latency for
/// a frame = (its display/present time) − (its SimulationStart time). We publish a rolling
/// average PCL and the SimulationStart cadence (rendered FPS).
///
/// DISCOVERY MODE: until the exact provider/marker constants are confirmed against the live
/// game, set HaloPclDiscovery=1 in the environment to log every event name + payload fields.
/// </summary>
public sealed class PclStatsProvider(string providerName, Guid providerGuidOverride) : ISensorProvider
{
    public string Name => "pclstats";
    public double MaxRateHz => 20;
    public double DefaultRateHz => CollectorRates.PclStats;

    /// <summary>Owns a real-time ETW session, which is admin-only. No unelevated path exists.</summary>
    public bool NeedsElevation => true;

    public string? UnavailableReason => _unavailableReason;
    private string? _unavailableReason;

    // marker enum (PCLSTATS_LATENCY_MARKER_TYPE); confirmed values filled in from the header.
    private const int SIMULATION_START = 0;
    private const int PRESENT_END = 5;
    private const int PC_LATENCY_PING = 8;
    private const int OUT_OF_BAND_PRESENT_END = 12;   // async-present titles

    private TraceEventSession? _session;
    private Thread? _etwThread;
    private volatile bool _stopping;
    private readonly bool _discovery = Environment.GetEnvironmentVariable("HaloPclDiscovery") == "1";
    private readonly HashSet<string> _discoverySeen = new();

    // frame accounting (accessed from ETW thread + poll thread). All times are the ETW
    // session-relative millisecond clock (monotonic), which is all interval math needs.
    private readonly object _lock = new();
    private readonly Dictionary<ulong, double> _pingConsumeMs = new();  // frameId -> PC_LATENCY_PING (consume) ms
    private readonly Dictionary<ulong, double> _queueForFrame = new();  // frameId -> queue wait (post→consume)
    private double _lastInputPostMs = -1;                              // PCLStatsInput (post) ms
    // time-windowed samples (ETW-relative ms, value) — averaged over the last ~1.5 s so PC LAT
    // tracks changes in ~1 s like the FPS headline, instead of a slow sample-count decay.
    private readonly Queue<(double Ms, double V)> _renderWin = new(); // ping consume → present (FS2P)
    private readonly Queue<(double Ms, double V)> _queueWin = new();  // input post → ping consume (I2FS, ②a)
    private const double LatencyWindowMs = 1500;
    private int _simCountWindow;
    private double _windowStartMs = -1;
    private double _lastEventMs;
    private long _lastEventWallQpc;
    private volatile int _targetPid;
    private readonly Dictionary<int, int> _markerHist = new();
    private DateTime _nextHistLog = DateTime.MinValue;

    public bool Initialize(MetricSink sink)
    {
        if (!IsElevated())
        {
            _unavailableReason = ProviderError.Unelevated; // ETW real-time session needs admin
            return false;
        }
        _unavailableReason = null;

        sink.Register(MetricNames.LatencyQueueMs, MetricType.Double, MetricUnit.Milliseconds, Name, DefaultRateHz);
        sink.Register(MetricNames.LatencyRenderMs, MetricType.Double, MetricUnit.Milliseconds, Name, DefaultRateHz);
        sink.Register(MetricNames.RenderRateHz, MetricType.Double, MetricUnit.Hertz, Name, DefaultRateHz);
        // The headline number every latency panel shows. Published here rather than computed in
        // each consumer so a third-party tool gets the same value Halo's own widget draws.
        sink.Register(MetricNames.LatencyPcMs, MetricType.Double, MetricUnit.Milliseconds, Name, DefaultRateHz,
            MetricSemantics.Calc, MetricFlags.Derived);

        Guid guid = providerGuidOverride != Guid.Empty
            ? providerGuidOverride
            : TraceEventProviders.GetEventSourceGuidFromName(providerName);
        Log.Info($"pclstats: provider '{providerName}' guid {guid}");

        try
        {
            _session = new TraceEventSession("HaloPclStats")
            {
                StopOnDispose = true,
            };
            _session.EnableProvider(guid, TraceEventLevel.Verbose, ulong.MaxValue);
            _session.Source.Dynamic.All += OnEvent;
            _session.Source.AllEvents += OnAnyEvent; // discovery / unmatched

            _stopping = false;
            _windowStartMs = -1;
            _etwThread = new Thread(() => { try { _session.Source.Process(); } catch (Exception ex) { if (!_stopping) Log.Error("pclstats ETW process", ex); } })
            { IsBackground = true, Name = "halo-pcl-etw" };
            _etwThread.Start();

            // No ping broadcast: per NVIDIA's reference pclstats.h the game self-pings once the
            // provider is enabled (the ETW enable callback flips its internal g_PCLStatsEnable).
            Log.Info("pclstats: ETW session started");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("pclstats init", ex);
            _unavailableReason = ProviderError.Failed;
            Dispose();
            return false;
        }
    }

    /// <summary>Foreground 3D app PID, read back each Poll from fps.app.pid (published by
    /// PresentMonProvider). 0 = accept all — what an unelevated or failed PresentMon provider
    /// degrades to, and the behaviour this provider had before the filter existed.
    ///
    /// Everything keyed on FrameID is dropped on a change: FrameID is monotonic PER PROCESS, so
    /// the previous game's half-finished frames would collide with the new one's and produce
    /// latency numbers belonging to neither.</summary>
    public void SetTargetPid(int pid)
    {
        if (pid == _targetPid) return;   // volatile read: the unchanged case never takes the lock
        lock (_lock)
        {
            if (pid == _targetPid) return;
            _targetPid = pid;
            _pingConsumeMs.Clear();
            _queueForFrame.Clear();
            _renderWin.Clear();
            _queueWin.Clear();
            _lastInputPostMs = -1;
            _simCountWindow = 0;
            _windowStartMs = -1;   // restart the rendered-rate window with the new game
        }
    }

    private void OnAnyEvent(TraceEvent data)
    {
        if (!_discovery) return;
        // dump each distinct event SHAPE once — per-event logging flooded a session log
        // to 232 MB (384k identical lines) when discovery was left on with a game running
        if (data.ProviderName?.Contains("PCL", StringComparison.OrdinalIgnoreCase) == true
            || data.ProviderName?.Contains("Latency", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (_discoverySeen.Add($"{data.ProviderName}/{data.EventName}/{(int)data.ID}"))
            {
                var fields = string.Join(",", data.PayloadNames);
                Log.Info($"pcl-discovery: prov='{data.ProviderName}' ev='{data.EventName}' id={data.ID} fields=[{fields}]");
            }
        }
    }

    private void OnEvent(TraceEvent data)
    {
        // Scope to the tracked game. Reflex is instrumented per process and FrameID only counts
        // up within one, so a second Reflex-enabled process (a launcher, a benchmark, a second
        // game) writes into the same FrameID-keyed maps and the published latency belongs to
        // neither. 0 keeps the documented accept-all fallback.
        int target = _targetPid;
        if (target != 0 && data.ProcessID != target) return;

        double ms = data.TimeStampRelativeMSec;

        // "PCLStatsInput" fires when the game's ping thread POSTS the synthetic input (the top
        // of segment ②a — where the NVIDIA overlay starts its clock). It carries no FrameID;
        // we time-correlate it to the next PC_LATENCY_PING (consume) below.
        if (data.EventName == "PCLStatsInput")
        {
            lock (_lock) _lastInputPostMs = ms;
            return;
        }

        // TraceLogging payloads: marker type + frame id. Field names vary by header revision.
        int marker = ReadInt(data, "Marker", "marker", "markerType", "MarkerType", "type");
        ulong frameId = ReadULong(data, "FrameID", "frameID", "FrameId", "frameId", "frame");
        if (marker < 0) return;

        lock (_lock)
        {
            _lastEventMs = ms;
            _lastEventWallQpc = Stopwatch.GetTimestamp();
            if (_discovery) { _markerHist.TryGetValue(marker, out int c); _markerHist[marker] = c + 1; }
            switch (marker)
            {
                case SIMULATION_START:
                    _simCountWindow++; // counted for rendered-rate (pre-frame-gen) only
                    break;

                // PC_LATENCY_PING = the synthetic input consumed at frame start (~5-10/sec).
                // queue wait (②a) = this consume time − the matching PCLStatsInput post time.
                case PC_LATENCY_PING:
                    _pingConsumeMs[frameId] = ms;
                    _queueForFrame[frameId] = (_lastInputPostMs >= 0 && ms > _lastInputPostMs && ms - _lastInputPostMs < 200)
                        ? ms - _lastInputPostMs : double.NaN;
                    if (_pingConsumeMs.Count > 256) TrimOldest();
                    break;

                // present closes the frame: render (FS2P) = present − ping-consume; queue (②a)
                // was stamped above. Widget adds present→display (P2D) for the full LAT.
                case PRESENT_END:
                case OUT_OF_BAND_PRESENT_END:
                    if (_pingConsumeMs.Remove(frameId, out double consumeMs) && ms > consumeMs)
                    {
                        double render = ms - consumeMs;
                        if (render is > 0 and < 500) _renderWin.Enqueue((ms, render));
                        if (_queueForFrame.Remove(frameId, out double q) && !double.IsNaN(q))
                            _queueWin.Enqueue((ms, q));
                    }
                    break;
            }
        }
    }

    private void TrimOldest()
    {
        // drop the 64 lowest frame ids whose present we never saw (stale pings)
        foreach (var k in _pingConsumeMs.Keys.OrderBy(x => x).Take(64).ToList())
        {
            _pingConsumeMs.Remove(k);
            _queueForFrame.Remove(k);
        }
    }

    public void Poll(MetricSink sink)
    {
        if (_session == null) return;
        if (_etwThread is { IsAlive: false }) throw new InvalidOperationException("pclstats ETW thread died");

        // The two providers run on their own host threads with no reference to each other, so the
        // target travels through the section the same way latency.pc.ms reads the display segment
        // back (below). A missing or stale pid widens to accept-all rather than going silent.
        SetTargetPid(sink.TryGet(MetricNames.FpsAppPid, out double pid, maxAgeS: 3) ? (int)pid : 0);

        double render = 0, queue = 0, renderHz = 0;
        bool haveRender, haveQueue, eventsFresh;
        lock (_lock)
        {
            // events flowing? (game stopped presenting → freeze/stale instead of last value)
            eventsFresh = _lastEventWallQpc != 0 && (Stopwatch.GetTimestamp() - _lastEventWallQpc) < 2 * Stopwatch.Frequency;
            double cutoff = _lastEventMs - LatencyWindowMs;
            render = WinAvg(_renderWin, cutoff, out haveRender);
            queue = WinAvg(_queueWin, cutoff, out haveQueue);
            if (_windowStartMs < 0) _windowStartMs = _lastEventMs;
            double windowMs = _lastEventMs - _windowStartMs;
            if (windowMs >= 500 && _simCountWindow > 0)
            {
                renderHz = _simCountWindow * 1000.0 / windowMs;
                _simCountWindow = 0;
                _windowStartMs = _lastEventMs;
            }
        }

        if (eventsFresh && haveRender && render > 0) sink.Set(MetricNames.LatencyRenderMs, render);
        else sink.MarkStale(MetricNames.LatencyRenderMs);
        if (eventsFresh && haveQueue) sink.Set(MetricNames.LatencyQueueMs, queue);
        else sink.MarkStale(MetricNames.LatencyQueueMs);
        PublishPcLatency(sink, eventsFresh && haveRender && render > 0, render, haveQueue ? queue : 0);
        if (renderHz > 0) sink.Set(MetricNames.RenderRateHz, renderHz);
        else if (!eventsFresh) sink.MarkStale(MetricNames.RenderRateHz);

        if (_discovery && DateTime.UtcNow >= _nextHistLog)
        {
            _nextHistLog = DateTime.UtcNow.AddSeconds(3);
            string hist;
            lock (_lock) hist = string.Join(" ", _markerHist.OrderBy(k => k.Key).Select(k => $"m{k.Key}={k.Value}"));
            Log.Info($"pcl-hist: {hist} | render={render:0.0} queue={queue:0.0} renderN={_renderWin.Count} inputPost={(_lastInputPostMs >= 0 ? "y" : "n")}");
        }
    }

    /// <summary>
    /// PC latency the way the NVIDIA overlay counts it: queue wait (input post → ping consume)
    /// + render (consume → present) + display (present → photons, from PresentMon's
    /// MsUntilDisplayed). Render is the mandatory component — without a marker-tagged frame there
    /// is no PC latency to report — and the other two add on when their provider has them, so a
    /// machine without the PresentMon service still gets the two-segment number.
    ///
    /// The display segment belongs to another provider, so it is read back out of the section
    /// with a freshness bound rather than cached here: a frozen component would otherwise keep
    /// inflating the sum after the game stopped presenting.
    /// </summary>
    private static void PublishPcLatency(MetricSink sink, bool haveRender, double render, double queue)
    {
        if (!haveRender)
        {
            sink.MarkStale(MetricNames.LatencyPcMs);
            return;
        }
        double display = sink.TryGet(MetricNames.FpsDisplayLatencyMs, out double d, maxAgeS: 3) ? d : 0;
        sink.Set(MetricNames.LatencyPcMs, queue + render + display);
    }

    /// <summary>Mean of samples newer than cutoff; trims older ones from the front. Caller holds _lock.</summary>
    private static double WinAvg(Queue<(double Ms, double V)> q, double cutoff, out bool any)
    {
        while (q.Count > 0 && q.Peek().Ms < cutoff) q.Dequeue();
        any = q.Count > 0;
        if (!any) return 0;
        double s = 0;
        foreach (var e in q) s += e.V;
        return s / q.Count;
    }

    private static int ReadInt(TraceEvent d, params string[] names)
    {
        foreach (var n in names)
        {
            object? v = SafePayload(d, n);
            if (v != null) { try { return Convert.ToInt32(v); } catch { } }
        }
        return -1;
    }

    private static ulong ReadULong(TraceEvent d, params string[] names)
    {
        foreach (var n in names)
        {
            object? v = SafePayload(d, n);
            if (v != null) { try { return Convert.ToUInt64(v); } catch { } }
        }
        return 0;
    }

    private static object? SafePayload(TraceEvent d, string name)
    {
        try { return d.PayloadByName(name); } catch { return null; }
    }

    private static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public void Dispose()
    {
        _stopping = true;
        try { _session?.Dispose(); } catch { }
        _session = null;
    }
}
