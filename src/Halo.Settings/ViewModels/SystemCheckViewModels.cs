using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Halo.Metrics;
using Halo.Settings.Services;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Shared.Panels;

namespace Halo.Settings.ViewModels;

public enum CheckLevel { Good, Warning, Error, Neutral }

public sealed class CheckCardViewModel : ObservableObject
{
    private string _title = "";
    private string _detail = "";
    private string _glyph = "•";
    private Brush _brush = Brushes.DimGray;

    public string Key { get; }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string Glyph { get => _glyph; private set => Set(ref _glyph, value); }
    public Brush StatusBrush { get => _brush; private set => Set(ref _brush, value); }

    public CheckCardViewModel(string key) => Key = key;

    public void Apply(string title, string detail, CheckLevel level)
    {
        Title = title;
        Detail = detail;
        Glyph = level switch { CheckLevel.Good => "✓", CheckLevel.Warning => "!", CheckLevel.Error => "×", _ => "•" };
        StatusBrush = level switch
        {
            CheckLevel.Good => new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x4D)),
            CheckLevel.Warning => new SolidColorBrush(Color.FromRgb(0x9A, 0x65, 0x00)),
            CheckLevel.Error => new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18)),
            _ => Brushes.DimGray,
        };
    }
}

public sealed class ProviderStatusViewModel : ObservableObject
{
    private string _status = "";
    private string _reason = "";
    private string _glyph = "•";
    private Brush _brush = Brushes.DimGray;

    public int Index { get; }
    public string Name { get; }
    public string DisplayName { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Reason { get => _reason; private set => Set(ref _reason, value); }
    public string Glyph { get => _glyph; private set => Set(ref _glyph, value); }
    public Brush StatusBrush { get => _brush; private set => Set(ref _brush, value); }

    public ProviderStatusViewModel(ProviderInfo info)
    {
        Index = info.Index;
        Name = info.Name;
        DisplayName = FriendlyProvider(info.Name);
        Apply(info);
    }

    public void Apply(ProviderInfo info)
    {
        CheckLevel level = info.State switch { ProviderState.Ok => CheckLevel.Good, ProviderState.Degraded => CheckLevel.Warning, _ => CheckLevel.Error };
        Status = info.State.ToString();
        Glyph = level switch { CheckLevel.Good => "✓", CheckLevel.Warning => "!", _ => "×" };
        StatusBrush = level switch
        {
            CheckLevel.Good => new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x4D)),
            CheckLevel.Warning => new SolidColorBrush(Color.FromRgb(0x9A, 0x65, 0x00)),
            _ => new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18)),
        };
        Reason = DescribeProvider(info);
    }

    public static string DescribeProvider(ProviderInfo info) => info.LastError switch
    {
        "unelevated" => "Needs the collector to run as administrator",
        "no-driver" => "PawnIO driver is not installed or loaded",
        "no-hw" => "No matching hardware found",
        "no-nvml" => "NVIDIA driver / NVML was not found",
        "no-sdk" => "PresentMon SDK payload was not found",
        "failed" => "Provider failed — copy the hardware report for details",
        "" when info.State == ProviderState.Ok => $"{info.RateHz:0.###} Hz · last poll {info.LastPollMs:0.##} ms",
        "" => info.NeedsElevation ? "May need the collector to run as administrator" : "No reason reported",
        _ => info.LastError,
    };

    private static string FriendlyProvider(string name) => name switch
    {
        "builtin" => "System",
        "cpu-kernel" => "CPU kernel",
        "process" => "Processes",
        "disk-io" => "Disk I/O",
        "network" => "Network",
        "nvml" => "NVIDIA NVML",
        "lhm-cpu" => "LHM CPU",
        "lhm-superio" => "LHM Super I/O",
        "lhm-storage" => "LHM storage",
        "lhm-gpu" => "LHM GPU",
        "presentmon" => "PresentMon",
        "pclstats" => "PCL Stats",
        _ => name,
    };
}

public sealed class HardwareStatusViewModel(string key, string label, string detail) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Detail { get; } = detail;
}

public sealed class PanelStatusViewModel(string id, string name, string status, string detail, CheckLevel level) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Status { get; } = status;
    public string Detail { get; } = detail;
    public string Glyph { get; } = level switch { CheckLevel.Good => "✓", CheckLevel.Warning => "!", CheckLevel.Error => "×", _ => "•" };
    public Brush StatusBrush { get; } = level switch
    {
        CheckLevel.Good => new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x4D)),
        CheckLevel.Warning => new SolidColorBrush(Color.FromRgb(0x9A, 0x65, 0x00)),
        CheckLevel.Error => new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18)),
        _ => Brushes.DimGray,
    };
}

public sealed record LayoutPlan(IReadOnlyList<WidgetInstance> Widgets, string Summary, bool CollectorOffline);

public sealed class SystemCheckViewModel : ObservableObject, IDisposable
{
    private readonly LiveConfigService _config;
    private readonly CollectorSession _session = new();
    private readonly Dictionary<string, CheckCardViewModel> _cards = new(StringComparer.Ordinal);
    private bool _refreshing;
    private bool _offline = true;
    private bool _isFirstRun;
    private bool _canInstallPawnIo;
    private bool _canRepairAutostart;
    private bool _canRescan;
    private string _pawnIoButtonText = "Install PawnIO…  ⛨";
    private string _pawnIoButtonToolTip = "Installs the optional PawnIO driver with administrator permission.";
    private string _autostartButtonText = "Repair autostart…  ⛨";
    private string _autostartButtonToolTip = "Recreates both scheduled tasks with administrator permission.";
    private string _actionStatus = "";
    private string _collectorVersion = "Not connected";
    private bool _pawnInstalled;
    private bool _hasExistingLayout;
    private DateTimeOffset _rescanBlockedUntilUtc;
    private IReadOnlyList<MetricInfo> _metrics = [];
    private IReadOnlyList<ProviderInfo> _providerInfos = [];

    public ObservableCollection<CheckCardViewModel> SummaryCards { get; } = [];
    public ObservableCollection<ProviderStatusViewModel> Providers { get; } = [];
    public ObservableCollection<HardwareStatusViewModel> Hardware { get; } = [];
    public ObservableCollection<PanelStatusViewModel> PanelStatuses { get; } = [];

    public bool Offline { get => _offline; private set => Set(ref _offline, value); }
    public bool IsFirstRun { get => _isFirstRun; private set => Set(ref _isFirstRun, value); }
    public bool CanInstallPawnIo { get => _canInstallPawnIo; private set => Set(ref _canInstallPawnIo, value); }
    public bool CanRepairAutostart { get => _canRepairAutostart; private set => Set(ref _canRepairAutostart, value); }
    public bool CanRescan { get => _canRescan; private set => Set(ref _canRescan, value); }
    public string PawnIoButtonText { get => _pawnIoButtonText; private set => Set(ref _pawnIoButtonText, value); }
    public string PawnIoButtonToolTip { get => _pawnIoButtonToolTip; private set => Set(ref _pawnIoButtonToolTip, value); }
    public string AutostartButtonText { get => _autostartButtonText; private set => Set(ref _autostartButtonText, value); }
    public string AutostartButtonToolTip { get => _autostartButtonToolTip; private set => Set(ref _autostartButtonToolTip, value); }
    public string ActionStatus { get => _actionStatus; set => Set(ref _actionStatus, value); }
    public string CollectorVersion { get => _collectorVersion; private set => Set(ref _collectorVersion, value); }
    public bool HasExistingLayout
    {
        get => _hasExistingLayout;
        private set
        {
            if (Set(ref _hasExistingLayout, value))
            {
                Raise(nameof(LayoutButtonText));
                Raise(nameof(FirstRunMessage));
            }
        }
    }
    public string LayoutButtonText => HasExistingLayout ? "Regenerate default layout…" : "Generate default layout";
    public string FirstRunMessage => HasExistingLayout
        ? "Halo already created a starting layout. Review the checks below; you can regenerate it for the hardware found here."
        : "Nothing has been written yet. Review the checks below, then generate a sensible starting layout.";

    public SystemCheckViewModel(LiveConfigService config, bool firstRun)
    {
        _config = config;
        _isFirstRun = firstRun;
        _hasExistingLayout = File.Exists(Path.Combine(_config.ConfigDir, "widgets.json"));
        foreach (string key in new[] { "collector", "pawnio", "autostart", "readiness" })
        {
            var card = new CheckCardViewModel(key);
            _cards[key] = card;
            SummaryCards.Add(card);
        }
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            HasExistingLayout = File.Exists(Path.Combine(_config.ConfigDir, "widgets.json"));
            _session.Poll();
            bool connected = _session.Attached && !_session.Stale;
            Offline = !connected;
            CanRescan = connected && DateTimeOffset.UtcNow >= _rescanBlockedUntilUtc;
            _metrics = connected ? _session.Metrics() : [];
            _providerInfos = connected ? _session.Providers() : [];
            CollectorVersion = connected
                ? (_session.GetText(MetricNames.SysCollectorVersion, _session.CollectorVersion) is { Length: > 0 } version ? version : _session.CollectorVersion)
                : "Not connected";

            bool collectorElevated = connected && _session.Get(MetricNames.SysElevated, 0) > .5;
            _pawnInstalled = (connected && _session.Get(MetricNames.SysPawnIoInstalled, 0) > .5) || PawnIoManager.IsInstalledFallback();
            string pawnVersion = connected ? _session.GetText(MetricNames.SysPawnIoVersion, "") : "";
            AutostartStatus autostart = await Task.Run(AutostartManager.GetStatus);

            ApplyCards(connected, collectorElevated, pawnVersion, autostart);
            ReconcileProviders(_providerInfos);
            BuildHardware(connected, collectorElevated);
            BuildPanelStatuses(connected);
            ApplyReadinessCard(connected);

            CanInstallPawnIo = !_pawnInstalled && PawnIoManager.PayloadPresent;
            PawnIoButtonText = _pawnInstalled ? "Installed" : "Install PawnIO…  ⛨";
            PawnIoButtonToolTip = _pawnInstalled
                ? (pawnVersion.Length > 0 ? $"PawnIO {pawnVersion} is installed." : "PawnIO is installed.")
                : PawnIoManager.PayloadPresent ? "Installs the optional PawnIO driver with administrator permission." : PawnIoManager.MissingPayloadMessage;
            // Deliberately still !Healthy and not !NeedsAttention: an install whose
            // --register-autostart failed leaves no tasks at all, which is indistinguishable from a
            // decline, and halo.iss tells that user to come here and press this. Gating the button
            // on NeedsAttention would remove it in exactly the state the installer points at.
            CanRepairAutostart = AutostartManager.HasTaskPayloads && !autostart.Healthy;
            AutostartButtonText = autostart.ActionLabel;
            AutostartButtonToolTip = !AutostartManager.HasTaskPayloads ? AutostartManager.MissingPayloadMessage
                : autostart.Healthy ? "Both scheduled tasks already point at this Halo installation."
                : autostart.Off ? "Registers both scheduled tasks so Halo starts at logon, with administrator permission."
                : "Recreates both scheduled tasks with administrator permission.";
        }
        catch (Exception ex)
        {
            Offline = true;
            CanRescan = false;
            ActionStatus = $"Could not refresh System check: {ex.Message}";
            _cards["collector"].Apply("Collector check failed", ex.Message, CheckLevel.Error);
        }
        finally { _refreshing = false; }
    }

    public async Task<int?> InstallPawnIoAsync()
    {
        ActionStatus = "Waiting for administrator permission…";
        int? result = await PawnIoManager.RunElevatedAsync();
        ActionStatus = result switch
        {
            null => "PawnIO installation was cancelled.",
            0 => "PawnIO installed. Refreshing checks…",
            3010 => "PawnIO installed; Windows must restart before the driver is available.",
            _ => $"PawnIO installer failed with exit code {result}.",
        };
        await RefreshAsync();
        return result;
    }

    public async Task<int?> RepairAutostartAsync()
    {
        ActionStatus = "Waiting for administrator permission…";
        int? result = await AutostartManager.RunElevatedAsync(enable: true);
        ActionStatus = result switch
        {
            null => "Autostart repair was cancelled.",
            0 => "Autostart repaired.",
            _ => $"Autostart repair failed with exit code {result}.",
        };
        await RefreshAsync();
        return result;
    }

    public async Task<bool> RescanAsync()
    {
        if (!CanRescan) return false;
        _rescanBlockedUntilUtc = DateTimeOffset.UtcNow.AddSeconds(10);
        CanRescan = false;
        bool sent = await Task.Run(() => ControlPipe.Send(ControlPipe.Rescan));
        if (!sent)
        {
            ActionStatus = "Could not reach the collector control pipe.";
            return false;
        }
        ActionStatus = "Rescan sent. Refreshing hardware in a moment…";
        await Task.Delay(2500);
        await RefreshAsync();
        ActionStatus = "Hardware list refreshed. Rescan is available again after 10 seconds.";
        return true;
    }

    public LayoutPlan CreateLayoutPlan()
    {
        bool connected = !Offline;
        List<WidgetInstance> widgets = DefaultLayout.Generate(
            name => _session.Get(name),
            name => _session.GetText(name),
            connected);
        if (!connected)
        {
            const string offline = "Collector not running — creating Clock, CPU/RAM and Network only. Open System check again once the collector is up to add GPU, drive and fan widgets.";
            return new(widgets, offline, true);
        }
        string names = string.Join(", ", widgets.Select(widget => widget.Type == "gpu"
            ? $"GPU {widget.Options.GetValueOrDefault("gpuIndex", "0")}" : PanelCatalog.Find(widget.Type)?.DisplayName ?? widget.Type));
        string action = HasExistingLayout ? "replace your current widgets with" : "create";
        return new(widgets,
            $"Halo will {action} {widgets.Count} widgets on the primary monitor: {names}. The FPS widget will show data when a game supplies frames.", false);
    }

    public async Task SaveLayoutAsync(LayoutPlan plan)
    {
        List<WidgetInstance> saved = plan.Widgets.Select(CloneWidget).ToList();
        _config.QueueWidgets("$", config =>
        {
            config.Widgets = saved.Select(CloneWidget).ToList();
            config.Arrange = WidgetsConfig.ArrangePending;
        }, flushImmediately: true);
        await _config.FlushAllAsync();
        HasExistingLayout = true;
        IsFirstRun = false;
        ActionStatus = $"Created {saved.Count} widgets. Halo Widgets will arrange them on the primary monitor.";
    }

    public async Task ArrangeWidgetsAsync()
    {
        if (!HasExistingLayout) return;
        _config.QueueWidgets("arrange", config => config.Arrange = WidgetsConfig.ArrangePending, flushImmediately: true);
        await _config.FlushAllAsync();
        ActionStatus = "Arrange requested. Halo Widgets will pack the current layout on the primary monitor.";
    }

    public string BuildHardwareReport()
    {
        var providerNames = _providerInfos.ToDictionary(info => info.Index, info => info.Name);
        var metrics = new List<Dictionary<string, object?>>();
        foreach (MetricInfo metric in _metrics)
        {
            var item = new Dictionary<string, object?>
            {
                ["name"] = metric.Name,
                ["type"] = metric.Type.ToString(),
                ["unit"] = metric.Unit.ToString(),
                ["semantics"] = metric.Semantics.ToString(),
                ["flags"] = metric.Flags.ToString(),
                ["provider"] = providerNames.GetValueOrDefault(metric.ProviderIndex, ""),
                ["nominalHz"] = metric.NominalRateHz,
                ["effectiveHz"] = metric.EffectiveRateHz,
            };
            if (metric.Type == MetricType.String)
                item["text"] = _session.Reader.TryReadString(metric.Index, out string text) ? text : null;
            else if (_session.Reader.TryRead(metric.Index, out double value, out double age))
            {
                item["value"] = value;
                item["ageS"] = Math.Round(age, 3);
                item["stale"] = age > 5;
            }
            else
            {
                item["value"] = null;
                item["stale"] = true;
            }
            metrics.Add(item);
        }

        var report = new
        {
            header = new
            {
                versionMajor = SharedMemoryLayout.VersionMajor,
                versionMinor = SharedMemoryLayout.VersionMinor,
                section = SharedMemoryLayout.SectionName,
                collectorVersion = CollectorVersion,
                collectorPid = _session.CollectorPid,
                heartbeatAgeS = Math.Round(_session.HeartbeatAgeSeconds, 3),
                qpcFrequency = _session.QpcFrequency,
                metricCount = _metrics.Count,
                totalSize = _session.Reader.TotalSize,
                dataDir = Paths.DataDir,
                portable = Paths.IsPortable,
            },
            providers = _providerInfos.Select(info => new
            {
                info.Index, info.Name, state = info.State.ToString().ToLowerInvariant(),
                info.NeedsElevation, info.RateHz, info.LastPollMs, info.LastError,
            }),
            metrics,
            frames = new { cursor = _session.Reader.FrameCursor },
        };
        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    }

    private void ApplyCards(bool connected, bool collectorElevated, string pawnVersion, AutostartStatus autostart)
    {
        bool pawnDriverError = connected && _providerInfos.Any(info => info.LastError == ProviderError.NoDriver);
        _cards["collector"].Apply(connected ? "Collector connected" : "Collector not running",
            connected ? $"Version {CollectorVersion} · PID {_session.CollectorPid} · {(collectorElevated ? "administrator" : "standard access")}" : $"No {SharedMemoryLayout.SectionName} section is live.",
            connected ? CheckLevel.Good : CheckLevel.Error);
        _cards["pawnio"].Apply(
            pawnDriverError ? "PawnIO driver unavailable" : _pawnInstalled ? "PawnIO installed" : "PawnIO not installed",
            pawnDriverError ? "The collector found the installation but could not load the driver."
                : _pawnInstalled && !connected ? "Installed; start the collector to check whether the driver loads."
                : _pawnInstalled ? (pawnVersion.Length > 0 ? $"Version {pawnVersion} · no provider reported a driver error" : "Installed · no provider reported a driver error")
                : "Optional: enables CPU temperature, power, drive temperature and fan sensors.",
            pawnDriverError ? CheckLevel.Error : _pawnInstalled && connected ? CheckLevel.Good : CheckLevel.Warning);
        // Three states, not two. "No tasks" is now how every user who declined "Start with Windows"
        // runs Halo — they start it from the shortcut and the collector elevates through UAC — so
        // rendering that as a Warning tells them their own choice is a fault, and makes the repair
        // button look like the remedy for a non-problem. Only a genuinely broken registration
        // (half-registered, pointing at another install, or a legacy Run value left behind) warns.
        (string title, CheckLevel level) = autostart switch
        {
            { Healthy: true } => ("Autostart on", CheckLevel.Good),
            { Off: true } => ("Autostart off", CheckLevel.Neutral),
            _ => ("Autostart needs attention", CheckLevel.Warning),
        };
        _cards["autostart"].Apply(title, autostart.Summary, level);
    }

    private void ApplyReadinessCard(bool connected)
    {
        if (!connected)
        {
            _cards["readiness"].Apply("Widget readiness unknown", "Start the collector to check every widget type.", CheckLevel.Neutral);
            return;
        }

        int ready = PanelStatuses.Count(row => row.Status == "Ready");
        int partial = PanelStatuses.Count(row => row.Status is "Partial" or "Needs administrator");
        int unavailable = PanelStatuses.Count - ready - partial;
        CheckLevel level = unavailable == 0 ? partial == 0 ? CheckLevel.Good : CheckLevel.Warning : CheckLevel.Error;
        _cards["readiness"].Apply($"{ready} of {PanelStatuses.Count} widget types ready",
            $"{partial} partial or need administrator · {unavailable} unavailable", level);
    }

    private void ReconcileProviders(IReadOnlyList<ProviderInfo> values)
    {
        foreach (ProviderInfo value in values)
        {
            ProviderStatusViewModel? row = Providers.FirstOrDefault(item => item.Index == value.Index);
            if (row is null) Providers.Add(new ProviderStatusViewModel(value)); else row.Apply(value);
        }
        foreach (ProviderStatusViewModel row in Providers.Where(row => values.All(value => value.Index != row.Index)).ToArray())
            Providers.Remove(row);
    }

    private void BuildHardware(bool connected, bool collectorElevated)
    {
        var next = new List<HardwareStatusViewModel>();
        int build = connected ? (int)Math.Round(_session.Get(MetricNames.SysOsBuild, Environment.OSVersion.Version.Build)) : Environment.OSVersion.Version.Build;
        next.Add(new("os", "Windows", $"Build {build}"));
        if (!connected)
        {
            next.Add(new("collector", "Hardware discovery", "Collector not running"));
            Replace(Hardware, next);
            return;
        }

        int logical = Math.Clamp((int)Math.Round(_session.Get(MetricNames.CpuLogicalCount, 0)), 0, 512);
        next.Add(new("cpu", "CPU", logical > 0 ? $"{logical} logical processors" : "Processor count unavailable"));
        int gpuCount = Math.Clamp((int)Math.Round(_session.Get(MetricNames.GpuCount, 0)), 0, 32);
        for (int index = 0; index < gpuCount; index++)
        {
            string name = _session.GetText(MetricNames.GpuName(index), "Unnamed GPU");
            string vendor = _session.GetText(MetricNames.GpuVendor(index), "unknown vendor");
            next.Add(new($"gpu.{index}", $"GPU {index}", $"{name} · {vendor}"));
        }

        foreach (string volume in DiscoverVolumes(_metrics))
        {
            char letter = volume[0];
            double total = _session.Get(MetricNames.DriveTotalB(letter), 0);
            string label = _session.GetText(MetricNames.DriveLabel(letter), "");
            next.Add(new($"drive.{volume}", $"Volume {volume}:", $"{(label.Length == 0 ? "Local volume" : label)} · {FormatBytes(total)}"));
        }

        int fanCount = Math.Clamp((int)Math.Round(_session.Get(MetricNames.FanCount, 0)), 0, 256);
        for (int index = 0; index < fanCount; index++)
        {
            string name = _session.GetText(MetricNames.FanName(index), $"Fan {index}");
            double rpm = _session.Get(MetricNames.FanRpm(index), 0);
            next.Add(new($"fan.{index}", $"Fan {index}", $"{name} · {rpm:0} RPM"));
        }
        if (fanCount == 0 && !collectorElevated && _providerInfos.Any(info => info.Name == "lhm-superio" && info.LastError == ProviderError.Unelevated))
            next.Add(new("fans-admin", "Fan channels", "Needs the collector to run as administrator"));
        Replace(Hardware, next);
    }

    private void BuildPanelStatuses(bool connected)
    {
        if (!connected)
        {
            Replace(PanelStatuses, PanelCatalog.All.Select(panel => new PanelStatusViewModel(panel.Id, panel.DisplayName,
                "Collector offline", panel.Requires ?? "Start the collector to check this widget.", CheckLevel.Neutral)));
            return;
        }

        Dictionary<string, MetricInfo> registry = _metrics.ToDictionary(metric => metric.Name, StringComparer.Ordinal);
        Dictionary<int, ProviderInfo> providers = _providerInfos.ToDictionary(provider => provider.Index);
        string volume = DiscoverVolumes(_metrics).FirstOrDefault() ?? "c";
        int coreCount = Math.Max(1, (int)Math.Round(_session.Get(MetricNames.CpuLogicalCount, 1)));
        int fanCount = Math.Max(1, (int)Math.Round(_session.Get(MetricNames.FanCount, 1)));
        int gpuCount = Math.Max(1, (int)Math.Round(_session.Get(MetricNames.GpuCount, 1)));
        var results = new List<PanelStatusViewModel>();

        foreach (PanelType panel in PanelCatalog.All)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (MetricSpec metric in panel.Metrics)
            {
                IEnumerable<string> repeats = metric.Repeat switch
                {
                    Repeat.PerCore => Enumerable.Range(0, coreCount).Take(2).Select(index => index.ToString(CultureInfo.InvariantCulture)),
                    Repeat.PerRank => ["0"],
                    Repeat.PerVolume => [volume],
                    Repeat.PerFan => Enumerable.Range(0, fanCount).Take(2).Select(index => index.ToString(CultureInfo.InvariantCulture)),
                    _ => [metric.MetricName.Contains("{n}", StringComparison.Ordinal) ? "0" : ""],
                };
                foreach (string repeat in repeats)
                    names.Add(PanelCatalog.ResolveMetricName(metric.MetricName, "0", repeat, volume, "displayed", false));
            }

            MetricInfo[] found = names.Where(registry.ContainsKey).Select(name => registry[name]).ToArray();
            ProviderInfo[] owners = found.Where(info => providers.ContainsKey(info.ProviderIndex)).Select(info => providers[info.ProviderIndex]).DistinctBy(info => info.Index).ToArray();
            bool adminBlocked = IsAdminBlocked(panel, owners);
            bool unavailableOwner = owners.Length > 0 && owners.All(info => info.State == ProviderState.Unavailable);
            int missing = names.Count - found.Length;
            CheckLevel level;
            string status;
            string detail;
            if (found.Length == 0 || unavailableOwner)
            {
                level = adminBlocked ? CheckLevel.Warning : CheckLevel.Error;
                status = adminBlocked ? "Needs administrator" : "Unavailable";
                detail = adminBlocked ? "Run the collector as administrator to register this panel's data." : panel.Requires ?? "No matching metrics are registered.";
            }
            else if (missing > 0 || owners.Any(info => info.State != ProviderState.Ok))
            {
                level = CheckLevel.Warning;
                status = "Partial";
                detail = adminBlocked ? "Core data works; extra sensors need the collector to run as administrator." : $"{found.Length} data rows available · {missing} optional rows absent. {panel.Requires}".Trim();
            }
            else
            {
                level = CheckLevel.Good;
                status = "Ready";
                detail = $"{found.Length} data rows available";
            }
            results.Add(new(panel.Id, panel.DisplayName, status, detail, level));
        }
        Replace(PanelStatuses, results);
    }

    private bool IsAdminBlocked(PanelType panel, IReadOnlyList<ProviderInfo> owners)
    {
        if (owners.Any(info => info.NeedsElevation && info.LastError == ProviderError.Unelevated)) return true;
        string[] likely = panel.Id switch
        {
            "cpu-ram" or "power" => ["lhm-cpu", "lhm-superio"],
            "drives" => ["lhm-storage"],
            "fans" => ["lhm-superio"],
            "fps" => ["presentmon"],
            "latency" => ["presentmon", "pclstats"],
            _ => [],
        };
        return _providerInfos.Any(info => likely.Contains(info.Name, StringComparer.Ordinal) && info.LastError == ProviderError.Unelevated);
    }

    private static IEnumerable<string> DiscoverVolumes(IEnumerable<MetricInfo> metrics)
        => metrics.Select(metric => Regex.Match(metric.Name, @"^drive\.([a-z])\.total\.b$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Where(match => match.Success).Select(match => match.Groups[1].Value.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);

    private static string FormatBytes(double bytes)
    {
        if (bytes <= 0) return "size unavailable";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return $"{bytes:0.#} {units[unit]}";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (T value in values) target.Add(value);
    }

    private static WidgetInstance CloneWidget(WidgetInstance source) => new()
    {
        Id = source.Id, Type = source.Type, Enabled = source.Enabled, Title = source.Title,
        Monitor = source.Monitor, X = source.X, Y = source.Y, ZMode = source.ZMode,
        ClickThrough = source.ClickThrough, KeepOnScreen = source.KeepOnScreen, Locked = source.Locked,
        Opacity = source.Opacity, HideOnFullscreen = source.HideOnFullscreen, RateHz = source.RateHz,
        Graph = new GraphSettings { HistoryS = source.Graph.HistoryS, Height = source.Graph.Height, Style = source.Graph.Style },
        Options = new Dictionary<string, string>(source.Options, StringComparer.Ordinal),
        Metrics = source.Metrics.ToDictionary(pair => pair.Key, pair => new MetricSetting
        {
            Show = pair.Value.Show, Label = pair.Value.Label, Graph = pair.Value.Graph,
            Color = pair.Value.Color, Warn = pair.Value.Warn?.ToArray(), Max = pair.Value.Max,
        }, StringComparer.Ordinal),
        Appearance = new WidgetAppearance
        {
            Colors = source.Appearance.Colors is null ? null : new Dictionary<string, string>(source.Appearance.Colors, StringComparer.Ordinal),
            FontFamily = source.Appearance.FontFamily, Scale = source.Appearance.Scale,
            ShowTitle = source.Appearance.ShowTitle, Width = source.Appearance.Width, TempUnit = source.Appearance.TempUnit,
        },
    };

    public void Dispose() => _session.Dispose();
}
