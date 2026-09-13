using System.Diagnostics;
using Halo.Metrics;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Shared.Panels;
using Halo.Widgets.Render;
using static Halo.Widgets.Native;

namespace Halo.Widgets;

/// <summary>
/// Owns all widget windows, the master tick loop, tray icon, snapping, desktop parenting,
/// config hot-reload and the collector watchdog (plan §11).
/// </summary>
public sealed unsafe class App : IDisposable
{
    public ConfigStore ConfigStore { get; }
    public MetricCache Metrics { get; } = new();
    public nint DesktopHost { get; private set; }

    private readonly Dx _dx;
    private readonly List<WidgetWindow> _windows = new();
    private readonly List<(string Device, RECT Work)> _monitors = new();
    private TrayIcon? _tray;
    private bool _quit;
    private volatile bool _configDirty;
    private volatile bool _deviceLost;
    private DateTime _nextWatchdogAttempt = DateTime.MinValue;
    private int _watchdogFailures;
    private nint _framesReadyEvent;          // collector's frames-ready signal (0 until opened)
    private long _nextFrameEventOpenQpc;
    private long _nextFrameWakeQpc;          // coalesce event-driven repaints to ~refresh rate
    private DateTime _suppressSaveReload = DateTime.MinValue;

    public App()
    {
        ConfigStore = new ConfigStore(Paths.ConfigDir);
        _dx = new Dx(Paths.FontsDir);
        RefreshMonitors();
        DesktopHost = FindDesktopHost();
        Log.Info($"desktop host: 0x{DesktopHost:X}");

        ConfigStore.Changed += () => _configDirty = true;
    }

    public void Run()
    {
        _tray = new TrayIcon(this);
        BuildWindows();
        _ = timeBeginPeriod(1);
        try { Loop(); }
        finally { _ = timeEndPeriod(1); }
    }

    private void BuildWindows()
    {
        foreach (var w in _windows) w.Dispose();
        _windows.Clear();

        foreach (var inst in ConfigStore.Widgets.Widgets.Where(w => w.Enabled))
        {
            try
            {
                var ctx = new PanelContext
                {
                    Metrics = Metrics,
                    // one resolved theme per widget: global appearance + this widget's overrides
                    Theme = Theme.Resolve(ConfigStore.Settings.Appearance, inst.Appearance, ReferenceScale),
                    Settings = ConfigStore.Settings,
                    Widget = inst,
                    Type = PanelCatalog.Find(inst.Type),
                };
                var panel = PanelDefs.PanelFactory.Create(inst.Type, ctx);
                if (panel == null)
                {
                    Log.Warn($"unknown widget type '{inst.Type}' ({inst.Id})");
                    continue;
                }
                _windows.Add(new WidgetWindow(this, _dx, inst, panel, ctx));
            }
            catch (Exception ex)
            {
                Log.Error($"widget {inst.Id} create failed", ex);
            }
        }
        Log.Info($"{_windows.Count} widgets created");
    }

    private void Loop()
    {
        long qpf = Stopwatch.Frequency;
        foreach (var w in _windows) w.NextDueQpc = Stopwatch.GetTimestamp();

        while (!_quit)
        {
            // pump all pending messages
            while (PeekMessageW(out MSG msg, 0, 0, 0, 1 /*PM_REMOVE*/))
            {
                if (msg.message == 0x0012 /*WM_QUIT*/) { _quit = true; break; }
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            if (_quit) break;

            if (_deviceLost) RecoverDevice();
            if (_configDirty) ApplyConfigChange();
            // outside the anyDue gate on purpose: if a rebuild ever yields zero windows, nothing
            // is ever due again and a recovery parked inside that gate could never run
            HostGuard();

            long now = Stopwatch.GetTimestamp();
            bool anyDue = false;
            foreach (var w in _windows)
                if (now >= w.NextDueQpc) { anyDue = true; break; }

            if (anyDue)
            {
                Metrics.Tick();
                Watchdog();
                PositionGuard();
                now = Stopwatch.GetTimestamp();
                foreach (var w in _windows)
                {
                    if (now < w.NextDueQpc) continue;
                    long period = (long)(qpf / w.RateHz);
                    w.NextDueQpc += period;
                    if (w.NextDueQpc < now) w.NextDueQpc = now + period;
                    try { w.Tick(); }
                    catch (Exception ex) { Log.Error($"tick {w.Config.Id}", ex); }
                }
            }

            // sleep until next due tick, next message, or the collector's frames-ready event
            long soonest = long.MaxValue;
            foreach (var w in _windows) soonest = Math.Min(soonest, w.NextDueQpc);
            now = Stopwatch.GetTimestamp();
            uint waitMs = soonest == long.MaxValue ? 100u
                : (uint)Math.Clamp((soonest - now) * 1000 / qpf, 0, 250);
            if (waitMs > 0)
            {
                if (_framesReadyEvent == 0 && now >= _nextFrameEventOpenQpc) TryOpenFramesEvent(now);
                if (_framesReadyEvent != 0)
                {
                    nint h = _framesReadyEvent;
                    uint wr = MsgWaitForMultipleObjectsEx(1, &h, waitMs, 0x04FF /*QS_ALLINPUT*/, 0x0004 /*MWMO_INPUTAVAILABLE*/);
                    if (wr == 0 /*WAIT_OBJECT_0: frames appended*/) OnFramesReady();
                }
                else
                {
                    _ = MsgWaitForMultipleObjectsEx(0, null, waitMs, 0x04FF /*QS_ALLINPUT*/, 0x0004 /*MWMO_INPUTAVAILABLE*/);
                }
            }
        }
    }

    /// <summary>The collector appended frames to the shared ring: pull frame-graph widgets'
    /// next tick forward so the graph paints now instead of at its (fallback) poll tick.
    /// Coalesced to ~7 ms so a busy tap lane can't repaint faster than the monitor refreshes.
    /// The boundary must be anchored to NOW and only advanced when a wake is granted:
    /// advancing it per event lets a >143 Hz event stream (tap presents + drain batches)
    /// push it further into the future than time advances, until event wakes stop beating
    /// the widgets' fallback timers and the graphs silently degrade to 5 Hz.</summary>
    private void OnFramesReady()
    {
        long now = Stopwatch.GetTimestamp();
        long due;
        if (now < _nextFrameWakeQpc)
        {
            due = _nextFrameWakeQpc; // inside the coalesce window: ride the planned wake
        }
        else
        {
            due = now;
            // 16 ms ≈ the 60 Hz widget monitor's refresh: repainting faster than the panel's
            // own display can show is pure CPU waste (was 7 ms, sized for a 144 Hz display)
            _nextFrameWakeQpc = now + Stopwatch.Frequency * 16 / 1000;
        }
        foreach (var w in _windows)
            if (w.Panel.HasFrameGraph && w.NextDueQpc > due) w.NextDueQpc = due;
    }

    /// <summary>Fire-and-forget contract: the event may not exist yet (collector starting later,
    /// or an older collector) — retry every 5 s; without it frame graphs just stay poll-driven.</summary>
    private void TryOpenFramesEvent(long now)
    {
        _nextFrameEventOpenQpc = now + 5 * Stopwatch.Frequency;
        nint h = OpenEventW(SYNCHRONIZE, false, SharedMemoryLayout.FramesReadyEventName);
        if (h != 0)
        {
            _framesReadyEvent = h;
            Log.Info("frames-ready event connected — frame graphs are event-driven");
        }
    }

    private void ApplyConfigChange()
    {
        // Keep dirty set while suppressed and retry next pass: the store has ALREADY
        // reloaded (new WidgetInstance objects) — skipping the rebuild for good would
        // leave windows bound to orphaned configs, and their next save writes stale data.
        if (DateTime.UtcNow < _suppressSaveReload) return;
        _configDirty = false;
        Log.Info("config hot-reload");
        BuildWindows();
    }

    private void RecoverDevice()
    {
        _deviceLost = false;
        Log.Warn("recreating D3D/D2D/DComp devices");
        try
        {
            _dx.Recreate(Paths.FontsDir);
            foreach (var w in _windows) w.RecreateGraphics();
        }
        catch (Exception ex)
        {
            Log.Error("device recovery failed, retrying in 2s", ex);
            Thread.Sleep(2000);
            _deviceLost = true;
        }
    }

    public void RequestDeviceRecovery() => _deviceLost = true;

    private DateTime _nextHostCheck = DateTime.MinValue;

    /// <summary>
    /// Explorer-restart resilience (observed 2026-08-31 09:19:38): Explorer died and took the
    /// WorkerW/Progman desktop host with it. Win32 destroys a window's children along with it,
    /// so every desktop-parented widget was destroyed too — while the process stayed alive and
    /// the position guard re-pinned dead HWNDs every 5 s forever (GetWindowRect fails on a dead
    /// handle and leaves the rect zeroed, so it read (0,0), "moved" it, and read (0,0) again).
    /// The cached host is therefore not trustworthy for the process lifetime: re-validate it and
    /// rebuild the windows onto whatever host exists now.
    /// </summary>
    private void HostGuard()
    {
        if (DateTime.UtcNow < _nextHostCheck) return;
        _nextHostCheck = DateTime.UtcNow.AddSeconds(2);

        bool hostDead = DesktopHost != 0 && !IsWindow(DesktopHost);
        // WM_DESTROY leaves WidgetWindow.Hwnd set, so a dead handle still reads back as non-zero
        bool windowsDead = _windows.Count > 0 && _windows.All(w => !IsWindow(w.Hwnd));

        if (hostDead || windowsDead)
        {
            nint host = FindDesktopHost();
            Log.Warn($"desktop host lost (0x{DesktopHost:X}) — rebuilding widgets on 0x{host:X}");
            DesktopHost = host;
            // Rebuild even when the shell isn't back yet (host == 0): ApplyZMode then falls back
            // to an unparented bottom-of-z-order window, which is visible. Waiting for a host
            // instead would leave the user staring at an empty desktop until Explorer settles.
            BuildWindows();
            long now = Stopwatch.GetTimestamp();
            foreach (var w in _windows) w.NextDueQpc = now;
            return;
        }

        // Rebuilt while the shell was still coming up: adopt the real host once it appears so the
        // widgets end up desktop-parented again instead of sitting unparented until next restart.
        if (DesktopHost == 0 && _windows.Any(w => w.Config.ZMode == ZMode.Desktop))
        {
            nint host = FindDesktopHost();
            if (host == 0) return;
            Log.Info($"desktop host adopted: 0x{host:X}");
            DesktopHost = host;
            foreach (var w in _windows) { w.ApplyZMode(); w.Reposition(); w.ForceRedraw(); }
        }
    }

    private DateTime _nextPositionCheck = DateTime.MinValue;

    /// <summary>
    /// Resilience (plan §11): display-mode/DPI changes and WorkerW re-hosting can shift
    /// desktop-parented children (observed 2026-07-19: resolution/DPI change moved half the
    /// widgets above the screen). Desktop children don't reliably receive WM_DISPLAYCHANGE,
    /// so every 5 s we re-derive each widget's expected screen rect from config and re-pin
    /// on drift. Skipped while a drag is in progress (drag saves config on mouse-up anyway).
    /// </summary>
    private void PositionGuard()
    {
        if (DateTime.UtcNow < _nextPositionCheck) return;
        _nextPositionCheck = DateTime.UtcNow.AddSeconds(5);
        RefreshMonitors();
        foreach (var w in _windows)
        {
            if (w.IsDragging) continue;
            // A destroyed handle reads back as (0,0) (GetWindowRect fails without clearing the
            // out rect), which the drift check below would treat as a real position and "fix"
            // on every pass, forever. HostGuard owns that case — don't fight it here.
            if (!IsWindow(w.Hwnd)) continue;
            var (ex, ey) = w.TargetScreenPos();
            var (ax, ay, _, _) = w.ScreenRect();
            if (Math.Abs(ax - ex) > 2 || Math.Abs(ay - ey) > 2)
            {
                Log.Info($"position guard: re-pinning {w.Config.Id} ({ax},{ay}) -> ({ex},{ey})");
                w.Reposition();
                w.ForceRedraw();
            }
        }
    }

    private bool? _collectorTaskExists;

    /// <summary>Collector watchdog: stale > 5 s → try to (re)start via the scheduled task
    /// (backoff 1/5/30 s per plan §11). If the task isn't installed, widgets just show
    /// their stale badges — no point spawning schtasks forever.</summary>
    private void Watchdog()
    {
        if (!Metrics.Stale)
        {
            _watchdogFailures = 0;
            return;
        }
        if (DateTime.UtcNow < _nextWatchdogAttempt) return;
        int delayS = _watchdogFailures switch { 0 => 1, 1 => 5, _ => 30 };
        _nextWatchdogAttempt = DateTime.UtcNow.AddSeconds(delayS);
        _watchdogFailures++;
        try
        {
            if (_collectorTaskExists == null)
            {
                using var q = Process.Start(new ProcessStartInfo("schtasks", "/Query /TN \"\\Halo\\Collector\"")
                { CreateNoWindow = true, UseShellExecute = false });
                q!.WaitForExit(3000);
                _collectorTaskExists = q.ExitCode == 0;
                if (_collectorTaskExists == false)
                    Log.Warn("watchdog: \\Halo\\Collector task not installed — run tools\\install-halo.ps1; widgets will show stale badges");
            }
            if (_collectorTaskExists == true)
            {
                Process.Start(new ProcessStartInfo("schtasks", "/Run /TN \"\\Halo\\Collector\"")
                { CreateNoWindow = true, UseShellExecute = false });
                Log.Info("watchdog: requested collector start via scheduled task");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"watchdog: {ex.Message}");
        }
    }

    // ---- monitors & snapping ----

    public void RefreshMonitors()
    {
        _monitors.Clear();
        EnumDisplayMonitors(0, 0, (nint mon, nint _, ref RECT _, nint _) =>
        {
            var mi = new MONITORINFOEXW { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEXW>() };
            if (GetMonitorInfoW(mon, ref mi))
                _monitors.Add((mi.szDevice, mi.rcWork));
            return true;
        }, 0);
    }

    public (int X, int Y, int W, int H) ResolveMonitorWorkArea(string device)
    {
        var m = _monitors.FirstOrDefault(m => m.Device.Equals(device, StringComparison.OrdinalIgnoreCase));
        if (m.Device == null && _monitors.Count > 0) m = _monitors[0];
        if (m.Device == null) return (0, 0, 1920, 1080);
        return (m.Work.Left, m.Work.Top, m.Work.W, m.Work.H);
    }

    /// <summary>
    /// Monitor whose work area best contains the rect (max overlap; nearest when the rect
    /// lies in a dead zone of the virtual desktop). KeepOnScreen clamps against THIS, not
    /// the configured monitor — a widget placed on a secondary monitor must not be yanked
    /// back to the configured one on rebuild.
    /// </summary>
    public (string Device, int X, int Y, int W, int H) MonitorForRect(int x, int y, int w, int h)
    {
        string? dev = null; RECT work = default;
        long bestArea = 0;
        foreach (var (d, r) in _monitors)
        {
            long ix = Math.Min(x + w, r.Right) - (long)Math.Max(x, r.Left);
            long iy = Math.Min(y + h, r.Bottom) - (long)Math.Max(y, r.Top);
            long area = Math.Max(0, ix) * Math.Max(0, iy);
            if (area > bestArea) { bestArea = area; dev = d; work = r; }
        }
        if (dev == null)
        {
            long best = long.MaxValue;
            int cx = x + w / 2, cy = y + h / 2;
            foreach (var (d, r) in _monitors)
            {
                long dx = cx - Math.Clamp(cx, r.Left, r.Right);
                long dy = cy - Math.Clamp(cy, r.Top, r.Bottom);
                long dist = dx * dx + dy * dy;
                if (dist < best) { best = dist; dev = d; work = r; }
            }
        }
        if (dev == null) return ("", 0, 0, 1920, 1080);
        return (dev, work.Left, work.Top, work.W, work.H);
    }

    /// <summary>Snap to screen edges + other widget edges, 8 px threshold (plan §9.3).</summary>
    public (int X, int Y) Snap(WidgetWindow self, int x, int y, int w, int h)
    {
        const int T = 8;
        var xCandidates = new List<int>();
        var yCandidates = new List<int>();
        foreach (var (_, work) in _monitors)
        {
            xCandidates.Add(work.Left); xCandidates.Add(work.Right - w);
            yCandidates.Add(work.Top); yCandidates.Add(work.Bottom - h);
        }
        foreach (var other in _windows)
        {
            if (other == self) continue;
            var (ox, oy, ow, oh) = other.ScreenRect();
            xCandidates.Add(ox); xCandidates.Add(ox + ow); xCandidates.Add(ox - w); xCandidates.Add(ox + ow - w);
            yCandidates.Add(oy); yCandidates.Add(oy + oh); yCandidates.Add(oy - h); yCandidates.Add(oy + oh - h);
        }
        foreach (int c in xCandidates) if (Math.Abs(x - c) <= T) { x = c; break; }
        foreach (int c in yCandidates) if (Math.Abs(y - c) <= T) { y = c; break; }
        return (x, y);
    }

    // ---- desktop parenting (plan §9.3 "On Desktop") ----

    private static nint FindDesktopHost()
    {
        try
        {
            // Progman can be absent for minutes after an Explorer restart (observed 2026-08-31:
            // Shell_TrayWnd and WorkerW were both up while Progman was still missing), so a
            // missing Progman must not abort the search — fall through to the WorkerW scan.
            nint progman = FindWindowW("Progman", null);
            if (progman != 0)
            {
                // ask Progman to spawn the wallpaper WorkerW (harmless if already done)
                SendMessageTimeoutW(progman, 0x052C, 0xD, 0, SMTO_ABORTIFHUNG, 1000, out _);
                SendMessageTimeoutW(progman, 0x052C, 0xD, 1, SMTO_ABORTIFHUNG, 1000, out _);

                if (FindWindowExW(progman, 0, "SHELLDLL_DefView", null) != 0)
                    return progman;
            }

            nint worker = 0;
            while ((worker = FindWindowExW(0, worker, "WorkerW", null)) != 0)
                if (FindWindowExW(worker, 0, "SHELLDLL_DefView", null) != 0)
                    return worker;
        }
        catch { }
        return 0;
    }

    // ---- actions ----

    public void SaveWidgetConfig()
    {
        _suppressSaveReload = DateTime.UtcNow.AddSeconds(2);
        ConfigStore.SaveWidgets();
    }

    public void SaveGeneralSettings()
    {
        _suppressSaveReload = DateTime.UtcNow.AddSeconds(2);
        ConfigStore.SaveSettings();
    }

    public void ResetPanelMax(string panelType)
    {
        string prefix = panelType switch
        {
            "power" => "",         // power panel shows cpu+gpu maxima — reset all
            "network" => "net.",
            _ => "",
        };
        ControlPipe.Send(prefix.Length == 0 ? "reset-max" : $"reset-max {prefix}");
        if (panelType == "network") ControlPipe.Send("reset-net");
    }

    public void OpenSettings()
    {
        try
        {
            // All three exes live in the same folder in every layout (packaging plan § Layout).
            string exe = Path.Combine(Paths.AppRoot, "Halo.Settings.exe");
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            else Log.Warn($"settings exe not found next to the widgets exe ({exe})");
        }
        catch (Exception ex) { Log.Error("open settings", ex); }
    }

    public void ToggleLockAll()
    {
        ConfigStore.Settings.LockAll = !ConfigStore.Settings.LockAll;
        SaveGeneralSettings();
    }

    /// <summary>The scale an "auto" appearance resolves to. Per-monitor DPI and the
    /// screen-size rule (hardware plan H5/H7) land with the widgets ticket; until then "auto"
    /// means the reference scale the layout was designed at.</summary>
    public const double ReferenceScale = 1.7;

    public void RefreshAll()
    {
        foreach (var w in _windows) w.ForceRedraw();
    }

    public void Quit()
    {
        _quit = true;
        PostQuitMessage(0);
    }

    public void Dispose()
    {
        _tray?.Dispose();
        foreach (var w in _windows) w.Dispose();
        _windows.Clear();
        if (_framesReadyEvent != 0) { CloseHandle(_framesReadyEvent); _framesReadyEvent = 0; }
        Metrics.Dispose();
        _dx.Dispose();
        ConfigStore.Dispose();
    }
}
