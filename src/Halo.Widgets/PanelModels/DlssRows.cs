using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelModels;

/// <summary>What a DLSS row is saying, before any skin decides how it looks.</summary>
public enum DlssRowKind
{
    /// <summary>No 3D app: the panel's idle state, not a statement about DLSS.</summary>
    Idle,
    /// <summary>The collector could not tell (no NVIDIA driver, a game it could not open).</summary>
    Na,
    /// <summary>The feature's DLL is not in the game.</summary>
    Off,
    /// <summary>The DLL is in the game, but the feature is not running or nothing says it is.</summary>
    Loaded,
    /// <summary>The feature is created and running.</summary>
    Active,
}

/// <summary>A formatted DLSS row: the words and whether they describe a running feature.</summary>
public readonly record struct DlssRow(DlssRowKind Kind, string Text)
{
    public bool IsActive => Kind == DlssRowKind.Active;
}

/// <summary>
/// The SR / RR / FG / FG MULT rows of the Latency panel, decided once for every skin ("a skin owns
/// how a panel looks, never what it measures"). Rainformer and the block-layout skins format the
/// same <see cref="DlssRow"/>; only colour and idle vocabulary differ per skin.
///
/// <para>Row text: running → <c>Preset D · Ultra Perf.</c> (preset and mode only exist while an
/// NVIDIA App override applies them); loaded → <c>loaded · 310.3.0</c>; DLL absent → <c>off</c>.</para>
/// </summary>
public static class DlssRows
{
    /// <summary>The three features in panel order: catalog key, then the collector's metrics.</summary>
    public static readonly (string Key, string Present, string Active, string Preset, string Mode)[] Features =
    [
        ("sr", MetricNames.DlssSrPresent, MetricNames.DlssSrActive, MetricNames.DlssSrPreset, MetricNames.DlssSrMode),
        ("rr", MetricNames.DlssRrPresent, MetricNames.DlssRrActive, MetricNames.DlssRrPreset, MetricNames.DlssRrMode),
        ("fg", MetricNames.DlssFgPresent, MetricNames.DlssFgActive, MetricNames.DlssFgPreset, MetricNames.DlssFgMode),
    ];

    /// <summary>The collector publishes these at 1 Hz; 3 s is the panel's own idle window.</summary>
    private const double MaxAgeS = 3;

    /// <summary>Above this the FG MULT row reads as "generating" (the old FRAME GEN threshold).</summary>
    public const double GeneratingAbove = 1.15;

    public static DlssRow Feature(PanelContext c, string key)
    {
        var f = Array.Find(Features, x => x.Key == key);
        if (f.Key == null) throw new ArgumentException($"not a DLSS feature row: {key}", nameof(key));
        if (Models.FpsIdle(c)) return new(DlssRowKind.Idle, "—");
        if (!c.Metrics.TryValue(f.Present, out double present, MaxAgeS)) return new(DlssRowKind.Na, "N/A");
        if (present < 0.5) return new(DlssRowKind.Off, "off");

        if (c.Metrics.TryValue(f.Active, out double active, MaxAgeS) && active >= 0.5)
        {
            string? preset = c.Metrics.TryValue(f.Preset, out double p, MaxAgeS) ? PresetLabel(p) : null;
            string? mode = c.Metrics.TryValue(f.Mode, out double m, MaxAgeS)
                ? key == "fg" ? FgModeLabel(m) : ModeLabel(m)
                : null;
            string text = preset != null && mode != null ? $"{preset} · {mode}"
                : preset ?? mode ?? "active";
            return new(DlssRowKind.Active, text);
        }

        string version = c.Metrics.Text(MetricNames.DlssVersion);
        return new(DlssRowKind.Loaded, version.Length > 0 ? $"loaded · {version}" : "loaded");
    }

    /// <summary>The measured multiplier, "1.8×". Active = frames are being generated.</summary>
    public static DlssRow Multiplier(PanelContext c)
    {
        if (Models.FpsIdle(c)) return new(DlssRowKind.Idle, "—");
        if (!c.Metrics.TryValue(MetricNames.FpsFgMultiplier, out double v, MaxAgeS)) return new(DlssRowKind.Na, "N/A");
        return new(v > GeneratingAbove ? DlssRowKind.Active : DlssRowKind.Loaded, $"{ValueFormat.Fixed(v, 1)}×");
    }

    /// <summary>NGX render preset number → "Preset K". 0 is "the game's default" and has no letter.</summary>
    public static string? PresetLabel(double v)
    {
        long n = (long)Math.Round(v);
        return n switch
        {
            0 => null,
            >= 1 and <= 15 => $"Preset {(char)('A' + n - 1)}",
            0x00FFFFFF => "Preset Rec.", // NVIDIA's "Recommended"
            _ => $"Preset #{n}",
        };
    }

    /// <summary>NGX performance mode (the runtime enum, not the DLSS-override setting's — they number
    /// differently) → NVIDIA overlay's short words.</summary>
    public static string ModeLabel(double v) => (long)Math.Round(v) switch
    {
        0 => "Perf",
        1 => "Balanced",
        2 => "Quality",
        3 => "Ultra Perf.",
        5 => "DLAA",
        6 => "Custom",
        var n => $"#{n}",
    };

    public static string FgModeLabel(double v) => (long)Math.Round(v) switch
    {
        0 => "Off",
        1 => "Fixed",
        2 => "Auto",
        3 => "Dynamic",
        var n => $"#{n}",
    };
}
