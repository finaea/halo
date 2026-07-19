using System.Runtime.InteropServices;

namespace Halo.Collector.Providers;

/// <summary>
/// P/Invoke surface for PresentMonAPI2.dll (Intel PresentMon 2 SDK middleware, MIT).
/// Mirrors tools\presentmon\sdk\PresentMonAPI.h (API version 3.3); only the calls and
/// metrics Halo uses are bound. The DLL is loaded from tools\presentmon\sdk via a
/// DllImportResolver (nothing is copied next to the exe; portable-first).
/// </summary>
internal static unsafe class PmApi
{
    private const string Dll = "PresentMonAPI2";
    private static string? _dllPath;
    private static bool _resolverHooked;

    /// <summary>Point the resolver at the bundled DLL. Safe to call repeatedly.</summary>
    public static void UseDll(string dllPath)
    {
        _dllPath = dllPath;
        if (_resolverHooked) return;
        _resolverHooked = true;
        NativeLibrary.SetDllImportResolver(typeof(PmApi).Assembly, (name, _, _) =>
            name == Dll && _dllPath != null && NativeLibrary.TryLoad(_dllPath, out nint h) ? h : 0);
    }

    // ---- PM_STATUS (subset; full list in PresentMonAPI.h) ----
    public const int Ok = 0;
    public const int StatusInvalidPid = 6; // PM_STATUS_INVALID_PID

    public static string StatusName(int s) => s switch
    {
        0 => "SUCCESS", 1 => "FAILURE", 2 => "BAD_ARGUMENT", 3 => "BAD_HANDLE",
        4 => "SERVICE_ERROR", 5 => "INVALID_ETL_FILE", 6 => "INVALID_PID",
        7 => "ALREADY_TRACKING_PROCESS", 8 => "UNABLE_TO_CREATE_NSM", 9 => "INVALID_ADAPTER_ID",
        10 => "OUT_OF_RANGE", 11 => "INSUFFICIENT_BUFFER", 12 => "PIPE_ERROR",
        13 => "SESSION_NOT_OPEN", 14 => "MIDDLEWARE_MISSING_PATH", 15 => "NONEXISTENT_FILE_PATH",
        16 => "MIDDLEWARE_INVALID_SIGNATURE", 17 => "MIDDLEWARE_MISSING_ENDPOINT",
        18 => "MIDDLEWARE_VERSION_LOW", 19 => "MIDDLEWARE_VERSION_HIGH",
        20 => "MIDDLEWARE_SERVICE_MISMATCH", 21 => "QUERY_MALFORMED", 22 => "MODE_MISMATCH",
        23 => "FEATURE_DISABLED", _ => $"UNKNOWN({s})",
    };

    // ---- PM_METRIC (values from the header's implicit enum numbering) ----
    public enum Metric : int
    {
        ClickToPhotonLatency = 25,     // PM_METRIC_CLICK_TO_PHOTON_LATENCY
        FrameType = 63,                // PM_METRIC_FRAME_TYPE
        AllInputToPhotonLatency = 65,  // PM_METRIC_ALL_INPUT_TO_PHOTON_LATENCY
        PresentStartQpc = 77,          // PM_METRIC_PRESENT_START_QPC
        BetweenPresents = 78,          // PM_METRIC_BETWEEN_PRESENTS
        BetweenDisplayChange = 80,     // PM_METRIC_BETWEEN_DISPLAY_CHANGE
        UntilDisplayed = 81,           // PM_METRIC_UNTIL_DISPLAYED
        BetweenSimulationStart = 83,   // PM_METRIC_BETWEEN_SIMULATION_START
    }

    // ---- PM_FRAME_TYPE ----
    public const int FrameNotSet = 0, FrameUnspecified = 1, FrameApplication = 2,
                     FrameRepeated = 3, FrameIntelXeFg = 50, FrameAmdAfmf = 100;

    [StructLayout(LayoutKind.Sequential)]
    public struct QueryElement // PM_QUERY_ELEMENT
    {
        public Metric Metric;
        public int Stat;         // PM_STAT; PM_STAT_NONE for frame queries
        public uint DeviceId;    // 0 = device-independent
        public uint ArrayIndex;
        public ulong DataOffset; // filled by pmRegisterFrameQuery
        public ulong DataSize;   // filled by pmRegisterFrameQuery
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Version // PM_VERSION
    {
        public ushort Major, Minor, Patch;
        public fixed byte Tag[22];
        public fixed byte Hash[8];
        public fixed byte Config[4];
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pmOpenSession(out nint session);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern int pmOpenSessionWithPipe(out nint session, string controlPipeName);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pmCloseSession(nint session);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pmStartTrackingProcess(nint session, uint pid);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pmStopTrackingProcess(nint session, uint pid);

    /// <summary>Delay-1 knob: how often the service flushes ETW buffers (0 = service default).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pmSetEtwFlushPeriod(nint session, uint periodMs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pmRegisterFrameQuery(nint session, out nint query,
        [In, Out] QueryElement[] elements, ulong numElements, out uint blobSize);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pmConsumeFrames(nint query, uint pid, byte[] blobs, ref uint numFrames);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pmFreeFrameQuery(nint query);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pmGetApiVersion(out Version version);
}
