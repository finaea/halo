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
    /// <summary>LHM hardware identifier → the index that card was given. LHM's *name* is not
    /// unique (two identical cards report the same model string) but its identifier is, so this
    /// is what makes a re-init hand the same card the same index instead of re-matching.</summary>
    private static readonly Dictionary<string, (int Index, bool MatchedNvml)> _byLhmId = new(StringComparer.Ordinal);
    private static bool _probed;

    /// <param name="Key">Stable identity: PCI bus id for NVML devices, LHM's hardware identifier
    /// otherwise. Never the model string — two identical cards share one of those.</param>
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
    ///
    /// <b>One NVML slot, one LHM card.</b> Two identical cards report the same model string, so
    /// name matching alone would send both to the lower slot and leave the higher one without
    /// LHM's voltage and fan RPM forever. Each card is remembered by its LHM identifier (unique
    /// where the name is not) and only ever matched against a slot no other card has taken.
    /// </summary>
    /// <param name="lhmId">LHM's hardware identifier, e.g. <c>/gpu-nvidia/1</c> — the stable key.</param>
    /// <param name="lhmName">LHM's model string, used only for matching.</param>
    /// <param name="matchedNvml">True when this is an NVML device LHM is only adding sensors to,
    /// so the caller knows not to re-publish name/vendor over NVML's values.</param>
    public static int IndexForLhm(string lhmId, string lhmName, bool isNvidiaVendor, out bool matchedNvml)
    {
        lock (Lock)
        {
            EnsureProbed();

            // A re-init (retry or rescan) must hand the same card the same index, not re-match it.
            if (_byLhmId.TryGetValue(lhmId, out var known))
            {
                matchedNvml = known.MatchedNvml;
                return known.Index;
            }

            if (isNvidiaVendor)
            {
                var claimed = ClaimedNvmlSlots();
                for (int i = 0; i < _slots.Count; i++)
                {
                    if (!_slots[i].Nvidia || claimed.Contains(i)) continue;
                    string nv = _slots[i].Name;
                    if (nv.Length == 0 || lhmName.Length == 0) continue;
                    if (nv.Contains(lhmName, StringComparison.OrdinalIgnoreCase) ||
                        lhmName.Contains(nv, StringComparison.OrdinalIgnoreCase))
                        return Assign(lhmId, i, matched: true, out matchedNvml);
                }

                // One NVIDIA card on each side that simply spells its name differently is the
                // common case, so fall back to elimination when exactly one NVML slot is still
                // free. AMD and Intel slots do not make this ambiguous — NVML only ever reports
                // NVIDIA devices, so a non-NVIDIA card could never have been the one in that slot.
                int onlyFree = -1, freeCount = 0;
                for (int i = 0; i < _slots.Count; i++)
                    if (_slots[i].Nvidia && !claimed.Contains(i)) { freeCount++; onlyFree = i; }
                if (freeCount == 1)
                {
                    Log.Info($"gpu: matching LHM '{lhmName}' to the only free NVML device '{_slots[onlyFree].Name}' by elimination");
                    return Assign(lhmId, onlyFree, matched: true, out matchedNvml);
                }

                Log.Warn($"gpu: LHM reports NVIDIA '{lhmName}' ({lhmId}) but no free NVML slot matches it " +
                         "— giving it its own index, so its voltage and fan RPM land there rather than on another card");
            }

            _slots.Add(new Slot(Nvidia: false, Key: lhmId, Name: lhmName, NvmlIndex: 0));
            Log.Info($"gpu: '{lhmName}' claims index {_slots.Count - 1} (no NVML device)");
            return Assign(lhmId, _slots.Count - 1, matched: false, out matchedNvml);
        }
    }

    private static int Assign(string lhmId, int index, bool matched, out bool matchedNvml)
    {
        _byLhmId[lhmId] = (index, matched);
        matchedNvml = matched;
        return index;
    }

    /// <summary>
    /// NVML slots an LHM card already holds. Derived from the assignment map rather than kept as
    /// a second set, so the two cannot drift apart across a re-init — and so <see cref="Reprobe"/>
    /// needs no reset, since it only ever appends slots.
    /// </summary>
    private static HashSet<int> ClaimedNvmlSlots()
    {
        var claimed = new HashSet<int>();
        foreach (var (index, matched) in _byLhmId.Values)
            if (matched) claimed.Add(index);
        return claimed;
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
