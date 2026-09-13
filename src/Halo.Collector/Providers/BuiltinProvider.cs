using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Halo.Metrics;
using Halo.Shared;
using Halo.Shared.Config;

namespace Halo.Collector.Providers;

/// <summary>
/// Builtin cheap metrics (plan §5): uptime, RAM, internal/external IP, drive space + labels,
/// and the sys.* capability metrics the System-check page reads.
/// 1 Hz; external IP (opt-in) on network-change + every N minutes.
/// </summary>
public sealed class BuiltinProvider(ConfigStore config, bool elevated) : ISensorProvider
{
    public string Name => "builtin";
    public double MaxRateHz => 4;
    public double DefaultRateHz => 1;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private DateTime _nextExternalIp = DateTime.MinValue;
    private volatile bool _netChanged;
    private string _externalIp = "N/A";
    private List<char> _drives = new();
    private bool _ramTotalPublished;

    public bool Initialize(MetricSink sink)
    {
        sink.Register(MetricNames.SysUptimeS, MetricType.Double, MetricUnit.Seconds, Name, DefaultRateHz, MetricSemantics.Cumulative);
        sink.Register(MetricNames.RamUsedGb, MetricType.Double, MetricUnit.Gigabytes, Name, DefaultRateHz);
        sink.Register(MetricNames.RamTotalGb, MetricType.Double, MetricUnit.Gigabytes, Name, 0, MetricSemantics.Static);
        sink.Register(MetricNames.RamPct, MetricType.Double, MetricUnit.Percent, Name, DefaultRateHz);
        sink.Register(MetricNames.NetIpInternal, MetricType.String, MetricUnit.Text, Name, DefaultRateHz);
        // one lookup per refreshMinutes, not per poll — say so, so nobody builds a graph on it
        sink.Register(MetricNames.NetIpExternal, MetricType.String, MetricUnit.Text, Name,
            1.0 / Math.Max(60, config.Settings.Collector.ExternalIp.RefreshMinutes * 60));

        PublishCapabilities(sink);

        _drives = new();
        SyncDrives(sink);

        NetworkChange.NetworkAddressChanged += (_, _) => { _netChanged = true; };
        return true;
    }

    /// <summary>
    /// sys.* — what this machine can actually do, so the System-check page and third-party tools
    /// can explain an empty panel without parsing logs (interface plan I5).
    /// </summary>
    private void PublishCapabilities(MetricSink sink)
    {
        sink.Register(MetricNames.SysElevated, MetricType.Double, MetricUnit.None, Name, 0, MetricSemantics.Static);
        sink.Register(MetricNames.SysOsBuild, MetricType.Double, MetricUnit.None, Name, 0, MetricSemantics.Static);
        sink.Register(MetricNames.SysCollectorVersion, MetricType.String, MetricUnit.Text, Name, 0, MetricSemantics.Static);
        sink.Register(MetricNames.SysPawnIoInstalled, MetricType.Double, MetricUnit.None, Name, 0, MetricSemantics.Static);
        sink.Register(MetricNames.SysPawnIoVersion, MetricType.String, MetricUnit.Text, Name, 0, MetricSemantics.Static);

        sink.Set(MetricNames.SysElevated, elevated ? 1 : 0);
        sink.Set(MetricNames.SysOsBuild, Environment.OSVersion.Version.Build);
        sink.SetString(MetricNames.SysCollectorVersion, AppVersion.Current);

        // LibreHardwareMonitor 0.9.6 has no WinRing0 any more: without the PawnIO driver, CPU
        // temperature/power/Vcore and fan RPM are simply absent (assessment §2).
        try
        {
            bool installed = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
            sink.Set(MetricNames.SysPawnIoInstalled, installed ? 1 : 0);
            sink.SetString(MetricNames.SysPawnIoVersion, installed ? LibreHardwareMonitor.PawnIo.PawnIo.Version?.ToString() ?? "" : "");
        }
        catch (Exception ex)
        {
            Log.Warn($"PawnIO probe failed: {ex.Message}");
            sink.Set(MetricNames.SysPawnIoInstalled, 0);
        }
    }

    /// <summary>
    /// Reconcile the registered drive set with the volumes present right now. Registers metrics
    /// for newly-seen letters (Register is idempotent) and marks removed ones stale, so plugging
    /// in a drive publishes its space/label without restarting the collector.
    /// </summary>
    private void SyncDrives(MetricSink sink)
    {
        var current = Volumes.Local();

        foreach (char c in current.Where(c => !_drives.Contains(c)))
        {
            sink.Register(MetricNames.DriveUsedB(c), MetricType.Double, MetricUnit.Bytes, Name, DefaultRateHz);
            sink.Register(MetricNames.DriveTotalB(c), MetricType.Double, MetricUnit.Bytes, Name, 0, MetricSemantics.Static);
            sink.Register(MetricNames.DriveLabel(c), MetricType.String, MetricUnit.Text, Name, 0, MetricSemantics.Static);
            PublishVolumeIdentity(sink, c);
        }
        foreach (char c in _drives.Where(c => !current.Contains(c)))
        {
            sink.MarkStale(MetricNames.DriveUsedB(c));
            sink.MarkStale(MetricNames.DriveTotalB(c));
            sink.MarkStale(MetricNames.DriveLabel(c));
        }
        _drives = current;
    }

    /// <summary>
    /// Capacity and label of a volume: written when the volume is discovered, not on every poll
    /// (Static semantics). A volume that is re-plugged or a `rescan` re-runs discovery and
    /// re-writes them, so a renamed drive updates without restarting the collector.
    /// </summary>
    private static void PublishVolumeIdentity(MetricSink sink, char c)
    {
        string rootPath = c + ":\\";
        if (GetDiskFreeSpaceExW(rootPath, out _, out ulong total, out _))
            sink.Set(MetricNames.DriveTotalB(c), total);
        sink.SetString(MetricNames.DriveLabel(c), GetVolumeLabel(rootPath));
    }

    public void Poll(MetricSink sink)
    {
        sink.Set(MetricNames.SysUptimeS, Environment.TickCount64 / 1000.0);

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            double totalGb = mem.ullTotalPhys / 1073741824.0;
            double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
            // Installed RAM cannot change while the process runs: Static, so publish it once.
            if (!_ramTotalPublished) { sink.Set(MetricNames.RamTotalGb, totalGb); _ramTotalPublished = true; }
            sink.Set(MetricNames.RamUsedGb, usedGb);
            sink.Set(MetricNames.RamPct, totalGb > 0 ? usedGb / totalGb * 100 : 0);
        }

        SyncDrives(sink);   // hot-plug reconcile: new volumes appear, removed ones go stale
        foreach (char c in _drives)
        {
            string rootPath = c + ":\\";
            if (GetDiskFreeSpaceExW(rootPath, out ulong freeToCaller, out ulong total, out _))
                sink.Set(MetricNames.DriveUsedB(c), total - freeToCaller);
            else
                sink.MarkStale(MetricNames.DriveUsedB(c));
        }

        sink.SetString(MetricNames.NetIpInternal, GetInternalIp());

        // The only outbound request Halo ever makes, and it is off unless the user asks for it
        // (packaging plan P9). Read live from the store so toggling it takes effect immediately.
        var ipCfg = config.Settings.Collector.ExternalIp;
        if (ipCfg.Enabled)
        {
            if (_netChanged || DateTime.UtcNow >= _nextExternalIp)
            {
                _netChanged = false;
                _nextExternalIp = DateTime.UtcNow.AddMinutes(Math.Max(1, ipCfg.RefreshMinutes));
                _ = FetchExternalIpAsync(sink, ipCfg.Url);
            }
            sink.SetString(MetricNames.NetIpExternal, _externalIp);
        }
        else
        {
            _externalIp = "N/A";
            sink.MarkStale(MetricNames.NetIpExternal);
        }
    }

    private async Task FetchExternalIpAsync(MetricSink sink, string url)
    {
        try
        {
            string ip = (await _http.GetStringAsync(url).ConfigureAwait(false)).Trim();
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
