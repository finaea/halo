using System.Diagnostics;
using System.Runtime.InteropServices;
using Halo.Shared;
using Halo.Shared.Metrics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Halo.Collector.Providers;

/// <summary>
/// Door-1 present tap: a private real-time ETW session on the DXGI and D3D9 runtime providers,
/// reporting each present the moment its start event flushes — no waiting for the frame's
/// displayed/dropped fate (fate resolution is what the resolved PresentMon lane is for).
/// Feeds the presented-stream FPS panel: per-frame ring entries tagged FrameFlags.Provisional
/// for the graph, plus live presented stats (1 s FPS/lows, 100 ms frametime mean) published
/// per present. Coverage: DXGI (D3D10/11/12) + D3D9(Ex); Vulkan/OpenGL have no runtime present
/// event, so those titles fall back to the resolved lane (vendoring PresentData for kernel-level
/// classification is the documented upgrade if that ever matters). The session is flushed
/// manually on a timer — the sub-second knob ETW's own 1 s flush granularity can't provide.
/// </summary>
internal sealed class PresentTap : IDisposable
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9"); // Microsoft-Windows-DXGI
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9"); // Microsoft-Windows-Direct3D9
    private const int DxgiPresentStart = 42;
    private const int DxgiPresentMpoStart = 55;
    private const int D3D9PresentStart = 1;
    private const uint DxgiPresentTest = 0x1;      // DXGI_PRESENT_TEST: an occlusion probe, not a present

    private TraceEventSession? _session;
    private Thread? _etwThread;
    private Thread? _flushThread;
    private volatile bool _stopping;
    private MetricSink? _sink;
    private volatile int _targetPid;
    private volatile int _flushPeriodMs = 10;
    private int _activeFlushMs = 10;
    private bool _idle;
    private long _lastFrameQpc;

    private readonly object _lock = new();         // guards _stats + _lastBySwapchain
    private readonly FrameStats _stats = new(60);
    private readonly Dictionary<ulong, long> _lastBySwapchain = new();
    private readonly FrameEntry[] _one = new FrameEntry[1];

    /// <summary>Diagnostics hook (--tap-smoketest): receives every accepted present; when set,
    /// nothing is published and nothing is written to the ring.</summary>
    public Action<FrameEntry>? Observer;

    /// <summary>Tracking a target and presents flowed within the last 2 s.</summary>
    public bool Active => _session != null && _targetPid != 0
        && Stopwatch.GetTimestamp() - Volatile.Read(ref _lastFrameQpc) < 2 * Stopwatch.Frequency;

    public bool Start(MetricSink? sink, double lowsWindowS, int flushMs, string sessionName = "HaloTap")
    {
        _sink = sink;
        _stats.SetWindow(lowsWindowS);
        try
        {
            // TraceEventSession takes over a stale same-name session from a killed collector
            _session = new TraceEventSession(sessionName) { StopOnDispose = true };
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue);
            _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational, ulong.MaxValue);
            _session.Source.Dynamic.All += OnEvent;

            _stopping = false;
            _etwThread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { if (!_stopping) Log.Error("present-tap ETW process", ex); }
            })
            { IsBackground = true, Name = "halo-tap-etw" };
            _etwThread.Start();

            _activeFlushMs = Math.Clamp(flushMs <= 0 ? 10 : flushMs, 1, 100);
            _flushPeriodMs = _activeFlushMs;
            _flushThread = new Thread(FlushLoop) { IsBackground = true, Name = "halo-tap-flush" };
            _flushThread.Start();

            Log.Info($"present-tap: session up (DXGI + D3D9, flush {_activeFlushMs} ms)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("present-tap start", ex);
            Dispose();
            return false;
        }
    }

    private void FlushLoop()
    {
        _ = timeBeginPeriod(1); // Sleep(10) is otherwise ~15.6 ms granular
        try
        {
            while (!_stopping)
            {
                Thread.Sleep(_flushPeriodMs); // idle mode slows this without restarting the thread
                try { _session?.Flush(); }
                catch { if (!_stopping) throw; }
            }
        }
        catch (Exception ex) { if (!_stopping) Log.Error("present-tap flush", ex); }
        finally { _ = timeEndPeriod(1); }
    }

    /// <summary>Idle-aware pipeline: with no 3D target there is nothing worth low latency, but
    /// the desktop's present firehose (browsers, editors) still hits our providers and the
    /// flush still sweeps kernel buffers. Idle mutes the providers (session and consumer thread
    /// stay alive) and relaxes the flush; re-arming is instant and loses only the first few
    /// presents after a target appears — which the target-switch stats reset discards anyway.</summary>
    public void SetIdle(bool idle)
    {
        if (_session == null || idle == _idle) return;
        try
        {
            if (idle)
            {
                _session.DisableProvider(DxgiProvider);
                _session.DisableProvider(D3D9Provider);
                _flushPeriodMs = 250;
            }
            else
            {
                _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue);
                _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational, ulong.MaxValue);
                _flushPeriodMs = _activeFlushMs;
            }
            _idle = idle;
            Log.Info(idle ? "present-tap: idle (providers muted, flush 250 ms)"
                          : $"present-tap: active (flush {_activeFlushMs} ms)");
        }
        catch (Exception ex)
        {
            Log.Warn($"present-tap: idle transition failed: {ex.Message}");
        }
    }

    public void SetTarget(int pid)
    {
        lock (_lock)
        {
            _targetPid = pid;
            _stats.Clear();
            _lastBySwapchain.Clear();
            Volatile.Write(ref _lastFrameQpc, 0);
        }
    }

    private void OnEvent(TraceEvent data)
    {
        int pid = _targetPid;
        if (pid == 0 || data.ProcessID != pid) return;

        ulong swapchain;
        int id = (int)data.ID;
        if (data.ProviderGuid == DxgiProvider && (id == DxgiPresentStart || id == DxgiPresentMpoStart))
        {
            if ((ReadU32(data, "Flags") & DxgiPresentTest) != 0) return;
            swapchain = ReadU64(data, "pIDXGISwapChain");
        }
        else if (data.ProviderGuid == D3D9Provider && id == D3D9PresentStart)
        {
            swapchain = ReadU64(data, "pSwapchain");
        }
        else return;

#pragma warning disable CS0618 // "discouraged" in favor of relative time — we specifically need
        // the raw QPC so ring entries share the Stopwatch clock domain with the resolved lane
        long qpc = data.TimeStampQPC;
#pragma warning restore CS0618

        FrameEntry entry;
        FrameStats.Result r = default;
        bool publish = false;
        lock (_lock)
        {
            if (pid != _targetPid) return; // target switched while the event was in flight

            double ftMs = 0;
            if (_lastBySwapchain.TryGetValue(swapchain, out long prev) && qpc > prev)
                ftMs = (qpc - prev) * 1000.0 / Stopwatch.Frequency;
            _lastBySwapchain[swapchain] = qpc;
            Volatile.Write(ref _lastFrameQpc, qpc);
            if (ftMs <= 0 || ftMs > 1000) return; // first present on this swapchain, or resumed after a pause: new baseline

            entry = new FrameEntry
            {
                Qpc = qpc,
                FrametimeMs = (float)ftMs,
                DisplayedFtMs = 0,
                Flags = (uint)(FrameFlags.Provisional | FrameFlags.AppFrame),
                Pid = (uint)pid,
            };

            if (Observer == null && _sink != null)
            {
                _stats.Add(entry);
                r = _stats.Consume(qpc);
                publish = true;
            }
        }

        if (Observer != null) { Observer(entry); return; }
        if (!publish) return;

        _one[0] = entry;
        _sink!.Writer.AppendFrames(_one); // also sets the frames-ready event → widget repaints
        _sink.Set(MetricNames.FpsPresented, r.FpsPresented);
        _sink.Set(MetricNames.FpsLow1Presented, r.Low1Presented);
        _sink.Set(MetricNames.FpsLow01Presented, r.Low01Presented);
        _sink.Set(MetricNames.FpsFrametimePresentedMs, r.AvgFrametimeShortMs);
        _sink.Set(MetricNames.FpsFrametimePresentedWorstMs, r.WorstFrametimeMs);
    }

    private static uint ReadU32(TraceEvent d, string name)
    {
        try { return d.PayloadByName(name) is object v ? Convert.ToUInt32(v) : 0; }
        catch { return 0; }
    }

    private static ulong ReadU64(TraceEvent d, string name)
    {
        try { return d.PayloadByName(name) is object v ? Convert.ToUInt64(v) : 0; }
        catch { return 0; }
    }

    public void Dispose()
    {
        _stopping = true;
        try { _session?.Dispose(); } catch { }
        _session = null;
    }

    [DllImport("winmm")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm")] private static extern uint timeEndPeriod(uint ms);
}
