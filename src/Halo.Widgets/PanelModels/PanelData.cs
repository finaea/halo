using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelModels;

/// <summary>
/// Data decisions every skin makes the same way: the staged warn colour, the single-threshold
/// test, which fan channel is the CPU fan and which core rows exist. Moved here unchanged from
/// Rainformer's CPU builder so a second skin calls the same code rather than a copy of it — the
/// Rainformer goldens are what prove the move changed nothing.
/// </summary>
public static class PanelData
{
    /// <summary>Logical CPUs to draw. Falls back to this process's view when the collector is
    /// down, so the panel still has the right shape before the first publish.</summary>
    public static int CoreCount(PanelContext ctx)
        => (int)Math.Clamp(ctx.Metrics.Value(MetricNames.CpuLogicalCount, Environment.ProcessorCount), 1, 256);

    // ---- per-core grid (hardware plan H3) ----

    /// <summary>One row of the grid: a core, or a "P-cores" / "E-cores" group heading.</summary>
    public readonly record struct CoreRow(int Logical, string Label, bool IsHeading);

    /// <summary>
    /// Which core rows exist and how many columns they are laid out in.
    /// <para>
    /// <c>coreView</c>: auto (threads up to 48, physical cores above) · thread · core · hidden.
    /// <c>coreColumns</c>: auto (1 up to 16 rows, 2 up to 32, 3 up to 48) · 1 · 2 · 3.
    /// </para>
    /// </summary>
    public static (List<CoreRow> Rows, int Columns) CoreGrid(PanelContext ctx)
    {
        int threads = CoreCount(ctx);
        string view = ctx.Option("coreView");
        if (view.Length == 0) view = "auto";
        if (view == "hidden") return ([], 1);

        // class 0 = performance, 1 = efficiency (published already inverted by the collector)
        var logical = new List<(int Index, int Class, int Physical)>(threads);
        for (int i = 0; i < threads; i++)
        {
            int cls = (int)ctx.Metrics.Value(MetricNames.CpuCoreClass(i), 0);
            int phys = (int)ctx.Metrics.Value(MetricNames.CpuCorePhysical(i), i);
            logical.Add((i, cls, phys));
        }

        bool perCore = view == "core" || (view == "auto" && threads > 48);

        // "Group P/E cores" (catalog key groupCoreTypes, default on). Read as "group unless the
        // value is exactly false", never through OptionBool: ctx.Option returns "" for a key the
        // catalog does not declare (Elements.cs:41-42), and OptionBool("") is false — which would
        // silently flatten every user's core grid the moment this shipped ahead of the catalog
        // entry. An unset value has to mean grouped.
        bool group = !ctx.Option("groupCoreTypes").Equals("false", StringComparison.OrdinalIgnoreCase);

        // Grouping also reorders: off is plain OS order, which is what v1 drew.
        var ordered = (group ? logical.OrderBy(l => l.Class).ThenBy(l => l.Index)
                             : logical.OrderBy(l => l.Index)).ToList();
        if (perCore)
        {
            // one row per physical core; the row reads the first logical CPU on that core
            var seen = new HashSet<int>();
            ordered = ordered.Where(l => seen.Add(l.Physical)).ToList();
        }

        int columns = ctx.Option("coreColumns") switch
        {
            "1" => 1,
            "2" => 2,
            "3" => 3,
            _ => ordered.Count <= 16 ? 1 : ordered.Count <= 32 ? 2 : 3,
        };

        // group headings only when grouping is on and the part actually has both kinds of core:
        // in OS order the rows are interleaved, so a "P-cores" heading would be a lie
        bool hybrid = group && ordered.Any(l => l.Class == 0) && ordered.Any(l => l.Class == 1);
        string template = ctx.UserLabel("cores") ?? (columns == 1 ? "Core {n}:" : "C{n}");

        var rows = new List<CoreRow>(ordered.Count + 2);
        int lastClass = -1;
        int ordinal = 0;
        foreach (var l in ordered)
        {
            if (hybrid && l.Class != lastClass)
            {
                rows.Add(new CoreRow(-1, l.Class == 0 ? "P-cores" : "E-cores", true));
                lastClass = l.Class;
            }
            ordinal++;
            string label = (ctx.UserLabel($"cores.{l.Index}") ?? template)
                .Replace("{n}", (perCore ? ordinal : l.Index + 1).ToString());
            rows.Add(new CoreRow(l.Index, label, false));
        }
        return (rows, columns);
    }

    /// <summary>Which discovered fan channel is the CPU fan: the user's pick, else the first
    /// channel whose sensor name mentions "CPU", else channel 0.</summary>
    public static int CpuFanChannel(PanelContext ctx)
    {
        if (int.TryParse(ctx.Option("cpuFanChannel"), out int pinned) && pinned >= 0) return pinned;
        int count = (int)ctx.Metrics.Value(MetricNames.FanCount, 0);
        for (int i = 0; i < count; i++)
            if (ctx.Metrics.Text(MetricNames.FanName(i)).Contains("CPU", StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
    }

    /// <summary>Staged device warn colors (DevTempWarnColorTh1..5) from a threshold list.</summary>
    public static string WarnColor(double v, IReadOnlyList<double> thresholds)
    {
        if (thresholds.Count < 4) return "devWarn1";
        return v < thresholds[0] ? "devWarn1"
            : v < thresholds[1] ? "devWarn2"
            : v < thresholds[2] ? "devWarn3"
            : v < thresholds[3] ? "devWarn4"
            : "devWarn5";
    }

    public static string WarnColor(double v, double t1, double t2, double t3, double t4)
        => WarnColor(v, new[] { t1, t2, t3, t4 });

    /// <summary>Single-threshold warn test ("the bar turns red past 75%").</summary>
    public static bool Over(double v, IReadOnlyList<double> thresholds)
        => thresholds.Count > 0 && v > thresholds[^1];

    // ---- drives ----

    /// <summary>
    /// Does this volume's row show free space instead of used? Answered live, per draw, because
    /// the option is not structural and so never costs a rebuild. Scans the raw "C,D,E" list
    /// rather than splitting it — this runs in both the measure and the draw pass of every drive
    /// row — and matches <see cref="SelectedVolumes"/>: an entry's first character is the letter.
    /// </summary>
    public static bool ShowsFree(PanelContext ctx, char drive)
    {
        string list = ctx.Option("freeMode");
        for (int i = 0; i < list.Length;)
        {
            int end = list.IndexOf(',', i);
            if (end < 0) end = list.Length;
            var entry = list.AsSpan(i, end - i).Trim();
            if (entry.Length > 0 && char.ToUpperInvariant(entry[0]) == drive) return true;
            i = end + 1;
        }
        return false;
    }

    /// <summary>The widget's volume list, or every volume the collector published.</summary>
    public static List<char> SelectedVolumes(PanelContext ctx)
    {
        var configured = ctx.OptionList("volumes")
            .Where(s => s.Length > 0)
            .Select(s => char.ToUpperInvariant(s[0]))
            .Distinct()
            .ToList();
        if (configured.Count > 0) return configured;

        // Discover from the registry: drive.<x>.total.b exists for every published volume.
        var found = new List<char>();
        foreach (var m in ctx.Metrics.Describe())
        {
            if (!m.Name.StartsWith("drive.", StringComparison.Ordinal) || !m.Name.EndsWith(".total.b", StringComparison.Ordinal)) continue;
            var parts = m.Name.Split('.');
            if (parts.Length >= 2 && parts[1].Length == 1) found.Add(char.ToUpperInvariant(parts[1][0]));
        }
        found.Sort();
        return found;
    }

    // ---- fans ----

    /// <summary>Floor for the auto max, so a fan that has only ever idled doesn't read 100%.</summary>
    public const double MinAutoMaxRpm = 1500;

    /// <summary>Duty cycle for a channel: the chip's own PWM value when it has one, else
    /// rpm ÷ max (per-channel setting, or the observed session maximum).</summary>
    public static double FanPercent(PanelContext c, int channel, string key)
    {
        if (c.Metrics.TryValue(MetricNames.FanControlPct(channel), out double duty))
            return Math.Clamp(duty, 0, 100);

        double rpm = c.Metrics.Value(MetricNames.FanRpm(channel));
        double observedMax = c.Metrics.Value(MetricNames.FanRpm(channel) + MetricNames.MaxSuffix);
        double max = c.MaxOf(key, Math.Max(observedMax, MinAutoMaxRpm));
        return max > 0 ? Math.Clamp(rpm / max * 100, 0, 100) : 0;
    }

    /// <summary>The widget's channel list, or every discovered channel that is spinning.</summary>
    public static List<int> SelectedChannels(PanelContext ctx)
    {
        var configured = ctx.OptionList("channels")
            .Select(s => int.TryParse(s, out int n) ? n : -1)
            .Where(n => n >= 0)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        if (configured.Count > 0) return configured;

        var found = new List<int>();
        int count = (int)ctx.Metrics.Value(MetricNames.FanCount, 0);
        for (int i = 0; i < count; i++)
            if (ctx.Metrics.TryValue(MetricNames.FanRpm(i), out double rpm) && rpm > 0)
                found.Add(i);
        return found;
    }
}
