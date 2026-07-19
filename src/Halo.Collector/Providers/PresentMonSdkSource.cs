using System.Diagnostics;
using System.Runtime.InteropServices;
using Halo.Shared;
using Halo.Shared.Metrics;

namespace Halo.Collector.Providers;

/// <summary>
/// SDK transport for frame data: talks to the PresentMon 2 service through PresentMonAPI2.dll
/// (both bundled, MIT) instead of parsing console-app CSV from a stdout pipe. This removes the
/// two transport delays of the console path — the ETW buffer flush cadence is tunable via
/// pmSetEtwFlushPeriod, and frames arrive through the service's shared-memory ring with no
/// block-buffered pipe — and it yields true per-frame PRESENT_START_QPC timestamps, so no
/// arrival-anchored timeline reconstruction is needed.
///
/// Connection ladder: (1) an already-running installed Intel service on the default control
/// pipe (works unelevated), (2) a Halo-owned service child from a previous collector run,
/// (3) spawn the bundled PresentMonService.exe as a child process. Outside the SCM,
/// StartServiceCtrlDispatcher fails benignly and the exe runs its real logic in console-debug
/// mode, so (3) needs no service registration: private pipe/ETW-session names, dies with us.
/// </summary>
internal sealed class PresentMonSdkSource : IDisposable
{
    private const string OwnPipe = @"\\.\pipe\Halo.PMSvc.Control";
    private const uint Capacity = 1024; // frames per pmConsumeFrames call

    public readonly record struct FrameSample(
        FrameEntry Entry, double ClickMs, double AllInputMs, double SimMs, double DispLatMs);

    private nint _session;
    private nint _query;
    private Process? _service;  // owned child; null when attached to an external service
    private uint _blobSize;
    private byte[] _buffer = [];
    private int _offQpc = -1, _offFt = -1, _offDispFt = -1, _offDispLat = -1,
                _offType = -1, _offClick = -1, _offAllInput = -1, _offSim = -1;

    public bool Tracking { get; private set; }
    public string Detail { get; private set; } = "";

    /// <summary>Frame-metric ladder: full instrumentation first, degrade if the service rejects.</summary>
    private static readonly PmApi.Metric[][] QueryLadder =
    [
        [PmApi.Metric.PresentStartQpc, PmApi.Metric.BetweenPresents, PmApi.Metric.BetweenDisplayChange,
         PmApi.Metric.UntilDisplayed, PmApi.Metric.FrameType, PmApi.Metric.ClickToPhotonLatency,
         PmApi.Metric.AllInputToPhotonLatency, PmApi.Metric.BetweenSimulationStart],
        [PmApi.Metric.PresentStartQpc, PmApi.Metric.BetweenPresents, PmApi.Metric.BetweenDisplayChange,
         PmApi.Metric.UntilDisplayed, PmApi.Metric.FrameType, PmApi.Metric.ClickToPhotonLatency,
         PmApi.Metric.AllInputToPhotonLatency],
        [PmApi.Metric.PresentStartQpc, PmApi.Metric.BetweenPresents, PmApi.Metric.BetweenDisplayChange,
         PmApi.Metric.UntilDisplayed],
    ];

    /// <param name="ownService">true (collector): kill any stray Halo service child and spawn a
    /// fresh one — a leftover child from a hard-killed collector may be data-dead and cannot be
    /// healed once attached to. false (diagnostics): attach to whatever is running, own nothing.</param>
    public bool Start(string projectRoot, bool elevated, int etwFlushMs, bool ownService = true)
    {
        Reset();

        string sdkDir = Path.Combine(projectRoot, "tools", "presentmon", "sdk");
        string dll = Path.Combine(sdkDir, "PresentMonAPI2.dll");
        string exe = Path.Combine(sdkDir, "PresentMonService.exe");
        if (!File.Exists(dll))
        {
            Log.Warn($"presentmon sdk: {dll} not found");
            return false;
        }
        PmApi.UseDll(dll);

        try
        {
            if (PmApi.pmGetApiVersion(out var v) == PmApi.Ok)
                Log.Info($"presentmon sdk: api {v.Major}.{v.Minor}.{v.Patch}");
        }
        catch (Exception ex)
        {
            Log.Warn($"presentmon sdk: cannot load middleware: {ex.Message}");
            return false;
        }

        // (1) installed Intel service on the default pipe (works without elevation;
        // externally managed, assumed healthy)
        if (PmApi.pmOpenSession(out _session) == PmApi.Ok)
        {
            Detail = "attached to installed service";
        }
        // (2) diagnostics mode: attach to a running Halo service child (e.g. the collector's)
        else if (!ownService && PmApi.pmOpenSessionWithPipe(out _session, OwnPipe) == PmApi.Ok)
        {
            Detail = "attached to existing Halo service child";
        }
        // (3) own a fresh service child; its real-time ETW session needs elevation
        else
        {
            _session = 0;
            if (!ownService || !elevated || !File.Exists(exe))
            {
                Reset();
                return false;
            }
            // a leftover child survives schtasks /End of the collector, and ETW sessions
            // outlive hard-killed owners: both linger in unknown state and can shadow or
            // starve a fresh capture — clear them before spawning
            KillStrayServiceChildren(exe);
            StopStaleEtwSession("HaloPMSvc");
            StopStaleEtwSession("HaloPM");
            if (!SpawnService(exe) || !ConnectOwnPipe())
            {
                Reset();
                return false;
            }
            Detail = "bundled service child";
        }

        if (etwFlushMs > 0)
        {
            int st = PmApi.pmSetEtwFlushPeriod(_session, (uint)Math.Clamp(etwFlushMs, 1, 1000));
            if (st != PmApi.Ok) Log.Warn($"presentmon sdk: set etw flush {etwFlushMs} ms: {PmApi.StatusName(st)}");
        }

        foreach (var metrics in QueryLadder)
        {
            var elements = new PmApi.QueryElement[metrics.Length];
            for (int i = 0; i < metrics.Length; i++) elements[i] = new PmApi.QueryElement { Metric = metrics[i] };
            int st = PmApi.pmRegisterFrameQuery(_session, out _query, elements, (ulong)elements.Length, out _blobSize);
            if (st != PmApi.Ok)
            {
                Log.Warn($"presentmon sdk: frame query ({metrics.Length} metrics): {PmApi.StatusName(st)}");
                _query = 0;
                continue;
            }
            foreach (var e in elements)
            {
                int off = (int)e.DataOffset;
                switch (e.Metric)
                {
                    case PmApi.Metric.PresentStartQpc: _offQpc = off; break;
                    case PmApi.Metric.BetweenPresents: _offFt = off; break;
                    case PmApi.Metric.BetweenDisplayChange: _offDispFt = off; break;
                    case PmApi.Metric.UntilDisplayed: _offDispLat = off; break;
                    case PmApi.Metric.FrameType: _offType = off; break;
                    case PmApi.Metric.ClickToPhotonLatency: _offClick = off; break;
                    case PmApi.Metric.AllInputToPhotonLatency: _offAllInput = off; break;
                    case PmApi.Metric.BetweenSimulationStart: _offSim = off; break;
                }
            }
            _buffer = new byte[_blobSize * Capacity];
            Log.Info($"presentmon sdk: frame query registered ({metrics.Length} metrics, blob {_blobSize} B)");
            return true;
        }

        Log.Warn("presentmon sdk: no frame query variant accepted");
        Reset();
        return false;
    }

    private static void KillStrayServiceChildren(string exe)
    {
        foreach (var p in Process.GetProcessesByName("PresentMonService"))
        {
            try
            {
                if (!string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                Log.Info($"presentmon sdk: killing stray service child {p.Id}");
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
            catch { } // access denied / already gone — the spawn will surface any real problem
            finally { p.Dispose(); }
        }
    }

    private bool SpawnService(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = @"--control-pipe \\.\pipe\Halo.PMSvc.Control --etw-session-name HaloPMSvc --shm-name-prefix halo_pm_",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            _service = Process.Start(psi);
            if (_service == null) return false;
            int logBudget = 12; // early startup/usage errors only; never block the pipes
            _service.OutputDataReceived += (_, e) =>
            { if (e.Data is { Length: > 0 } && Interlocked.Decrement(ref logBudget) >= 0) Log.Info($"pm-svc: {e.Data}"); };
            _service.ErrorDataReceived += (_, e) =>
            { if (e.Data is { Length: > 0 } && Interlocked.Decrement(ref logBudget) >= 0) Log.Warn($"pm-svc: {e.Data}"); };
            _service.BeginOutputReadLine();
            _service.BeginErrorReadLine();
            if (_service.WaitForExit(1200))
            {
                Log.Warn($"presentmon sdk: service exited immediately ({_service.ExitCode})");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("presentmon sdk: service spawn", ex);
            return false;
        }
    }

    private bool ConnectOwnPipe()
    {
        for (int i = 0; i < 25; i++)
        {
            if (_service is { HasExited: true })
            {
                Log.Warn($"presentmon sdk: service exited during connect ({_service.ExitCode})");
                return false;
            }
            if (PmApi.pmOpenSessionWithPipe(out _session, OwnPipe) == PmApi.Ok) return true;
            _session = 0;
            Thread.Sleep(200);
        }
        Log.Warn("presentmon sdk: timed out connecting to service control pipe");
        return false;
    }

    /// <summary>Throws when the transport is dead so the host re-initialises the provider.</summary>
    public void EnsureHealthy()
    {
        if (_session == 0 || _query == 0)
            throw new InvalidOperationException("presentmon sdk session not open");
        if (_service is { HasExited: true })
            throw new InvalidOperationException("presentmon sdk service child exited");
    }

    public void OnTargetChanged(int oldPid, int newPid)
    {
        if (_session == 0) return;
        if (oldPid != 0) _ = PmApi.pmStopTrackingProcess(_session, (uint)oldPid);
        Tracking = false;
        if (newPid == 0) return;
        int st = PmApi.pmStartTrackingProcess(_session, (uint)newPid);
        Tracking = st is PmApi.Ok or 7; // 7 = ALREADY_TRACKING_PROCESS
        if (!Tracking) Log.Warn($"presentmon sdk: track pid {newPid}: {PmApi.StatusName(st)}");
    }

    /// <summary>Pull all frames queued for pid since the last call. Called on the poll thread.</summary>
    public void Drain(int pid, List<FrameSample> into)
    {
        while (true)
        {
            uint n = Capacity;
            int st = PmApi.pmConsumeFrames(_query, (uint)pid, _buffer, ref n);
            if (st == PmApi.StatusInvalidPid) { Tracking = false; return; } // target exited between polls
            if (st != PmApi.Ok) throw new InvalidOperationException($"pmConsumeFrames: {PmApi.StatusName(st)}");

            for (uint i = 0; i < n; i++)
            {
                int b = (int)(i * _blobSize);
                double ft = ReadD(b, _offFt);
                double dispFt = ReadD(b, _offDispFt);
                double dispLat = ReadD(b, _offDispLat);
                int type = _offType >= 0 ? BitConverter.ToInt32(_buffer, b + _offType) : PmApi.FrameNotSet;

                // same semantics as the console-CSV path: displayed when either display-side
                // value is real; generated = any tagged type that isn't app/not-set/repeated
                bool displayed = (dispLat > 0 && !double.IsNaN(dispLat)) || (dispFt > 0 && !double.IsNaN(dispFt));
                bool generated = type is not (PmApi.FrameNotSet or PmApi.FrameApplication or PmApi.FrameRepeated);

                long qpc = _offQpc >= 0 ? (long)BitConverter.ToUInt64(_buffer, b + _offQpc) : 0;
                if (qpc == 0) qpc = Stopwatch.GetTimestamp();

                var entry = new FrameEntry
                {
                    Qpc = qpc,
                    FrametimeMs = double.IsNaN(ft) ? 0f : (float)ft,
                    DisplayedFtMs = double.IsNaN(dispFt) ? 0f : (float)dispFt,
                    Flags = (displayed ? (uint)FrameFlags.Displayed : 0)
                          | (generated ? (uint)FrameFlags.Generated : (uint)FrameFlags.AppFrame)
                          | (type == PmApi.FrameRepeated ? (uint)FrameFlags.Repeated : 0)
                          | (!displayed ? (uint)FrameFlags.Dropped : 0),
                    Pid = (uint)pid,
                };
                into.Add(new FrameSample(entry, ReadD(b, _offClick), ReadD(b, _offAllInput), ReadD(b, _offSim), dispLat));
            }

            if (n < Capacity) return;
        }
    }

    private double ReadD(int blobStart, int offset)
        => offset >= 0 ? BitConverter.ToDouble(_buffer, blobStart + offset) : double.NaN;

    private static void StopStaleEtwSession(string name)
    {
        const uint EVENT_TRACE_CONTROL_STOP = 1;
        const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
        const int ERROR_WMI_INSTANCE_NOT_FOUND = 4201;
        int structSize = Marshal.SizeOf<EventTraceProperties>();
        int size = structSize + 4096; // room for the logger/logfile name strings ControlTrace writes back
        nint buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buf, size);
            var props = new EventTraceProperties();
            props.Wnode.BufferSize = (uint)size;
            props.Wnode.Flags = WNODE_FLAG_TRACED_GUID;
            props.LoggerNameOffset = (uint)structSize;
            props.LogFileNameOffset = (uint)(structSize + 2048);
            Marshal.StructureToPtr(props, buf, false);
            int rc = ControlTraceW(0, name, buf, EVENT_TRACE_CONTROL_STOP);
            if (rc == 0) Log.Info($"presentmon sdk: stopped stale ETW session '{name}'");
            else if (rc != ERROR_WMI_INSTANCE_NOT_FOUND) Log.Warn($"presentmon sdk: stop ETW session '{name}': error {rc}");
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public ulong TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public nint LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }

    [DllImport("advapi32", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(ulong traceHandle, string instanceName, nint properties, uint controlCode);

    private void Reset()
    {
        if (_query != 0) { try { PmApi.pmFreeFrameQuery(_query); } catch { } _query = 0; }
        if (_session != 0) { try { PmApi.pmCloseSession(_session); } catch { } _session = 0; }
        try { if (_service is { HasExited: false }) _service.Kill(entireProcessTree: true); } catch { }
        _service?.Dispose();
        _service = null;
        Tracking = false;
    }

    public void Dispose() => Reset();
}
