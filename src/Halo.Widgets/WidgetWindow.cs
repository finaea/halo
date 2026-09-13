using System.Runtime.InteropServices;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Shared.Panels;
using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Halo.Widgets.Native;

namespace Halo.Widgets;

/// <summary>
/// One widget = one WS_EX_NOREDIRECTIONBITMAP popup with a DirectComposition swapchain
/// (plan §8), Rainmeter-grade window behaviors (plan §9.3): drag+snap when unlocked,
/// z-order modes, click-through, opacity, context menu.
/// </summary>
public sealed unsafe class WidgetWindow : IDisposable
{
    private const string ClassName = "HaloWidgetWindow";
    private static readonly Dictionary<nint, WidgetWindow> ByHwnd = new();
    private static ushort _classAtom;
    private static WndProcDelegate? _wndProcKeeper;
    private delegate nint WndProcDelegate(nint hwnd, uint msg, nuint wParam, nint lParam);

    public nint Hwnd { get; private set; }
    public WidgetInstance Config { get; private set; }
    public Panel Panel { get; }
    public PanelContext Ctx { get; }

    /// <summary>Fastest useful repaint for this widget: the highest nominal rate among its
    /// metrics, floor 1 Hz, ceiling 10 Hz (rates plan R2). Event-driven panels don't get a
    /// slider at all — they repaint on the frames-ready event with a fixed fallback tick.</summary>
    public double MaxRateHz { get; private set; } = PanelRates.CeilingHz;

    /// <summary>Repaint rate. The upper bound is the fastest data source in this panel, so a
    /// slider can never promise data the collector does not produce (rates plan R2).</summary>
    public double RateHz => Ctx.Type?.EventDriven == true
        ? PanelRates.EventDrivenFallbackHz
        : PanelRates.Clamp(Config.RateHz, MaxRateHz);

    public long NextDueQpc;

    /// <summary>The monitor this widget is currently drawn on — its own when that monitor
    /// exists, otherwise the fallback the packer placed it on.</summary>
    public MonitorInfo HostMonitor { get; private set; } = MonitorInfo.None;

    /// <summary>True while the configured monitor is missing. The position and scale used are
    /// then in memory only; widgets.json still holds the real layout (hardware plan H6).</summary>
    public bool Displaced { get; private set; }

    private int? _packedX, _packedY;

    public (int W, int H) PixelSize => (_pxW, _pxH);

    /// <summary>
    /// True while <see cref="PixelSize"/> still describes a scale this widget is no longer drawn
    /// at. The size is measured in <see cref="Tick"/> from the theme's effective scale, so
    /// re-theming the widget — moving it to a monitor with a different DPI or auto scale —
    /// invalidates it until the next tick redraws. The packer lays out boxes of exactly this size,
    /// so it must never pack a window whose size still describes the old scale.
    /// </summary>
    public bool SizeStale => _pxH <= 0 || Math.Abs(_measuredScale - Ctx.Theme.EffectiveScale) > 1e-9;

    /// <summary>Make the next tick re-measure whatever the panel thinks about dirtiness.</summary>
    public void RemeasureSoon() => _needsFullRedraw = true;

    private readonly Dx _dx;
    private readonly App _app;
    private IDXGISwapChain1? _swapChain;
    private IDCompositionTarget? _compTarget;
    private IDCompositionVisual? _compVisual;
    private ID2D1DeviceContext? _d2dDc;
    private RenderContext? _rc;
    private int _pxW, _pxH;
    private double _measuredScale = -1;      // effective scale _pxW/_pxH were measured at
    private float _dcDpi;
    private bool _needsFullRedraw = true;

    public bool IsDragging => _dragging;

    // drag state
    private bool _dragging;
    private POINT _dragStartCursor;
    private int _dragStartX, _dragStartY;

    public WidgetWindow(App app, Dx dx, WidgetInstance config, Panel panel, PanelContext ctx,
        MonitorInfo monitor, bool displaced, double maxRateHz)
    {
        _app = app;
        _dx = dx;
        Config = config;
        Panel = panel;
        Ctx = ctx;
        HostMonitor = monitor;
        Displaced = displaced;
        MaxRateHz = maxRateHz;
        ctx.MaxRateHz = maxRateHz;

        EnsureClass();
        int ex = WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        Hwnd = CreateWindowExW(ex, (nint)_classAtom, "Halo " + config.Id, WS_POPUP,
            0, 0, 100, 100, 0, 0, GetModuleHandleW(null), 0);
        if (Hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        ByHwnd[Hwnd] = this;

        CreateGraphics();
        ApplyZMode();
        Reposition();
        ShowWindow(Hwnd, 8 /*SW_SHOWNA*/);
    }

    private static void EnsureClass()
    {
        if (_classAtom != 0) return;
        _wndProcKeeper = StaticWndProc;
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeeper),
            hInstance = GetModuleHandleW(null),
            lpszClassName = Marshal.StringToHGlobalUni(ClassName),
            hCursor = LoadIconW(0, 0) == 0 ? 0 : 0,
        };
        _classAtom = RegisterClassW(ref wc);
        if (_classAtom == 0) throw new InvalidOperationException($"RegisterClass failed: {Marshal.GetLastWin32Error()}");
    }

    private void CreateGraphics()
    {
        _d2dDc = _dx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        _rc = new RenderContext(_d2dDc, _dx.DWrite, _dx.CustomFonts, Ctx.Theme);
        _dx.CompDevice.CreateTargetForHwnd(Hwnd, false, out IDCompositionTarget target).CheckError();
        _compTarget = target;
        _compVisual = _dx.CompDevice.CreateVisual();
        _dcDpi = 0;
        _needsFullRedraw = true;
    }

    public void RecreateGraphics()
    {
        ReleaseGraphics();
        CreateGraphics();
        _pxW = _pxH = 0; // force swapchain rebuild
    }

    private void ReleaseGraphics()
    {
        _rc?.Dispose(); _rc = null;
        if (_d2dDc != null) { _d2dDc.Target = null; _d2dDc.Dispose(); _d2dDc = null; }
        _swapChain?.Dispose(); _swapChain = null;
        _compVisual?.Dispose(); _compVisual = null;
        _compTarget?.Dispose(); _compTarget = null;
    }

    private void EnsureSwapChain(int w, int h)
    {
        if (_swapChain != null && w == _pxW && h == _pxH) return;
        _pxW = w; _pxH = h;

        if (_swapChain == null)
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)w,
                Height = (uint)h,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
                Flags = SwapChainFlags.None,
            };
            _swapChain = _dx.DxgiFactory.CreateSwapChainForComposition(_dx.D3D, desc);
            _compVisual!.SetContent(_swapChain);
            _compTarget!.SetRoot(_compVisual);
            _dx.CompDevice.Commit();
        }
        else
        {
            _d2dDc!.Target = null;
            _swapChain.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        }
        _needsFullRedraw = true;
    }

    /// <summary>Update data / layout / render if dirty. Returns true if it drew.</summary>
    public bool Tick()
    {
        Ctx.Now = DateTime.Now;
        Ctx.NowQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        Ctx.TickIndex++;
        bool wasStale = Ctx.Stale;
        Ctx.Stale = Ctx.Metrics.Stale;
        bool dirty = Panel.Update(Ctx) || _needsFullRedraw || wasStale != Ctx.Stale;
        if (!dirty) return false;

        // Window size is in physical pixels, so it carries the monitor's DPI; the D2D transform
        // carries only the base scale, because the target bitmap is created at that same DPI.
        double scale = Ctx.Theme.EffectiveScale;
        double logicalH = Panel.Layout(_rc!, Ctx);
        int pxW = (int)Math.Ceiling(Ctx.Theme.BgWidth * scale);
        int pxH = (int)Math.Ceiling(logicalH * scale);
        if (pxH < 8) pxH = 8;
        _measuredScale = scale;

        bool sizeChanged = pxW != _pxW || pxH != _pxH;
        if (sizeChanged)
        {
            SetWindowPos(Hwnd, 0, 0, 0, pxW, pxH, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            EnsureSwapChain(pxW, pxH);
        }

        Render();
        return true;
    }

    private void Render()
    {
        if (_swapChain == null || _d2dDc == null || _rc == null) return;
        try
        {
            using var surface = _swapChain.GetBuffer<IDXGISurface>(0);
            // The target is created at the monitor's DPI (hardware plan H5), so D2D converts
            // DIPs to pixels itself and DirectWrite grid-fits at the real device resolution.
            // The world transform therefore carries only the user's scale.
            float dpi = (float)Ctx.Theme.Dpi;
            var props = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                dpi, dpi, BitmapOptions.Target | BitmapOptions.CannotDraw);
            using var bitmap = _d2dDc.CreateBitmapFromDxgiSurface(surface, props);
            _d2dDc.Target = bitmap;
            // SetTarget does not adopt the bitmap's DPI — the context keeps whatever it was
            // created with (the system DPI), so say it explicitly or a 125 % monitor renders at
            // the primary monitor's scale and gets clipped.
            if (_dcDpi != dpi) { _d2dDc.SetDpi(dpi, dpi); _dcDpi = dpi; }

            _d2dDc.BeginDraw();
            _d2dDc.Clear(new Color4(0, 0, 0, 0));
            _d2dDc.Transform = System.Numerics.Matrix3x2.CreateScale((float)Ctx.Theme.BaseScale);

            bool useOpacityLayer = Config.Opacity < 0.999;
            if (useOpacityLayer)
                _d2dDc.PushLayer(new LayerParameters1 { ContentBounds = new Rect(0, 0, 100000, 100000), Opacity = (float)Config.Opacity }, null!);

            Panel.Draw(_rc, Ctx);

            if (useOpacityLayer) _d2dDc.PopLayer();
            _d2dDc.EndDraw();
            _d2dDc.Target = null;
            _swapChain.Present(0, PresentFlags.None);
            _needsFullRedraw = false;
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            Log.Error($"render {Config.Id} (device lost?)", ex);
            _app.RequestDeviceRecovery();
        }
    }

    // ---- positioning ----

    /// <summary>Resolve monitor-relative config position to screen and move there (plan §9.3).</summary>
    public void Reposition()
    {
        var (x, y) = TargetScreenPos();
        MoveWindowScreen(x, y);
    }

    /// <summary>
    /// Final screen position for the current config. KeepOnScreen clamps within the monitor
    /// that best contains the target rect — not the configured monitor, which for legacy
    /// configs (monitor="") is always the first one and would drag cross-monitor widgets
    /// back to it. The position guard must use this same math or it fights the clamp.
    /// </summary>
    public (int X, int Y) TargetScreenPos()
    {
        int x, y;
        if (_packedX is { } px && _packedY is { } py)
        {
            // displaced (or first-run) placement from the column packer — already screen coords
            x = px; y = py;
        }
        else
        {
            x = HostMonitor.X + Config.X;
            y = HostMonitor.Y + Config.Y;
        }
        if (Config.KeepOnScreen)
        {
            var (_, wx, wy, ww, wh) = _app.MonitorForRect(x, y, _pxW, _pxH);
            x = Math.Clamp(x, wx, Math.Max(wx, wx + ww - _pxW));
            y = Math.Clamp(y, wy, Math.Max(wy, wy + wh - _pxH));
        }
        return (x, y);
    }

    /// <summary>Position chosen by the packer, in screen coordinates. Memory only — the config
    /// keeps the user's real layout so it comes back when the monitor does (H6).</summary>
    public void SetPackedPosition(int x, int y)
    {
        if (_packedX == x && _packedY == y) return;
        _packedX = x;
        _packedY = y;
        Reposition();
        _needsFullRedraw = true;
    }

    /// <summary>The packed position has just been written into the config as the real one, so
    /// this widget is no longer displaced and resolves through Config.X/Y from now on.</summary>
    public void MarkPlaced()
    {
        _packedX = null;
        _packedY = null;
        Displaced = false;
    }

    /// <summary>Re-bind to a monitor (display change, or the configured one came back) and take
    /// the theme that was resolved for it — scale and DPI both depend on which monitor this is.</summary>
    public void ApplyMonitor(MonitorInfo monitor, bool displaced, Theme resolved)
    {
        bool changed = !HostMonitor.Equals(monitor) || displaced != Displaced;
        HostMonitor = monitor;
        Displaced = displaced;
        if (!displaced) { _packedX = null; _packedY = null; }
        if (Math.Abs(Ctx.Theme.BaseScale - resolved.BaseScale) > 1e-9 || Math.Abs(Ctx.Theme.Dpi - monitor.Dpi) > 1e-9)
            changed = true;
        Ctx.Theme.CopyFrom(resolved);
        Ctx.Theme.Dpi = monitor.Dpi;
        if (changed)
        {
            _needsFullRedraw = true;
            Reposition();
        }
    }

    private void MoveWindowScreen(int x, int y)
    {
        // when parented to the desktop host, coordinates are client-relative
        nint parent = GetParentWindow();
        if (parent != 0)
        {
            var pt = new POINT { X = x, Y = y };
            MapWindowPoints(0, parent, ref pt, 1);
            SetWindowPos(Hwnd, 0, pt.X, pt.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        else
        {
            SetWindowPos(Hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    private nint GetParentWindow() => Config.ZMode == ZMode.Desktop ? _app.DesktopHost : 0;

    public void ApplyZMode()
    {
        switch (Config.ZMode)
        {
            case ZMode.Desktop:
                if (_app.DesktopHost != 0)
                {
                    SetParent(Hwnd, _app.DesktopHost);
                }
                else
                {
                    SetParent(Hwnd, 0);
                    SetWindowPos(Hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                break;
            case ZMode.Normal:
                SetParent(Hwnd, 0);
                SetWindowPos(Hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                break;
            case ZMode.Topmost:
                SetParent(Hwnd, 0);
                SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                break;
        }
        Reposition();
    }

    public (int X, int Y, int W, int H) ScreenRect()
    {
        GetWindowRect(Hwnd, out RECT r);
        if (GetParentWindow() != 0)
        {
            // GetWindowRect returns screen coords even for children — fine
        }
        return (r.Left, r.Top, r.W, r.H);
    }

    // ---- input ----

    private static nint StaticWndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
        => ByHwnd.TryGetValue(hwnd, out var w) ? w.WndProc(hwnd, msg, wParam, lParam) : DefWindowProcW(hwnd, msg, wParam, lParam);

    private nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST when Config.ClickThrough:
                return -1; // HTTRANSPARENT

            case WM_LBUTTONDOWN:
                if (!IsLocked())
                {
                    _dragging = true;
                    GetCursorPos(out _dragStartCursor);
                    var (sx, sy, _, _) = ScreenRect();
                    _dragStartX = sx; _dragStartY = sy;
                    SetCapture(hwnd);
                }
                return 0;

            case WM_MOUSEMOVE when _dragging:
                {
                    GetCursorPos(out POINT p);
                    int nx = _dragStartX + (p.X - _dragStartCursor.X);
                    int ny = _dragStartY + (p.Y - _dragStartCursor.Y);
                    (nx, ny) = _app.Snap(this, nx, ny, _pxW, _pxH);
                    MoveWindowScreen(nx, ny);
                }
                return 0;

            case WM_LBUTTONUP when _dragging:
                _dragging = false;
                ReleaseCapture();
                PersistPosition();
                return 0;

            case WM_RBUTTONUP:
                ShowContextMenu();
                return 0;

            // A desktop-parented child does not reliably get either of these, so the 5 s position
            // guard re-resolves DPI and monitors anyway; handling them here just makes the
            // common cases (Normal / Topmost z-modes) instant.
            case WM_DPICHANGED:
            case WM_DISPLAYCHANGE:
                _app.RequestPlacementRefresh();
                return 0;

            case WM_WINDOWPOSCHANGING when Config.ZMode == ZMode.Desktop && _app.DesktopHost == 0:
                // keep at bottom when desktop-parenting unavailable
                var wp = (WINDOWPOS*)lParam;
                if ((wp->flags & SWP_NOZORDER) == 0) wp->hwndInsertAfter = HWND_BOTTOM;
                return 0;

            case WM_DESTROY:
                ByHwnd.Remove(hwnd);
                return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private bool IsLocked() => Config.Locked || Ctx.Settings.LockAll;

    private void PersistPosition()
    {
        // Record the monitor the widget actually sits on, coords relative to it — so
        // Reposition/KeepOnScreen resolve against the right monitor after a rebuild,
        // and the layout survives monitor rearrangement.
        var (sx, sy, w, h) = ScreenRect();
        var (dev, mx, my, _, _) = _app.MonitorForRect(sx, sy, w, h);
        Config.Monitor = dev;
        Config.X = sx - mx;
        Config.Y = sy - my;
        // dragging a displaced widget is the user saying "it lives here now"
        _packedX = null;
        _packedY = null;
        Displaced = false;
        string monitor = dev; int cx = Config.X, cy = Config.Y;
        _app.PatchWidget(Config.Id, w => { w.Monitor = monitor; w.X = cx; w.Y = cy; });
        _app.RefreshPlacement();
    }

    private void ShowContextMenu()
    {
        nint menu = CreatePopupMenu();
        nint zMenu = CreatePopupMenu();
        nint opMenu = CreatePopupMenu();
        try
        {
            AppendMenuW(zMenu, MF_STRING | (Config.ZMode == ZMode.Desktop ? MF_CHECKED : 0), 11, "On desktop");
            AppendMenuW(zMenu, MF_STRING | (Config.ZMode == ZMode.Normal ? MF_CHECKED : 0), 12, "Normal");
            AppendMenuW(zMenu, MF_STRING | (Config.ZMode == ZMode.Topmost ? MF_CHECKED : 0), 13, "Always on top");
            foreach (var (id, pct) in new[] { (21, 100), (22, 90), (23, 75), (24, 50) })
                AppendMenuW(opMenu, MF_STRING | (Math.Abs(Config.Opacity * 100 - pct) < 5 ? MF_CHECKED : 0), (nuint)id, $"{pct}%");

            AppendMenuW(menu, MF_STRING | (Config.Locked ? MF_CHECKED : 0), 1, "Lock position");
            AppendMenuW(menu, MF_POPUP, (nuint)zMenu, "Z-order");
            AppendMenuW(menu, MF_POPUP, (nuint)opMenu, "Opacity");
            AppendMenuW(menu, MF_STRING | (Config.ClickThrough ? MF_CHECKED : 0), 2, "Click through");
            AppendMenuW(menu, MF_STRING | (Config.KeepOnScreen ? MF_CHECKED : 0), 3, "Keep on screen");
            if (Config.Type is "topcpu" or "topram")
            {
                bool agg = Config.Options.GetValueOrDefault("aggregate") == "true";
                AppendMenuW(menu, MF_STRING | (agg ? MF_CHECKED : 0), 30, "Sum same-name processes");
            }
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, 4, "Refresh");
            AppendMenuW(menu, MF_STRING, 5, "Reset session max (this panel)");
            AppendMenuW(menu, MF_STRING, 6, "Settings…");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING | (Ctx.Settings.LockAll ? MF_CHECKED : 0), 7, "Lock all widgets");
            AppendMenuW(menu, MF_STRING, 9, "Exit Halo");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(Hwnd);
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, Hwnd, 0);
            HandleMenu(cmd);
        }
        finally
        {
            DestroyMenu(opMenu);
            DestroyMenu(zMenu);
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Quick toggles write through the same merge-on-write path the drag does: re-read
    /// widgets.json, change only this field, write it back. A whole-file save from our in-memory
    /// copy would undo whatever the Settings app wrote since our last reload (settings plan S3
    /// writes every ~200 ms while a user is dragging a slider).
    /// </summary>
    private void HandleMenu(int cmd)
    {
        switch (cmd)
        {
            case 1:
                Config.Locked = !Config.Locked;
                Patch(w => w.Locked = Config.Locked);
                break;
            case 11: case 12: case 13:
                Config.ZMode = cmd == 11 ? ZMode.Desktop : cmd == 12 ? ZMode.Normal : ZMode.Topmost;
                ApplyZMode();
                Patch(w => w.ZMode = Config.ZMode);
                break;
            case 21: Config.Opacity = 1.0; goto case 99;
            case 22: Config.Opacity = 0.9; goto case 99;
            case 23: Config.Opacity = 0.75; goto case 99;
            case 24: Config.Opacity = 0.5; goto case 99;
            case 2:
                Config.ClickThrough = !Config.ClickThrough;
                Patch(w => w.ClickThrough = Config.ClickThrough);
                break;
            case 3:
                Config.KeepOnScreen = !Config.KeepOnScreen;
                Patch(w => w.KeepOnScreen = Config.KeepOnScreen);
                Reposition();
                break;
            case 4: _needsFullRedraw = true; break;
            case 5: _app.ResetPanelMax(Config.Type); break;
            case 6: _app.OpenSettings(); break;
            case 7: _app.ToggleLockAll(); break;
            case 9: _app.Quit(); break;
            case 30:
                Config.Options["aggregate"] = Config.Options.GetValueOrDefault("aggregate") == "true" ? "false" : "true";
                string agg = Config.Options["aggregate"];
                Patch(w => w.Options["aggregate"] = agg);
                _needsFullRedraw = true;
                break;
            case 99:
                _needsFullRedraw = true;
                Patch(w => w.Opacity = Config.Opacity);
                break;
        }
    }

    private void Patch(Action<WidgetInstance> mutate)
    {
        _needsFullRedraw = true;
        _app.PatchWidget(Config.Id, mutate);
    }

    public void ForceRedraw() => _needsFullRedraw = true;

    /// <summary>
    /// Adopt a changed config without rebuilding the window (settings plan §Live-apply). Anything
    /// that only changes values — rate, graph history/height/style, colours, labels, warn points,
    /// scale, placement, opacity, z-mode, click-through, title — lands here; only a type change,
    /// a structural option or a metric show/graph toggle costs a rebuild, and then App replaces
    /// this window instead of calling this.
    /// </summary>
    public void ApplyInPlace(WidgetInstance next, AppSettings settings, MonitorInfo monitor, bool displaced,
        Theme resolved, double maxRateHz)
    {
        var prev = Config;
        Config = next;
        Ctx.Widget = next;
        Ctx.Settings = settings;
        MaxRateHz = maxRateHz;
        Ctx.MaxRateHz = maxRateHz;

        ApplyMonitor(monitor, displaced, resolved);

        if (prev.ZMode != next.ZMode) ApplyZMode();
        if (prev.X != next.X || prev.Y != next.Y || prev.Monitor != next.Monitor
            || prev.KeepOnScreen != next.KeepOnScreen) Reposition();

        if (Math.Abs(prev.Graph.HistoryS - next.Graph.HistoryS) > 1e-9
            || Math.Abs(prev.Graph.Height - next.Graph.Height) > 1e-9
            || !string.Equals(prev.Graph.Style, next.Graph.Style, StringComparison.OrdinalIgnoreCase)
            || Math.Abs(prev.RateHz - next.RateHz) > 1e-9)
        {
            Panel.ApplyGraphSettings(Ctx.GraphHistoryS, Ctx.GraphHeight, Ctx.GraphStyle, maxRateHz);
        }

        _needsFullRedraw = true;
    }

    /// <summary>Re-clamp the rate and re-size the graph rings after the registry grew (a provider
    /// that was not running when this widget was built can raise its bound, R2).</summary>
    public void ApplyRateBound(double maxRateHz)
    {
        if (Math.Abs(MaxRateHz - maxRateHz) < 1e-9) return;
        MaxRateHz = maxRateHz;
        Ctx.MaxRateHz = maxRateHz;
        Panel.ApplyGraphSettings(Ctx.GraphHistoryS, Ctx.GraphHeight, Ctx.GraphStyle, maxRateHz);
    }

    public void Dispose()
    {
        ReleaseGraphics();
        if (Hwnd != 0) { DestroyWindow(Hwnd); ByHwnd.Remove(Hwnd); Hwnd = 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS { public nint hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
}
