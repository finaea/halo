using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Halo.Shared.Metrics;

/// <summary>Collector-side writer for the Halo.Metrics.v1 section. Thread-safe.</summary>
public sealed unsafe class MetricsWriter : IDisposable
{
    private readonly NativeSection _section;
    private readonly Dictionary<ulong, int> _indexById = new();
    private readonly object _registryLock = new();
    private int _stringSlotsUsed;

    private byte* B => _section.Base;

    public MetricsWriter()
    {
        _section = NativeSection.Create(SharedMemoryLayout.SectionName, SharedMemoryLayout.TotalSize);
        new Span<byte>(B, SharedMemoryLayout.TotalSize).Clear();
        *(uint*)(B + 0) = SharedMemoryLayout.Magic;
        *(uint*)(B + 4) = SharedMemoryLayout.Version;
        *(long*)(B + SharedMemoryLayout.OffQpcFrequency) = Stopwatch.Frequency;
        *(int*)(B + SharedMemoryLayout.OffCollectorPid) = Environment.ProcessId;
        *(long*)(B + SharedMemoryLayout.OffCollectorStartQpc) = Stopwatch.GetTimestamp();
        Heartbeat();
    }

    public void MarkReady() => Volatile.Write(ref *(uint*)(B + SharedMemoryLayout.OffReady), 1u);

    public void Heartbeat() => Volatile.Write(ref *(long*)(B + SharedMemoryLayout.OffHeartbeatQpc), Stopwatch.GetTimestamp());

    /// <summary>Register a metric (idempotent). Returns the slot index used in Set* calls.</summary>
    public int Register(in MetricDescriptor d)
    {
        ulong id = d.Id;
        lock (_registryLock)
        {
            if (_indexById.TryGetValue(id, out int existing)) return existing;

            int count = *(int*)(B + SharedMemoryLayout.OffRegistryCount);
            if (count >= SharedMemoryLayout.MaxMetrics) throw new InvalidOperationException("Metric registry full");

            ushort stringSlot = 0xFFFF;
            if (d.Type == MetricType.String)
            {
                if (_stringSlotsUsed >= SharedMemoryLayout.MaxStringMetrics) throw new InvalidOperationException("String slots full");
                stringSlot = (ushort)_stringSlotsUsed++;
            }

            byte* e = B + SharedMemoryLayout.RegistryOffset + count * SharedMemoryLayout.RegistryEntrySize;
            *(ulong*)e = id;
            var nameSpan = new Span<byte>(e + 8, SharedMemoryLayout.NameBytes);
            nameSpan.Clear();
            int written = Encoding.UTF8.GetBytes(d.Name, nameSpan[..(SharedMemoryLayout.NameBytes - 1)]);
            _ = written;
            e[56] = (byte)d.Type;
            e[57] = (byte)d.Unit;
            *(ushort*)(e + 58) = stringSlot;
            *(float*)(e + 60) = d.MaxRateHz;

            Volatile.Write(ref *(int*)(B + SharedMemoryLayout.OffRegistryCount), count + 1);
            _indexById[id] = count;
            return count;
        }
    }

    public void SetEffectiveRate(int index, float hz)
    {
        byte* e = B + SharedMemoryLayout.RegistryOffset + index * SharedMemoryLayout.RegistryEntrySize;
        *(float*)(e + 60) = hz;
    }

    /// <summary>Publish a double value. Lock-free; safe from any thread.</summary>
    public void Set(int index, double value)
    {
        byte* v = B + SharedMemoryLayout.ValuesOffset + index * SharedMemoryLayout.ValueEntrySize;
        *(double*)v = value;                                    // atomic 8-byte store on x64
        Volatile.Write(ref *(long*)(v + 8), Stopwatch.GetTimestamp()); // release: timestamp last
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
        ushort slot = *(ushort*)(e + 58);
        if (slot == 0xFFFF) return;

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

    /// <summary>Append frames to the ring and publish the cursor (single producer).</summary>
    public void AppendFrames(ReadOnlySpan<FrameEntry> frames)
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

    public void Dispose() => _section.Dispose();
}
