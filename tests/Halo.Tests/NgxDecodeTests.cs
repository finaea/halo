using Halo.Collector.Providers;

namespace Halo.Tests;

/// <summary>
/// <see cref="NgxDecode"/> turns NVIDIA's per-process NGX override state into the SR / RR / FG rows.
/// The anchor is a live reading, not an invented one: Cyberpunk 2077 under NVIDIA App overrides on
/// 2026-10-04 (driver 616.92), which NVIDIA's own DLSS statistics view showed as SR inactive,
/// RR Preset D at Ultra Performance, FG Preset A in Dynamic mode. Six polls two seconds apart
/// returned SR 0x1f and RR 0x67f every time, and FG alternating 0x263f / 0x223f — only the
/// EVALUATE bit moving, while frame generation was plainly running.
/// </summary>
public class NgxDecodeTests
{
    private static NgxOverrideState Cyberpunk(ulong fgMask) => new(
        SrMask: 0x1f, RrMask: 0x67f, FgMask: fgMask, ScalingRatio: 0,
        PerformanceMode: 3, RenderPreset: 4, FrameGenerationCount: 0, FrameGenerationPreset: 1, FrameGenerationMode: 3);

    [Fact]
    public void Cyberpunk_SrIsLoadedButNotRunning()
    {
        var (sr, _, _) = NgxDecode.Decode(Cyberpunk(0x263f));
        Assert.Equal(new DlssFeatureState(Present: true, Active: false, Preset: null, Mode: null), sr);
    }

    [Fact]
    public void Cyberpunk_RrRunsPresetDAtUltraPerformance()
    {
        // the shared renderPreset/performanceMode belong to RR: only RR has PRESET and PERF_MODE set
        var (_, rr, _) = NgxDecode.Decode(Cyberpunk(0x263f));
        Assert.Equal(new DlssFeatureState(Present: true, Active: true, Preset: 4u, Mode: 3u), rr);
    }

    [Theory]
    [InlineData(0x263fUL)] // EVALUATE set
    [InlineData(0x223fUL)] // EVALUATE clear: same row — keying on EVALUATE would blink every couple of seconds
    public void Cyberpunk_FgRunsPresetADynamic_WhicheverWayEvaluateFlickers(ulong fgMask)
    {
        var (_, _, fg) = NgxDecode.Decode(Cyberpunk(fgMask));
        Assert.Equal(new DlssFeatureState(Present: true, Active: true, Preset: 1u, Mode: 3u), fg);
    }

    [Fact]
    public void ZeroMask_IsUnknown_SoTheDllScanDecides()
    {
        var (sr, rr, fg) = NgxDecode.Decode(new NgxOverrideState(0, 0, 0, 0, 3, 4, 0, 1, 3));
        Assert.Equal(DlssFeatureState.Unknown, sr);
        Assert.Equal(DlssFeatureState.Unknown, rr);
        Assert.Equal(DlssFeatureState.Unknown, fg);
    }

    [Fact]
    public void OverrideRecordButDllNotLoaded_IsOff()
    {
        // INITIALIZED | ENABLED | DLL_EXISTS: the override is set up, the game never loaded the feature
        var (_, _, fg) = NgxDecode.Decode(new NgxOverrideState(0, 0, 0b111, 0, 0, 0, 0, 1, 3));
        Assert.Equal(new DlssFeatureState(Present: false, Active: false, Preset: null, Mode: null), fg);
    }

    [Theory]
    [InlineData(1L << 16)] // ERR_FAILED
    [InlineData(1L << 20)] // ERR_DLL_LOAD
    public void ErrorBits_LeaveWhatWasAppliedUnknown(long errorBit)
    {
        // loaded: present stays true, but an override that failed says nothing about preset/mode
        ulong loaded = 0x67f | (ulong)errorBit;
        var (_, rr, _) = NgxDecode.Decode(new NgxOverrideState(0, loaded, 0, 0, 3, 4, 0, 0, 0));
        Assert.Equal(new DlssFeatureState(Present: true, Active: null, Preset: null, Mode: null), rr);

        // not loaded: the game may be running its own copy, so the scan decides even "present"
        var (sr, _, _) = NgxDecode.Decode(new NgxOverrideState(0b111 | (ulong)errorBit, 0, 0, 0, 3, 4, 0, 0, 0));
        Assert.Equal(DlssFeatureState.Unknown, sr);
    }

    [Fact]
    public void BothSrAndRrClaimingThePreset_BothGetIt()
    {
        var (sr, rr, _) = NgxDecode.Decode(new NgxOverrideState(0x67f, 0x67f, 0, 0, 2, 11, 0, 0, 0));
        Assert.Equal(11u, sr.Preset);
        Assert.Equal(11u, rr.Preset);
        Assert.Equal(2u, sr.Mode);
        Assert.Equal(2u, rr.Mode);
    }

    [Fact]
    public void CreatedWithoutPresetOrModeBits_IsActiveWithNeither()
    {
        // INITIALIZED..DLL_SELECTED + CREATED, no PRESET / PERF_MODE: running at the game's own choice
        var (sr, _, _) = NgxDecode.Decode(new NgxOverrideState(0x21f, 0, 0, 0, 3, 11, 0, 0, 0));
        Assert.Equal(new DlssFeatureState(Present: true, Active: true, Preset: null, Mode: null), sr);
    }

    [Fact]
    public void PresetZero_IsTheDefaultAndHasNoLetter()
    {
        var (_, rr, _) = NgxDecode.Decode(new NgxOverrideState(0, 0x67f, 0, 0, 3, 0, 0, 0, 0));
        Assert.Null(rr.Preset);
        Assert.Equal(3u, rr.Mode);
    }

    [Fact]
    public void FgModeNeedsItsOwnBit_NotPerfMode()
    {
        // FG with PERF_MODE (bit 6) but not FG_MODE (bit 13): the FG mode field is not an applied value
        var (_, _, fg) = NgxDecode.Decode(new NgxOverrideState(0, 0, 0x67f, 0, 0, 0, 0, 1, 3));
        Assert.Null(fg.Mode);
        Assert.Equal(1u, fg.Preset);
    }

    [Fact]
    public void Version_IsDotted_NotTheRawCommaString()
        => Assert.Equal("310.3.0", NgxDecode.FormatVersion(310, 3, 0));
}
