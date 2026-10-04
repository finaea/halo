using System.Runtime.InteropServices;

namespace Halo.Collector.Providers;

/// <summary>
/// The one NVAPI call the DLSS rows need: <c>NvAPI_NGX_GetNGXOverrideState</c>, the per-process
/// NGX override state NVIDIA's own overlay polls for its DLSS view (NVIDIA App's NvPerfMon.dll
/// calls exactly this). Public API — <c>nvapi.h</c> in github.com/NVIDIA/nvapi, MIT, R570+.
///
/// nvapi64.dll ships with the NVIDIA driver; it exports a single symbol, <c>nvapi_QueryInterface</c>,
/// and every function is looked up by the 32-bit id listed in <c>nvapi_interface.h</c>.
///
/// <para><b>Initialize and Unload are reference-counted and must be paired</b> ("NvAPI_Unload:
/// Decrements the ref-counter … must be called in pairs with NvAPI_Initialize"). The LHM GPU part
/// uses NVAPI in this same process, so one Initialize per instance and exactly one Unload in
/// <see cref="Dispose"/> — an extra Unload would take NVAPI away from LHM mid-poll.</para>
/// </summary>
internal sealed class NvApi : IDisposable
{
    public const int Ok = 0;
    public const int NvidiaDeviceNotFound = -6;
    public const int IncompatibleStructVersion = -9;
    public const int DataNotFound = -121;

    private const uint IdInitialize = 0x0150e828;
    private const uint IdUnload = 0xd22bdd7e;
    private const uint IdGetErrorMessage = 0x6c2d048c;
    private const uint IdNgxGetOverrideState = 0x3fd96fba;

    // NV_NGX_DLSS_OVERRIDE_GET_STATE_PARAMS: V2 is 96 bytes, V1 the first 56 plus reserved[2] = 64.
    // MAKE_NVAPI_VERSION(type, ver) = sizeof(type) | ver << 16.
    private const uint StateParamsV2 = 96 | 2 << 16;
    private const uint StateParamsV1 = 64 | 1 << 16;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint QueryInterfaceFn(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ErrorMessageFn(int status, [Out] byte[] shortString);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetOverrideStateFn(ref StateParams p);

    /// <summary>The V2 layout, sized for V2 so a V1 call (which writes less) can share it.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 96)]
    private struct StateParams
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public uint ProcessIdentifier;
        [FieldOffset(8)] public ulong FeedbackMaskSr;
        [FieldOffset(16)] public ulong FeedbackMaskRr;
        [FieldOffset(24)] public ulong FeedbackMaskFg;
        [FieldOffset(32)] public float ScalingRatio;
        [FieldOffset(36)] public uint PerformanceMode;
        [FieldOffset(40)] public uint RenderPreset;
        [FieldOffset(44)] public uint FrameGenerationCount;
        [FieldOffset(48)] public uint FrameGenerationPreset;
        [FieldOffset(52)] public uint FrameGenerationMode;
    }

    private readonly VoidFn _unload;
    private readonly ErrorMessageFn? _errorMessage;
    private readonly GetOverrideStateFn? _getOverrideState;
    private uint _stateVersion = StateParamsV2;
    private bool _disposed;

    private NvApi(VoidFn unload, ErrorMessageFn? errorMessage, GetOverrideStateFn? getOverrideState)
    {
        _unload = unload;
        _errorMessage = errorMessage;
        _getOverrideState = getOverrideState;
    }

    /// <summary>False on a driver older than R570, where the override-state call does not exist.</summary>
    public bool HasNgxOverrideState => _getOverrideState != null;

    /// <summary>
    /// Load and initialise NVAPI. Null when there is no NVIDIA driver (nvapi64.dll absent) or no
    /// NVIDIA GPU; <paramref name="detail"/> says which.
    /// </summary>
    public static NvApi? TryOpen(out string detail)
    {
        if (!NativeLibrary.TryLoad("nvapi64.dll", out nint lib)
            || !NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out nint qiPtr))
        {
            detail = "nvapi64.dll not found (no NVIDIA driver)";
            return null;
        }
        var qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(qiPtr);
        T? Fn<T>(uint id) where T : Delegate
        {
            nint p = qi(id);
            return p == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        var initialize = Fn<VoidFn>(IdInitialize);
        var unload = Fn<VoidFn>(IdUnload);
        if (initialize == null || unload == null)
        {
            detail = "nvapi64.dll has no NvAPI_Initialize/NvAPI_Unload";
            return null;
        }
        var errorMessage = Fn<ErrorMessageFn>(IdGetErrorMessage);
        int st = initialize();
        if (st != Ok)
        {
            // no Unload: a failed Initialize took no reference
            detail = $"NvAPI_Initialize: {Describe(errorMessage, st)}";
            return null;
        }
        detail = "";
        return new NvApi(unload, errorMessage, Fn<GetOverrideStateFn>(IdNgxGetOverrideState));
    }

    /// <summary>
    /// Ask the driver for <paramref name="pid"/>'s NGX override state. Returns the NVAPI status:
    /// <see cref="Ok"/> with <paramref name="state"/> filled, <see cref="DataNotFound"/> for a
    /// process NGX has no override record for (any game without an NVIDIA App override, any
    /// non-DLSS process), anything else is a real error. ~30–100 µs (measured 2026-10-04).
    /// </summary>
    public int GetNgxOverrideState(int pid, out NgxOverrideState state)
    {
        state = default;
        if (_getOverrideState == null || _disposed) return -3; // NVAPI_NO_IMPLEMENTATION
        var p = new StateParams { Version = _stateVersion, ProcessIdentifier = (uint)pid };
        int st = _getOverrideState(ref p);
        if (st == IncompatibleStructVersion && _stateVersion == StateParamsV2)
        {
            // an R570-era driver that only knows V1; remember so the next call goes straight there
            _stateVersion = StateParamsV1;
            p = new StateParams { Version = _stateVersion, ProcessIdentifier = (uint)pid };
            st = _getOverrideState(ref p);
        }
        if (st == Ok)
            state = new NgxOverrideState(p.FeedbackMaskSr, p.FeedbackMaskRr, p.FeedbackMaskFg, p.ScalingRatio,
                p.PerformanceMode, p.RenderPreset, p.FrameGenerationCount, p.FrameGenerationPreset, p.FrameGenerationMode);
        return st;
    }

    /// <summary>"-121 NVAPI_DATA_NOT_FOUND"-style text for a log line.</summary>
    public string Describe(int status) => Describe(_errorMessage, status);

    private static string Describe(ErrorMessageFn? errorMessage, int status)
    {
        if (errorMessage == null) return status.ToString();
        var buf = new byte[64]; // NvAPI_ShortString
        try { errorMessage(status, buf); } catch { return status.ToString(); }
        int len = Array.IndexOf(buf, (byte)0);
        return $"{status} {System.Text.Encoding.ASCII.GetString(buf, 0, len < 0 ? buf.Length : len)}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _unload(); // the one Unload that pairs with this instance's Initialize
    }
}

/// <summary>What <c>NvAPI_NGX_GetNGXOverrideState</c> returned for one process: a 64-bit feedback
/// mask per feature plus the values the override applied. SR and RR share one preset and one
/// performance-mode field.</summary>
public readonly record struct NgxOverrideState(
    ulong SrMask,
    ulong RrMask,
    ulong FgMask,
    float ScalingRatio,
    uint PerformanceMode,
    uint RenderPreset,
    uint FrameGenerationCount,
    uint FrameGenerationPreset,
    uint FrameGenerationMode);
