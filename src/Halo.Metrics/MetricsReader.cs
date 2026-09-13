using System.Diagnostics;
using System.Text;

namespace Halo.Metrics;

/// <summary>
/// Consumer-side lock-free reader for the Halo.Metrics.v2 section.
///
/// Five rules every consumer must follow (docs\metrics-protocol.md):
///  1. check magic and major version — this class refuses to attach otherwise;
///  2. drop cached name→index mappings when CollectorStartQpc changes (a restarted collector
///     rebuilds the registry, so stale indexes silently read the wrong metric);
///  3. timestamp 0 means N/A — a value with a timestamp is real but may be old (check the age);
///  4. string reads retry on the per-slot seqlock;
///  5. re-read the frame cursor after copying frames and discard entries the writer has
///     since overwritten (the ring has no per-entry lock).
/// <see cref="CollectorSession"/> implements 1–5 for you; use it unless you need raw access.
/// </summary>
public sealed unsafe class MetricsReader : IDisposable
{
    private NativeSection? _section;
    private readonly Dictionary<ulong, int> _indexById = new();
    private int _registrySeen;

    // header-published geometry, re-read on attach (never compiled in)
    private int _providersOffset, _providerCapacity, _providerEntrySize;
    private int _registryOffset, _registryCapacity, _registryEntrySize;
    private int _valuesOffset, _valueEntrySize;
    private int _stringsOffset, _stringCapacity, _stringEntrySize, _stringValueBytes;
    private int _frameRingOffset, _frameRingCapacity, _frameEntrySize;

    private byte* B => _section!.Base;

    public bool IsAttached => _section != null;

    /// <summary>Version of the section last seen, even when attaching was refused.</summary>
    public (ushort Major, ushort Minor) LastSeenVersion { get; private set; }

    /// <summary>Try to attach to the collector's section. Cheap to call repeatedly.</summary>
    public bool TryAttach()
    {
        if (_section != null) return true;
        var s = NativeSection.OpenReadOnly(SharedMemoryLayout.SectionName);
        if (s == null) return false;

        if (*(uint*)(s.Base + SharedMemoryLayout.OffMagic) != SharedMemoryLayout.Magic)
        {
            s.Dispose();
            return false;
        }
        ushort major = *(ushort*)(s.Base + SharedMemoryLayout.OffVersionMajor);
        ushort minor = *(ushort*)(s.Base + SharedMemoryLayout.OffVersionMinor);
        LastSeenVersion = (major, minor);
        if (major != SharedMemoryLayout.VersionMajor)
        {
            // A different major means a different layout: refuse rather than misread.
            s.Dispose();
            return false;
        }
        if (Volatile.Read(ref *(uint*)(s.Base + SharedMemoryLayout.OffReady)) != 1)
        {
            s.Dispose();
            return false;
        }

        _section = s;
        ReadGeometry();
        _registrySeen = 0;
        _indexById.Clear();
        RefreshRegistry();
        return true;
    }

    private void ReadGeometry()
    {
        _providersOffset = (int)*(uint*)(B + SharedMemoryLayout.OffProvidersOffset);
        _providerCapacity = (int)*(uint*)(B + SharedMemoryLayout.OffProviderCapacity);
        _providerEntrySize = (int)*(uint*)(B + SharedMemoryLayout.OffProviderEntrySize);
        _registryOffset = (int)*(uint*)(B + SharedMemoryLayout.OffRegistryOffset);
        _registryCapacity = (int)*(uint*)(B + SharedMemoryLayout.OffRegistryCapacity);
        _registryEntrySize = (int)*(uint*)(B + SharedMemoryLayout.OffRegistryEntrySize);
        _valuesOffset = (int)*(uint*)(B + SharedMemoryLayout.OffValuesOffset);
        _valueEntrySize = (int)*(uint*)(B + SharedMemoryLayout.OffValueEntrySize);
        _stringsOffset = (int)*(uint*)(B + SharedMemoryLayout.OffStringsOffset);
        _stringCapacity = (int)*(uint*)(B + SharedMemoryLayout.OffStringCapacity);
        _stringEntrySize = (int)*(uint*)(B + SharedMemoryLayout.OffStringEntrySize);
        _stringValueBytes = (int)*(uint*)(B + SharedMemoryLayout.OffStringValueBytes);
        _frameRingOffset = (int)*(uint*)(B + SharedMemoryLayout.OffFrameRingOffset);
        _frameRingCapacity = (int)*(uint*)(B + SharedMemoryLayout.OffFrameRingCapacity);
        _frameEntrySize = (int)*(uint*)(B + SharedMemoryLayout.OffFrameEntrySize);
    }

    /// <summary>Detach (e.g. after collector restart so the stale section is released).</summary>
    public void Detach()
    {
        _section?.Dispose();
        _section = null;
        _indexById.Clear();
        _registrySeen = 0;
    }

    public void RefreshRegistry()
    {
        if (_section == null) return;
        int count = Volatile.Read(ref *(int*)(B + SharedMemoryLayout.OffRegistryCount));
        if (count > _registryCapacity) count = _registryCapacity;
        for (int i = _registrySeen; i < count; i++)
            _indexById[*(ulong*)(RegEntry(i) + SharedMemoryLayout.RegOffIdHash)] = i;
        _registrySeen = count;
    }

    /// <summary>Seconds since the collector heartbeat; large values mean the collector is gone.</summary>
    public double HeartbeatAgeSeconds
    {
        get
        {
            if (_section == null) return double.MaxValue;
            long hb = Volatile.Read(ref *(long*)(B + SharedMemoryLayout.OffHeartbeatQpc));
            long freq = *(long*)(B + SharedMemoryLayout.OffQpcFrequency);
            if (hb == 0 || freq == 0) return double.MaxValue;
            return (double)(Stopwatch.GetTimestamp() - hb) / freq;
        }
    }

    public int CollectorPid => _section == null ? 0 : *(int*)(B + SharedMemoryLayout.OffCollectorPid);
    public long CollectorStartQpc => _section == null ? 0 : *(long*)(B + SharedMemoryLayout.OffCollectorStartQpc);
    public long QpcFrequency => _section == null ? Stopwatch.Frequency : *(long*)(B + SharedMemoryLayout.OffQpcFrequency);
    public int MetricCount => _registrySeen;
    public int TotalSize => _section == null ? 0 : (int)*(uint*)(B + SharedMemoryLayout.OffTotalSize);

    public string CollectorVersion => _section == null
        ? ""
        : ReadFixedUtf8(B + SharedMemoryLayout.OffCollectorVersion, SharedMemoryLayout.CollectorVersionBytes);

    public int ResolveIndex(string name)
    {
        ulong id = MetricId.Hash(name);
        if (_indexById.TryGetValue(id, out int idx)) return idx;
        RefreshRegistry();
        return _indexById.TryGetValue(id, out idx) ? idx : -1;
    }

    /// <summary>Read a double + its age. Returns false if never written or marked N/A.</summary>
    public bool TryRead(int index, out double value, out double ageSeconds)
    {
        value = 0; ageSeconds = double.MaxValue;
        if (_section == null || index < 0) return false;
        byte* v = B + _valuesOffset + index * _valueEntrySize;
        long ts = Volatile.Read(ref *(long*)(v + 8));
        if (ts == 0) return false;
        value = *(double*)v;
        ageSeconds = (double)(Stopwatch.GetTimestamp() - ts) / QpcFrequency;
        return true;
    }

    public double ReadOr(int index, double fallback)
        => TryRead(index, out double v, out _) ? v : fallback;

    /// <summary>Read a string metric (seqlock retry).</summary>
    public bool TryReadString(int index, out string value)
    {
        value = "";
        if (_section == null || index < 0) return false;
        ushort slot = *(ushort*)(RegEntry(index) + SharedMemoryLayout.RegOffStringSlot);
        if (slot == SharedMemoryLayout.NoStringSlot || slot >= _stringCapacity) return false;
        byte* s = B + _stringsOffset + slot * _stringEntrySize;

        Span<byte> buf = stackalloc byte[_stringValueBytes];
        for (int attempt = 0; attempt < 8; attempt++)
        {
            uint v1 = Volatile.Read(ref *(uint*)s);
            if ((v1 & 1) != 0) { Thread.SpinWait(20); continue; }
            uint len = *(uint*)(s + 4);
            if (len > _stringValueBytes) { Thread.SpinWait(20); continue; }
            new ReadOnlySpan<byte>(s + 8, (int)len).CopyTo(buf);
            // The payload copy must not be reordered past the second sequence read, or a torn
            // read compares two identical versions. x64 would not reorder it; the .NET memory
            // model permits it, so make the ordering explicit.
            Interlocked.MemoryBarrier();
            uint v2 = Volatile.Read(ref *(uint*)s);
            if (v1 == v2)
            {
                value = Encoding.UTF8.GetString(buf[..(int)len]);
                return true;
            }
        }
        return false;
    }

    /// <summary>Full descriptor of a registry slot, rebuilt from the section alone.</summary>
    public MetricInfo? DescribeIndex(int index)
    {
        if (_section == null || index < 0 || index >= _registrySeen) return null;
        byte* e = RegEntry(index);
        return new MetricInfo(
            index,
            ReadFixedUtf8(e + SharedMemoryLayout.RegOffName, SharedMemoryLayout.NameBytes),
            (MetricType)e[SharedMemoryLayout.RegOffType],
            (MetricUnit)e[SharedMemoryLayout.RegOffUnit],
            (MetricSemantics)e[SharedMemoryLayout.RegOffSemantics],
            (MetricFlags)e[SharedMemoryLayout.RegOffFlags],
            *(ushort*)(e + SharedMemoryLayout.RegOffProviderIndex) == 0xFFFF ? -1 : *(ushort*)(e + SharedMemoryLayout.RegOffProviderIndex),
            *(float*)(e + SharedMemoryLayout.RegOffNominalRateHz),
            *(float*)(e + SharedMemoryLayout.RegOffEffectiveRateHz),
            (int)*(uint*)(e + SharedMemoryLayout.RegOffWindowMs));
    }

    // ---- provider table ----

    public int ProviderCapacity => _section == null ? 0 : _providerCapacity;

    /// <summary>Provider row, or null for an unused slot (empty name).</summary>
    public ProviderInfo? DescribeProvider(int index)
    {
        if (_section == null || index < 0 || index >= _providerCapacity) return null;
        byte* e = B + _providersOffset + index * _providerEntrySize;
        string name = ReadFixedUtf8(e + SharedMemoryLayout.ProvOffName, SharedMemoryLayout.ProviderNameBytes);
        if (name.Length == 0) return null;
        return new ProviderInfo(
            index,
            name,
            (ProviderState)Volatile.Read(ref e[SharedMemoryLayout.ProvOffState]),
            e[SharedMemoryLayout.ProvOffNeedsElevation] != 0,
            *(float*)(e + SharedMemoryLayout.ProvOffRateHz),
            Volatile.Read(ref *(long*)(e + SharedMemoryLayout.ProvOffLastPollQpc)),
            *(float*)(e + SharedMemoryLayout.ProvOffLastPollMs),
            ReadFixedUtf8(e + SharedMemoryLayout.ProvOffLastError, SharedMemoryLayout.ProviderErrorBytes));
    }

    public IEnumerable<ProviderInfo> Providers()
    {
        for (int i = 0; i < ProviderCapacity; i++)
            if (DescribeProvider(i) is { } p) yield return p;
    }

    // ---- frame ring ----

    public ulong FrameCursor => _section == null ? 0 : Volatile.Read(ref *(ulong*)(B + SharedMemoryLayout.OffFrameCursor));

    /// <summary>
    /// Copy frames [from, cursor) into dst (newest last). Returns count copied and the new cursor.
    /// If the reader fell behind more than the ring capacity, older frames are lost (skipped).
    /// Reader rule 5: the ring has no per-entry lock, so the cursor is re-read after the copy and
    /// any entry the writer overwrote meanwhile is dropped rather than handed back as garbage.
    /// </summary>
    public (int Count, ulong NewCursor) ReadFrames(ulong from, Span<FrameEntry> dst)
    {
        if (_section == null) return (0, from);
        ulong cursor = FrameCursor;
        if (cursor <= from) return (0, cursor);
        ulong available = cursor - from;
        if (available > (ulong)_frameRingCapacity)
            available = (ulong)_frameRingCapacity;
        int n = (int)Math.Min(available, (ulong)dst.Length);
        // read the newest n frames ending at cursor
        ulong start = cursor - (ulong)n;
        for (int i = 0; i < n; i++)
        {
            byte* e = B + _frameRingOffset + (int)((start + (ulong)i) % (ulong)_frameRingCapacity) * _frameEntrySize;
            dst[i] = *(FrameEntry*)e;
        }

        // The writer may have lapped us during the copy: everything below cursor2 - capacity has
        // been overwritten by newer frames. Shift those out and report only what is still sound.
        ulong cursor2 = FrameCursor;
        if (cursor2 - start > (ulong)_frameRingCapacity)
        {
            ulong oldestIntact = cursor2 - (ulong)_frameRingCapacity;
            int drop = (int)Math.Min((ulong)n, oldestIntact - start);
            if (drop > 0)
            {
                dst[drop..n].CopyTo(dst[..(n - drop)]);   // overlapping-safe (memmove)
                n -= drop;
            }
        }
        return (n, cursor);
    }

    private byte* RegEntry(int index) => B + _registryOffset + index * _registryEntrySize;

    private static string ReadFixedUtf8(byte* p, int capacity)
    {
        var span = new ReadOnlySpan<byte>(p, capacity);
        int nul = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul >= 0 ? span[..nul] : span);
    }

    public void Dispose() => Detach();
}
