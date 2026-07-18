using System.Diagnostics;
using System.Text;

namespace Halo.Shared.Metrics;

/// <summary>Widget-side lock-free reader for the Halo.Metrics.v1 section.</summary>
public sealed unsafe class MetricsReader : IDisposable
{
    private NativeSection? _section;
    private readonly Dictionary<ulong, int> _indexById = new();
    private int _registrySeen;

    private byte* B => _section!.Base;

    public bool IsAttached => _section != null;

    /// <summary>Try to attach to the collector's section. Cheap to call repeatedly.</summary>
    public bool TryAttach()
    {
        if (_section != null) return true;
        var s = NativeSection.OpenReadOnly(SharedMemoryLayout.SectionName);
        if (s == null) return false;
        if (*(uint*)s.Base != SharedMemoryLayout.Magic || Volatile.Read(ref *(uint*)(s.Base + SharedMemoryLayout.OffReady)) != 1)
        {
            s.Dispose();
            return false;
        }
        _section = s;
        _registrySeen = 0;
        _indexById.Clear();
        RefreshRegistry();
        return true;
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
        for (int i = _registrySeen; i < count; i++)
        {
            byte* e = B + SharedMemoryLayout.RegistryOffset + i * SharedMemoryLayout.RegistryEntrySize;
            _indexById[*(ulong*)e] = i;
        }
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

    public int ResolveIndex(string name)
    {
        ulong id = MetricId.Hash(name);
        if (_indexById.TryGetValue(id, out int idx)) return idx;
        RefreshRegistry();
        return _indexById.TryGetValue(id, out idx) ? idx : -1;
    }

    /// <summary>Read a double + its age. Returns false if never written or missing.</summary>
    public bool TryRead(int index, out double value, out double ageSeconds)
    {
        value = 0; ageSeconds = double.MaxValue;
        if (_section == null || index < 0) return false;
        byte* v = B + SharedMemoryLayout.ValuesOffset + index * SharedMemoryLayout.ValueEntrySize;
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
        byte* e = B + SharedMemoryLayout.RegistryOffset + index * SharedMemoryLayout.RegistryEntrySize;
        ushort slot = *(ushort*)(e + 58);
        if (slot == 0xFFFF) return false;
        byte* s = B + SharedMemoryLayout.StringsOffset + slot * SharedMemoryLayout.StringEntrySize;

        Span<byte> buf = stackalloc byte[SharedMemoryLayout.StringValueBytes];
        for (int attempt = 0; attempt < 8; attempt++)
        {
            uint v1 = Volatile.Read(ref *(uint*)s);
            if ((v1 & 1) != 0) { Thread.SpinWait(20); continue; }
            uint len = *(uint*)(s + 4);
            if (len > SharedMemoryLayout.StringValueBytes) { Thread.SpinWait(20); continue; }
            new ReadOnlySpan<byte>(s + 8, (int)len).CopyTo(buf);
            uint v2 = Volatile.Read(ref *(uint*)s);
            if (v1 == v2)
            {
                value = Encoding.UTF8.GetString(buf[..(int)len]);
                return true;
            }
        }
        return false;
    }

    public (MetricType Type, MetricUnit Unit, float RateHz, string Name)? DescribeIndex(int index)
    {
        if (_section == null || index < 0 || index >= _registrySeen) return null;
        byte* e = B + SharedMemoryLayout.RegistryOffset + index * SharedMemoryLayout.RegistryEntrySize;
        var nameSpan = new ReadOnlySpan<byte>(e + 8, SharedMemoryLayout.NameBytes);
        int nul = nameSpan.IndexOf((byte)0);
        string name = Encoding.UTF8.GetString(nul >= 0 ? nameSpan[..nul] : nameSpan);
        return ((MetricType)e[56], (MetricUnit)e[57], *(float*)(e + 60), name);
    }

    public int MetricCount => _registrySeen;

    public ulong FrameCursor => _section == null ? 0 : Volatile.Read(ref *(ulong*)(B + SharedMemoryLayout.OffFrameCursor));

    /// <summary>
    /// Copy frames [from, cursor) into dst (newest last). Returns count copied and the new cursor.
    /// If the reader fell behind more than the ring capacity, older frames are lost (skipped).
    /// </summary>
    public (int Count, ulong NewCursor) ReadFrames(ulong from, Span<FrameEntry> dst)
    {
        if (_section == null) return (0, from);
        ulong cursor = FrameCursor;
        if (cursor <= from) return (0, cursor);
        ulong available = cursor - from;
        if (available > SharedMemoryLayout.FrameRingCapacity)
        {
            from = cursor - SharedMemoryLayout.FrameRingCapacity;
            available = SharedMemoryLayout.FrameRingCapacity;
        }
        int n = (int)Math.Min(available, (ulong)dst.Length);
        // read the newest n frames ending at cursor
        ulong start = cursor - (ulong)n;
        for (int i = 0; i < n; i++)
        {
            byte* e = B + SharedMemoryLayout.FrameRingOffset + (int)((start + (ulong)i) % SharedMemoryLayout.FrameRingCapacity) * SharedMemoryLayout.FrameEntrySize;
            dst[i] = *(FrameEntry*)e;
        }
        return (n, cursor);
    }

    public void Dispose() => Detach();
}
