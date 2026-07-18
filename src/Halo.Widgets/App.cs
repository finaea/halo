using System.Diagnostics;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Widgets.Render;
using static Halo.Widgets.Native;

namespace Halo.Widgets;

/// <summary>
/// Owns all widget windows, the master tick loop, tray icon, snapping, desktop parenting,
/// config hot-reload and the collector watchdog (plan §11).
/// </summary>
public sealed unsafe class App : IDisposable
{
    public string ProjectRoot { get; }
    public ConfigStore ConfigStore { get; }
    public Theme Theme { get; private set; }
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
    private DateTime _suppressSaveReload = DateTime.MinValue;

    public App(string projectRoot)
    {
        ProjectRoot = projectRoot;
        ConfigStore = new ConfigStore(Path.Combine(projectRoot, "config"));
        Theme = Theme.Load(ConfigStore.ConfigDir);
        _dx = new Dx(Path.Combine(projectRoot, "assets", "fonts"));
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
                    Theme = Theme,
                    Settings = ConfigStore.Settings,
                    Options = inst.Options,
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

            long now = Stopwatch.GetTimestamp();
            bool anyDue = false;
            foreach (var w in _windows)
                if (now >= w.NextDueQpc) { anyDue = true; break; }

            if (anyDue)
            {
                Metrics.Tick();
                Watchdog();
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

            // sleep until next due tick or next message
            long soonest = long.MaxValue;
            foreach (var w in _windows) soonest = Math.Min(soonest, w.NextDueQpc);
            now = Stopwatch.GetTimestamp();
            uint waitMs = soonest == long.MaxValue ? 100u
                : (uint)Math.Clamp((soonest - now) * 1000 / qpf, 0, 250);
            if (waitMs > 0)
                _ = MsgWaitForMultipleObjectsEx(0, null, waitMs, 0x04FF /*QS_ALLINPUT*/, 0x0004 /*MWMO_INPUTAVAILABLE*/);
        }
    }

    private void ApplyConfigChange()
    {
        _configDirty = false;
        if (DateTime.UtcNow < _suppressSaveReload) return;
        Log.Info("config hot-reload");
        Theme = Theme.Load(ConfigStore.ConfigDir);
        BuildWindows();
    }

    private void RecoverDevice()
    {
        _deviceLost = false;
        Log.Warn("recreating D3D/D2D/DComp devices");
        try
        {
            _dx.Recreate(Path.Combine(ProjectRoot, "assets", "fonts"));
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
            nint progman = FindWindowW("Progman", null);
            if (progman == 0) return 0;
            // ask Progman to spawn the wallpaper WorkerW (harmless if already done)
            SendMessageTimeoutW(progman, 0x052C, 0xD, 0, 0, 1000, out _);
            SendMessageTimeoutW(progman, 0x052C, 0xD, 1, 0, 1000, out _);

            if (FindWindowExW(progman, 0, "SHELLDLL_DefView", null) != 0)
                return progman;

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
            string exe = Path.Combine(ProjectRoot, "bin", "Halo.Settings", "Halo.Settings.exe");
            if (!File.Exists(exe))
                exe = Path.Combine(ProjectRoot, "src", "Halo.Settings", "bin", "Debug", "net9.0-windows", "win-x64", "Halo.Settings.exe");
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            else Log.Warn("settings exe not found");
        }
        catch (Exception ex) { Log.Error("open settings", ex); }
    }

    public void ToggleLockAll()
    {
        ConfigStore.Settings.LockAll = !ConfigStore.Settings.LockAll;
        SaveGeneralSettings();
    }

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
        Metrics.Dispose();
        _dx.Dispose();
        ConfigStore.Dispose();
    }
}
