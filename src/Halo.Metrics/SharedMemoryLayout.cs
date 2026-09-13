namespace Halo.Metrics;

/// <summary>
/// Layout of the «Halo.Metrics.v2» shared-memory section. The byte tables are documented for
/// third parties in docs\metrics-protocol.md — keep the two in sync.
///
/// The constants below are the values the <b>writer</b> stamps into the header. Readers must
/// take every offset and capacity from the header they mapped, never from these constants:
/// that is what lets a minor version add regions without breaking anyone.
///
/// Concurrency model (x64 only):
///  - Double value slots are 8-byte aligned; 8-byte aligned loads/stores are atomic on x64,
///    so doubles are written value-first then timestamp (release) — no global lock needed.
///  - String slots carry a per-slot version counter (odd = writer inside), seqlock style.
///  - The frame ring is append-only: entries are written, then the monotonic cursor is
///    published with a release store. Readers never see partially written entries.
///  - Registry is append-only; RegistryCount published with release semantics after the
///    entry is fully written.
/// </summary>
public static class SharedMemoryLayout
{
    public const string SectionName = "Local\\Halo.Metrics.v2";

    /// <summary>Auto-reset event set by the writer after frame-ring appends; widgets may wait
    /// on it to repaint frame graphs immediately instead of on their poll tick. Fire-and-forget:
    /// a missing/ignored event degrades to pure polling on both sides. Versioned with the
    /// section so a v1 consumer can never wake on a section it cannot read.</summary>
    public const string FramesReadyEventName = "Local\\Halo.FramesReady.v2";

    public const uint Magic = 0x4F4C4148;           // "HALO"

    /// <summary>Same major = layout-compatible (offsets come from the header). A breaking change
    /// bumps the major AND the section name. Minor versions may only claim reserved bytes.</summary>
    public const ushort VersionMajor = 2;
    public const ushort VersionMinor = 0;

    // ---- capacities the writer allocates ----
    public const int MaxMetrics = 1024;
    public const int NameBytes = 64;                // UTF-8, NUL padded; writer rejects longer names
    public const int StringValueBytes = 64;         // UTF-8, NUL padded
    public const int MaxStringMetrics = 256;
    public const int FrameRingCapacity = 8192;
    public const int MaxProviders = 32;
    public const int ProviderNameBytes = 24;
    public const int ProviderErrorBytes = 16;
    public const int CollectorVersionBytes = 16;

    // ---- Header (offset 0, 256 bytes) ----
    public const int HeaderSize = 256;
    public const int OffMagic = 0;
    public const int OffVersionMajor = 4;           // u16
    public const int OffVersionMinor = 6;           // u16
    public const int OffHeartbeatQpc = 8;           // i64, writer stamps ~1 Hz
    public const int OffQpcFrequency = 16;          // i64
    public const int OffRegistryCount = 24;         // i32, append-only, release-published
    public const int OffCollectorPid = 28;          // i32
    public const int OffFrameCursor = 32;           // u64, monotonic
    public const int OffCollectorStartQpc = 40;     // i64 — readers drop cached name→index when it changes
    public const int OffReady = 48;                 // u32
    public const int OffHeaderSize = 52;            // u32
    public const int OffProvidersOffset = 56;       // u32
    public const int OffProviderCapacity = 60;      // u32
    public const int OffProviderEntrySize = 64;     // u32
    public const int OffRegistryOffset = 68;        // u32
    public const int OffRegistryCapacity = 72;      // u32
    public const int OffRegistryEntrySize = 76;     // u32
    public const int OffValuesOffset = 80;          // u32
    public const int OffValueEntrySize = 84;        // u32
    public const int OffStringsOffset = 88;         // u32
    public const int OffStringCapacity = 92;        // u32
    public const int OffStringEntrySize = 96;       // u32
    public const int OffStringValueBytes = 100;     // u32
    public const int OffFrameRingOffset = 104;      // u32
    public const int OffFrameRingCapacity = 108;    // u32
    public const int OffFrameEntrySize = 112;       // u32
    public const int OffTotalSize = 116;            // u32
    public const int OffCollectorVersion = 120;     // byte[16], UTF-8 semver, NUL padded
    // 136..255 reserved (zero) — minor versions may claim from here

    // ---- Provider table (32 x 64 B) ----
    // 0 byte[24] name · 24 u8 state · 25 u8 needsElevation · 26 u16 reserved · 28 f32 rateHz
    // 32 i64 lastPollQpc · 40 f32 lastPollMs · 44 byte[16] lastError · 60..63 reserved
    public const int ProvidersOffset = HeaderSize;
    public const int ProviderEntrySize = 64;
    public const int ProvOffName = 0;
    public const int ProvOffState = 24;
    public const int ProvOffNeedsElevation = 25;
    public const int ProvOffRateHz = 28;
    public const int ProvOffLastPollQpc = 32;
    public const int ProvOffLastPollMs = 40;
    public const int ProvOffLastError = 44;
    public const int ProvidersSize = MaxProviders * ProviderEntrySize;

    // ---- Registry (append-only array of registry entries, 128 B each) ----
    // 0 u64 idHash · 8 byte[64] name · 72 u8 type · 73 u8 unit · 74 u8 semantics · 75 u8 flags
    // 76 u16 stringSlot (0xFFFF none) · 78 u16 providerIndex · 80 f32 nominalRateHz
    // 84 f32 effectiveRateHz · 88 u32 windowMs · 92..127 reserved
    public const int RegistryOffset = ProvidersOffset + ProvidersSize;
    public const int RegistryEntrySize = 128;
    public const int RegOffIdHash = 0;
    public const int RegOffName = 8;
    public const int RegOffType = 72;
    public const int RegOffUnit = 73;
    public const int RegOffSemantics = 74;
    public const int RegOffFlags = 75;
    public const int RegOffStringSlot = 76;
    public const int RegOffProviderIndex = 78;
    public const int RegOffNominalRateHz = 80;
    public const int RegOffEffectiveRateHz = 84;
    public const int RegOffWindowMs = 88;
    public const int RegistrySize = MaxMetrics * RegistryEntrySize;

    // ---- Values (index-parallel to registry) ----
    public const int ValuesOffset = RegistryOffset + RegistrySize;
    public const int ValueEntrySize = 16;           // 0 f64 value · 8 i64 timestampQpc (0 = N/A)
    public const int ValuesSize = MaxMetrics * ValueEntrySize;

    // ---- String values ----
    public const int StringsOffset = ValuesOffset + ValuesSize;
    public const int StringEntrySize = 8 + StringValueBytes;   // 0 u32 seq · 4 u32 length · 8 bytes[64]
    public const int StringsSize = MaxStringMetrics * StringEntrySize;

    // ---- Frame ring ----
    public const int FrameRingOffset = StringsOffset + StringsSize;
    public const int FrameEntrySize = 24;           // 0 i64 qpc · 8 f32 frametimeMs · 12 f32 displayedFtMs · 16 u32 flags · 20 u32 pid
    public const int FrameRingSize = FrameRingCapacity * FrameEntrySize;

    public const int TotalSize = FrameRingOffset + FrameRingSize;

    public const ushort NoStringSlot = 0xFFFF;
}

[Flags]
public enum FrameFlags : uint
{
    None = 0,
    Displayed = 1,      // frame reached the screen (has valid displayedFtMs)
    Dropped = 2,        // presented but never displayed
    AppFrame = 4,       // FrameType == Application (rendered, not generated)
    Generated = 8,      // frame-generation frame (DLSS-G etc.)
    Repeated = 16,      // FrameType == Repeated
    Provisional = 32,   // door-1 tap lane: present observed, fate unknown (never revised)
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
public struct FrameEntry
{
    public long Qpc;             // present start QPC
    public float FrametimeMs;    // CPU present-to-present (msBetweenPresents)
    public float DisplayedFtMs;  // display-to-display time, 0 if not displayed
    public uint Flags;           // FrameFlags
    public uint Pid;
}
