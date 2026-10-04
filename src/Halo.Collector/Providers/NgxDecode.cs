namespace Halo.Collector.Providers;

/// <summary>One DLSS feature's state as the panels need it. Null = unknown, which the provider
/// turns into "fall back to the DLL scan" (present) or N/A (active, preset, mode).</summary>
public readonly record struct DlssFeatureState(bool? Present, bool? Active, uint? Preset, uint? Mode)
{
    public static readonly DlssFeatureState Unknown = new(null, null, null, null);
}

/// <summary>
/// Turns an <see cref="NgxOverrideState"/> into per-feature SR / RR / FG states. Pure, so the
/// rules are pinned by tests against the live Cyberpunk 2077 reading of 2026-10-04 (SR 0x1f,
/// RR 0x67f, FG 0x263f/0x223f).
///
/// <para><b>Active is CREATED, not EVALUATE.</b> EVALUATE ("override engaged" in NVIDIA's own
/// plugin) flickers with dynamic frame generation — on for 3 of 6 polls two seconds apart while
/// FG was plainly running — so a row keyed on it would blink. CREATED held steady.</para>
///
/// <para><b>SR and RR share <c>renderPreset</c> and <c>performanceMode</c>.</b> A value goes to
/// whichever of the two has its PRESET / PERF_MODE bit set; both get it if both do. Live, only RR
/// had them while SR was loaded but never created, and the value matched RR's saved preset.</para>
/// </summary>
public static class NgxDecode
{
    public const int BitDllLoaded = 3;
    public const int BitPreset = 5;
    public const int BitPerfMode = 6;
    public const int BitCreated = 9;
    public const int BitEvaluate = 10;
    public const int BitFgMode = 13;
    /// <summary>ERR_FAILED, ERR_DENIED, ERR_DRS, ERR_NOT_FOUND, ERR_DLL_LOAD (bits 16–20).</summary>
    public const ulong ErrorMask = 0x1FUL << 16;

    private static bool Has(ulong mask, int bit) => (mask & (1UL << bit)) != 0;

    public static (DlssFeatureState Sr, DlssFeatureState Rr, DlssFeatureState Fg) Decode(in NgxOverrideState s)
        => (Feature(s.SrMask, s.RenderPreset, s.PerformanceMode, BitPerfMode),
            Feature(s.RrMask, s.RenderPreset, s.PerformanceMode, BitPerfMode),
            Feature(s.FgMask, s.FrameGenerationPreset, s.FrameGenerationMode, BitFgMode));

    private static DlssFeatureState Feature(ulong mask, uint preset, uint mode, int modeBit)
    {
        // no record for this feature: NVAPI knows nothing, the DLL scan decides
        if (mask == 0) return DlssFeatureState.Unknown;
        bool loaded = Has(mask, BitDllLoaded);
        // an override that failed (couldn't load its DLL, was denied…): the game may well be
        // running its own copy, so what it applied is unknown — let the scan say whether it's there
        if ((mask & ErrorMask) != 0) return new(loaded ? true : null, null, null, null);
        bool active = loaded && Has(mask, BitCreated);
        return new(
            loaded,
            active,
            active && Has(mask, BitPreset) && preset != 0 ? preset : null,
            active && Has(mask, modeBit) ? mode : null);
    }

    /// <summary>"310.3.0" from a file version's parts — the raw FileVersion string reads
    /// "310,3,0,0" on NVIDIA's DLLs.</summary>
    public static string FormatVersion(int major, int minor, int build) => $"{major}.{minor}.{build}";
}
