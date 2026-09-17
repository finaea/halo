using System.Runtime.InteropServices;
using System.Text;
using Halo.Metrics;
using Halo.Shared;

namespace Halo.Collector.Providers;

/// <summary>
/// GPU fast path via NVML (nvml.dll ships with the driver; ~0.2–1 ms per call, cap 20 Hz).
/// Covers temp/usage/VRAM/fan%/clocks/power for <b>every</b> NVIDIA device on the machine.
/// GPU voltage + fan RPM come from the LHM GPU part (NVAPI) — NVML has no public voltage API.
/// Vendor swap = replace this module (plan §13).
///
/// Metrics are published per device index (<c>gpu.0.temp.c</c>, …) with <c>gpu.count</c> saying
/// how many exist. NVML devices claim the low indexes, ordered by PCI bus id; LHM's AMD/Intel
/// devices continue the range. <see cref="GpuIndexSpace"/> owns that mapping (hardware plan H2).
/// </summary>
public sealed class NvmlProvider : ISensorProvider
{
    private static readonly ComponentLog Log2 = Log.For("nvml");

    public string Name => "nvml";
    public double MaxRateHz => 20;
    public double DefaultRateHz => CollectorRates.Nvml;
    public string? UnavailableReason => _unavailableReason;

    /// <summary>Initialize enumerates the NVML device list, so `rescan` re-runs it (a laptop
    /// dGPU that was powered off at boot shows up on the next one).</summary>
    public bool RescanReinitialises => true;

    private string? _unavailableReason = ProviderError.NoNvml;
    private bool _nvmlInited;

    /// <summary>One entry per published NVIDIA GPU, in halo-index order.</summary>
    private readonly List<Device> _devices = new();

    private sealed class Device
    {
        public int Index;              // halo index = gpu.<Index>.*
        public nint Handle;
        public string Name = "";
        public bool HasFanRpm;
        /// <summary>
        /// Reject-above threshold for power samples, in milliwatts (0 = no ceiling known).
        /// Waking from S3 makes NVML answer with garbage while the driver reinitializes — it
        /// returns NVML_SUCCESS and a nonsense reading (observed 2026-08-30 21:27:13, one second
        /// into a resume: 371,940 W on a card whose own limit is 310 W). MetricSink latches any
        /// sample as the session max forever, so one of those poisons gpu.power.w.max until a
        /// manual reset. The card reports its own limit, so the bound scales to any GPU.
        /// </summary>
        public ulong PowerCeilingMw;
        public DateTime NextRejectLog = DateTime.MinValue;
    }

    /// <summary>Headroom over the card's own limit. Generous on purpose: the point is to reject
    /// readings that are physically impossible (the observed one was 1,200x the limit), not to
    /// police plausible ones — a card can legitimately overshoot its limit briefly, and board
    /// partners' limits move around. 4x is far above anything real and far below anything bogus.</summary>
    private const uint PowerCeilingFactor = 4;

    /// <summary>One NVML device as the index space sees it, before handles are opened.</summary>
    internal readonly record struct NvDevice(uint NvmlIndex, string BusId, string Name);

    /// <summary>
    /// Enumerate every NVIDIA device, ordered by PCI bus id. Called once by
    /// <see cref="GpuIndexSpace"/>; safe when nvml.dll is absent (returns an empty list).
    /// Bus-id order rather than NVML's own index order, so the numbering does not move when the
    /// driver reorders devices between boots.
    /// </summary>
    internal static List<NvDevice> ProbeDevices()
    {
        var found = new List<NvDevice>();
        try
        {
            if (nvmlInit_v2() != 0) return found;
            if (nvmlDeviceGetCount_v2(out uint count) != 0) return found;
            for (uint i = 0; i < count; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out nint h) != 0) continue;
                var name = new StringBuilder(96);
                string devName = nvmlDeviceGetName(h, name, 96) == 0 ? name.ToString() : $"NVIDIA GPU {i}";
                string busId = "";
                var pci = new nvmlPciInfo();
                if (nvmlDeviceGetPciInfo_v3(h, ref pci) == 0) busId = PciBusId(ref pci);
                found.Add(new NvDevice(i, busId, devName));
            }
        }
        catch (DllNotFoundException) { /* no NVIDIA driver: not an error, just no devices */ }
        catch (Exception ex)
        {
            // Runs on whichever provider thread reaches the index space first, so it must never
            // throw into that provider's Initialize — an unreadable NVML just means no NVIDIA GPUs.
            Log2.Warn($"probe failed ({ex.GetType().Name}: {ex.Message}) — no NVIDIA GPUs published");
        }

        // Devices whose bus id we could not read sort last but keep their relative NVML order.
        found.Sort((a, b) =>
        {
            if (a.BusId.Length == 0 != (b.BusId.Length == 0)) return a.BusId.Length == 0 ? 1 : -1;
            int c = string.CompareOrdinal(a.BusId, b.BusId);
            return c != 0 ? c : a.NvmlIndex.CompareTo(b.NvmlIndex);
        });
        return found;
    }

    public bool Initialize(MetricSink sink)
    {
        int r = nvmlInit_v2();
        if (r != 0)
        {
            Log2.Warn($"nvmlInit_v2 -> {r}");
            _unavailableReason = ProviderError.NoNvml;
            return false;
        }
        _nvmlInited = true;

        // On a rescan this picks up a device that was not there at boot (laptop dGPU); the index
        // space only ever appends, so already-published gpu.<i>.* keep meaning the same card.
        if (_devices.Count > 0) GpuIndexSpace.Reprobe();

        var probed = GpuIndexSpace.NvidiaDevices();
        if (probed.Count == 0)
        {
            _unavailableReason = ProviderError.NoHardware;
            return false;
        }

        _devices.Clear();
        foreach (var (haloIndex, nvmlIndex, devName) in probed)
        {
            if (nvmlDeviceGetHandleByIndex_v2(nvmlIndex, out nint handle) != 0)
            {
                Log2.Warn($"no handle for device {nvmlIndex} ({devName}) — gpu.{haloIndex}.* stays N/A");
                continue;
            }
            // The fan-RPM API exists on newer drivers only; probe the export once per process.
            var d = new Device { Index = haloIndex, Handle = handle, Name = devName, HasFanRpm = HasFanRpmExport() };
            RegisterDevice(sink, d);
            _devices.Add(d);
        }
        if (_devices.Count == 0)
        {
            _unavailableReason = ProviderError.NoHardware;
            return false;
        }

        _unavailableReason = null;
        sink.Set(MetricNames.GpuCount, GpuIndexSpace.Count);
        return true;
    }

    private void RegisterDevice(MetricSink sink, Device d)
    {
        int i = d.Index;
        sink.Register(MetricNames.GpuCount, MetricType.Double, MetricUnit.Count, Name, 0, MetricSemantics.Static);
        sink.Register(MetricNames.GpuName(i), MetricType.String, MetricUnit.Text, Name, 0, MetricSemantics.Static);
        sink.Register(MetricNames.GpuVendor(i), MetricType.String, MetricUnit.Text, Name, 0, MetricSemantics.Static);
        sink.Register(MetricNames.GpuTempC(i), MetricType.Double, MetricUnit.Celsius, Name, DefaultRateHz);
        // NVML computes utilisation over its own sampling window, so polling faster does not make
        // this number fresher — the registry rate says so and the widget "?" popover repeats it.
        sink.Register(MetricNames.GpuUsagePct(i), MetricType.Double, MetricUnit.Percent, Name, DefaultRateHz, MetricSemantics.RollingWindow, windowMs: 1000);
        sink.Register(MetricNames.GpuVramUsedMb(i), MetricType.Double, MetricUnit.Megabytes, Name, DefaultRateHz);
        sink.Register(MetricNames.GpuVramTotalMb(i), MetricType.Double, MetricUnit.Megabytes, Name, 0, MetricSemantics.Static);
        sink.Register(MetricNames.GpuVramPct(i), MetricType.Double, MetricUnit.Percent, Name, DefaultRateHz, MetricSemantics.Calc);
        sink.Register(MetricNames.GpuFanPct(i), MetricType.Double, MetricUnit.Percent, Name, DefaultRateHz);
        sink.Register(MetricNames.GpuClockCoreMhz(i), MetricType.Double, MetricUnit.Megahertz, Name, DefaultRateHz);
        sink.Register(MetricNames.GpuClockMemMhz(i), MetricType.Double, MetricUnit.Megahertz, Name, DefaultRateHz);
        sink.RegisterWithMax(MetricNames.GpuPowerW(i), MetricUnit.Watts, Name, DefaultRateHz);
        if (d.HasFanRpm) sink.Register(MetricNames.GpuFanRpm(i), MetricType.Double, MetricUnit.Rpm, Name, DefaultRateHz);

        sink.SetString(MetricNames.GpuName(i), d.Name);
        sink.SetString(MetricNames.GpuVendor(i), "nvidia");
        // VRAM size is a property of the board: Static, published at discovery, not per poll.
        if (nvmlDeviceGetMemoryInfo(d.Handle, out var vram) == 0 && vram.total > 0)
            sink.Set(MetricNames.GpuVramTotalMb(i), vram.total / 1048576.0);

        // Prefer the constraints' upper bound over the currently-set limit: the user can raise the
        // power slider at runtime, and re-reading the limit on every poll would be a wasted call.
        if (nvmlDeviceGetPowerManagementLimitConstraints(d.Handle, out _, out uint maxLimitMw) == 0 && maxLimitMw > 0)
            d.PowerCeilingMw = (ulong)maxLimitMw * PowerCeilingFactor;
        else if (nvmlDeviceGetPowerManagementLimit(d.Handle, out uint limitMw) == 0 && limitMw > 0)
            d.PowerCeilingMw = (ulong)limitMw * PowerCeilingFactor;

        if (d.PowerCeilingMw > 0)
            Log2.Info($"gpu.{i} ({d.Name}): power ceiling {d.PowerCeilingMw / 1000.0:0.#} W ({PowerCeilingFactor}x card limit) — samples above are dropped");
        else
            Log2.Warn($"gpu.{i} ({d.Name}): power limit unavailable — power samples are unfiltered");
    }

    public void Poll(MetricSink sink)
    {
        foreach (var d in _devices) PollDevice(sink, d);
    }

    /// <summary>
    /// Every read here is a sensor read, so a non-zero NVML return code means "this number could
    /// not be taken" — the metric goes N/A for this poll rather than keeping the previous sample
    /// with a fresh-looking timestamp. Per-sensor only: a single failure never reinitialises the
    /// device, and a provider-wide outage is the host's job (<see cref="ProviderHost"/>).
    ///
    /// <b>Fan speed is the one to read carefully.</b> A successful call returning 0 is a real
    /// reading — zero-RPM / fan-stop mode on a modern card genuinely reports 0 — so it is
    /// published as 0. Only the failed call is N/A.
    /// </summary>
    private static void PollDevice(MetricSink sink, Device d)
    {
        int i = d.Index;
        if (nvmlDeviceGetTemperature(d.Handle, 0 /*GPU*/, out uint temp) == 0)
            sink.Set(MetricNames.GpuTempC(i), temp);
        else
            sink.MarkStale(MetricNames.GpuTempC(i));

        if (nvmlDeviceGetUtilizationRates(d.Handle, out var util) == 0)
            sink.Set(MetricNames.GpuUsagePct(i), util.gpu);
        else
            sink.MarkStale(MetricNames.GpuUsagePct(i));

        if (nvmlDeviceGetMemoryInfo(d.Handle, out var mem) == 0 && mem.total > 0)
        {
            double usedMb = mem.used / 1048576.0, totalMb = mem.total / 1048576.0;
            sink.Set(MetricNames.GpuVramUsedMb(i), usedMb);
            sink.Set(MetricNames.GpuVramPct(i), usedMb / totalMb * 100);
        }
        else
        {
            sink.MarkStale(MetricNames.GpuVramUsedMb(i));
            sink.MarkStale(MetricNames.GpuVramPct(i));
        }

        // 0% is a stopped fan, which is a reading; a failed call is not.
        if (nvmlDeviceGetFanSpeed_v2(d.Handle, 0, out uint fanPct) == 0)
            sink.Set(MetricNames.GpuFanPct(i), fanPct);
        else
            sink.MarkStale(MetricNames.GpuFanPct(i));

        if (d.HasFanRpm)
        {
            var info = new nvmlFanSpeedInfo { version = 0x1000000 | (uint)Marshal.SizeOf<nvmlFanSpeedInfo>(), fan = 0 };
            // Same rule: info.speed == 0 means the fan is stopped, not that we failed to read it.
            if (nvmlDeviceGetFanSpeedRPM(d.Handle, ref info) == 0)
                sink.Set(MetricNames.GpuFanRpm(i), info.speed);
            else
                sink.MarkStale(MetricNames.GpuFanRpm(i));
        }

        if (nvmlDeviceGetClockInfo(d.Handle, 0 /*GRAPHICS*/, out uint core) == 0)
            sink.Set(MetricNames.GpuClockCoreMhz(i), core);
        else
            sink.MarkStale(MetricNames.GpuClockCoreMhz(i));
        if (nvmlDeviceGetClockInfo(d.Handle, 2 /*MEM*/, out uint memclk) == 0)
            sink.Set(MetricNames.GpuClockMemMhz(i), memclk);
        else
            sink.MarkStale(MetricNames.GpuClockMemMhz(i));

        if (nvmlDeviceGetPowerUsage(d.Handle, out uint mw) == 0)
        {
            // Drop the sample entirely rather than just keeping it out of the max: a bogus value
            // published as the live reading would flash on the panel for a frame anyway.
            if (d.PowerCeilingMw > 0 && mw > d.PowerCeilingMw)
            {
                if (DateTime.UtcNow >= d.NextRejectLog)
                {
                    d.NextRejectLog = DateTime.UtcNow.AddMinutes(1);
                    Log2.Warn($"gpu.{i}: power sample rejected: {mw / 1000.0:0.#} W > ceiling {d.PowerCeilingMw / 1000.0:0.#} W");
                }
            }
            else
            {
                sink.Set(MetricNames.GpuPowerW(i), mw / 1000.0);
            }
        }
        else
        {
            sink.MarkStale(MetricNames.GpuPowerW(i));
        }
    }

    private static bool? _fanRpmExport;

    private static bool HasFanRpmExport()
        => _fanRpmExport ??= NativeLibrary.TryLoad("nvml.dll", out nint lib) &&
                             NativeLibrary.TryGetExport(lib, "nvmlDeviceGetFanSpeedRPM", out _);

    /// <summary>"domain:bus:device.function" from the NVML PCI record — the same shape nvidia-smi
    /// prints, and what makes the index order reproducible.</summary>
    private static string PciBusId(ref nvmlPciInfo pci)
        => $"{pci.domain:x4}:{pci.bus:x2}:{pci.device:x2}";

    public void Dispose()
    {
        _devices.Clear();
        if (_nvmlInited) { try { nvmlShutdown(); } catch { } }
        _nvmlInited = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct nvmlUtilization { public uint gpu, memory; }

    [StructLayout(LayoutKind.Sequential)]
    private struct nvmlMemory { public ulong total, free, used; }

    [StructLayout(LayoutKind.Sequential)]
    private struct nvmlFanSpeedInfo { public uint version; public uint fan; public uint speed; }

    // nvmlPciInfo_v2_t: busIdLegacy[16], domain, bus, device, pciDeviceId, pciSubSystemId,
    // busId[32]. Only the numeric triple is read; the char arrays are kept so the size matches
    // what the driver writes.
    [StructLayout(LayoutKind.Sequential)]
    private struct nvmlPciInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] busIdLegacy;
        public uint domain, bus, device, pciDeviceId, pciSubSystemId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] busId;

        public nvmlPciInfo() { busIdLegacy = new byte[16]; busId = new byte[32]; }
    }

    [DllImport("nvml")] private static extern int nvmlInit_v2();
    [DllImport("nvml")] private static extern int nvmlShutdown();
    [DllImport("nvml")] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);
    [DllImport("nvml", CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(nint device, StringBuilder name, uint length);
    [DllImport("nvml")] private static extern int nvmlDeviceGetPciInfo_v3(nint device, ref nvmlPciInfo pci);
    [DllImport("nvml")] private static extern int nvmlDeviceGetTemperature(nint device, int sensorType, out uint temp);
    [DllImport("nvml")] private static extern int nvmlDeviceGetUtilizationRates(nint device, out nvmlUtilization util);
    [DllImport("nvml")] private static extern int nvmlDeviceGetMemoryInfo(nint device, out nvmlMemory mem);
    [DllImport("nvml")] private static extern int nvmlDeviceGetFanSpeed_v2(nint device, uint fan, out uint speedPct);
    [DllImport("nvml")] private static extern int nvmlDeviceGetFanSpeedRPM(nint device, ref nvmlFanSpeedInfo info);
    [DllImport("nvml")] private static extern int nvmlDeviceGetClockInfo(nint device, int clockType, out uint mhz);
    [DllImport("nvml")] private static extern int nvmlDeviceGetPowerUsage(nint device, out uint milliwatts);
    // "the power management limit ... in milliwatts. The power limit defines the upper boundary
    // for the card's power draw" — same units as nvmlDeviceGetPowerUsage, so no conversion needed
    [DllImport("nvml")] private static extern int nvmlDeviceGetPowerManagementLimit(nint device, out uint limitMw);
    [DllImport("nvml")] private static extern int nvmlDeviceGetPowerManagementLimitConstraints(nint device, out uint minLimitMw, out uint maxLimitMw);
}
