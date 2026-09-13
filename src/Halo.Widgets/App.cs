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
    private readonly List<MonitorInfo> _monitors = new();
    private TrayIcon? _tray;
    private bool _quit;
    private volatile bool _configDirty;
    private volatile bool _placementDirty = true;
    private volatile bool _deviceLost;
    private string _monitorSignature = "";
    private string _packSignature = "";
    private long _nextPackRetryQpc;          // throttles the packer's forced re-measure
    private long _packWaitSinceQpc;          // when the current wait-for-sizes started (0 = not waiting)
    private bool _packWaitLogged;
    private int _lastMetricCount = -1;

    /// <summary>Someone asked for the whole layout to be arranged on the primary monitor — the
    /// Settings app's "Generate default layout", or our own first run. Only the widget process
    /// can honour it: the packer needs laid-out pixel sizes (H6).</summary>
    private bool ArrangeRequested => ConfigStore.Widgets.ArrangeRequested;
    private DateTime _nextWatchdogAttempt = DateTime.MinValue;
    private int _watchdogFailures;
    private nint _framesReadyEvent;          // collector's frames-ready signal (0 until opened)
    private long _nextFrameEventOpenQpc;
    private long _nextFrameWakeQpc;          // coalesce event-driven repaints to ~refresh rate

    public App()
    {
        bool freshInstall = !File.Exists(Path.Combine(Paths.ConfigDir, "widgets.json"));
        ConfigStore = new ConfigStore(Paths.ConfigDir);
        _dx = new Dx(Paths.FontsDir);
        RefreshMonitors();
        DesktopHost = FindDesktopHost();
        Log.Info($"desktop host: 0x{DesktopHost:X}");

        if (freshInstall && ConfigStore.Widgets.Widgets.Count == 0) GenerateFirstRunLayout();
        if (ArrangeRequested) Log.Info("config asks for an arrange pass — widgets will be packed onto the primary monitor");

        ConfigStore.Changed += () => _configDirty = true;
    }

    public void Run()
    {
        _tray = new TrayIcon(this);
        // Panels are built from discovered hardware — the core grid's P/E classes, the drive and
        // fan channel lists, the GPU count. Attach before building or every one of those reads
        // its fallback and the first layout is wrong until something else forces a rebuild.
        Metrics.Tick();
        _wasAttached = Metrics.Attached;
        BuildWindows();
        _ = timeBeginPeriod(1);
        try { Loop(); }
        finally { _ = timeEndPeriod(1); }
    }

    private void BuildWindows()
    {
        foreach (var w in _windows) w.Dispose();
        _windows.Clear();

        var configured = ConfigStore.Widgets.Widgets;
        foreach (var inst in configured.Where(w => w.Enabled))
        {
            var win = CreateWindow(inst);
            if (win != null) _windows.Add(win);
        }
        _packSignature = "";
        _placementDirty = true;

        // Name the disabled ones. The Settings app lists them alongside the enabled ones, so
        // "it is in the list but not on the desktop" otherwise looks identical to a widget whose
        // window failed to build — which is exactly how a disabled fps counter got reported as a
        // missing window (release feedback item 11).
        var skipped = configured.Where(w => !w.Enabled).Select(w => w.Id).ToList();
        Log.Info($"{_windows.Count} widgets created"
            + (skipped.Count > 0
                ? $"; {skipped.Count} disabled in widgets.json, no window: {string.Join(", ", skipped)}"
                : ""));
    }

    /// <summary>Build one widget window: resolve its monitor (and whether that monitor is
    /// missing), its refresh bound and its theme, then hand all three to the panel.</summary>
    private WidgetWindow? CreateWindow(WidgetInstance inst)
    {
        try
        {
            var (mon, missing, displaced) = ResolveHost(inst);
            double maxHz = MaxRateFor(inst);
            var ctx = new PanelContext
            {
                Metrics = Metrics,
                // one resolved theme per widget: global appearance + this widget's overrides
                Theme = ResolveTheme(inst, mon, missing),
                Settings = ConfigStore.Settings,
                Widget = inst,
                Type = PanelCatalog.Find(inst.Type),
                MaxRateHz = maxHz,
            };
            var panel = PanelDefs.PanelFactory.Create(inst.Type, ctx);
            if (panel == null)
            {
                Log.Warn($"unknown widget type '{inst.Type}' ({inst.Id})");
                return null;
            }
            if (missing)
                Log.Info($"widget {inst.Id}: monitor '{inst.Monitor}' not present — auto-arranging on {mon.Device} at scale {ctx.Theme.BaseScale:0.##}");
            return new WidgetWindow(this, _dx, inst, panel, ctx, mon, displaced, maxHz);
        }
        catch (Exception ex)
        {
            Log.Error($"widget {inst.Id} create failed", ex);
            return null;
        }
    }

    /// <summary>Fastest useful repaint for this widget, from the live registry (rates plan R2).</summary>
    private double MaxRateFor(WidgetInstance inst)
    {
        var type = PanelCatalog.Find(inst.Type);
        return PanelRates.MaxHz(type, PanelRates.MetricNamesFor(type, inst.Options), Metrics.NominalRateHz);
    }

    /// <summary>
    /// Global appearance + this widget's overrides, with "auto" resolved for the monitor it will
    /// live on. A widget on a <b>missing</b> monitor never renders larger than the fallback
    /// monitor's auto scale, so a 3.4× layout from a 4K screen still fits the 1080p one it lands
    /// on (H7).
    ///
    /// That clamp is deliberately <b>not</b> applied to an arrange. The clamp is temporary — it
    /// lasts only while the real monitor is away, and it is dropped the moment the widget stops
    /// being displaced. An arrange is permanent: it writes the target monitor into the config, so
    /// the very next reload re-resolves the widget as not-displaced and drops the clamp. Packing
    /// at the clamped size and then rendering at the unclamped one is the same packed-size ≠
    /// rendered-size mismatch that made an arrange overlap in the first place — it just needs a
    /// per-widget <c>appearance.scale</c> above the target's auto scale to show up instead of a
    /// DPI difference.
    /// </summary>
    private Theme ResolveTheme(WidgetInstance inst, MonitorInfo mon, bool missing)
    {
        double auto = AutoScale.For(mon);
        var theme = Theme.Resolve(ConfigStore.Settings.Appearance, inst, auto);
        if (missing && !ArrangeRequested) theme.BaseScale = Math.Min(theme.BaseScale, auto);
        theme.Dpi = mon.Dpi;
        return theme;
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
                // The packer needs laid-out pixel sizes, which only exist after a tick.
                if (_placementDirty) RefreshPlacement(); else RunPacker();
                RateBoundGuard();
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

    /// <summary>
    /// Hot-reload, one widget at a time (settings plan §Live-apply). A reload always produces new
    /// <see cref="WidgetInstance"/> objects, so every window is re-pointed at its new one even
    /// when nothing else changed — a window left holding the old object would render and save
    /// stale config. Only a type change, a structural option or a metric show/graph toggle costs
    /// a window rebuild; everything else is applied in place.
    /// </summary>
    private void ApplyConfigChange()
    {
        _configDirty = false;
        var wanted = ConfigStore.Widgets.Widgets.Where(w => w.Enabled).ToList();
        var settings = ConfigStore.Settings;

        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            var win = _windows[i];
            if (wanted.Any(w => w.Id == win.Config.Id)) continue;
            Log.Info($"config: widget {win.Config.Id} removed — closing its window");
            win.Dispose();
            _windows.RemoveAt(i);
        }

        foreach (var inst in wanted)
        {
            var win = _windows.FirstOrDefault(w => w.Config.Id == inst.Id);
            if (win == null)
            {
                var created = CreateWindow(inst);
                if (created == null) continue;
                created.NextDueQpc = Stopwatch.GetTimestamp();
                _windows.Add(created);
                Log.Info($"config: widget {inst.Id} added — new window");
                _placementDirty = true;
                continue;
            }

            string? rebuild = RebuildReason(win.Config, inst);
            if (rebuild != null)
            {
                Log.Info($"config: widget {inst.Id} rebuilt ({rebuild})");
                long due = win.NextDueQpc;
                int index = _windows.IndexOf(win);
                win.Dispose();
                var created = CreateWindow(inst);
                if (created == null) { _windows.RemoveAt(index); continue; }
                created.NextDueQpc = due;
                _windows[index] = created;
                _placementDirty = true;
                continue;
            }

            var (mon, missing, displaced) = ResolveHost(inst);
            double maxHz = MaxRateFor(inst);
            win.ApplyInPlace(inst, settings, mon, displaced, ResolveTheme(inst, mon, missing), maxHz);
        }

        // keep window order in step with the config so the packer and snapping are deterministic
        _windows.Sort((a, b) => wanted.FindIndex(w => w.Id == a.Config.Id).CompareTo(wanted.FindIndex(w => w.Id == b.Config.Id)));
        Log.Info($"config: applied in place to {_windows.Count} widget(s)");
        _placementDirty = true;
    }

    /// <summary>Why this widget cannot be updated in place, or null when it can.</summary>
    private static string? RebuildReason(WidgetInstance prev, WidgetInstance next)
    {
        if (prev.Type != next.Type) return "type changed";

        var type = PanelCatalog.Find(next.Type);
        if (type != null)
            foreach (var opt in type.Options)
            {
                if (!opt.Structural) continue;
                if (type.OptionValue(prev.Options, opt.Key) != type.OptionValue(next.Options, opt.Key))
                    return $"structural option '{opt.Key}'";
            }

        // show / graph decide which rows and series exist, so they change the element tree
        foreach (string key in prev.Metrics.Keys.Union(next.Metrics.Keys))
        {
            var a = prev.Metrics.GetValueOrDefault(key);
            var b = next.Metrics.GetValueOrDefault(key);
            if (a?.Show != b?.Show) return $"metrics.{key}.show";
            if (a?.Graph != b?.Graph) return $"metrics.{key}.graph";
        }

        // these are baked into the element tree at build time (pill widths, cached text formats)
        var pa = prev.Appearance;
        var pb = next.Appearance;
        if (pa.Width != pb.Width) return "appearance.width";
        if (pa.ShowTitle != pb.ShowTitle) return "appearance.showTitle";
        if (pa.FontFamily != pb.FontFamily) return "appearance.fontFamily";
        return null;
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
        // desktop-parented children don't reliably get WM_DISPLAYCHANGE / WM_DPICHANGED, so this
        // is where a resolution, DPI or monitor-set change actually gets noticed
        if (_placementDirty) RefreshPlacement();
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

    private bool _wasAttached;

    /// <summary>
    /// Two things change when the registry does. A provider that was not running when a widget
    /// was built can register faster metrics later, so the refresh bound is re-evaluated (R2).
    /// And the collector coming up for the first time is when hardware discovery finally has
    /// answers — the core grid, volume list, fan channels and GPU count all read it at build
    /// time — so that one transition rebuilds the windows.
    /// </summary>
    private void RateBoundGuard()
    {
        bool attached = Metrics.Attached;
        if (attached && !_wasAttached)
        {
            _wasAttached = true;
            _lastMetricCount = Metrics.MetricCount;
            Log.Info("collector attached — rebuilding widgets from discovered hardware");
            BuildWindows();
            long now = Stopwatch.GetTimestamp();
            foreach (var w in _windows) w.NextDueQpc = now;
            return;
        }
        _wasAttached = attached;

        int count = Metrics.MetricCount;
        if (count == _lastMetricCount) return;
        _lastMetricCount = count;
        foreach (var w in _windows) w.ApplyRateBound(MaxRateFor(w.Config));
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
                _monitors.Add(new MonitorInfo(mi.szDevice, mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.W, mi.rcWork.H,
                    MonitorDpi(mon), (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, 0);

        // Work area, DPI and the set of devices all feed scale and placement, so any change to
        // them has to re-resolve every widget (hardware plan H5/H6).
        string sig = string.Join(" · ", _monitors.Select(m =>
            $"{m.Device} {m.W}x{m.H}+{m.X}+{m.Y} @{m.Dpi:0}dpi auto={AutoScale.For(m):0.00}{(m.Primary ? " primary" : "")}"));
        if (sig != _monitorSignature)
        {
            Log.Info($"monitors: {sig}");
            _monitorSignature = sig;
            _placementDirty = true;
        }
    }

    public MonitorInfo PrimaryMonitor
        => _monitors.FirstOrDefault(m => m.Primary, _monitors.Count > 0 ? _monitors[0] : MonitorInfo.None);

    /// <summary>
    /// The monitor a widget should be drawn on, plus whether its configured one is missing.
    /// v1 silently substituted monitor 0 here, which is what stacked a whole layout into one
    /// corner when a display was unplugged; the caller now knows and can auto-arrange (H6).
    /// </summary>
    public (MonitorInfo Monitor, bool Missing) ResolveMonitor(string device)
    {
        foreach (var m in _monitors)
            if (m.Device.Equals(device, StringComparison.OrdinalIgnoreCase))
                return (m, false);
        // "" has always meant "the primary monitor", not a monitor that went away
        return (PrimaryMonitor, device.Length > 0);
    }

    /// <summary>
    /// The monitor a widget is themed and drawn on, and whether that is a fallback rather than its
    /// own. This must agree with the monitor <see cref="RunPacker"/> packs onto, because base
    /// scale and DPI together decide the window's pixel size and the packer lays out boxes of
    /// exactly that size. A missing monitor already resolves to the primary, so the two only
    /// disagreed on an explicit arrange — which themed each widget for its configured monitor
    /// while packing every one of them onto the primary. Measured 2026-09-13: ten widgets themed
    /// for a 96 dpi display, packed onto the 120 dpi primary, gave 351 px-wide boxes for windows
    /// that are really 438 px wide (1.25×), so the columns and the rows inside them overlapped.
    /// </summary>
    private (MonitorInfo Monitor, bool Missing, bool Displaced) ResolveHost(WidgetInstance inst)
    {
        var (mon, missing) = ResolveMonitor(inst.Monitor);
        return ArrangeRequested ? (PrimaryMonitor, missing, true) : (mon, missing, missing);
    }

    /// <summary>
    /// Monitor whose work area best contains the rect (max overlap; nearest when the rect
    /// lies in a dead zone of the virtual desktop). KeepOnScreen clamps against THIS, not
    /// the configured monitor — a widget placed on a secondary monitor must not be yanked
    /// back to the configured one on rebuild.
    /// </summary>
    public (string Device, int X, int Y, int W, int H) MonitorForRect(int x, int y, int w, int h)
    {
        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (var m in _monitors)
        {
            long ix = Math.Min(x + w, m.X + m.W) - (long)Math.Max(x, m.X);
            long iy = Math.Min(y + h, m.Y + m.H) - (long)Math.Max(y, m.Y);
            long area = Math.Max(0, ix) * Math.Max(0, iy);
            if (area > bestArea) { bestArea = area; best = m; }
        }
        if (best == null)
        {
            long nearest = long.MaxValue;
            int cx = x + w / 2, cy = y + h / 2;
            foreach (var m in _monitors)
            {
                long dx = cx - Math.Clamp(cx, m.X, m.X + m.W);
                long dy = cy - Math.Clamp(cy, m.Y, m.Y + m.H);
                long dist = dx * dx + dy * dy;
                if (dist < nearest) { nearest = dist; best = m; }
            }
        }
        if (best == null) return ("", 0, 0, 1920, 1080);
        var r = best.Value;
        return (r.Device, r.X, r.Y, r.W, r.H);
    }

    /// <summary>Snap to screen edges + other widget edges, 8 px threshold (plan §9.3).</summary>
    public (int X, int Y) Snap(WidgetWindow self, int x, int y, int w, int h)
    {
        const int T = 8;
        var xCandidates = new List<int>();
        var yCandidates = new List<int>();
        foreach (var m in _monitors)
        {
            xCandidates.Add(m.X); xCandidates.Add(m.X + m.W - w);
            yCandidates.Add(m.Y); yCandidates.Add(m.Y + m.H - h);
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

    /// <summary>
    /// Write one widget's changed fields back to widgets.json without clobbering the Settings
    /// app's concurrent edits: <see cref="ConfigStore.UpdateWidget"/> re-reads the file and only
    /// applies <paramref name="mutate"/>.
    /// </summary>
    public void PatchWidget(string id, Action<WidgetInstance> mutate)
    {
        try
        {
            if (!ConfigStore.UpdateWidget(id, mutate))
                Log.Warn($"widget {id} is no longer in widgets.json — change not saved");
        }
        catch (Exception ex) { Log.Error($"saving widget {id}", ex); }
    }

    public void RequestPlacementRefresh() => _placementDirty = true;

    /// <summary>Re-resolve every widget's monitor, DPI and scale, then re-run the packer for the
    /// ones whose monitor is missing. Cheap and idempotent — called on display change, after a
    /// drag, and every 5 s from the position guard.</summary>
    public void RefreshPlacement()
    {
        _placementDirty = false;
        foreach (var win in _windows)
        {
            var (mon, missing, displaced) = ResolveHost(win.Config);
            win.ApplyMonitor(mon, displaced, ResolveTheme(win.Config, mon, missing));
        }
        RunPacker();
    }

    /// <summary>
    /// Column-pack the widgets that have nowhere of their own to be (hardware plan H6): a missing
    /// monitor, or every widget on first run. Positions are memory-only unless this is the
    /// first run, so unplugging a monitor never rewrites the user's layout.
    /// </summary>
    private void RunPacker()
    {
        var packed = _windows.Where(w => w.Displaced).ToList();
        if (packed.Count == 0) { _packSignature = ""; return; }

        // A pack is all-or-nothing, and it lays out boxes of exactly PixelSize — so it may only
        // run once EVERY packed window has laid out at the scale it will actually be drawn at.
        // Sizes exist only after a tick, and re-theming a widget (an arrange moves it to the
        // primary monitor's DPI) invalidates the one it has until its next tick. Packing against
        // a stale size is precisely how widgets end up on top of each other, so bail — before the
        // signature is stored, which is what guarantees the retry — and pull the windows' ticks
        // forward so the retry lands on the next pass instead of at their own refresh rate.
        if (packed.Any(w => w.SizeStale))
        {
            long stamp = Stopwatch.GetTimestamp();
            if (_packWaitSinceQpc == 0) _packWaitSinceQpc = stamp;
            if (stamp < _nextPackRetryQpc) return;     // a window that never measures must not spin
            _nextPackRetryQpc = stamp + Stopwatch.Frequency / 10;
            // Packing around a widget that will not measure is the overlap bug itself, so that is
            // never the answer — but a widget whose layout keeps throwing would otherwise hold the
            // arrange forever with nothing in the log to say why. Nothing has moved and nothing was
            // written, so the state is simply "not arranged yet"; name it once and keep retrying.
            if (!_packWaitLogged && stamp - _packWaitSinceQpc > 5 * Stopwatch.Frequency)
            {
                _packWaitLogged = true;
                Log.Warn("auto-arrange: still no laid-out size from "
                    + string.Join(", ", packed.Where(w => w.SizeStale).Select(w => w.Config.Id))
                    + " — no widget has been moved and the arrange stays pending");
            }
            foreach (var w in packed)
            {
                w.RemeasureSoon();
                if (w.NextDueQpc > stamp) w.NextDueQpc = stamp;
            }
            return;
        }
        _packWaitSinceQpc = 0;
        _packWaitLogged = false;

        var target = PrimaryMonitor;
        // the arrange flag is part of the signature: a fresh request must re-pack even when the
        // widgets and their sizes are exactly what they were last time
        string sig = (ArrangeRequested ? "arrange|" : "") + target.Device + "|" + target.W + "x" + target.H + "|"
            + string.Join(',', packed.Select(w => $"{w.Config.Id}:{w.PixelSize.W}x{w.PixelSize.H}"));
        if (sig == _packSignature) return;
        _packSignature = sig;

        var occupied = _windows.Where(w => !w.Displaced)
            .Select(w => { var (x, y, ww, hh) = w.ScreenRect(); return new ColumnPacker.Box(x, y, ww, hh); })
            .ToList();
        var items = packed.Select(w => new ColumnPacker.Item(w.Config.Id, w.PixelSize.W, w.PixelSize.H)).ToList();

        var placements = ColumnPacker.Pack(target, items, occupied);
        foreach (var place in placements)
        {
            var win = packed.First(w => w.Config.Id == place.Id);
            win.SetPackedPosition(place.X, place.Y);
        }
        Log.Info($"auto-arrange: packed {items.Count} widget(s) onto {target.Device} "
            + $"({target.W}x{target.H} @{target.Dpi:0}dpi): "
            + string.Join(", ", placements.Select(p =>
            {
                var w = packed.First(x => x.Config.Id == p.Id);
                return $"{p.Id}@{p.X},{p.Y} {w.PixelSize.W}x{w.PixelSize.H} scale {w.Ctx.Theme.BaseScale:0.##}";
            })));

        if (ArrangeRequested) PersistArrangedLayout(target);
    }

    /// <summary>
    /// An arrange pass is the one case where the packed layout is written back: it becomes the
    /// user's layout. <c>appearance.scale</c> is deliberately left alone so an "auto" widget keeps
    /// adapting after a monitor swap (H7). The write is a merge — the Settings app may be editing
    /// the same file — and clearing the flag is what tells the requester it is done.
    /// </summary>
    private void PersistArrangedLayout(MonitorInfo target)
    {
        // in-memory first, so ArrangeRequested is false before the file change comes back to us
        ConfigStore.Widgets.Arrange = null;
        var placed = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
        foreach (var win in _windows)
        {
            var (sx, sy, _, _) = win.ScreenRect();
            win.Config.Monitor = target.Device;
            win.Config.X = sx - target.X;
            win.Config.Y = sy - target.Y;
            win.MarkPlaced();
            placed[win.Config.Id] = (win.Config.X, win.Config.Y);
        }
        try
        {
            string device = target.Device;
            ConfigStore.UpdateWidgets(cfg =>
            {
                cfg.Arrange = null;
                foreach (var w in cfg.Widgets)
                    if (placed.TryGetValue(w.Id, out var p))
                    {
                        w.Monitor = device;
                        w.X = p.X;
                        w.Y = p.Y;
                    }
            });
            Log.Info($"arrange: wrote {placed.Count} position(s) to widgets.json and cleared the flag");
        }
        catch (Exception ex) { Log.Error("arrange: saving the packed layout", ex); }
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

    /// <summary>Tray / context-menu lock toggle. settings.json has two writers as well, and the
    /// widget process owns exactly this one field there.</summary>
    public void ToggleLockAll()
    {
        bool next = !ConfigStore.Settings.LockAll;
        ConfigStore.Settings.LockAll = next;
        try { ConfigStore.UpdateSettings(s => s.LockAll = next); }
        catch (Exception ex) { Log.Error("saving lockAll", ex); }
    }

    /// <summary>
    /// Fresh install: ask <see cref="DefaultLayout"/> what this PC should have — the same list the
    /// Settings app's "Generate default layout" produces — write it with the arrange flag set, and
    /// let the normal arrange path place it. Nothing about which widgets to create lives here.
    /// </summary>
    private void GenerateFirstRunLayout()
    {
        bool online = WaitForCollector(5);
        var widgets = DefaultLayout.Generate(n => Metrics.Value(n), n => Metrics.Text(n), online);

        ConfigStore.Widgets.Widgets.Clear();
        ConfigStore.Widgets.Widgets.AddRange(widgets);
        ConfigStore.Widgets.Arrange = WidgetsConfig.ArrangePending;
        try { ConfigStore.SaveWidgets(); }
        catch (Exception ex) { Log.Error("first run: writing the generated layout", ex); }
        Log.Info($"first run: no widgets.json — generated {widgets.Count} widget(s) "
            + $"(collector {(online ? $"online, {Metrics.MetricCount} metrics" : "not running — minimal layout")})");
    }

    /// <summary>
    /// Wait for the collector's registry to settle before reading hardware off it. The installer
    /// starts the collector task first, but NVML and LHM rows keep appearing for a second or two
    /// after the section exists — generating a layout at the wrong moment silently drops the GPU
    /// or fans widget. Returns whether there is a collector at all.
    /// </summary>
    private bool WaitForCollector(double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        int lastCount = -1, stableFor = 0;
        while (DateTime.UtcNow < deadline)
        {
            Metrics.Tick();
            if (Metrics.Attached)
            {
                int count = Metrics.MetricCount;
                if (count == lastCount && count > 0)
                {
                    if (++stableFor >= 5) return true;    // ~500 ms without a new metric
                }
                else { lastCount = count; stableFor = 0; }
            }
            Thread.Sleep(100);
        }
        return Metrics.Attached;
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
        if (_framesReadyEvent != 0) { CloseHandle(_framesReadyEvent); _framesReadyEvent = 0; }
        Metrics.Dispose();
        _dx.Dispose();
        ConfigStore.Dispose();
    }
}
