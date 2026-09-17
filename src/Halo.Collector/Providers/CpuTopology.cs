using System.Runtime.InteropServices;
using Halo.Shared;

namespace Halo.Collector.Providers;

/// <summary>
/// What the CPU actually looks like, read from the OS rather than assumed (hardware plan H1/H3):
/// how many logical processors, which physical core each belongs to, and whether that core is a
/// performance or an efficiency core.
///
/// Windows numbers logical processors inside <i>processor groups</i> of at most 64. A global
/// index is the sum of the sizes of the groups before it plus the bit position inside its own
/// group mask — the same numbering <c>KeGetProcessorIndexFromNumber</c> produces, and the one
/// every <c>cpu.core.&lt;i&gt;.*</c> metric uses.
/// </summary>
internal static class CpuTopology
{
    private static readonly ComponentLog Log2 = Log.For("cpu-topology");

    /// <param name="Class">0 = performance, 1 = efficiency. Non-hybrid parts report 0 for all.</param>
    /// <param name="PhysicalCore">Index of the owning physical core; SMT siblings share it.</param>
    internal readonly record struct Logical(int Index, int Group, int Class, int PhysicalCore);

    /// <summary>Total logical processors across every processor group.</summary>
    public static int LogicalCount { get; private set; }

    /// <summary>Number of processor groups (1 on anything under 65 logical CPUs).</summary>
    public static int GroupCount { get; private set; } = 1;

    /// <summary>Logical processors this PC has, ordered by global index. Empty when the OS call
    /// failed — callers fall back to <c>Environment.ProcessorCount</c> and publish no topology.</summary>
    public static IReadOnlyList<Logical> Read()
    {
        var result = new List<Logical>();
        try
        {
            byte[]? buf = QueryProcessorCore();
            if (buf == null) return result;

            // Group sizes first: a global index needs to know how many CPUs precede its group.
            int[] groupSizes = ReadGroupSizes();
            GroupCount = Math.Max(1, groupSizes.Length);
            int[] groupBase = new int[groupSizes.Length];
            for (int g = 1; g < groupSizes.Length; g++) groupBase[g] = groupBase[g - 1] + groupSizes[g - 1];

            int physical = 0;
            unsafe
            {
                fixed (byte* p = buf)
                {
                    byte* end = p + buf.Length;
                    for (byte* e = p; e + 8 <= end;)
                    {
                        uint relationship = *(uint*)e;
                        uint size = *(uint*)(e + 4);
                        if (size < 8 || e + size > end) break;
                        if (relationship == RelationProcessorCore)
                        {
                            // PROCESSOR_RELATIONSHIP: Flags(1) EfficiencyClass(1) Reserved(20)
                            //                         GroupCount(2) GroupMask[GroupCount]
                            byte* pr = e + 8;
                            int efficiencyClass = pr[1];
                            ushort groups = *(ushort*)(pr + 22);
                            for (int g = 0; g < groups; g++)
                            {
                                // GROUP_AFFINITY: Mask(nuint) Group(2) Reserved(6)
                                byte* ga = pr + 24 + g * 16;
                                ulong mask = *(ulong*)ga;
                                ushort group = *(ushort*)(ga + 8);
                                int bas = group < groupBase.Length ? groupBase[group] : 0;
                                for (int bit = 0; bit < 64; bit++)
                                    if ((mask & (1UL << bit)) != 0)
                                        result.Add(new Logical(bas + bit, group, efficiencyClass, physical));
                            }
                            physical++;
                        }
                        e += size;
                    }
                }
            }

            // Intel hybrid reports E-cores as EfficiencyClass 0 and P-cores as the highest class;
            // Halo's contract is the opposite (0 = performance), so invert against the maximum.
            // A non-hybrid part has one class value throughout and comes out all-zero either way.
            int maxClass = 0;
            foreach (var l in result) maxClass = Math.Max(maxClass, l.Class);
            if (maxClass > 0)
                for (int i = 0; i < result.Count; i++)
                    result[i] = result[i] with { Class = result[i].Class == maxClass ? 0 : 1 };

            result.Sort((a, b) => a.Index.CompareTo(b.Index));
            LogicalCount = result.Count;
            if (LogicalCount == 0) LogicalCount = Environment.ProcessorCount;
        }
        catch (Exception ex)
        {
            Log2.Warn($"{ex.Message} — falling back to {Environment.ProcessorCount} flat logical CPUs");
            result.Clear();
            LogicalCount = Environment.ProcessorCount;
        }
        return result;
    }

    /// <summary>Logical CPUs per processor group, from RelationGroup.</summary>
    private static int[] ReadGroupSizes()
    {
        byte[]? buf = Query(RelationGroup);
        if (buf == null) return [Environment.ProcessorCount];
        unsafe
        {
            fixed (byte* p = buf)
            {
                // one RelationGroup record: GROUP_RELATIONSHIP at +8 =
                // MaximumGroupCount(2) ActiveGroupCount(2) Reserved(20) GroupInfo[]
                if (buf.Length < 12) return [Environment.ProcessorCount];
                byte* gr = p + 8;
                ushort active = *(ushort*)(gr + 2);
                if (active == 0) return [Environment.ProcessorCount];
                var sizes = new int[active];
                for (int g = 0; g < active; g++)
                {
                    // PROCESSOR_GROUP_INFO: MaximumProcessorCount(1) ActiveProcessorCount(1)
                    //                       Reserved(38) ActiveProcessorMask(nuint)
                    byte* gi = gr + 24 + g * 48;
                    sizes[g] = gi[1];
                }
                return sizes;
            }
        }
    }

    private static byte[]? QueryProcessorCore() => Query(RelationProcessorCore);

    private static byte[]? Query(uint relationship)
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(relationship, null, ref len);
        if (len == 0) return null;
        var buf = new byte[len];
        if (!GetLogicalProcessorInformationEx(relationship, buf, ref len))
        {
            Log2.Warn($"GetLogicalProcessorInformationEx({relationship}) failed: {Marshal.GetLastWin32Error()}");
            return null;
        }
        return buf;
    }

    private const uint RelationProcessorCore = 0;
    private const uint RelationGroup = 4;

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(uint relationshipType, byte[]? buffer, ref uint returnedLength);
}
