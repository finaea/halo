using System.Runtime.InteropServices;
using static Halo.Widgets.Native;

namespace Halo.Widgets;

/// <summary>Tray icon with the global menu (lock all, refresh, settings, exit).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private nint _hwnd;
    private static TrayIcon? _instance;
    private static WndProcDelegate? _procKeeper;
    private delegate nint WndProcDelegate(nint hwnd, uint msg, nuint wParam, nint lParam);

    public TrayIcon(App app)
    {
        _app = app;
        _instance = this;
        _procKeeper = WndProc;
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_procKeeper),
            hInstance = GetModuleHandleW(null),
            lpszClassName = Marshal.StringToHGlobalUni("HaloTrayWindow"),
        };
        RegisterClassW(ref wc);
        _hwnd = CreateWindowExW(0, wc.lpszClassName, "HaloTray", 0, 0, 0, 0, 0, unchecked((nint)(-3)) /*HWND_MESSAGE*/, 0, wc.hInstance, 0);

        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = LoadIconW(0, 32512 /*IDI_APPLICATION*/),
            szTip = "Halo — Hardware Analytics & Live Overlay",
        };
        Shell_NotifyIconW(NIM_ADD, ref data);
    }

    private nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            uint mouse = (uint)(lParam & 0xFFFF);
            if (mouse is WM_RBUTTONUP or WM_LBUTTONUP)
                ShowMenu();
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        try
        {
            bool locked = _app.ConfigStore.Settings.LockAll;
            bool stale = _app.Metrics.Stale;
            AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, stale ? "Collector: not running" : "Collector: OK");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING | (locked ? MF_CHECKED : 0), 1, "Lock all widgets");
            AppendMenuW(menu, MF_STRING, 2, "Refresh all");
            AppendMenuW(menu, MF_STRING, 3, "Settings…");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, 9, "Exit Halo");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hwnd);
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, 0);
            switch (cmd)
            {
                case 1: _app.ToggleLockAll(); break;
                case 2: _app.RefreshAll(); break;
                case 3: _app.OpenSettings(); break;
                case 9: _app.Quit(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
        };
        Shell_NotifyIconW(NIM_DELETE, ref data);
        if (_hwnd != 0) { DestroyWindow(_hwnd); _hwnd = 0; }
        _instance = null;
    }
}
