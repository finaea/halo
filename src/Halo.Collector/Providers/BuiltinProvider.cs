using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Shared.Metrics;

namespace Halo.Collector.Providers;

/// <summary>
/// Builtin cheap metrics (plan §5): uptime, RAM, internal/external IP, drive space + labels.
/// 1 Hz; external IP on network-change + every N minutes.
/// </summary>
public sealed class BuiltinProvider(GeneralSettings settings) : ISensorProvider
{
    public string Name => "builtin";
    public double MaxRateHz => 4;
    public double DefaultRateHz => 1;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private DateTime _nextExternalIp = DateTime.MinValue;
    private volatile bool _netChanged;
    private string _externalIp = "N/A";
    private List<char> _drives = new();

    public bool Initialize(MetricSink sink)
    {
        sink.Register(MetricNames.SysUptimeS, MetricType.Double, MetricUnit.Seconds, Name, MaxRateHz);
        sink.Register(MetricNames.RamUsedGb, MetricType.Double, MetricUnit.Gigabytes, Name, MaxRateHz);
        sink.Register(MetricNames.RamTotalGb, MetricType.Double, MetricUnit.Gigabytes, Name, MaxRateHz);
        sink.Register(MetricNames.RamPct, MetricType.Double, MetricUnit.Percent, Name, MaxRateHz);
        sink.Register(MetricNames.NetIpInternal, MetricType.String, MetricUnit.Text, Name, 1);
        sink.Register(MetricNames.NetIpExternal, MetricType.String, MetricUnit.Text, Name, 1);

        _drives = settings.DriveLetters.Select(s => char.ToUpperInvariant(s[0])).ToList();
        foreach (char c in _drives)
        {
            sink.Register(MetricNames.DriveUsedB(c), MetricType.Double, MetricUnit.Bytes, Name, 1);
            sink.Register(MetricNames.DriveTotalB(c), MetricType.Double, MetricUnit.Bytes, Name, 1);
            sink.Register(MetricNames.DriveLabel(c), MetricType.String, MetricUnit.Text, Name, 1);
        }

        NetworkChange.NetworkAddressChanged += (_, _) => { _netChanged = true; };
        return true;
    }

    public void Poll(MetricSink sink)
    {
        sink.Set(MetricNames.SysUptimeS, Environment.TickCount64 / 1000.0);

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            double totalGb = mem.ullTotalPhys / 1073741824.0;
            double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
            sink.Set(MetricNames.RamTotalGb, totalGb);
            sink.Set(MetricNames.RamUsedGb, usedGb);
            sink.Set(MetricNames.RamPct, totalGb > 0 ? usedGb / totalGb * 100 : 0);
        }

        foreach (char c in _drives)
        {
            string rootPath = c + ":\\";
            if (GetDiskFreeSpaceExW(rootPath, out ulong freeToCaller, out ulong total, out _))
            {
                sink.Set(MetricNames.DriveUsedB(c), total - freeToCaller);
                sink.Set(MetricNames.DriveTotalB(c), total);
                var label = GetVolumeLabel(rootPath);
                sink.SetString(MetricNames.DriveLabel(c), label);
            }
            else
            {
                sink.MarkStale(MetricNames.DriveUsedB(c));
                sink.MarkStale(MetricNames.DriveTotalB(c));
            }
        }

        sink.SetString(MetricNames.NetIpInternal, GetInternalIp());

        if (_netChanged || DateTime.UtcNow >= _nextExternalIp)
        {
            _netChanged = false;
            _nextExternalIp = DateTime.UtcNow.AddMinutes(Math.Max(1, settings.ExternalIpRefreshMinutes));
            _ = FetchExternalIpAsync(sink);
        }
        sink.SetString(MetricNames.NetIpExternal, _externalIp);
    }

    private async Task FetchExternalIpAsync(MetricSink sink)
    {
        try
        {
            string ip = (await _http.GetStringAsync(settings.ExternalIpUrl).ConfigureAwait(false)).Trim();
            if (ip.Length is > 6 and < 46) _externalIp = ip;
        }
        catch (Exception ex)
        {
            Log.Warn($"external IP fetch failed: {ex.Message}");
            _externalIp = "N/A";
        }
        sink.SetString(MetricNames.NetIpExternal, _externalIp);
    }

    private static string GetInternalIp()
    {
        try
        {
            // UDP connect trick: picks the interface with the default route, no traffic sent
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Connect("8.8.8.8", 65530);
            return ((System.Net.IPEndPoint)sock.LocalEndPoint!).Address.ToString();
        }
        catch { return "N/A"; }
    }

    private static string GetVolumeLabel(string root)
    {
        var name = new System.Text.StringBuilder(261);
        return GetVolumeInformationW(root, name, (uint)name.Capacity, out _, out _, out _, null, 0)
            ? name.ToString() : "";
    }

    public void Dispose() => _http.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeInformationW(string lpRootPathName, System.Text.StringBuilder lpVolumeNameBuffer, uint nVolumeNameSize, out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags, System.Text.StringBuilder? lpFileSystemNameBuffer, uint nFileSystemNameSize);
}
