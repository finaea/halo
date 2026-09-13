using System.Runtime.InteropServices;
using Halo.Shared;
using static Halo.Widgets.Native;

namespace Halo.Widgets;

/// <summary>Tray icon with the global menu (lock all, refresh, settings, exit).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private nint _hwnd;
    private nint _hIcon;
    private readonly uint _taskbarCreated;
    private static TrayIcon? _instance;
    private static WndProcDelegate? _procKeeper;
    private delegate nint WndProcDelegate(nint hwnd, uint msg, nuint wParam, nint lParam);

    public TrayIcon(App app)
    {
        _app = app;
        _instance = this;
        _procKeeper = WndProc;
        // Explorer broadcasts this when it restarts; the icon it was holding is gone by then.
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_procKeeper),
            hInstance = GetModuleHandleW(null),
            lpszClassName = Marshal.StringToHGlobalUni("HaloTrayWindow"),
        };
        RegisterClassW(ref wc);
        // NOT a message-only (HWND_MESSAGE) window: those "[do] not receive broadcast messages"
        // per the Win32 docs, and TaskbarCreated is broadcast only to top-level windows — so a
        // message-only host can never learn that Explorer restarted and would silently lose the
        // tray icon forever (observed 2026-08-31). A hidden, never-shown top-level window with
        // WS_EX_TOOLWINDOW gets the broadcast while staying out of the taskbar and alt-tab.
        _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, wc.lpszClassName, "HaloTray", WS_POPUP, 0, 0, 0, 0, 0, 0, wc.hInstance, 0);

        string icoPath = Halo.Shared.Paths.IconFile;
        _hIcon = File.Exists(icoPath)
            ? LoadImageW(0, icoPath, 1 /*IMAGE_ICON*/, 0, 0, 0x10 /*LR_LOADFROMFILE*/ | 0x40 /*LR_DEFAULTSIZE*/)
            : 0;
        if (_hIcon == 0) _hIcon = LoadIconW(0, 32512 /*IDI_APPLICATION*/);

        AddIcon();
    }

    /// <summary>Add (or re-add) the notification-area icon. Safe to call repeatedly — the icon
    /// handle is kept for the process lifetime precisely so a re-add after an Explorer restart
    /// doesn't have to reload it from disk.</summary>
    private void AddIcon()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _hIcon,
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
        // RegisterWindowMessage returns 0 on failure — never match on that
        if (msg != 0 && msg == _taskbarCreated)
        {
            Log.Info("taskbar recreated — re-adding tray icon");
            AddIcon();
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
