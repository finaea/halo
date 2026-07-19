using System.Runtime.InteropServices;

namespace Halo.Shared.Metrics;

/// <summary>
/// Layout of the «Halo.Metrics.v1» shared-memory section.
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
    public const string SectionName = "Local\\Halo.Metrics.v1";
    /// <summary>Auto-reset event set by the writer after frame-ring appends; widgets may wait
    /// on it to repaint frame graphs immediately instead of on their poll tick. Fire-and-forget:
    /// a missing/ignored event degrades to pure polling on both sides.</summary>
    public const string FramesReadyEventName = "Local\\Halo.FramesReady.v1";
    public const uint Magic = 0x4F4C4148;           // "HALO"
    public const uint Version = 1;

    public const int MaxMetrics = 512;
    public const int NameBytes = 48;                // UTF-8, NUL padded
    public const int StringValueBytes = 64;         // UTF-8, NUL padded
    public const int MaxStringMetrics = 128;
    public const int FrameRingCapacity = 8192;

    // ---- Header (offset 0, 128 bytes) ----
    // 0   u32 Magic
    // 4   u32 Version
    // 8   i64 HeartbeatQpc          (writer stamps ~1 Hz; readers detect staleness)
    // 16  i64 QpcFrequency
    // 24  i32 RegistryCount         (append-only, release-published)
    // 28  i32 CollectorPid
    // 32  u64 FrameCursor           (monotonic count of frames ever written, release-published)
    // 40  i64 CollectorStartQpc
    // 48  u32 Ready                 (1 once registry initialised)
    // 52  ... reserved
    public const int HeaderSize = 128;
    public const int OffHeartbeatQpc = 8;
    public const int OffQpcFrequency = 16;
    public const int OffRegistryCount = 24;
    public const int OffCollectorPid = 28;
    public const int OffFrameCursor = 32;
    public const int OffCollectorStartQpc = 40;
    public const int OffReady = 48;

    // ---- Registry (append-only array of RegistryEntry) ----
    public const int RegistryOffset = HeaderSize;
    public const int RegistryEntrySize = 64;
    // entry: 0 u64 idHash · 8 byte[48] name · 56 u8 type · 57 u8 unit · 58 u16 stringSlot(or 0xFFFF) · 60 f32 effectiveRateHz
    public const int RegistrySize = MaxMetrics * RegistryEntrySize;

    // ---- Values (index-parallel to registry) ----
    public const int ValuesOffset = RegistryOffset + RegistrySize;
    public const int ValueEntrySize = 16;           // 0 f64 value · 8 i64 timestampQpc (0 = never written)
    public const int ValuesSize = MaxMetrics * ValueEntrySize;

    // ---- String values ----
    public const int StringsOffset = ValuesOffset + ValuesSize;
    public const int StringEntrySize = 8 + StringValueBytes;   // 0 u32 version · 4 u32 length · 8 bytes[64]
    public const int StringsSize = MaxStringMetrics * StringEntrySize;

    // ---- Frame ring ----
    public const int FrameRingOffset = StringsOffset + StringsSize;
    public const int FrameEntrySize = 24;           // 0 i64 qpc · 8 f32 frametimeMs · 12 f32 displayedFtMs · 16 u32 flags · 20 u32 pid
    public const int FrameRingSize = FrameRingCapacity * FrameEntrySize;

    public const int TotalSize = FrameRingOffset + FrameRingSize;
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

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FrameEntry
{
    public long Qpc;             // present start QPC
    public float FrametimeMs;    // CPU present-to-present (msBetweenPresents)
    public float DisplayedFtMs;  // display-to-display time, 0 if not displayed
    public uint Flags;           // FrameFlags
    public uint Pid;
}
