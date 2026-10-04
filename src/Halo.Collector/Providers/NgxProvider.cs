using System.Diagnostics;
using Halo.Metrics;
using Halo.Shared;

namespace Halo.Collector.Providers;

/// <summary>
/// DLSS state of the game the frame pipeline is tracking: which of Super Resolution, Ray
/// Reconstruction and Frame Generation it has loaded, which are running, and — when an NVIDIA App
/// override is applying them — the preset and mode. Owns every <c>dlss.*</c> metric.
///
/// <para>Two sources, in order. <b>NVAPI</b> (<see cref="NvApi"/>, 1 Hz, ~0.1 ms): the same
/// per-process NGX override state NVIDIA's overlay shows in its DLSS view. It answers only for a
/// game an override applies to, and it is the only thing that sees an override at all — the
/// override swaps the game's nvngx_dlss*.dll for a <c>.bin</c> from
/// <c>ProgramData\NVIDIA\NGX\models</c>, so a module scan finds nothing (live, 2026-10-04:
/// Cyberpunk 2077 ran RR and FG while the old scan published "not loaded"). <b>The DLL scan</b>
/// (every 10 s and on a target change) covers every feature NVAPI has no record for, i.e. any
/// game without an override. Nothing public reports a game's own preset, so without an override
/// preset and mode stay N/A — NVIDIA's overlay shows "Off" there too.</para>
///
/// <para>The target pid travels through the section, read back from <c>fps.app.pid</c> exactly as
/// <see cref="PclStatsProvider"/> does: the providers run on their own threads and share nothing
/// else.</para>
/// </summary>
public sealed class NgxProvider : ISensorProvider
{
    private static readonly ComponentLog Log2 = Log.For("ngx");

    public string Name => "ngx";
    public double MaxRateHz => 2;
    public double DefaultRateHz => CollectorRates.Ngx;

    /// <summary>NVAPI answers unelevated. The DLL-scan fallback can't open some games without
    /// admin and degrades to N/A for those, which is not a reason to call the provider down.</summary>
    public bool NeedsElevation => false;

    public string? UnavailableReason => _unavailableReason;
    private string? _unavailableReason;

    /// <summary>The DLL scan opens the game process and walks its modules (a few ms), so it runs at
    /// this cadence plus once on every target change, never per poll.</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);
    private const double ScanRateHz = 1.0 / 10;

    private static readonly (string Present, string Active, string Preset, string? Mode)[] Features =
    [
        (MetricNames.DlssSrPresent, MetricNames.DlssSrActive, MetricNames.DlssSrPreset, MetricNames.DlssSrMode),
        (MetricNames.DlssRrPresent, MetricNames.DlssRrActive, MetricNames.DlssRrPreset, MetricNames.DlssRrMode),
        (MetricNames.DlssFgPresent, MetricNames.DlssFgActive, MetricNames.DlssFgPreset, MetricNames.DlssFgMode),
    ];

    private NvApi? _nvapi;
    private int _pid;
    private DateTime _nextScan = DateTime.MinValue;
    private ScanResult? _scan;              // last DLL scan of _pid; null = none yet, or it failed
    private int _lastStatusLogged = int.MinValue;
    private bool _scanFailureLogged;        // a protected game fails every 10 s; say so once per target

    private readonly record struct ScanResult(bool Sr, bool Rr, bool Fg, string Version);

    public bool Initialize(MetricSink sink)
    {
        foreach (var f in Features)
        {
            sink.Register(f.Present, MetricType.Double, MetricUnit.None, Name, DefaultRateHz);
            sink.Register(f.Active, MetricType.Double, MetricUnit.None, Name, DefaultRateHz);
            sink.Register(f.Preset, MetricType.Double, MetricUnit.None, Name, DefaultRateHz);
        }
        sink.Register(MetricNames.DlssSrMode, MetricType.Double, MetricUnit.None, Name, DefaultRateHz);
        sink.Register(MetricNames.DlssRrMode, MetricType.Double, MetricUnit.None, Name, DefaultRateHz);
        sink.Register(MetricNames.DlssFgMode, MetricType.Double, MetricUnit.None, Name, DefaultRateHz);
        // written only when a scan completes, so it registers the scan's cadence
        sink.Register(MetricNames.DlssVersion, MetricType.String, MetricUnit.Text, Name, ScanRateHz);

        // re-init after poll failures: drop the old reference first, keeping Initialize/Unload paired
        _nvapi?.Dispose();
        _nvapi = NvApi.TryOpen(out string detail);
        if (_nvapi == null)
        {
            // no NVIDIA driver or GPU: there is no DLSS to report, and the scan alone would only
            // ever find a DLL that cannot run here
            Log2.Info($"unavailable: {detail}");
            _unavailableReason = ProviderError.NoHardware;
            return false;
        }
        _unavailableReason = null;
        Log2.Info(_nvapi.HasNgxOverrideState
            ? "NVAPI NGX override state available"
            : "driver older than R570 (no NvAPI_NGX_GetNGXOverrideState): DLL scan only");
        return true;
    }

    public void Poll(MetricSink sink)
    {
        // 0 or stale = no game: the same widening rule PclStatsProvider uses
        int pid = sink.TryGet(MetricNames.FpsAppPid, out double p, maxAgeS: 3) ? (int)p : 0;
        if (pid != _pid)
        {
            _pid = pid;
            _scan = null;
            _nextScan = DateTime.MinValue;
            _lastStatusLogged = int.MinValue;
            _scanFailureLogged = false;
            sink.MarkStale(MetricNames.DlssVersion); // the old game's version is not this one's
        }

        if (pid == 0)
        {
            // nothing tracked: nothing is loaded (a fact, so 0), and nothing else is a reading
            foreach (var f in Features)
            {
                sink.Set(f.Present, 0);
                sink.MarkStale(f.Active);
                sink.MarkStale(f.Preset);
                if (f.Mode != null) sink.MarkStale(f.Mode);
            }
            return;
        }

        var (sr, rr, fg) = ReadNvapi(pid);
        DlssFeatureState[] states = [sr, rr, fg];

        // the scan only matters for features NVAPI said nothing about
        if (states.Any(s => s.Present == null) && DateTime.UtcNow >= _nextScan)
        {
            _nextScan = DateTime.UtcNow + ScanInterval;
            _scan = ScanModules(pid, out string? error);
            if (_scan is { } done) sink.SetString(MetricNames.DlssVersion, done.Version);
            else sink.MarkStale(MetricNames.DlssVersion);
            if (error != null && !_scanFailureLogged)
            {
                _scanFailureLogged = true;
                Log2.Warn($"module scan pid {pid}: {error} — DLSS rows read N/A for features NVAPI has no record of");
            }
        }

        bool?[] scanned = _scan is { } r ? [r.Sr, r.Rr, r.Fg] : [null, null, null];
        for (int i = 0; i < Features.Length; i++)
        {
            var f = Features[i];
            var s = states[i];
            // a scan that could not open the game is "could not look", not "not loaded"
            if ((s.Present ?? scanned[i]) is bool present) sink.Set(f.Present, present ? 1 : 0);
            else sink.MarkStale(f.Present);
            SetOrStale(sink, f.Active, s.Active is bool a ? (a ? 1 : 0) : null);
            SetOrStale(sink, f.Preset, s.Preset);
            if (f.Mode != null) SetOrStale(sink, f.Mode, s.Mode);
        }
    }

    private (DlssFeatureState Sr, DlssFeatureState Rr, DlssFeatureState Fg) ReadNvapi(int pid)
    {
        if (_nvapi is not { HasNgxOverrideState: true })
            return (DlssFeatureState.Unknown, DlssFeatureState.Unknown, DlssFeatureState.Unknown);
        int st = _nvapi.GetNgxOverrideState(pid, out var state);
        if (st != _lastStatusLogged)
        {
            // once per target and status: the normal no-override answer is DATA_NOT_FOUND, logged
            // at Debug; anything else is unexpected and worth a line
            _lastStatusLogged = st;
            string line = st == NvApi.Ok
                ? $"pid {pid}: override state SR=0x{state.SrMask:x} RR=0x{state.RrMask:x} FG=0x{state.FgMask:x}"
                : $"pid {pid}: {_nvapi.Describe(st)}";
            if (st is NvApi.Ok or NvApi.DataNotFound) Log2.Debug(line);
            else Log2.Warn(line);
        }
        return st == NvApi.Ok
            ? NgxDecode.Decode(state)
            : (DlssFeatureState.Unknown, DlssFeatureState.Unknown, DlssFeatureState.Unknown);
    }

    private static void SetOrStale(MetricSink sink, string name, double? value)
    {
        if (value is double v) sink.Set(name, v);
        else sink.MarkStale(name);
    }

    /// <summary>Which DLSS DLLs the game itself has loaded, by file name. Null when the process
    /// can't be opened (a protected or elevated game seen from an unelevated collector, or one
    /// that just exited) — that is "could not look", and the metrics go N/A rather than 0.</summary>
    private static ScanResult? ScanModules(int pid, out string? error)
    {
        bool sr = false, rr = false, fg = false;
        string version = "";
        error = null;
        try
        {
            using var proc = Process.GetProcessById(pid);
            foreach (ProcessModule m in proc.Modules)
            {
                string f = m.ModuleName.ToLowerInvariant();
                if (f.StartsWith("nvngx_dlssg")) fg = true;
                else if (f.StartsWith("nvngx_dlssd")) rr = true;
                else if (f.StartsWith("nvngx_dlss"))
                {
                    sr = true;
                    var v = m.FileVersionInfo;
                    version = NgxDecode.FormatVersion(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        return new ScanResult(sr, rr, fg, version);
    }

    public void Dispose()
    {
        _nvapi?.Dispose();
        _nvapi = null;
    }
}
