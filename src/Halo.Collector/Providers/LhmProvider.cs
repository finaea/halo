using LibreHardwareMonitor.Hardware;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Shared.Metrics;

namespace Halo.Collector.Providers;

/// <summary>
/// LibreHardwareMonitorLib-backed sensors, split into independently-scheduled parts
/// (plan §5): Cpu = MSR (fast ioctl, cap 20 Hz) · SuperIo = NCT6687D fans + Vcore
/// (~ms port I/O behind the ISA mutex, cap 2 Hz) · Storage = SMART/NVMe temps (10s of ms
/// per drive, cap 0.2 Hz) · Gpu = NVAPI extras NVML can't provide (voltage, fan RPM).
/// Requires elevation for Cpu/SuperIo/Storage (PawnIO/ring0); Gpu works unelevated.
/// </summary>
public sealed class LhmProvider : ISensorProvider
{
    public enum Part { Cpu, SuperIo, Storage, Gpu }

    private readonly Part _part;
    private readonly GeneralSettings? _settings;
    private Computer? _computer;

    public LhmProvider(Part part, GeneralSettings? settings = null)
    {
        _part = part;
        _settings = settings;
    }

    public string Name => $"lhm-{_part.ToString().ToLowerInvariant()}";

    public double MaxRateHz => _part switch
    {
        Part.Cpu => 20,
        Part.SuperIo => 2,
        Part.Storage => 0.2,
        Part.Gpu => 2,
        _ => 1,
    };

    public double DefaultRateHz => _part switch
    {
        Part.Cpu => 10,
        Part.SuperIo => 1,
        Part.Storage => 1.0 / 30,
        Part.Gpu => 1,
        _ => 1,
    };

    public bool Initialize(MetricSink sink)
    {
        _computer?.Close();
        _computer = new Computer
        {
            IsCpuEnabled = _part == Part.Cpu,
            IsMotherboardEnabled = _part == Part.SuperIo,
            IsStorageEnabled = _part == Part.Storage,
            IsGpuEnabled = _part == Part.Gpu,
        };
        _computer.Open();

        bool any = false;
        foreach (var hw in AllHardware())
        {
            hw.Update();
            any = true;
        }
        if (!any)
        {
            Log.Warn($"{Name}: no hardware found (needs elevation / PawnIO?)");
            _computer.Close();
            _computer = null;
            return false;
        }

        switch (_part)
        {
            case Part.Cpu:
                sink.Register(MetricNames.CpuName, MetricType.String, MetricUnit.Text, Name, 1);
                sink.Register(MetricNames.CpuPackageTempC, MetricType.Double, MetricUnit.Celsius, Name, MaxRateHz);
                sink.RegisterWithMax(MetricNames.CpuPackagePowerW, MetricUnit.Watts, Name, MaxRateHz);
                sink.Register(MetricNames.CpuClockMhz, MetricType.Double, MetricUnit.Megahertz, Name, MaxRateHz);
                break;
            case Part.SuperIo:
                sink.RegisterWithMax(MetricNames.CpuVcoreV, MetricUnit.Volts, Name, MaxRateHz);
                for (int i = 0; i < 8; i++)
                {
                    sink.Register(MetricNames.FanRpm(i), MetricType.Double, MetricUnit.Rpm, Name, MaxRateHz);
                    sink.Register(MetricNames.FanPct(i), MetricType.Double, MetricUnit.Percent, Name, MaxRateHz);
                    sink.Register(MetricNames.FanName(i), MetricType.String, MetricUnit.Text, Name, 1);
                }
                break;
            case Part.Storage:
                foreach (char c in DriveLetters())
                    sink.Register(MetricNames.DriveTempC(c), MetricType.Double, MetricUnit.Celsius, Name, MaxRateHz);
                break;
            case Part.Gpu:
                sink.RegisterWithMax(MetricNames.GpuVoltageV, MetricUnit.Volts, Name, MaxRateHz);
                sink.Register(MetricNames.GpuFanRpm, MetricType.Double, MetricUnit.Rpm, Name, MaxRateHz);
                break;
        }

        Log.Info($"{Name}: hardware = {string.Join("; ", AllHardware().Select(h => $"{h.HardwareType}:{h.Name}"))}");
        return true;
    }

    private IEnumerable<IHardware> AllHardware()
    {
        if (_computer == null) yield break;
        foreach (var hw in _computer.Hardware)
        {
            yield return hw;
            foreach (var sub in hw.SubHardware) yield return sub;
        }
    }

    private IEnumerable<char> DriveLetters()
        => (_settings?.DriveLetters ?? ["C"]).Select(s => char.ToUpperInvariant(s[0]));

    public void Poll(MetricSink sink)
    {
        if (_computer == null) return;
        foreach (var hw in AllHardware()) hw.Update();

        switch (_part)
        {
            case Part.Cpu: PollCpu(sink); break;
            case Part.SuperIo: PollSuperIo(sink); break;
            case Part.Storage: PollStorage(sink); break;
            case Part.Gpu: PollGpu(sink); break;
        }
    }

    private void PollCpu(MetricSink sink)
    {
        foreach (var hw in AllHardware().Where(h => h.HardwareType == HardwareType.Cpu))
        {
            sink.SetString(MetricNames.CpuName, hw.Name);
            double maxCoreClock = 0;
            foreach (var s in hw.Sensors)
            {
                if (s.Value is not { } v || float.IsNaN((float)v)) continue;
                switch (s.SensorType)
                {
                    case SensorType.Temperature when v > 0 && s.Name is "CPU Package" or "Core (Tctl/Tdie)":
                        sink.Set(MetricNames.CpuPackageTempC, v);
                        break;
                    case SensorType.Power when v > 0 && s.Name is "CPU Package" or "Package":
                        sink.Set(MetricNames.CpuPackagePowerW, v);
                        break;
                    case SensorType.Clock when s.Name.StartsWith("CPU Core", StringComparison.Ordinal):
                        if (v > maxCoreClock) maxCoreClock = v;
                        break;
                }
            }
            if (maxCoreClock > 0) sink.Set(MetricNames.CpuClockMhz, maxCoreClock);
        }
    }

    private void PollSuperIo(MetricSink sink)
    {
        foreach (var hw in AllHardware().Where(h => h.HardwareType == HardwareType.SuperIO))
        {
            int fanIdx = 0;
            foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Fan).OrderBy(s => s.Identifier.ToString()))
            {
                if (fanIdx >= 8) break;
                double rpm = s.Value is { } v && !float.IsNaN((float)v) ? v : 0;
                sink.Set(MetricNames.FanRpm(fanIdx), rpm);
                sink.SetString(MetricNames.FanName(fanIdx), s.Name);
                double maxRpm = MaxRpmFor(fanIdx);
                sink.Set(MetricNames.FanPct(fanIdx), maxRpm > 0 ? Math.Clamp(rpm / maxRpm * 100, 0, 100) : 0);
                fanIdx++;
            }

            var vcore = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Voltage &&
                            (s.Name.Contains("core", StringComparison.OrdinalIgnoreCase) || s.Name == "VIN0"));
            if (vcore?.Value is { } vc && !float.IsNaN((float)vc))
                sink.Set(MetricNames.CpuVcoreV, vc);
        }
    }

    private double MaxRpmFor(int channel)
    {
        if (_settings != null && _settings.FanMaxRpm.TryGetValue(channel.ToString(), out double v) && v > 0) return v;
        return 2000;
    }

    private Dictionary<char, string>? _letterToModel;

    private void PollStorage(MetricSink sink)
    {
        _letterToModel ??= DriveMap.LetterToModel(DriveLetters());

        var tempByModel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var hw in AllHardware().Where(h => h.HardwareType == HardwareType.Storage))
        {
            var temp = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
            if (temp?.Value is { } t && !float.IsNaN((float)t))
                tempByModel[hw.Name.Trim()] = t;
        }

        foreach (var (letter, model) in _letterToModel)
        {
            // model strings may differ slightly (vendor prefix, size suffix) between the
            // IOCTL descriptor and LHM's name — match on containment either way
            var match = tempByModel.FirstOrDefault(kv =>
                kv.Key.Contains(model, StringComparison.OrdinalIgnoreCase) ||
                model.Contains(kv.Key, StringComparison.OrdinalIgnoreCase));
            if (match.Key != null)
                sink.Set(MetricNames.DriveTempC(letter), match.Value);
            else
                sink.MarkStale(MetricNames.DriveTempC(letter));
        }
    }

    private void PollGpu(MetricSink sink)
    {
        foreach (var hw in AllHardware().Where(h => h.HardwareType == HardwareType.GpuNvidia))
        {
            var volt = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Voltage);
            if (volt?.Value is { } v && !float.IsNaN((float)v))
                sink.Set(MetricNames.GpuVoltageV, v);

            var fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
            if (fan?.Value is { } f && !float.IsNaN((float)f))
                sink.Set(MetricNames.GpuFanRpm, f);
        }
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch { }
        _computer = null;
    }
}
