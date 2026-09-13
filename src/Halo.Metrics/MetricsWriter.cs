using System.Diagnostics;
using System.Text;

namespace Halo.Metrics;

/// <summary>Collector-side writer for the Halo.Metrics.v2 section. Thread-safe.</summary>
public sealed unsafe class MetricsWriter : IDisposable
{
    private readonly NativeSection _section;
    private readonly Dictionary<ulong, int> _indexById = new();
    private readonly Dictionary<string, int> _providerIndexByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _registryLock = new();
    private readonly object _providerLock = new();
    private readonly object _frameLock = new();     // ring has multiple writer threads (poll + tap)
    private readonly EventWaitHandle _framesReady =
        new(false, EventResetMode.AutoReset, SharedMemoryLayout.FramesReadyEventName);
    private readonly Action<string>? _warn;
    private int _stringSlotsUsed;

    private byte* B => _section.Base;

    /// <param name="collectorVersion">Semver stamped into the header (≤ 16 UTF-8 bytes).</param>
    /// <param name="warn">Optional diagnostics sink; the package itself has no logger.</param>
    public MetricsWriter(string collectorVersion = "", Action<string>? warn = null)
    {
        _warn = warn;
        _section = NativeSection.Create(SharedMemoryLayout.SectionName, SharedMemoryLayout.TotalSize);
        new Span<byte>(B, SharedMemoryLayout.TotalSize).Clear();

        *(uint*)(B + SharedMemoryLayout.OffMagic) = SharedMemoryLayout.Magic;
        *(ushort*)(B + SharedMemoryLayout.OffVersionMajor) = SharedMemoryLayout.VersionMajor;
        *(ushort*)(B + SharedMemoryLayout.OffVersionMinor) = SharedMemoryLayout.VersionMinor;
        *(long*)(B + SharedMemoryLayout.OffQpcFrequency) = Stopwatch.Frequency;
        *(int*)(B + SharedMemoryLayout.OffCollectorPid) = Environment.ProcessId;
        *(long*)(B + SharedMemoryLayout.OffCollectorStartQpc) = Stopwatch.GetTimestamp();

        // Every region's location and size lives in the header: readers never compile in offsets.
        *(uint*)(B + SharedMemoryLayout.OffHeaderSize) = SharedMemoryLayout.HeaderSize;
        *(uint*)(B + SharedMemoryLayout.OffProvidersOffset) = SharedMemoryLayout.ProvidersOffset;
        *(uint*)(B + SharedMemoryLayout.OffProviderCapacity) = SharedMemoryLayout.MaxProviders;
        *(uint*)(B + SharedMemoryLayout.OffProviderEntrySize) = SharedMemoryLayout.ProviderEntrySize;
        *(uint*)(B + SharedMemoryLayout.OffRegistryOffset) = SharedMemoryLayout.RegistryOffset;
        *(uint*)(B + SharedMemoryLayout.OffRegistryCapacity) = SharedMemoryLayout.MaxMetrics;
        *(uint*)(B + SharedMemoryLayout.OffRegistryEntrySize) = SharedMemoryLayout.RegistryEntrySize;
        *(uint*)(B + SharedMemoryLayout.OffValuesOffset) = SharedMemoryLayout.ValuesOffset;
        *(uint*)(B + SharedMemoryLayout.OffValueEntrySize) = SharedMemoryLayout.ValueEntrySize;
        *(uint*)(B + SharedMemoryLayout.OffStringsOffset) = SharedMemoryLayout.StringsOffset;
        *(uint*)(B + SharedMemoryLayout.OffStringCapacity) = SharedMemoryLayout.MaxStringMetrics;
        *(uint*)(B + SharedMemoryLayout.OffStringEntrySize) = SharedMemoryLayout.StringEntrySize;
        *(uint*)(B + SharedMemoryLayout.OffStringValueBytes) = SharedMemoryLayout.StringValueBytes;
        *(uint*)(B + SharedMemoryLayout.OffFrameRingOffset) = SharedMemoryLayout.FrameRingOffset;
        *(uint*)(B + SharedMemoryLayout.OffFrameRingCapacity) = SharedMemoryLayout.FrameRingCapacity;
        *(uint*)(B + SharedMemoryLayout.OffFrameEntrySize) = SharedMemoryLayout.FrameEntrySize;
        *(uint*)(B + SharedMemoryLayout.OffTotalSize) = SharedMemoryLayout.TotalSize;
        WriteFixedUtf8(B + SharedMemoryLayout.OffCollectorVersion, SharedMemoryLayout.CollectorVersionBytes, collectorVersion);

        Heartbeat();
    }

    public void MarkReady() => Volatile.Write(ref *(uint*)(B + SharedMemoryLayout.OffReady), 1u);

    public void Heartbeat() => Volatile.Write(ref *(long*)(B + SharedMemoryLayout.OffHeartbeatQpc), Stopwatch.GetTimestamp());

    // ---- provider table ----

    /// <summary>Register (idempotent) a provider row and return its index.</summary>
    public int RegisterProvider(string name, bool needsElevation)
    {
        lock (_providerLock)
        {
            if (_providerIndexByName.TryGetValue(name, out int existing)) return existing;
            int idx = _providerIndexByName.Count;
            if (idx >= SharedMemoryLayout.MaxProviders)
            {
                _warn?.Invoke($"provider table full, '{name}' not published");
                return -1;
            }
            byte* e = ProviderEntry(idx);
            WriteFixedUtf8(e + SharedMemoryLayout.ProvOffName, SharedMemoryLayout.ProviderNameBytes, name);
            e[SharedMemoryLayout.ProvOffState] = (byte)ProviderState.Unavailable;
            e[SharedMemoryLayout.ProvOffNeedsElevation] = needsElevation ? (byte)1 : (byte)0;
            _providerIndexByName[name] = idx;
            return idx;
        }
    }

    /// <summary>Publish a provider's health. <paramref name="lastError"/> is a short code
    /// (see <see cref="ProviderError"/>); "" clears it.</summary>
    public void SetProviderState(int index, ProviderState state, double rateHz, string lastError = "")
    {
        if (index < 0 || index >= SharedMemoryLayout.MaxProviders) return;
        byte* e = ProviderEntry(index);
        *(float*)(e + SharedMemoryLayout.ProvOffRateHz) = (float)rateHz;
        WriteFixedUtf8(e + SharedMemoryLayout.ProvOffLastError, SharedMemoryLayout.ProviderErrorBytes, lastError);
        Volatile.Write(ref e[SharedMemoryLayout.ProvOffState], (byte)state);
    }

    /// <summary>Publish the timing of the poll that just finished.</summary>
    public void SetProviderPoll(int index, long qpc, double elapsedMs)
    {
        if (index < 0 || index >= SharedMemoryLayout.MaxProviders) return;
        byte* e = ProviderEntry(index);
        *(float*)(e + SharedMemoryLayout.ProvOffLastPollMs) = (float)elapsedMs;
        Volatile.Write(ref *(long*)(e + SharedMemoryLayout.ProvOffLastPollQpc), qpc);
    }

    private byte* ProviderEntry(int index)
        => B + SharedMemoryLayout.ProvidersOffset + index * SharedMemoryLayout.ProviderEntrySize;

    // ---- registry ----

    /// <summary>
    /// Register a metric (idempotent on the name hash). Returns the slot index used in Set* calls.
    /// Two providers may legitimately register the same name (e.g. NVML and LHM both feeding
    /// gpu.0.fan.rpm): the first registration owns the descriptor, later ones are no-ops.
    /// A hash hit on a *different* name is a 64-bit FNV-1a collision and throws — aliasing two
    /// metrics onto one slot would publish one sensor's value under the other's name.
    /// </summary>
    public int Register(in MetricDescriptor d)
    {
        ulong id = d.Id;
        lock (_registryLock)
        {
            if (_indexById.TryGetValue(id, out int existing))
            {
                byte* prev = B + SharedMemoryLayout.RegistryOffset + existing * SharedMemoryLayout.RegistryEntrySize;
                string prevName = ReadFixedUtf8(prev + SharedMemoryLayout.RegOffName, SharedMemoryLayout.NameBytes);
                if (!string.Equals(prevName, d.Name, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"metric id collision: '{d.Name}' hashes to the same 64-bit id as the already-registered '{prevName}'. Rename one of them.");
                return existing;
            }

            int count = *(int*)(B + SharedMemoryLayout.OffRegistryCount);
            if (count >= SharedMemoryLayout.MaxMetrics) throw new InvalidOperationException("Metric registry full");

            int nameBytes = Encoding.UTF8.GetByteCount(d.Name);
            if (nameBytes > SharedMemoryLayout.NameBytes - 1)
                throw new ArgumentException($"metric name '{d.Name}' is {nameBytes} bytes; the section allows {SharedMemoryLayout.NameBytes - 1}", nameof(d));

            ushort stringSlot = SharedMemoryLayout.NoStringSlot;
            if (d.Type == MetricType.String)
            {
                if (_stringSlotsUsed >= SharedMemoryLayout.MaxStringMetrics) throw new InvalidOperationException("String slots full");
                stringSlot = (ushort)_stringSlotsUsed++;
            }

            int providerIndex = string.IsNullOrEmpty(d.Provider) ? -1 : RegisterProvider(d.Provider, needsElevation: false);

            byte* e = B + SharedMemoryLayout.RegistryOffset + count * SharedMemoryLayout.RegistryEntrySize;
            *(ulong*)(e + SharedMemoryLayout.RegOffIdHash) = id;
            WriteFixedUtf8(e + SharedMemoryLayout.RegOffName, SharedMemoryLayout.NameBytes, d.Name);
            e[SharedMemoryLayout.RegOffType] = (byte)d.Type;
            e[SharedMemoryLayout.RegOffUnit] = (byte)d.Unit;
            e[SharedMemoryLayout.RegOffSemantics] = (byte)d.Semantics;
            e[SharedMemoryLayout.RegOffFlags] = (byte)d.Flags;
            *(ushort*)(e + SharedMemoryLayout.RegOffStringSlot) = stringSlot;
            *(ushort*)(e + SharedMemoryLayout.RegOffProviderIndex) = (ushort)(providerIndex < 0 ? 0xFFFF : providerIndex);
            *(float*)(e + SharedMemoryLayout.RegOffNominalRateHz) = (float)d.NominalRateHz;
            *(float*)(e + SharedMemoryLayout.RegOffEffectiveRateHz) = (float)d.NominalRateHz;
            *(uint*)(e + SharedMemoryLayout.RegOffWindowMs) = (uint)Math.Max(0, d.WindowMs);

            Volatile.Write(ref *(int*)(B + SharedMemoryLayout.OffRegistryCount), count + 1);
            _indexById[id] = count;
            return count;
        }
    }

    /// <summary>Set a flag bit on an already-registered metric (e.g. HasMaxCompanion).</summary>
    public void AddFlags(int index, MetricFlags flags)
    {
        byte* e = B + SharedMemoryLayout.RegistryOffset + index * SharedMemoryLayout.RegistryEntrySize;
        e[SharedMemoryLayout.RegOffFlags] = (byte)(e[SharedMemoryLayout.RegOffFlags] | (byte)flags);
    }

    public void SetEffectiveRate(int index, float hz)
    {
        byte* e = B + SharedMemoryLayout.RegistryOffset + index * SharedMemoryLayout.RegistryEntrySize;
        *(float*)(e + SharedMemoryLayout.RegOffEffectiveRateHz) = hz;
    }

    // ---- values ----

    /// <summary>Publish a double value. Lock-free; safe from any thread.</summary>
    public void Set(int index, double value)
    {
        byte* v = B + SharedMemoryLayout.ValuesOffset + index * SharedMemoryLayout.ValueEntrySize;
        *(double*)v = value;                                    // atomic 8-byte store on x64
        Volatile.Write(ref *(long*)(v + 8), Stopwatch.GetTimestamp()); // release: timestamp last
    }

    /// <summary>
    /// Read back a value this writer published. Not a substitute for <see cref="MetricsReader"/> —
    /// it exists so the collector can compute a Calc metric from metrics another provider owns
    /// (latency.pc.ms sums a PCL-marker value and a PresentMon one) without a second mapping of
    /// the section. Returns false for a slot that was never written or is marked N/A.
    /// </summary>
    public bool TryRead(int index, out double value, out double ageSeconds)
    {
        value = 0;
        ageSeconds = double.MaxValue;
        if (index < 0 || index >= SharedMemoryLayout.MaxMetrics) return false;
        byte* v = B + SharedMemoryLayout.ValuesOffset + index * SharedMemoryLayout.ValueEntrySize;
        long ts = Volatile.Read(ref *(long*)(v + 8));
        if (ts == 0) return false;
        value = *(double*)v;
        ageSeconds = (double)(Stopwatch.GetTimestamp() - ts) / Stopwatch.Frequency;
        return true;
    }

    /// <summary>Mark a metric stale/N-A without touching the last value.</summary>
    public void MarkStale(int index)
    {
        byte* v = B + SharedMemoryLayout.ValuesOffset + index * SharedMemoryLayout.ValueEntrySize;
        Volatile.Write(ref *(long*)(v + 8), 0L);
    }

    /// <summary>Publish a string value (seqlock per slot).</summary>
    public void SetString(int index, string value)
    {
        byte* e = B + SharedMemoryLayout.RegistryOffset + index * SharedMemoryLayout.RegistryEntrySize;
        ushort slot = *(ushort*)(e + SharedMemoryLayout.RegOffStringSlot);
        if (slot == SharedMemoryLayout.NoStringSlot) return;

        byte* s = B + SharedMemoryLayout.StringsOffset + slot * SharedMemoryLayout.StringEntrySize;
        Span<byte> buf = stackalloc byte[SharedMemoryLayout.StringValueBytes];
        buf.Clear();
        // Encode as much as fits (truncates cleanly at a char boundary).
        System.Text.Unicode.Utf8.FromUtf16(value, buf, out _, out int len, replaceInvalidSequences: true, isFinalBlock: true);

        uint ver = *(uint*)s;
        Volatile.Write(ref *(uint*)s, ver + 1);                 // odd: writer inside
        new Span<byte>(s + 8, SharedMemoryLayout.StringValueBytes).Clear();
        buf[..len].CopyTo(new Span<byte>(s + 8, SharedMemoryLayout.StringValueBytes));
        *(uint*)(s + 4) = (uint)len;
        Volatile.Write(ref *(uint*)s, ver + 2);                 // even: done

        byte* v = B + SharedMemoryLayout.ValuesOffset + index * SharedMemoryLayout.ValueEntrySize;
        Volatile.Write(ref *(long*)(v + 8), Stopwatch.GetTimestamp());
    }

    /// <summary>Append frames to the ring and publish the cursor.</summary>
    public void AppendFrames(ReadOnlySpan<FrameEntry> frames)
    {
        if (frames.Length == 0) return;
        lock (_frameLock)
        {
            ulong cursor = *(ulong*)(B + SharedMemoryLayout.OffFrameCursor);
            foreach (ref readonly var f in frames)
            {
                byte* e = B + SharedMemoryLayout.FrameRingOffset + (int)(cursor % SharedMemoryLayout.FrameRingCapacity) * SharedMemoryLayout.FrameEntrySize;
                *(FrameEntry*)e = f;
                cursor++;
            }
            Volatile.Write(ref *(ulong*)(B + SharedMemoryLayout.OffFrameCursor), cursor);
        }
        _framesReady.Set(); // fire-and-forget wake for frame-graph widgets; no waiter = no cost
    }

    private static string ReadFixedUtf8(byte* p, int capacity)
    {
        var span = new ReadOnlySpan<byte>(p, capacity);
        int nul = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul >= 0 ? span[..nul] : span);
    }

    private static void WriteFixedUtf8(byte* dst, int capacity, string value)
    {
        var span = new Span<byte>(dst, capacity);
        span.Clear();
        if (value.Length == 0) return;
        System.Text.Unicode.Utf8.FromUtf16(value, span[..(capacity - 1)], out _, out _,
            replaceInvalidSequences: true, isFinalBlock: true);
    }

    public void Dispose()
    {
        _framesReady.Dispose();
        _section.Dispose();
    }
}
