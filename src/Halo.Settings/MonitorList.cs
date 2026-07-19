using System.Runtime.InteropServices;

namespace Halo.Settings;

/// <summary>Enumerates monitors (device id + bounds) for the widget monitor picker.</summary>
public static class MonitorList
{
    public sealed record Entry(string Device, int X, int Y, int W, int H, bool Primary)
    {
        public string Label =>
            $"{Device.Replace(@"\\.\", "")} — {W}×{H} at ({X}, {Y}){(Primary ? " · primary" : "")}";
    }

    public static List<Entry> Get()
    {
        var list = new List<Entry>();
        EnumDisplayMonitors(0, 0, (nint mon, nint _, ref RECT _, nint _) =>
        {
            var mi = new MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
            if (GetMonitorInfoW(mon, ref mi))
                list.Add(new Entry(mi.szDevice, mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    (mi.dwFlags & 1) != 0));
            return true;
        }, 0);
        return list;
    }

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
}
