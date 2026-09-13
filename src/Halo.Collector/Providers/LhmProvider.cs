using LibreHardwareMonitor.Hardware;
using Halo.Metrics;
using Halo.Shared;

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
    private Computer? _computer;
    private string? _unavailableReason;

    public LhmProvider(Part part) => _part = part;

    public string Name => $"lhm-{_part.ToString().ToLowerInvariant()}";

    /// <summary>Everything except the GPU part reads MSRs / port I/O / SMART through PawnIO.</summary>
    public bool NeedsElevation => _part != Part.Gpu;

    public string? UnavailableReason => _unavailableReason;

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
        // 5 Hz, not 10: at 10 Hz the MSR sweep overruns its period on 2.78 % of polls
        // (docs\current-metrics-inventory.md § Provider cost measurement, rates plan R1).
        Part.Cpu => 5,
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
            // Distinguish "this PC has no such hardware" from "we cannot reach it": the System
            // check turns these codes into a sentence the user can act on.
            _unavailableReason = NeedsElevation && !Elevation.IsElevated ? ProviderError.Unelevated
                : NeedsElevation && !PawnIoInstalled() ? ProviderError.NoDriver
                : ProviderError.NoHardware;
            Log.Warn($"{Name}: no hardware found ({_unavailableReason})");
            _computer.Close();
            _computer = null;
            return false;
        }
        _unavailableReason = null;

        switch (_part)
        {
            case Part.Cpu:
                sink.Register(MetricNames.CpuName, MetricType.String, MetricUnit.Text, Name, 0, MetricSemantics.Static);
                sink.Register(MetricNames.CpuPackageTempC, MetricType.Double, MetricUnit.Celsius, Name, DefaultRateHz,
                    flags: MetricFlags.NeedsElevation);
                sink.RegisterWithMax(MetricNames.CpuPackagePowerW, MetricUnit.Watts, Name, DefaultRateHz,
                    flags: MetricFlags.NeedsElevation);
                sink.Register(MetricNames.CpuClockMhz, MetricType.Double, MetricUnit.Megahertz, Name, DefaultRateHz,
                    flags: MetricFlags.NeedsElevation);
                break;
            case Part.SuperIo:
                sink.RegisterWithMax(MetricNames.CpuVcoreV, MetricUnit.Volts, Name, DefaultRateHz,
                    flags: MetricFlags.NeedsElevation);
                sink.Register(MetricNames.FanCount, MetricType.Double, MetricUnit.Count, Name, 0, MetricSemantics.Static);
                // Channels are registered as they are discovered in the first poll — the count
                // is a property of the board, not a constant (hardware plan H1).
                break;
            case Part.Storage:
                foreach (char c in Volumes.Local())
                    sink.Register(MetricNames.DriveTempC(c), MetricType.Double, MetricUnit.Celsius, Name, DefaultRateHz,
                        flags: MetricFlags.NeedsElevation);
                break;
            case Part.Gpu:
                sink.RegisterWithMax(MetricNames.GpuVoltageV(0), MetricUnit.Volts, Name, DefaultRateHz);
                sink.Register(MetricNames.GpuFanRpm(0), MetricType.Double, MetricUnit.Rpm, Name, DefaultRateHz);
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

    private static bool PawnIoInstalled()
    {
        try { return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
        catch { return false; }
    }

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
                    case SensorType.Clock when !s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase):
                        // core clocks are named "CPU Core #N" (or "Core #N" depending on LHM
                        // version); take the max of everything that isn't the bus clock
                        if (v > maxCoreClock) maxCoreClock = v;
                        break;
                }
            }
            if (maxCoreClock > 0) sink.Set(MetricNames.CpuClockMhz, maxCoreClock);
        }
    }

    /// <summary>
    /// Publishes every fan channel the board exposes (v1 stopped at 8 and needed a config list).
    /// A fan's percentage is no longer computed here: the duty cycle the chip itself reports
    /// (<c>fan.&lt;n&gt;.control.pct</c>) is published when a paired Control sensor exists, and
    /// widgets fall back to rpm ÷ max otherwise — no board-specific max table in the collector
    /// (hardware plan H4).
    /// </summary>
    private void PollSuperIo(MetricSink sink)
    {
        foreach (var hw in AllHardware().Where(h => h.HardwareType == HardwareType.SuperIO))
        {
            // LHM names them "Fan #N" / "Fan Control #N"; pair on the trailing number, else by order.
            var controls = hw.Sensors.Where(s => s.SensorType == SensorType.Control)
                                     .OrderBy(s => s.Identifier.ToString()).ToList();

            int fanIdx = 0;
            foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Fan).OrderBy(s => s.Identifier.ToString()))
            {
                if (fanIdx >= MaxFanChannels) break;
                if (!_registeredFans.Contains(fanIdx))
                {
                    sink.RegisterWithMax(MetricNames.FanRpm(fanIdx), MetricUnit.Rpm, Name, DefaultRateHz,
                        flags: MetricFlags.NeedsElevation);
                    sink.Register(MetricNames.FanName(fanIdx), MetricType.String, MetricUnit.Text, Name, 0, MetricSemantics.Static);
                    _registeredFans.Add(fanIdx);
                    sink.Set(MetricNames.FanCount, _registeredFans.Count);
                }

                double rpm = s.Value is { } v && !float.IsNaN((float)v) ? v : 0;
                sink.Set(MetricNames.FanRpm(fanIdx), rpm);
                sink.SetString(MetricNames.FanName(fanIdx), s.Name);

                var control = MatchControl(controls, s, fanIdx);
                if (control?.Value is { } duty && !float.IsNaN((float)duty))
                {
                    if (!_registeredControls.Contains(fanIdx))
                    {
                        sink.Register(MetricNames.FanControlPct(fanIdx), MetricType.Double, MetricUnit.Percent, Name,
                            DefaultRateHz, flags: MetricFlags.NeedsElevation);
                        _registeredControls.Add(fanIdx);
                    }
                    sink.Set(MetricNames.FanControlPct(fanIdx), Math.Clamp(duty, 0, 100));
                }
                fanIdx++;
            }

            var vcore = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Voltage &&
                            (s.Name.Contains("core", StringComparison.OrdinalIgnoreCase) || s.Name == "VIN0"));
            if (vcore?.Value is { } vc && !float.IsNaN((float)vc))
                sink.Set(MetricNames.CpuVcoreV, vc);
        }
    }

    private const int MaxFanChannels = 16;
    private readonly HashSet<int> _registeredFans = new();
    private readonly HashSet<int> _registeredControls = new();

    private static ISensor? MatchControl(List<ISensor> controls, ISensor fan, int fanIdx)
    {
        int? fanNumber = TrailingNumber(fan.Name) ?? TrailingNumber(fan.Identifier.ToString());
        if (fanNumber is { } n)
        {
            var byNumber = controls.FirstOrDefault(c =>
                (TrailingNumber(c.Name) ?? TrailingNumber(c.Identifier.ToString())) == n);
            if (byNumber != null) return byNumber;
        }
        return fanIdx < controls.Count ? controls[fanIdx] : null;
    }

    private static int? TrailingNumber(string s)
    {
        int end = s.Length;
        while (end > 0 && !char.IsDigit(s[end - 1])) end--;
        if (end == 0) return null;
        int start = end;
        while (start > 0 && char.IsDigit(s[start - 1])) start--;
        return int.TryParse(s.AsSpan(start, end - start), out int n) ? n : null;
    }

    private Dictionary<char, string>? _letterToModel;
    private string _storageLetters = "";

    private void PollStorage(MetricSink sink)
    {
        // Hot-plug: when the set of volumes changes, register temp metrics for any newly-seen
        // volume and invalidate the letter→disk-model map so it is rebuilt to include the new
        // drive (temps otherwise stay N/A until the collector restarts).
        var volumes = Volumes.Local();
        string live = Volumes.Key(volumes);
        if (live != _storageLetters)
        {
            foreach (char c in volumes)
                sink.Register(MetricNames.DriveTempC(c), MetricType.Double, MetricUnit.Celsius, Name, DefaultRateHz,
                    flags: MetricFlags.NeedsElevation);
            _letterToModel = null;
            _unmatchedLogged.Clear();
            _storageLetters = live;
        }

        if (_letterToModel == null)
        {
            _letterToModel = DriveMap.LetterToModel(volumes);
            var missing = volumes.Where(l => !_letterToModel.ContainsKey(l)).ToList();
            if (missing.Count > 0)
                Log.Warn($"lhm-storage: no disk-model descriptor for volume(s) {string.Join(",", missing)} — temps will read N/A (drive likely reports empty vendor/product strings)");
        }

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
            // IOCTL descriptor and LHM's name — match on containment either way, then on
            // the first whitespace-stripped token overlap as a last resort
            var match = tempByModel.FirstOrDefault(kv =>
                kv.Key.Contains(model, StringComparison.OrdinalIgnoreCase) ||
                model.Contains(kv.Key, StringComparison.OrdinalIgnoreCase));
            if (match.Key == null)
            {
                string squished = model.Replace(" ", "");
                match = tempByModel.FirstOrDefault(kv =>
                    kv.Key.Replace(" ", "").Contains(squished, StringComparison.OrdinalIgnoreCase) ||
                    squished.Contains(kv.Key.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            }
            if (match.Key != null)
            {
                sink.Set(MetricNames.DriveTempC(letter), match.Value);
            }
            else
            {
                if (!_unmatchedLogged.Contains(letter))
                {
                    _unmatchedLogged.Add(letter);
                    Log.Warn($"lhm-storage: no LHM match for {letter}: descriptor model '{model}'; LHM names: {string.Join(" | ", tempByModel.Keys)}");
                }
                sink.MarkStale(MetricNames.DriveTempC(letter));
            }
        }
    }

    private readonly HashSet<char> _unmatchedLogged = new();

    private void PollGpu(MetricSink sink)
    {
        foreach (var hw in AllHardware().Where(h => h.HardwareType == HardwareType.GpuNvidia))
        {
            var volt = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Voltage);
            if (volt?.Value is { } v && !float.IsNaN((float)v))
                sink.Set(MetricNames.GpuVoltageV(0), v);

            var fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
            if (fan?.Value is { } f && !float.IsNaN((float)f))
                sink.Set(MetricNames.GpuFanRpm(0), f);
        }
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch { }
        _computer = null;
    }
}
