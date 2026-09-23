using System.Runtime.InteropServices;

namespace Halo.Widgets;

internal static unsafe partial class Native
{
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_WINDOWPOSCHANGING = 0x0046;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_USER = 0x0400;
    public const uint WM_APP_TRAY = WM_USER + 1;
    public const uint WM_SETTINGCHANGE = 0x001A;

    public const int HTCAPTION = 2, HTCLIENT = 1;
    public const int GWL_EXSTYLE = -20;

    public static readonly nint HWND_BOTTOM = 1;
    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_NOZORDER = 0x0004;
    // the desktop-host probe runs periodically now, so never block the render loop on a hung shell
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int W => Right - Left; public int H => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSW
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
    }

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassW(ref WNDCLASSW wc);

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(int exStyle, nint className, string? windowName, int style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32")] public static extern bool GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32")] public static extern bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32")] public static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32")] public static extern void PostQuitMessage(int code);
    [DllImport("user32")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32")] public static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32", SetLastError = true)] public static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32", SetLastError = true)] public static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32")] public static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32")] public static extern nint SetCapture(nint hwnd);
    [DllImport("user32")] public static extern bool ReleaseCapture();
    [DllImport("user32")] public static extern uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint timeoutMs, uint wakeMask, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint OpenEventW(uint desiredAccess, bool inheritHandle, string name);
    [DllImport("kernel32")] public static extern bool CloseHandle(nint handle);
    public const uint SYNCHRONIZE = 0x00100000;
    [DllImport("user32")] public static extern nint GetForegroundWindow();
    [DllImport("user32")] public static extern bool SetForegroundWindow(nint hwnd);

    // The only dialog this process ever shows. Halo.Widgets is a WinExe with no window until the
    // overlay is up, so a startup failure has nowhere to surface: stderr goes to a console that
    // does not exist, and the user sees a shortcut click that did nothing. Settings gets this for
    // free from WPF's MessageBox (Halo.Settings\App.xaml.cs:59); here it is one import.
    [DllImport("user32", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
    public const uint MB_OK = 0x00000000, MB_ICONERROR = 0x00000010,
        MB_SETFOREGROUND = 0x00010000, MB_TOPMOST = 0x00040000;

    // menus
    [DllImport("user32")] public static extern nint CreatePopupMenu();
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? item);
    [DllImport("user32")] public static extern bool DestroyMenu(nint menu);
    [DllImport("user32")] public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    public const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 0x8, MF_GRAYED = 0x1, MF_POPUP = 0x10;
    public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;

    // monitors
    public delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint data);
    [DllImport("user32")] public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW mi);
    [DllImport("user32")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32")] public static extern nint MonitorFromPoint(POINT pt, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public const uint MONITORINFOF_PRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    // Per-monitor DPI (hardware plan H5). The exe manifest declares PerMonitorV2, so these
    // return the monitor's real DPI instead of the process-wide virtualised 96.
    public const int MDT_EFFECTIVE_DPI = 0;
    [DllImport("shcore")] public static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Effective DPI of a monitor, or 96 when shcore is unavailable (Win7-era shells).</summary>
    public static double MonitorDpi(nint hmonitor)
    {
        try
        {
            if (GetDpiForMonitor(hmonitor, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0) return dx;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 96;
    }

    // desktop (WorkerW) parenting for "on desktop" z-mode
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string? cls, string? name);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint FindWindowExW(nint parent, nint after, string? cls, string? name);
    [DllImport("user32")] public static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32")] public static extern nint SetParent(nint child, nint parent);
    [DllImport("user32")] public static extern bool ScreenToClient(nint hwnd, ref POINT pt);
    [DllImport("user32")] public static extern bool MapWindowPoints(nint from, nint to, ref POINT pt, uint count);
    [DllImport("user32")] public static extern bool IsWindow(nint hwnd);

    // tray icon
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
    // "TaskbarCreated" is broadcast to all top-level windows when Explorer restarts
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;

    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint LoadImageW(nint inst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32")] public static extern nint LoadIconW(nint inst, nint name);
    [DllImport("user32")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }
    public const uint TME_LEAVE = 2;

    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);

    // timer resolution for high-rate ticks
    [DllImport("winmm")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm")] public static extern uint timeEndPeriod(uint ms);
}
