using System.Runtime.InteropServices;
using System.Text;
using Halo.Shared;
using Halo.Shared.Metrics;

namespace Halo.Collector.Providers;

/// <summary>
/// GPU fast path via NVML (nvml.dll ships with the driver; ~0.2–1 ms per call, cap 20 Hz).
/// Covers temp/usage/VRAM/fan%/clocks/power. GPU voltage + fan RPM come from the LHM GPU
/// part (NVAPI) — NVML has no public voltage API. Vendor swap = replace this module (plan §13).
/// </summary>
public sealed class NvmlProvider : ISensorProvider
{
    public string Name => "nvml";
    public double MaxRateHz => 20;
    public double DefaultRateHz => 10;

    private nint _device;
    private bool _nvmlInited;
    private bool _hasFanRpm;

    /// <summary>
    /// Reject-above threshold for power samples, in milliwatts (0 = no ceiling known, accept all).
    /// Waking from S3 makes NVML answer with garbage while the driver reinitializes — it returns
    /// NVML_SUCCESS and a nonsense reading (observed 2026-08-30 21:27:13, one second into a resume:
    /// 371,940 W on a card whose own limit is 310 W). MetricSink latches any sample as the session
    /// max forever, so a single one of those poisons gpu.power.w.max until a manual reset.
    /// The card reports its own limit, so the bound scales to whatever GPU is installed.
    /// </summary>
    private ulong _powerCeilingMw;   // ulong: limit x factor would overflow uint if NVML ever hands back a garbage limit too

    /// <summary>Headroom over the card's own limit. Generous on purpose: the point is to reject
    /// readings that are physically impossible (the observed one was 1,200x the limit), not to
    /// police plausible ones — a card can legitimately overshoot its limit briefly, and board
    /// partners' limits move around. 4x is far above anything real and far below anything bogus.</summary>
    private const uint PowerCeilingFactor = 4;

    private DateTime _nextRejectLog = DateTime.MinValue;

    public bool Initialize(MetricSink sink)
    {
        int r = nvmlInit_v2();
        if (r != 0) { Log.Warn($"nvmlInit_v2 -> {r}"); return false; }
        _nvmlInited = true;

        if (nvmlDeviceGetCount_v2(out uint count) != 0 || count == 0) return false;
        if (nvmlDeviceGetHandleByIndex_v2(0, out _device) != 0) return false;

        var name = new StringBuilder(96);
        if (nvmlDeviceGetName(_device, name, 96) == 0)
            Log.Info($"nvml device 0: {name}");

        sink.Register(MetricNames.GpuName, MetricType.String, MetricUnit.Text, Name, 1);
        sink.Register(MetricNames.GpuTempC, MetricType.Double, MetricUnit.Celsius, Name, MaxRateHz);
        sink.Register(MetricNames.GpuUsagePct, MetricType.Double, MetricUnit.Percent, Name, MaxRateHz);
        sink.Register(MetricNames.GpuVramUsedMb, MetricType.Double, MetricUnit.Megabytes, Name, MaxRateHz);
        sink.Register(MetricNames.GpuVramTotalMb, MetricType.Double, MetricUnit.Megabytes, Name, MaxRateHz);
        sink.Register(MetricNames.GpuVramPct, MetricType.Double, MetricUnit.Percent, Name, MaxRateHz);
        sink.Register(MetricNames.GpuFanPct, MetricType.Double, MetricUnit.Percent, Name, MaxRateHz);
        sink.Register(MetricNames.GpuClockCoreMhz, MetricType.Double, MetricUnit.Megahertz, Name, MaxRateHz);
        sink.Register(MetricNames.GpuClockMemMhz, MetricType.Double, MetricUnit.Megahertz, Name, MaxRateHz);
        sink.RegisterWithMax(MetricNames.GpuPowerW, MetricUnit.Watts, Name, MaxRateHz);

        // Prefer the constraints' upper bound over the currently-set limit: the user can raise the
        // power slider at runtime, and re-reading the limit on every poll would be a wasted call.
        if (nvmlDeviceGetPowerManagementLimitConstraints(_device, out _, out uint maxLimitMw) == 0 && maxLimitMw > 0)
            _powerCeilingMw = (ulong)maxLimitMw * PowerCeilingFactor;
        else if (nvmlDeviceGetPowerManagementLimit(_device, out uint limitMw) == 0 && limitMw > 0)
            _powerCeilingMw = (ulong)limitMw * PowerCeilingFactor;

        if (_powerCeilingMw > 0)
            Log.Info($"nvml power ceiling: {_powerCeilingMw / 1000.0:0.#} W ({PowerCeilingFactor}x card limit) — samples above are dropped");
        else
            Log.Warn("nvml power limit unavailable — power samples are unfiltered");

        // fan RPM API exists on newer drivers only
        _hasFanRpm = NativeLibrary.TryLoad("nvml.dll", out nint lib) && NativeLibrary.TryGetExport(lib, "nvmlDeviceGetFanSpeedRPM", out _);
        if (_hasFanRpm) sink.Register(MetricNames.GpuFanRpm, MetricType.Double, MetricUnit.Rpm, Name, MaxRateHz);

        sink.SetString(MetricNames.GpuName, name.ToString());
        return true;
    }

    public void Poll(MetricSink sink)
    {
        if (nvmlDeviceGetTemperature(_device, 0 /*GPU*/, out uint temp) == 0)
            sink.Set(MetricNames.GpuTempC, temp);

        if (nvmlDeviceGetUtilizationRates(_device, out var util) == 0)
            sink.Set(MetricNames.GpuUsagePct, util.gpu);

        if (nvmlDeviceGetMemoryInfo(_device, out var mem) == 0 && mem.total > 0)
        {
            double usedMb = mem.used / 1048576.0, totalMb = mem.total / 1048576.0;
            sink.Set(MetricNames.GpuVramUsedMb, usedMb);
            sink.Set(MetricNames.GpuVramTotalMb, totalMb);
            sink.Set(MetricNames.GpuVramPct, usedMb / totalMb * 100);
        }

        if (nvmlDeviceGetFanSpeed_v2(_device, 0, out uint fanPct) == 0)
            sink.Set(MetricNames.GpuFanPct, fanPct);

        if (_hasFanRpm)
        {
            var info = new nvmlFanSpeedInfo { version = 0x1000000 | (uint)Marshal.SizeOf<nvmlFanSpeedInfo>(), fan = 0 };
            if (nvmlDeviceGetFanSpeedRPM(_device, ref info) == 0)
                sink.Set(MetricNames.GpuFanRpm, info.speed);
        }

        if (nvmlDeviceGetClockInfo(_device, 0 /*GRAPHICS*/, out uint core) == 0)
            sink.Set(MetricNames.GpuClockCoreMhz, core);
        if (nvmlDeviceGetClockInfo(_device, 2 /*MEM*/, out uint memclk) == 0)
            sink.Set(MetricNames.GpuClockMemMhz, memclk);

        if (nvmlDeviceGetPowerUsage(_device, out uint mw) == 0)
        {
            // Drop the sample entirely rather than just keeping it out of the max: a bogus value
            // published as the live reading would flash on the panel for a frame anyway.
            if (_powerCeilingMw > 0 && mw > _powerCeilingMw)
            {
                if (DateTime.UtcNow >= _nextRejectLog)
                {
                    _nextRejectLog = DateTime.UtcNow.AddMinutes(1);
                    Log.Warn($"nvml power sample rejected: {mw / 1000.0:0.#} W > ceiling {_powerCeilingMw / 1000.0:0.#} W");
                }
            }
            else
            {
                sink.Set(MetricNames.GpuPowerW, mw / 1000.0);
            }
        }
    }

    public void Dispose()
    {
        if (_nvmlInited) { try { nvmlShutdown(); } catch { } }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct nvmlUtilization { public uint gpu, memory; }

    [StructLayout(LayoutKind.Sequential)]
    private struct nvmlMemory { public ulong total, free, used; }

    [StructLayout(LayoutKind.Sequential)]
    private struct nvmlFanSpeedInfo { public uint version; public uint fan; public uint speed; }

    [DllImport("nvml")] private static extern int nvmlInit_v2();
    [DllImport("nvml")] private static extern int nvmlShutdown();
    [DllImport("nvml")] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);
    [DllImport("nvml", CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(nint device, StringBuilder name, uint length);
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
