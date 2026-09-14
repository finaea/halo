using LibreHardwareMonitor.Hardware;
using Halo.Metrics;
using Halo.Shared;

namespace Halo.Collector.Providers;

/// <summary>
/// LibreHardwareMonitorLib-backed sensors, split into independently-scheduled parts
/// (plan §5): Cpu = MSR (fast ioctl, cap 20 Hz) · SuperIo = every fan channel the board exposes,
/// plus Vcore (~ms port I/O behind the ISA mutex, cap 2 Hz) · Storage = SMART/NVMe temps (10s of
/// ms per drive, cap 0.2 Hz) · Gpu = the NVAPI/ADL extras NVML cannot provide on an NVIDIA card,
/// and the whole gpu.&lt;i&gt;.* family on an AMD or Intel one (hardware plan H2).
/// Requires elevation for Cpu/SuperIo/Storage (PawnIO/ring0); Gpu works unelevated.
/// </summary>
public sealed class LhmProvider : ISensorProvider
{
    public enum Part { Cpu, SuperIo, Storage, Gpu }

    private readonly Part _part;
    private Computer? _computer;
    private string? _unavailableReason;

    /// <summary>
    /// Serialises LibreHardwareMonitor's <b>process-global</b> plumbing across the four parts.
    /// Every LHM call in this file goes through it; none may be made outside it.
    ///
    /// LHM 0.9.6's <c>Computer.Open()</c>/<c>Close()</c> call <c>OpCode.Open()</c>/<c>Close()</c>
    /// and <c>Mutexes.Open()</c>/<c>Close()</c> unconditionally, and <c>OpCode</c> is an
    /// <c>internal static</c> class with no reference counting: <c>Open()</c> VirtualAllocs one
    /// PAGE_EXECUTE_READWRITE page holding the hand-written rdtsc/cpuid stubs and points the
    /// static <c>Rdtsc</c>/<c>CpuId</c> delegates at it, <c>Close()</c> nulls both delegates and
    /// MEM_RELEASEs the page. Only <c>Computer._open</c> is per-instance; the page is not.
    ///
    /// Halo runs four Computers on four provider threads (Program.cs), so without this gate one
    /// part's Close breaks every other live part: a null delegate surfaces as a
    /// NullReferenceException out of GenericCpu.Update or
    /// GenericCpu.EstimateTimeStampCounterFrequency, and freeing the page while another thread is
    /// executing inside it is an 0xC0000005 — uncatchable, process gone (diagnosed 2026-09-14).
    ///
    /// Write = the lifecycle calls that touch that global state. Read = <c>hw.Update()</c>, which
    /// is what dereferences the delegates. Sensor values are floats cached by Update, so reading
    /// them stays outside the gate: steady-state polling is exactly as concurrent as it was, which
    /// is what keeps a multi-drive storage poll from stalling the 5 Hz CPU one (rates plan R1).
    ///
    /// <b>Excluding threads is only half of it.</b> The gate stops two parts touching that state
    /// at once; it does nothing about a part that releases the lock having left it torn down. So
    /// the second rule is that no <c>Close()</c> may be unpaired — every one is followed by an
    /// <c>Open()</c> before the write lock is released, and an unavailable part keeps its empty
    /// Computer rather than closing it. Measured 2026-09-14: with this gate but without that rule,
    /// an unelevated lhm-storage retry still broke lhm-cpu's poll 129 ms later.
    /// </summary>
    private static readonly ReaderWriterLockSlim Lhm = new(LockRecursionPolicy.NoRecursion);

    /// <summary>How long <see cref="Dispose"/> waits for the gate before giving up on a clean
    /// Close. Matches ProviderHost.Dispose's per-runner join budget.</summary>
    private static readonly TimeSpan ShutdownGateTimeout = TimeSpan.FromSeconds(2);

    public LhmProvider(Part part) => _part = part;

    public string Name => $"lhm-{_part.ToString().ToLowerInvariant()}";

    /// <summary>Everything except the GPU part reads MSRs / port I/O / SMART through PawnIO.</summary>
    public bool NeedsElevation => _part != Part.Gpu;

    public string? UnavailableReason => _unavailableReason;

    /// <summary>
    /// Initialize opens the LHM Computer, which is where the hardware tree — fans, GPUs, disks —
    /// is enumerated, so a `rescan` re-runs it.
    ///
    /// <b>The CPU part is excluded</b> because it has nothing to re-discover: a fixed four
    /// metrics, no indexed family, and a CPU that cannot be hot-plugged. Re-opening it is pure
    /// cost, so a rescan does not ask for one.
    ///
    /// It used to be excluded for a second reason as well — that re-opening a Computer with
    /// IsCpuEnabled is inherently unsafe (an NRE out of `CpuId.Get`, an 0xC0000005 when two
    /// re-opens land seconds apart, measured 2026-09-13). That reasoning was wrong, and the
    /// conclusion it drew — that the host's poll-failure retry path was therefore fine — was
    /// wrong with it: the danger was never re-opening as such but LHM's process-global OpCode
    /// plumbing being torn down under a *sibling* part (see <see cref="Lhm"/>, diagnosed
    /// 2026-09-14). With the gate in place a re-open is as safe as any other LHM call.
    /// </summary>
    public bool RescanReinitialises => _part != Part.Cpu;

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
        Part.Cpu => CollectorRates.LhmCpu,
        Part.SuperIo => CollectorRates.LhmSuperIo,
        Part.Storage => CollectorRates.LhmStorage,
        Part.Gpu => CollectorRates.LhmGpu,
        _ => 1,
    };

    public bool Initialize(MetricSink sink)
    {
        // Close-then-Open is the whole reason the gate exists: between the two, LHM's global
        // rdtsc/cpuid delegates are null and their page is freed, so no other part may be inside
        // hw.Update() for the duration.
        //
        // Note what is NOT here: the old "no hardware found" path used to Close and null the
        // Computer, which left LHM's globals torn down *after* the write lock was released — so
        // the next part to poll dereferenced a null delegate even though the gate had done its
        // job. Excluding threads is only half of it; every Close must be paired with an Open in
        // the same hold, so the process never leaves this block with LHM's globals shut. An
        // unavailable part therefore keeps its (empty) Computer open until its next retry
        // Close+Opens it, or until Dispose. Measured 2026-09-14: without this, an unelevated
        // lhm-storage retry still broke lhm-cpu 129 ms later, gate or no gate.
        bool any = false;
        Lhm.EnterWriteLock();
        try
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

            foreach (var hw in AllHardware())
            {
                hw.Update();
                any = true;
            }
        }
        finally { Lhm.ExitWriteLock(); }

        if (!any)
        {
            // Distinguish "this PC has no such hardware" from "we cannot reach it": the System
            // check turns these codes into a sentence the user can act on.
            _unavailableReason = NeedsElevation && !Elevation.IsElevated ? ProviderError.Unelevated
                : NeedsElevation && !PawnIoInstalled() ? ProviderError.NoDriver
                : ProviderError.NoHardware;
            Log.Warn($"{Name}: no hardware found ({_unavailableReason})");
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
                // Re-init (retry or rescan) re-enumerated the disks, so the letter→model map and
                // the "no match" log suppressor start over with the fresh tree.
                _letterToModel = null;
                _unmatchedLogged.Clear();
                var vols = Volumes.Local();
                _storageLetters = Volumes.Key(vols);
                foreach (char c in vols)
                    sink.Register(MetricNames.DriveTempC(c), MetricType.Double, MetricUnit.Celsius, Name, DefaultRateHz,
                        flags: MetricFlags.NeedsElevation);
                break;
            case Part.Gpu:
                InitGpu(sink);
                break;
        }

        Log.Info($"{Name}: hardware = {string.Join("; ", AllHardware().Select(h => $"{h.HardwareType}:{h.Name}"))}");
        return true;
    }

    /// <summary>Close and re-open the LHM Computer so its hardware tree is enumerated again.
    /// Runs on this provider's own poll thread, so nothing else is touching _computer — but it
    /// does touch LHM's global state, hence the write side of the gate. Callers must not hold the
    /// read lock (the gate is non-recursive): PollStorage releases it before calling this.</summary>
    private void ReopenComputer()
    {
        if (_computer == null) return;
        Lhm.EnterWriteLock();
        try
        {
            try { _computer.Close(); } catch (Exception ex) { Log.Warn($"{Name}: close before re-open: {ex.Message}"); }
            _computer.Open();
            foreach (var hw in AllHardware()) hw.Update();
        }
        finally { Lhm.ExitWriteLock(); }
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
        // Update is what dereferences LHM's global rdtsc/cpuid delegates, so it takes the read
        // side. The Poll* methods below only read floats Update already cached, and PollStorage
        // may call ReopenComputer, which needs the write side — so the lock is released first.
        Lhm.EnterReadLock();
        try { foreach (var hw in AllHardware()) hw.Update(); }
        finally { Lhm.ExitReadLock(); }

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
    /// Publishes every fan channel the board exposes — all of them, in LHM identifier order, with
    /// no cap and no name matching (v1 stopped at 8 and needed a per-board config list). Which
    /// channels are worth showing is the widget's decision, not the collector's (plan H1).
    ///
    /// A fan's percentage is not computed here: the duty cycle the chip itself reports
    /// (<c>fan.&lt;n&gt;.control.pct</c>) is published when a paired Control sensor exists, and
    /// widgets fall back to rpm ÷ max otherwise — no board-specific max table in the collector
    /// (hardware plan H4).
    /// </summary>
    private void PollSuperIo(MetricSink sink)
    {
        // Channel numbering runs across every SuperIO chip on the board, so a second controller
        // continues the range instead of overwriting channel 0.
        int fanIdx = 0;
        foreach (var hw in AllHardware().Where(h => h.HardwareType == HardwareType.SuperIO))
        {
            // LHM names them "Fan #N" / "Fan Control #N"; pair on the trailing number, else by order.
            var controls = hw.Sensors.Where(s => s.SensorType == SensorType.Control)
                                     .OrderBy(s => s.Identifier.ToString()).ToList();

            int chipOrdinal = 0;
            foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Fan).OrderBy(s => s.Identifier.ToString()))
            {
                if (_registeredFans.Add(fanIdx))
                {
                    sink.RegisterWithMax(MetricNames.FanRpm(fanIdx), MetricUnit.Rpm, Name, DefaultRateHz,
                        flags: MetricFlags.NeedsElevation);
                    sink.Register(MetricNames.FanName(fanIdx), MetricType.String, MetricUnit.Text, Name, 0, MetricSemantics.Static);
                    // Static: the channel's name is written when the channel is discovered.
                    sink.SetString(MetricNames.FanName(fanIdx), s.Name);
                    sink.Set(MetricNames.FanCount, _registeredFans.Count);
                }

                double rpm = s.Value is { } v && !float.IsNaN((float)v) ? v : 0;
                sink.Set(MetricNames.FanRpm(fanIdx), rpm);

                var control = MatchControl(controls, s, chipOrdinal);
                if (control?.Value is { } duty && !float.IsNaN((float)duty))
                {
                    if (_registeredControls.Add(fanIdx))
                        sink.Register(MetricNames.FanControlPct(fanIdx), MetricType.Double, MetricUnit.Percent, Name,
                            DefaultRateHz, flags: MetricFlags.NeedsElevation);
                    sink.Set(MetricNames.FanControlPct(fanIdx), Math.Clamp(duty, 0, 100));
                }
                fanIdx++;
                chipOrdinal++;
            }

            var vcore = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Voltage &&
                            (s.Name.Contains("core", StringComparison.OrdinalIgnoreCase) || s.Name == "VIN0"));
            if (vcore?.Value is { } vc && !float.IsNaN((float)vc))
                sink.Set(MetricNames.CpuVcoreV, vc);
        }
    }

    private readonly HashSet<int> _registeredFans = new();
    private readonly HashSet<int> _registeredControls = new();

    private static ISensor? MatchControl(List<ISensor> controls, ISensor fan, int chipOrdinal)
    {
        int? fanNumber = TrailingNumber(fan.Name) ?? TrailingNumber(fan.Identifier.ToString());
        if (fanNumber is { } n)
        {
            var byNumber = controls.FirstOrDefault(c =>
                (TrailingNumber(c.Name) ?? TrailingNumber(c.Identifier.ToString())) == n);
            if (byNumber != null) return byNumber;
        }
        return chipOrdinal < controls.Count ? controls[chipOrdinal] : null;
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
        // Hot-plug: when the set of volumes changes, re-open LHM (its Computer enumerates disks
        // at Open(), so a drive attached afterwards is simply not in the tree), register temp
        // metrics for any newly-seen volume, and invalidate the letter→disk-model map so it is
        // rebuilt to include the new drive. Without this the temps stay N/A until a restart.
        var volumes = Volumes.Local();
        string live = Volumes.Key(volumes);
        if (live != _storageLetters)
        {
            if (_storageLetters.Length > 0)
            {
                Log.Info($"{Name}: volumes changed ({_storageLetters} -> {live}) — re-opening LHM storage");
                ReopenComputer();
            }
            foreach (char c in volumes)
                sink.Register(MetricNames.DriveTempC(c), MetricType.Double, MetricUnit.Celsius, Name, DefaultRateHz,
                    flags: MetricFlags.NeedsElevation);
            foreach (char c in _storageLetters.Where(c => !volumes.Contains(c)))
                sink.MarkStale(MetricNames.DriveTempC(c));
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

    // ---- GPUs (hardware plan H2) ----

    /// <summary>LHM hardware identifier → halo gpu index.</summary>
    private readonly Dictionary<string, int> _gpuIndex = new();
    /// <summary>Identifiers of LHM GPUs that NVML already publishes; for those this part only
    /// adds the two sensors NVML has no API for.</summary>
    private readonly HashSet<string> _gpuOwnedByNvml = new();
    /// <summary>Metric names this part has registered, so sensors can register on first sight
    /// (a GPU that never reports power simply has no <c>gpu.i.power.w</c> and the widget row
    /// hides itself).</summary>
    private readonly HashSet<string> _lazyRegistered = new();

    private static bool IsGpu(IHardware h) =>
        h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    private static string VendorOf(IHardware h) => h.HardwareType switch
    {
        HardwareType.GpuNvidia => "nvidia",
        HardwareType.GpuAmd => "amd",
        HardwareType.GpuIntel => "intel",
        _ => "",
    };

    /// <summary>
    /// Claim an index per GPU LHM can see. NVIDIA cards match the NVML device already publishing
    /// that index and only gain voltage + fan RPM (NVML exposes neither); AMD and Intel cards get
    /// a fresh index and LHM becomes the only source for the whole <c>gpu.&lt;i&gt;.*</c> family.
    /// </summary>
    private void InitGpu(MetricSink sink)
    {
        _gpuIndex.Clear();
        _gpuOwnedByNvml.Clear();
        sink.Register(MetricNames.GpuCount, MetricType.Double, MetricUnit.Count, Name, 0, MetricSemantics.Static);

        foreach (var hw in AllHardware().Where(IsGpu))
        {
            string id = hw.Identifier.ToString();
            int idx = GpuIndexSpace.IndexForLhm(id, hw.Name, hw.HardwareType == HardwareType.GpuNvidia, out bool matched);
            _gpuIndex[id] = idx;
            if (matched) _gpuOwnedByNvml.Add(id);

            // Voltage and fan RPM (the two NVML has no API for) register on the first real
            // reading in PollGpu — a passively-cooled card then has no gpu.<i>.fan.rpm at all and
            // the widget row hides itself instead of showing a permanent N/A.
            if (!matched)
            {
                sink.Register(MetricNames.GpuName(idx), MetricType.String, MetricUnit.Text, Name, 0, MetricSemantics.Static);
                sink.Register(MetricNames.GpuVendor(idx), MetricType.String, MetricUnit.Text, Name, 0, MetricSemantics.Static);
                sink.SetString(MetricNames.GpuName(idx), hw.Name);
                sink.SetString(MetricNames.GpuVendor(idx), VendorOf(hw));
                Log.Info($"{Name}: gpu.{idx} = {hw.Name} ({VendorOf(hw)}), LHM is the only source");
            }
        }
        sink.Set(MetricNames.GpuCount, GpuIndexSpace.Count);
    }

    private void RegisterLazy(MetricSink sink, string metric, MetricUnit unit, bool withMax = false)
    {
        if (!_lazyRegistered.Add(metric)) return;
        if (withMax) sink.RegisterWithMax(metric, unit, Name, DefaultRateHz);
        else sink.Register(metric, MetricType.Double, unit, Name, DefaultRateHz);
    }

    /// <summary>Publish a sensor value, registering the metric the first time the sensor is seen.</summary>
    private void Publish(MetricSink sink, string metric, MetricUnit unit, ISensor? s, bool withMax = false)
    {
        if (s?.Value is not { } v || float.IsNaN((float)v)) return;
        RegisterLazy(sink, metric, unit, withMax);
        sink.Set(metric, v);
    }

    /// <summary>
    /// First sensor of a type whose name contains one of the candidates, in preference order.
    /// LHM's sensor names are English literals that differ by vendor and library version, so every
    /// lookup lists the spellings seen in the wild rather than one exact string. No candidates at
    /// all (or <paramref name="orAny"/>) means "take whatever sensor of that type exists" — right
    /// when a card only ever has one of them.
    /// </summary>
    private static ISensor? Pick(IHardware hw, SensorType type, bool orAny, params string[] nameContains)
    {
        var of = hw.Sensors.Where(s => s.SensorType == type).ToList();
        foreach (string want in nameContains)
        {
            var hit = of.FirstOrDefault(s => s.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return orAny || nameContains.Length == 0 ? of.FirstOrDefault() : null;
    }

    private void PollGpu(MetricSink sink)
    {
        foreach (var hw in AllHardware().Where(IsGpu))
        {
            string id = hw.Identifier.ToString();
            if (!_gpuIndex.TryGetValue(id, out int idx)) continue;

            // Every vendor: the two NVML has no API for.
            Publish(sink, MetricNames.GpuVoltageV(idx), MetricUnit.Volts, Pick(hw, SensorType.Voltage, orAny: true, "GPU Core", "Core"), withMax: true);
            Publish(sink, MetricNames.GpuFanRpm(idx), MetricUnit.Rpm, Pick(hw, SensorType.Fan, orAny: true));

            if (_gpuOwnedByNvml.Contains(id)) continue;   // NVML owns the rest of this index

            // AMD / Intel: LHM is the whole story. Sensor names are English literals that differ
            // between vendors and LHM versions, so match on containment with fallbacks; anything
            // this card does not report simply never registers and its widget row hides itself.
            Publish(sink, MetricNames.GpuTempC(idx), MetricUnit.Celsius, Pick(hw, SensorType.Temperature, orAny: true, "GPU Core", "GPU Hot Spot", "GPU"));
            Publish(sink, MetricNames.GpuUsagePct(idx), MetricUnit.Percent, Pick(hw, SensorType.Load, orAny: false, "GPU Core", "D3D 3D", "GPU"));
            Publish(sink, MetricNames.GpuClockCoreMhz(idx), MetricUnit.Megahertz, Pick(hw, SensorType.Clock, orAny: false, "GPU Core", "GPU Graphics"));
            Publish(sink, MetricNames.GpuClockMemMhz(idx), MetricUnit.Megahertz, Pick(hw, SensorType.Clock, orAny: false, "GPU Memory"));
            Publish(sink, MetricNames.GpuPowerW(idx), MetricUnit.Watts, Pick(hw, SensorType.Power, orAny: true, "GPU Package", "GPU Total", "GPU PPT", "GPU"), withMax: true);
            Publish(sink, MetricNames.GpuFanPct(idx), MetricUnit.Percent, Pick(hw, SensorType.Control, orAny: true, "GPU Fan", "Fan"));

            // VRAM: LHM reports it as SmallData in MB.
            var used = Pick(hw, SensorType.SmallData, orAny: false, "GPU Memory Used", "D3D Dedicated Memory Used");
            var total = Pick(hw, SensorType.SmallData, orAny: false, "GPU Memory Total");
            Publish(sink, MetricNames.GpuVramUsedMb(idx), MetricUnit.Megabytes, used);
            if (total?.Value is { } t && t > 0)
            {
                // Static: the board's VRAM size, written once.
                if (_lazyRegistered.Add(MetricNames.GpuVramTotalMb(idx)))
                {
                    sink.Register(MetricNames.GpuVramTotalMb(idx), MetricType.Double, MetricUnit.Megabytes, Name, 0, MetricSemantics.Static);
                    sink.Set(MetricNames.GpuVramTotalMb(idx), t);
                }
                if (used?.Value is { } u && !float.IsNaN((float)u))
                {
                    RegisterLazy(sink, MetricNames.GpuVramPct(idx), MetricUnit.Percent);
                    sink.Set(MetricNames.GpuVramPct(idx), u / t * 100);
                }
            }
        }
    }

    public void Dispose()
    {
        // The gate must not turn shutdown into a hang. A poll thread wedged inside an LHM driver
        // call holds the read lock, and ProviderHost.Dispose has already given each runner 2 s to
        // join before it gets here — so a gate we still cannot take means someone is stuck, not
        // merely busy, and blocking forever would be a worse bug than the one the gate fixes.
        // Skipping Close leaks an LHM Computer into a process that is exiting anyway.
        if (!Lhm.TryEnterWriteLock(ShutdownGateTimeout))
        {
            if (_computer != null) Log.Warn($"{Name}: LHM gate still busy after {ShutdownGateTimeout.TotalSeconds:0}s, leaving the Computer open");
            _computer = null;
            return;
        }
        try { _computer?.Close(); }
        catch { }
        finally { Lhm.ExitWriteLock(); }
        _computer = null;
    }
}
