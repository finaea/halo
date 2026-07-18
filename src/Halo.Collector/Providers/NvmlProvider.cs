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
            sink.Set(MetricNames.GpuPowerW, mw / 1000.0);
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
}
