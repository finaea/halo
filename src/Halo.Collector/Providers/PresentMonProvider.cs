using System.Diagnostics;
using System.Runtime.InteropServices;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Metrics;

namespace Halo.Collector.Providers;

/// <summary>
/// Frame pipeline data from Intel PresentMon (bundled, MIT). Filters frames by the foreground
/// 3D app; per-frame events land in the shared-memory frame ring, windowed stats (1%/0.1% lows,
/// worst frametime, FG ratio, Click-to-Photon) are published at poll rate.
///
/// Two transports behind one seam (collector.presentMonTransport: auto | sdk):
///  - **sdk** (plan D7, preferred): PresentMon 2 service + PresentMonAPI2.dll. Frames are pulled
///    from the service's shared-memory ring each Poll with true PRESENT_START_QPC timestamps;
///    ETW flush cadence is tuned via pmSetEtwFlushPeriod (collector.presentMonEtwFlushMs), so
///    frame data is ~flush+poll fresh instead of ~1 s (console ETW batching + 4 KB stdout pipe).
///    See PresentMonSdkSource for the service lifecycle (no SCM registration needed).
///  - **console**: the capture app as a child process with CSV over stdout ("--stop_existing_session";
///    frame timestamps reconstructed by anchoring the CSV time column to arrival QPC). Fallback
///    when the SDK path is unavailable.
/// Both need elevation to own an ETW session; attaching to an already-installed running
/// PresentMon service works unelevated.
/// </summary>
public sealed class PresentMonProvider(ConfigStore config) : ISensorProvider
{
    /// <summary>Collector knobs, read live: ConfigStore.Reload allocates a new settings object,
    /// so holding a snapshot means never seeing an edit (assessment §4.3).</summary>
    private CollectorSettings Settings => config.Settings.Collector;

    public string Name => "presentmon";
    public double MaxRateHz => 120;    // frame drain + stats publish; lows cached at 2 Hz inside FrameStats
    public double DefaultRateHz => CollectorRates.PresentMon; // frames reach the ring in ≤25 ms batches; stats publish per poll

    /// <summary>The 1% / 0.1% lows are recomputed at 2 Hz and cached between (the sort over the
    /// full window dominates), so that — not the poll rate — is their nominal cadence.</summary>
    private const double LowsRateHz = 2;

    /// <summary>Owning an ETW session needs admin. Attaching to an already-installed running
    /// PresentMon service works unelevated, which is why this is a flag and not a hard gate.</summary>
    public bool NeedsElevation => true;

    public string? UnavailableReason => _unavailableReason;
    private string? _unavailableReason;

    private Process? _proc;
    private Thread? _pumpThread;
    private volatile bool _stopping;
    private PresentMonSdkSource? _sdk;
    private PresentTap? _tap;
    private readonly List<PresentMonSdkSource.FrameSample> _sdkScratch = new(256);

    private readonly object _statsLock = new();
    private readonly FrameStats _presented = new(60);
    private FrameStats Stats => _presented;

    // shared with pump thread
    private volatile int _targetPid;
    private string _targetName = "";
    private long _lastTargetFrameQpc;
    private double _clickSum, _allInputSum;
    private int _clickCount, _allInputCount;
    private double _simMsSum;                    // app simulation pacing (FG-ratio fallback)
    private int _simCount;
    private double _dispLatSum;                 // present->displayed (P2D)
    private int _dispLatCount;
    private readonly List<FrameEntry> _pendingRing = new(256);

    private MetricSink? _sink;
    private DateTime _nextNgxScan = DateTime.MinValue;
    private int _ngxScannedPid;
    private long _nextSlowPublishQpc; // 1 Hz cadence for app name/pid/refresh (constants between target changes)
    private long _idleSinceQpc;       // 0 while a target is tracked
    private bool _fpsIdleMode;        // relaxed flush + muted tap after 10 s without a target

    public bool Initialize(MetricSink sink)
    {
        _sink = sink;

        // re-init safety (host calls Initialize again after repeated poll failures):
        // tear down any previous transport before starting a fresh one
        _stopping = true;
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        _proc = null;
        _sdk?.Dispose();
        _sdk = null;
        _tap?.Dispose();
        _tap = null;
        _fpsIdleMode = false;
        _idleSinceQpc = 0;

        // registration is idempotent and must precede the elevation gate: the sdk transport
        // can attach to an already-installed running service without admin.
        // Each metric registers the cadence it really changes at, not the provider's ceiling:
        // stats publish once per poll, the lows are recomputed at LowsRateHz inside FrameStats,
        // and the app name / refresh rate / DLSS scan run on their own slower timers.
        sink.Register(MetricNames.FpsPresented, MetricType.Double, MetricUnit.Fps, Name, DefaultRateHz);
        sink.Register(MetricNames.FpsDisplayed, MetricType.Double, MetricUnit.Fps, Name, DefaultRateHz);
        sink.Register(MetricNames.FpsFrametimePresentedMs, MetricType.Double, MetricUnit.Milliseconds, Name, DefaultRateHz);
        sink.Register(MetricNames.FpsFrametimePresentedWorstMs, MetricType.Double, MetricUnit.Milliseconds, Name, DefaultRateHz);
        sink.Register(MetricNames.FpsFrametimeDisplayedMs, MetricType.Double, MetricUnit.Milliseconds, Name, DefaultRateHz);
        sink.Register(MetricNames.FpsFrametimeDisplayedWorstMs, MetricType.Double, MetricUnit.Milliseconds, Name, DefaultRateHz);
        sink.Register(MetricNames.FpsTapActive, MetricType.Double, MetricUnit.None, Name, 1);
        sink.Register(MetricNames.FpsLow1Presented, MetricType.Double, MetricUnit.Fps, Name, LowsRateHz);
        sink.Register(MetricNames.FpsLow01Presented, MetricType.Double, MetricUnit.Fps, Name, LowsRateHz);
        sink.Register(MetricNames.FpsLow1Displayed, MetricType.Double, MetricUnit.Fps, Name, LowsRateHz);
        sink.Register(MetricNames.FpsLow01Displayed, MetricType.Double, MetricUnit.Fps, Name, LowsRateHz);
        sink.Register(MetricNames.FpsFgRatio, MetricType.Double, MetricUnit.None, Name, DefaultRateHz);
        sink.Register(MetricNames.FpsRefreshHz, MetricType.Double, MetricUnit.Hertz, Name, 1);
        sink.Register(MetricNames.FpsAppName, MetricType.String, MetricUnit.Text, Name, 1);
        sink.Register(MetricNames.LatencyClickMs, MetricType.Double, MetricUnit.Milliseconds, Name, DefaultRateHz);
        sink.Register(MetricNames.LatencyAllInputMs, MetricType.Double, MetricUnit.Milliseconds, Name, DefaultRateHz);
        // latency.pcl.ms is owned by PclStatsProvider (true marker-based). PresentMon only
        // contributes the present->displayed (P2D) span it uniquely measures.
        sink.Register(MetricNames.FpsDisplayLatencyMs, MetricType.Double, MetricUnit.Milliseconds, Name, DefaultRateHz);
        sink.Register(MetricNames.DlssModel, MetricType.String, MetricUnit.Text, Name, 0.5);
        sink.Register(MetricNames.DlssSrPresent, MetricType.Double, MetricUnit.None, Name, 0.5);
        sink.Register(MetricNames.DlssFgPresent, MetricType.Double, MetricUnit.None, Name, 0.5);
        sink.Register(MetricNames.DlssRrPresent, MetricType.Double, MetricUnit.None, Name, 0.5);
        sink.Register(MetricNames.DlssVersion, MetricType.String, MetricUnit.Text, Name, 0.5);

        Stats.SetWindow(Settings.FrameLowsWindowS);

        bool elevated = Elevation.IsElevated;
        // Documented values are "auto" and "sdk" (the console capture app is an internal
        // fallback, not something a user picks any more). Anything else means auto.
        string transport = Settings.PresentMonTransport.Trim().ToLowerInvariant();
        if (transport is not ("auto" or "sdk"))
        {
            Log.Warn($"collector.presentMonTransport '{transport}' is not a value Halo knows — using \"auto\"");
            transport = "auto";
        }
        _unavailableReason = null;

        var sdk = new PresentMonSdkSource();
        if (sdk.Start(Paths.PresentMonDir, elevated, Settings.PresentMonEtwFlushMs))
        {
            _sdk = sdk;
            Log.Info($"presentmon transport: sdk ({sdk.Detail})");
            StartTap(sink, elevated);
            return true;
        }
        sdk.Dispose();
        if (transport == "sdk")
        {
            _unavailableReason = elevated ? ProviderError.NoSdk : ProviderError.Unelevated;
            return false;
        }

        if (!elevated)
        {
            // The console transport owns its own ETW session, so without admin there is nothing
            // left to try. The host retries with backoff in case a service appears later.
            _unavailableReason = ProviderError.Unelevated;
            return false;
        }

        string? exe = Directory.Exists(Paths.PresentMonDir)
            ? Directory.EnumerateFiles(Paths.PresentMonDir, "PresentMon-*-x64.exe").FirstOrDefault()
            : null;
        if (exe == null)
        {
            Log.Warn("presentmon: binary not found under tools\\presentmon");
            _unavailableReason = ProviderError.NoSdk;
            return false;
        }

        // instrumentation ladder: prefer app-timing (marker-based PCL + sim pacing) + frame types
        string[] argLadder =
        [
            " --track_frame_type --track_app_timing",
            " --track_app_timing",
            " --track_frame_type",
            "",
        ];
        bool started = false;
        foreach (string extra in argLadder)
            if (StartCapture(exe, extra)) { started = true; break; }
        if (started)
        {
            Log.Info("presentmon transport: console app");
            StartTap(sink, elevated: true);
        }
        else _unavailableReason = ProviderError.Failed;
        return started;
    }

    /// <summary>Door-1 tap for the presented stream (collector.presentedTap). Independent of the
    /// resolved transport; failure just leaves the presented panel on the resolved lane.</summary>
    private void StartTap(MetricSink sink, bool elevated)
    {
        if (!elevated || !Settings.PresentedTap.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) return;
        var tap = new PresentTap();
        if (tap.Start(sink, Settings.FrameLowsWindowS, Settings.PresentMonEtwFlushMs))
        {
            _tap = tap;
            if (_targetPid != 0) tap.SetTarget(_targetPid);
        }
        else
        {
            tap.Dispose();
        }
    }

    private bool StartCapture(string exe, string extraArgs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--output_stdout --stop_existing_session --session_name HaloPM" + extraArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            _proc = Process.Start(psi);
            if (_proc == null) return false;

            // fail fast if it dies immediately (bad arg, no admin, session conflict)
            if (_proc.WaitForExit(1500))
            {
                string err = _proc.StandardError.ReadToEnd();
                Log.Warn($"presentmon exited {_proc.ExitCode} (args=[{extraArgs}]): {Truncate(err, 400)}");
                _proc = null;
                return false;
            }

            _stopping = false;
            _pumpThread = new Thread(() => Pump(_proc)) { IsBackground = true, Name = "halo-pm-pump" };
            _pumpThread.Start();
            Log.Info($"presentmon started (args=[{extraArgs}])");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("presentmon start", ex);
            return false;
        }
    }

    private void Pump(Process proc)
    {
        try
        {
            var reader = proc.StandardOutput;
            string? header = reader.ReadLine();
            if (header == null) return;
            var cols = ParseHeader(header);
            Log.Info($"presentmon columns: time={cols.Time}(x{cols.TimeScale}) ft={cols.FrameTime} dispFt={cols.DisplayedTime} dispLat={cols.DisplayLatency} click={cols.Click} allInput={cols.AllInput} frameType={cols.FrameType} | header: {Truncate(header, 800)}");
            long qpcFreq = Stopwatch.Frequency;
            long anchorQpc = 0;
            double anchorTime = double.NaN;
            long synthQpc = 0; // fallback timeline accumulated from frametimes (no usable time column)

            string? line;
            while (!_stopping && (line = reader.ReadLine()) != null)
            {
                var f = line.Split(',');
                if (f.Length < 4) continue;

                uint pid = ParseU(f, cols.Pid);
                int target = _targetPid;
                if (target == 0 || pid != (uint)target) continue;

                double t = ParseD(f, cols.Time) * cols.TimeScale;
                double ft = ParseD(f, cols.FrameTime);
                double dispFt = ParseD(f, cols.DisplayedTime);
                double dispLat = ParseD(f, cols.DisplayLatency);
                double click = ParseD(f, cols.Click);
                double allInput = ParseD(f, cols.AllInput);
                string frameType = cols.FrameType >= 0 && cols.FrameType < f.Length ? f[cols.FrameType] : "";

                if (double.IsNaN(anchorTime) && !double.IsNaN(t))
                {
                    anchorTime = t;
                    anchorQpc = Stopwatch.GetTimestamp();
                }
                long qpc;
                if (!double.IsNaN(t))
                {
                    qpc = anchorQpc + (long)((t - anchorTime) * qpcFreq);
                }
                else
                {
                    // no time column: build a monotonic timeline by accumulating frametimes
                    // (bursty stdout would otherwise clump many frames onto one timestamp,
                    // which wrecks any short-window rate math)
                    if (synthQpc == 0) synthQpc = Stopwatch.GetTimestamp();
                    else synthQpc += (long)((double.IsNaN(ft) ? 0.007 : ft / 1000.0) * qpcFreq);
                    qpc = synthQpc;
                }

                bool displayed = !double.IsNaN(dispLat) || (!double.IsNaN(dispFt) && dispFt > 0);
                bool generated = frameType.Length > 0 && !frameType.Equals("Application", StringComparison.OrdinalIgnoreCase)
                                                       && !frameType.Equals("NotSet", StringComparison.OrdinalIgnoreCase)
                                                       && !frameType.Equals("Repeated", StringComparison.OrdinalIgnoreCase);

                var entry = new FrameEntry
                {
                    Qpc = qpc,
                    FrametimeMs = double.IsNaN(ft) ? 0f : (float)ft,
                    DisplayedFtMs = double.IsNaN(dispFt) ? 0f : (float)dispFt,
                    Flags = (displayed ? (uint)FrameFlags.Displayed : 0)
                          | (generated ? (uint)FrameFlags.Generated : (uint)FrameFlags.AppFrame)
                          | (frameType.Equals("Repeated", StringComparison.OrdinalIgnoreCase) ? (uint)FrameFlags.Repeated : 0)
                          | (!displayed ? (uint)FrameFlags.Dropped : 0),
                    Pid = pid,
                };

                double simMs = ParseD(f, cols.SimStart);

                lock (_statsLock)
                {
                    Stats.Add(entry);
                    _pendingRing.Add(entry);
                    _lastTargetFrameQpc = qpc;
                    if (!double.IsNaN(click) && click > 0) { _clickSum += click; _clickCount++; }
                    if (!double.IsNaN(allInput) && allInput > 0) { _allInputSum += allInput; _allInputCount++; }
                    if (!double.IsNaN(simMs) && simMs > 0) { _simMsSum += simMs; _simCount++; }
                    if (!double.IsNaN(dispLat) && dispLat is > 0 and < 200) { _dispLatSum += dispLat; _dispLatCount++; }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stopping) Log.Error("presentmon pump", ex);
        }
        finally
        {
            if (!_stopping) Log.Warn("presentmon pump ended (process died?)");
        }
    }

    public void Poll(MetricSink sink)
    {
        if (_sdk != null)
            _sdk.EnsureHealthy(); // throws when transport died; host re-inits with backoff
        else if (_proc == null || _proc.HasExited)
            throw new InvalidOperationException("presentmon process not running"); // host re-inits with backoff

        UpdateForegroundTarget(sink);
        UpdateIdleMode();
        DrainSdkFrames();

        FrameEntry[] ring;
        long lastFrame;
        lock (_statsLock)
        {
            ring = _pendingRing.ToArray();
            _pendingRing.Clear();
            lastFrame = _lastTargetFrameQpc;
        }
        if (ring.Length > 0) sink.Writer.AppendFrames(ring);

        // stats are consumed and published every poll — rolling aggregates glide, so there is
        // no flicker to gate against; the lows stay cheap via FrameStats' 2 Hz cache
        long now = Stopwatch.GetTimestamp();

        FrameStats.Result r;
        double click = 0, allInput = 0, simMs = 0, dispLat = 0;
        lock (_statsLock)
        {
            r = Stats.Consume(now);
            if (_clickCount > 0) { click = _clickSum / _clickCount; }
            if (_allInputCount > 0) { allInput = _allInputSum / _allInputCount; }
            if (_simCount > 0) { simMs = _simMsSum / _simCount; }
            if (_dispLatCount > 0) { dispLat = _dispLatSum / _dispLatCount; }
            // decay accumulators slowly (rolling-ish, non-zero average)
            if (_clickCount > 200) { _clickSum /= 2; _clickCount /= 2; }
            if (_allInputCount > 200) { _allInputSum /= 2; _allInputCount /= 2; }
            if (_simCount > 200) { _simMsSum /= 2; _simCount /= 2; }
            if (_dispLatCount > 200) { _dispLatSum /= 2; _dispLatCount /= 2; }
        }

        bool tapActive = _tap?.Active == true;
        bool active = _targetPid != 0 && lastFrame != 0 &&
                      (now - lastFrame) < 2 * Stopwatch.Frequency;

        if (active && r.SampleCount > 0)
        {
            if (!tapActive)
            {
                // presented set is tap-owned while the tap sees frames; resolved fallback otherwise
                sink.Set(MetricNames.FpsPresented, r.FpsPresented);
                sink.Set(MetricNames.FpsLow1Presented, r.Low1Presented);
                sink.Set(MetricNames.FpsLow01Presented, r.Low01Presented);
                sink.Set(MetricNames.FpsFrametimePresentedMs, r.AvgFrametimeShortMs);
                sink.Set(MetricNames.FpsFrametimePresentedWorstMs, r.WorstFrametimeMs);
            }
            sink.Set(MetricNames.FpsDisplayed, r.FpsDisplayed);
            sink.Set(MetricNames.FpsFrametimeDisplayedMs, r.AvgDisplayedFtMs);
            sink.Set(MetricNames.FpsFrametimeDisplayedWorstMs, r.WorstDisplayedFtMs);
            sink.Set(MetricNames.FpsLow1Displayed, r.Low1Displayed);
            sink.Set(MetricNames.FpsLow01Displayed, r.Low01Displayed);
            // FG multiplier: prefer displayed-rate ÷ app-simulation-rate (works even when
            // generated frames aren't type-tagged); fall back to FrameType-based ratio
            double fgMult = simMs > 0.5 && r.FpsDisplayed > 0
                ? Math.Clamp(r.FpsDisplayed * simMs / 1000.0, 0.25, 8)
                : r.FgRatio;
            sink.Set(MetricNames.FpsFgRatio, fgMult);
            if (click > 0) sink.Set(MetricNames.LatencyClickMs, click);
            if (allInput > 0) sink.Set(MetricNames.LatencyAllInputMs, allInput);
            if (dispLat > 0) sink.Set(MetricNames.FpsDisplayLatencyMs, dispLat);
        }
        else
        {
            // "no 3D app" idle state (plan §7): stale the fps metrics + our own latency
            // contributions (NOT latency.pcl.ms — PclStatsProvider owns that independently)
            sink.MarkAllStale("fps.");
            sink.MarkStale(MetricNames.LatencyClickMs);
            sink.MarkStale(MetricNames.LatencyAllInputMs);
        }
        sink.Set(MetricNames.FpsTapActive, tapActive ? 1 : 0);
    }

    /// <summary>Idle-aware fps pipeline: ETW flush cadence and the tap's providers only buy
    /// latency while a game is presenting, but their cost runs regardless — measured at ~2/3
    /// of Halo's idle CPU. The criterion is FRAMES, not focus: any foreground app becomes a
    /// "target" (editors, browsers), but only a presenting 3D app produces frames for its own
    /// pid. 10 s without frames → relax the service flush, mute the tap; frames resuming (or
    /// a target switch, handled in UpdateForegroundTarget) re-arms instantly.</summary>
    private void UpdateIdleMode()
    {
        long now = Stopwatch.GetTimestamp();
        long lastFrame;
        lock (_statsLock) lastFrame = _lastTargetFrameQpc;
        if (lastFrame != 0) _idleSinceQpc = 0;
        else if (_idleSinceQpc == 0) _idleSinceQpc = now; // no frames yet: anchor the countdown

        long anchor = lastFrame != 0 ? lastFrame : _idleSinceQpc;
        bool shouldIdle = now - anchor > 10 * Stopwatch.Frequency;
        if (shouldIdle == _fpsIdleMode) return;

        _fpsIdleMode = shouldIdle;
        if (shouldIdle)
        {
            _sdk?.SetFlushPeriod(100);
            _tap?.SetIdle(true);
            Log.Info("fps pipeline idle: no frames for 10 s — service flush 100 ms, tap muted");
        }
        else
        {
            _sdk?.SetFlushPeriod(Settings.PresentMonEtwFlushMs);
            _tap?.SetIdle(false);
            Log.Info("fps pipeline active: full flush cadence restored");
        }
    }

    /// <summary>sdk transport: pull queued frames into stats/ring (console pump pushes instead).</summary>
    private void DrainSdkFrames()
    {
        int pid = _targetPid;
        if (_sdk == null || pid == 0 || !_sdk.Tracking) return;
        _sdkScratch.Clear();
        _sdk.Drain(pid, _sdkScratch);
        if (_sdkScratch.Count == 0) return;

        lock (_statsLock)
        {
            foreach (var s in _sdkScratch)
            {
                Stats.Add(s.Entry);
                _pendingRing.Add(s.Entry);
                if (s.Entry.Qpc > _lastTargetFrameQpc) _lastTargetFrameQpc = s.Entry.Qpc;
                if (!double.IsNaN(s.ClickMs) && s.ClickMs > 0) { _clickSum += s.ClickMs; _clickCount++; }
                if (!double.IsNaN(s.AllInputMs) && s.AllInputMs > 0) { _allInputSum += s.AllInputMs; _allInputCount++; }
                if (!double.IsNaN(s.SimMs) && s.SimMs > 0) { _simMsSum += s.SimMs; _simCount++; }
                if (!double.IsNaN(s.DispLatMs) && s.DispLatMs is > 0 and < 200) { _dispLatSum += s.DispLatMs; _dispLatCount++; }
            }
        }
    }

    private void UpdateForegroundTarget(MetricSink sink)
    {
        nint hwnd = GetForegroundWindow();
        int pid = 0;
        if (hwnd != 0) _ = GetWindowThreadProcessId(hwnd, out pid);

        string name = "";
        if (pid > 0)
        {
            try { name = Process.GetProcessById(pid).ProcessName; } catch { pid = 0; }
        }
        if (IsShellProcess(name)) pid = 0;

        if (pid != _targetPid)
        {
            lock (_statsLock)
            {
                Stats.Clear();
                _pendingRing.Clear();
                _clickSum = _allInputSum = _simMsSum = _dispLatSum = 0;
                _clickCount = _allInputCount = _simCount = _dispLatCount = 0;
                _lastTargetFrameQpc = 0;
            }
            _sdk?.OnTargetChanged(_targetPid, pid);
            _tap?.SetTarget(pid);
            // idle enter/exit is frame-based (UpdateIdleMode), NOT focus-based: any desktop app
            // becomes a target when focused, but only presenting apps produce frames. On a real
            // game start, resolved-lane frames (unaffected by tap mute) exit idle within ~150 ms
            // and the presented panel rides its resolved fallback until the tap re-arms.
            _targetPid = pid;
            _targetName = name;
            _nextNgxScan = DateTime.MinValue; // rescan DLSS on app switch
            _nextSlowPublishQpc = 0;          // republish name/refresh immediately
            Log.Info($"presentmon target: {(pid == 0 ? "none" : $"{name} ({pid})")}");
        }

        long qnow = Stopwatch.GetTimestamp();
        if (qnow >= _nextSlowPublishQpc)
        {
            _nextSlowPublishQpc = qnow + Stopwatch.Frequency;
            sink.SetString(MetricNames.FpsAppName, _targetName);

            // monitor refresh of the window's monitor (req: read actual refresh, not hardcoded 144)
            double hz = GetRefreshHz(hwnd);
            if (hz > 0) sink.Set(MetricNames.FpsRefreshHz, hz);
        }

        if (_targetPid != 0 && DateTime.UtcNow >= _nextNgxScan)
        {
            _nextNgxScan = DateTime.UtcNow.AddSeconds(10);
            ScanNgxModules(sink, _targetPid);
        }
        else if (_targetPid == 0)
        {
            sink.Set(MetricNames.DlssSrPresent, 0);
            sink.Set(MetricNames.DlssFgPresent, 0);
            sink.Set(MetricNames.DlssRrPresent, 0);
        }
    }

    private static bool IsShellProcess(string name) => name.Length == 0 || name is "explorer" or "dwm" or "SearchHost"
        or "StartMenuExperienceHost" or "ShellExperienceHost" or "ApplicationFrameHost" or "TextInputHost"
        or "Halo.Widgets" or "Halo.Settings" or "LockApp" or "Rainmeter";

    private void ScanNgxModules(MetricSink sink, int pid)
    {
        if (pid == _ngxScannedPid && _nextNgxScan != DateTime.MinValue) { }
        _ngxScannedPid = pid;
        bool sr = false, fg = false, rr = false;
        string version = "";
        int major = 0;
        bool driverOverride = false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            foreach (ProcessModule m in proc.Modules)
            {
                string f = m.ModuleName.ToLowerInvariant();
                if (f.StartsWith("nvngx_dlssg")) { fg = true; }
                else if (f.StartsWith("nvngx_dlssd")) { rr = true; }
                else if (f.StartsWith("nvngx_dlss"))
                {
                    sr = true;
                    version = m.FileVersionInfo.FileVersion ?? "";
                    major = m.FileVersionInfo.FileMajorPart;
                    // NVIDIA App / driver DLSS overrides load the DLL from the DriverStore
                    // instead of the game folder — a reliable App-free override signal.
                    string path = m.FileName ?? "";
                    driverOverride = path.Contains("\\DriverStore\\", StringComparison.OrdinalIgnoreCase)
                                  || path.Contains("\\FileRepository\\", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"ngx scan pid {pid}: {ex.Message}"); // 32-bit/protected process etc.
        }
        sink.Set(MetricNames.DlssSrPresent, sr ? 1 : 0);
        sink.Set(MetricNames.DlssFgPresent, fg ? 1 : 0);
        sink.Set(MetricNames.DlssRrPresent, rr ? 1 : 0);
        if (version.Length > 0) sink.SetString(MetricNames.DlssVersion, version);

        // model family: DLL 310+ = DLSS4 transformer generation, older = CNN. The exact
        // runtime preset without an override needs NGX hooking (out of scope, plan D5/§7).
        string model = !sr ? "" : (major >= 310 ? "Transformer" : "CNN") + (driverOverride ? " · override" : " · game DLL");
        sink.SetString(MetricNames.DlssModel, model);
    }

    private static double GetRefreshHz(nint hwnd)
    {
        try
        {
            nint mon = hwnd != 0 ? MonitorFromWindow(hwnd, 2 /*NEAREST*/) : MonitorFromPoint(default, 2);
            var mi = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
            if (mon != 0 && GetMonitorInfoW(mon, ref mi))
            {
                var dm = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
                if (EnumDisplaySettingsW(mi.szDevice, -1 /*CURRENT*/, ref dm))
                    return dm.dmDisplayFrequency;
            }
        }
        catch { }
        return 0;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    public void Dispose()
    {
        _stopping = true;
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        _proc = null;
        _sdk?.Dispose();
        _sdk = null;
        _tap?.Dispose();
        _tap = null;
    }

    // ---- CSV header mapping (tolerates console-v2 "TimeInMs/MsBetweenâ€¦", SDK "CPUStartTime/
    // FrameTime" and v1 naming; missing columns resolve to -1). Observed 2.5.1 console header:
    // Application,ProcessID,â€¦,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,
    // MsBetweenDisplayChange,â€¦,MsUntilDisplayed,CPUStartTimeInMs,â€¦ ----
    private record struct Cols(int Pid, int Time, double TimeScale, int FrameTime, int DisplayedTime, int DisplayLatency, int Click, int AllInput, int FrameType, int Instrumented, int SimStart);

    private static Cols ParseHeader(string header)
    {
        var names = header.Split(',');
        int Find(params string[] candidates)
        {
            foreach (string c in candidates)
                for (int i = 0; i < names.Length; i++)
                    if (string.Equals(names[i].Trim(), c, StringComparison.OrdinalIgnoreCase))
                        return i;
            return -1;
        }
        int time = Find("TimeInMs", "CPUStartTimeInMs", "CPUStartTime", "TimeInSeconds");
        double timeScale = time >= 0 && names[time].Trim().EndsWith("InMs", StringComparison.OrdinalIgnoreCase) ? 0.001 : 1.0;
        return new Cols(
            Pid: Find("ProcessID"),
            Time: time,
            TimeScale: timeScale,
            FrameTime: Find("MsBetweenPresents", "FrameTime"),
            DisplayedTime: Find("MsBetweenDisplayChange", "DisplayedTime"),
            DisplayLatency: Find("MsUntilDisplayed", "DisplayLatency"),
            Click: Find("MsClickToPhotonLatency", "ClickToPhotonLatency"),
            AllInput: Find("MsAllInputToPhotonLatency", "AllInputToPhotonLatency"),
            FrameType: Find("FrameType"),
            Instrumented: Find("MsInstrumentedLatency", "InstrumentedLatency"),
            SimStart: Find("MsBetweenSimulationStart"));
    }

    private static double ParseD(string[] f, int idx)
    {
        if (idx < 0 || idx >= f.Length) return double.NaN;
        return double.TryParse(f[idx], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
    }

    private static uint ParseU(string[] f, int idx)
    {
        if (idx < 0 || idx >= f.Length) return 0;
        return uint.TryParse(f[idx], out uint v) ? v : 0;
    }

    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32")] private static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW lpmi);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODEW devMode);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
}

