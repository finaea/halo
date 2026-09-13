using Halo.Shared;

namespace Halo.Collector.Providers;

/// <summary>
/// The one authority on what <c>gpu.&lt;i&gt;.*</c> means (hardware plan H2).
///
/// NVML devices claim the low indexes, ordered by PCI bus id so the numbering does not move when
/// the driver reorders devices between boots. Everything LibreHardwareMonitor finds that NVML
/// does not — AMD and Intel GPUs — is appended after that block. Both <see cref="NvmlProvider"/>
/// and the GPU part of <see cref="LhmProvider"/> publish into the same namespace from different
/// threads, so the mapping has to live outside both; the NVML probe runs once, lazily, behind a
/// lock, and whichever provider initialises first pays for it.
///
/// <b>Append-only.</b> An index, once handed out, is that GPU's for the life of the process — the
/// shared-memory registry is append-only too, and a metric name that silently re-pointed would
/// leave every widget bound to it reading a different card. So a GPU that turns up later (a
/// rescan after a laptop's dGPU powers on) takes the next free index rather than inserting itself
/// into the NVIDIA block.
/// </summary>
internal static class GpuIndexSpace
{
    private static readonly object Lock = new();
    private static readonly List<Slot> _slots = new();
    private static bool _probed;

    /// <param name="Key">Stable identity: PCI bus id for NVML devices, LHM's name otherwise.</param>
    private readonly record struct Slot(bool Nvidia, string Key, string Name, uint NvmlIndex);

    /// <summary>Total GPUs published so far.</summary>
    public static int Count
    {
        get { lock (Lock) { EnsureProbed(); return _slots.Count; } }
    }

    /// <summary>NVML devices with the halo index each one owns.</summary>
    public static List<(int Index, uint NvmlIndex, string Name)> NvidiaDevices()
    {
        lock (Lock)
        {
            EnsureProbed();
            var list = new List<(int, uint, string)>();
            for (int i = 0; i < _slots.Count; i++)
                if (_slots[i].Nvidia) list.Add((i, _slots[i].NvmlIndex, _slots[i].Name));
            return list;
        }
    }

    /// <summary>
    /// Halo index for a GPU LibreHardwareMonitor found. An NVIDIA card is matched to its NVML
    /// slot by name containment (the same trick <see cref="DriveMap"/> uses for disk models) so
    /// LHM's NVAPI-only sensors land on the index NVML already owns; anything unmatched gets a
    /// fresh index at the end.
    /// </summary>
    /// <param name="matchedNvml">True when this is an NVML device LHM is only adding sensors to,
    /// so the caller knows not to re-publish name/vendor over NVML's values.</param>
    public static int IndexForLhm(string lhmName, bool isNvidiaVendor, out bool matchedNvml)
    {
        lock (Lock)
        {
            EnsureProbed();
            if (isNvidiaVendor)
            {
                for (int i = 0; i < _slots.Count; i++)
                {
                    if (!_slots[i].Nvidia) continue;
                    string nv = _slots[i].Name;
                    if (nv.Length == 0 || lhmName.Length == 0) continue;
                    if (nv.Contains(lhmName, StringComparison.OrdinalIgnoreCase) ||
                        lhmName.Contains(nv, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedNvml = true;
                        return i;
                    }
                }
                // One NVIDIA card on each side that simply spells its name differently is the
                // common case; only guess that way when there is exactly one of each.
                int onlyNvidia = -1, nvidiaCount = 0;
                for (int i = 0; i < _slots.Count; i++)
                    if (_slots[i].Nvidia) { nvidiaCount++; onlyNvidia = i; }
                if (nvidiaCount == 1 && _slots.Count == 1)
                {
                    Log.Info($"gpu: matching LHM '{lhmName}' to the only NVML device '{_slots[onlyNvidia].Name}' by elimination");
                    matchedNvml = true;
                    return onlyNvidia;
                }
            }

            matchedNvml = false;
            for (int i = 0; i < _slots.Count; i++)
                if (!_slots[i].Nvidia && string.Equals(_slots[i].Key, lhmName, StringComparison.OrdinalIgnoreCase))
                    return i;

            _slots.Add(new Slot(Nvidia: false, Key: lhmName, Name: lhmName, NvmlIndex: 0));
            Log.Info($"gpu: '{lhmName}' claims index {_slots.Count - 1} (no NVML device)");
            return _slots.Count - 1;
        }
    }

    /// <summary>Re-read NVML and append anything new (the <c>rescan</c> path). Existing indexes
    /// never move.</summary>
    public static void Reprobe()
    {
        lock (Lock)
        {
            _probed = false;
            EnsureProbed();
        }
    }

    private static void EnsureProbed()
    {
        if (_probed) return;
        _probed = true;

        int before = _slots.Count;
        foreach (var d in NvmlProvider.ProbeDevices())
        {
            string key = d.BusId.Length > 0 ? d.BusId : $"nvml:{d.Name}";
            bool known = false;
            foreach (var s in _slots)
                if (s.Nvidia && string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
            if (!known) _slots.Add(new Slot(Nvidia: true, key, d.Name, d.NvmlIndex));
        }

        if (_slots.Count != before)
            Log.Info("gpu index space: " + string.Join(", ", _slots.Select((s, i) => $"gpu.{i}={s.Name}")));
    }
}
