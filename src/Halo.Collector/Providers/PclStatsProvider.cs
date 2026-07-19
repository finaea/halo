using System.Diagnostics;
using System.Runtime.InteropServices;
using Halo.Shared;
using Halo.Shared.Metrics;
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
    public double DefaultRateHz => 10;

    // marker enum (PCLSTATS_LATENCY_MARKER_TYPE); confirmed values filled in from the header.
    private const int SIMULATION_START = 0;
    private const int PRESENT_END = 5;
    private const int PC_LATENCY_PING = 8;
    private const int OUT_OF_BAND_PRESENT_END = 12;   // async-present titles

    private TraceEventSession? _session;
    private Thread? _etwThread;
    private volatile bool _stopping;
    private readonly bool _discovery = Environment.GetEnvironmentVariable("HaloPclDiscovery") == "1";

    // frame accounting (accessed from ETW thread + poll thread). All times are the ETW
    // session-relative millisecond clock (monotonic), which is all interval math needs.
    private readonly object _lock = new();
    private readonly Dictionary<ulong, double> _simStartMs = new();  // frameId -> SimulationStart ms
    private double _pclSumMs;
    private int _pclCount;
    private int _simCountWindow;
    private double _windowStartMs = -1;
    private double _lastEventMs;
    private volatile int _targetPid;
    private readonly Dictionary<int, int> _markerHist = new();
    private DateTime _nextHistLog = DateTime.MinValue;

    public bool Initialize(MetricSink sink)
    {
        if (!IsElevated()) return false; // ETW real-time session needs admin

        sink.Register(MetricNames.LatencyPclMs, MetricType.Double, MetricUnit.Milliseconds, Name, MaxRateHz);
        sink.Register(MetricNames.RenderRateHz, MetricType.Double, MetricUnit.Hertz, Name, MaxRateHz);

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
            Dispose();
            return false;
        }
    }

    /// <summary>Foreground 3D app PID, set by the PresentMon provider's tracking via the sink.
    /// Until wired, 0 = accept all (single-GPU, one game at a time is the norm).</summary>
    public void SetTargetPid(int pid) => _targetPid = pid;

    private void OnAnyEvent(TraceEvent data)
    {
        if (!_discovery) return;
        // one-time-ish dump of shape; throttled by only logging PC-latency-ish providers
        if (data.ProviderName?.Contains("PCL", StringComparison.OrdinalIgnoreCase) == true
            || data.ProviderName?.Contains("Latency", StringComparison.OrdinalIgnoreCase) == true)
        {
            var fields = string.Join(",", data.PayloadNames);
            Log.Info($"pcl-discovery: prov='{data.ProviderName}' ev='{data.EventName}' id={data.ID} fields=[{fields}]");
        }
    }

    private void OnEvent(TraceEvent data)
    {
        // TraceLogging payloads: marker type + frame id. Field names vary by header revision;
        // probe the common spellings.
        int marker = ReadInt(data, "Marker", "marker", "markerType", "MarkerType", "type");
        ulong frameId = ReadULong(data, "FrameID", "frameID", "FrameId", "frameId", "frame");
        if (marker < 0) return;

        double ms = data.TimeStampRelativeMSec;

        lock (_lock)
        {
            _lastEventMs = ms;
            if (_discovery) { _markerHist.TryGetValue(marker, out int c); _markerHist[marker] = c + 1; }
            switch (marker)
            {
                case SIMULATION_START:
                    _simStartMs[frameId] = ms;
                    _simCountWindow++;
                    if (_simStartMs.Count > 512) TrimOldest();
                    break;

                // frame reaches the screen: PRESENT_END (or the async-present variant) is the
                // last ETW-visible point. SimulationStart→PresentEnd is the pure-ETW PCL proxy
                // (frame-start-to-present); it under-reports the overlay's number by the
                // scan-out/flip time (P2D), which ETW can't see without LDAT hardware.
                case PRESENT_END:
                case OUT_OF_BAND_PRESENT_END:
                    if (_simStartMs.Remove(frameId, out double simMs) && ms > simMs)
                    {
                        double lat = ms - simMs;
                        if (lat is > 0 and < 500) { _pclSumMs += lat; _pclCount++; }
                    }
                    break;
            }
        }
    }

    private void TrimOldest()
    {
        // drop the 128 lowest frame ids (stale, never matched a present)
        foreach (var k in _simStartMs.Keys.OrderBy(x => x).Take(128).ToList())
            _simStartMs.Remove(k);
    }

    public void Poll(MetricSink sink)
    {
        if (_session == null) return;
        if (_etwThread is { IsAlive: false }) throw new InvalidOperationException("pclstats ETW thread died");

        double pcl = 0, renderHz = 0;
        bool fresh;
        lock (_lock)
        {
            if (_pclCount > 0) pcl = _pclSumMs / _pclCount;
            if (_windowStartMs < 0) _windowStartMs = _lastEventMs;
            double windowMs = _lastEventMs - _windowStartMs;
            if (windowMs >= 500 && _simCountWindow > 0)
            {
                renderHz = _simCountWindow * 1000.0 / windowMs;
                _simCountWindow = 0;
                _windowStartMs = _lastEventMs;
            }
            if (_pclCount > 200) { _pclSumMs /= 2; _pclCount /= 2; } // rolling, not cumulative
            fresh = _simStartMs.Count > 0 || _pclCount > 0;
        }

        if (pcl > 0) sink.Set(MetricNames.LatencyPclMs, pcl);
        else sink.MarkStale(MetricNames.LatencyPclMs);
        if (renderHz > 0) sink.Set(MetricNames.RenderRateHz, renderHz);
        else if (!fresh) sink.MarkStale(MetricNames.RenderRateHz);

        if (_discovery && DateTime.UtcNow >= _nextHistLog)
        {
            _nextHistLog = DateTime.UtcNow.AddSeconds(3);
            string hist;
            lock (_lock) hist = string.Join(" ", _markerHist.OrderBy(k => k.Key).Select(k => $"m{k.Key}={k.Value}"));
            Log.Info($"pcl-hist: {hist} | pending-sim={_simStartMs.Count} pclCount={_pclCount}");
        }
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
