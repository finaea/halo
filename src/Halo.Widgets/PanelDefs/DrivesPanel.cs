using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// DRIVES panel per tools\extracted\drives.json. Iterates the widget's selected volumes and
/// stacks one 5-sub-row block per drive at a 37-unit pitch:
///   row1  label "(C:) &lt;vol&gt;" (center) + temp "NN°C" (right, staged warn colors)
///   row2  "Used: &lt;auto&gt;B" (left) + "Total: &lt;auto&gt;B" (right)   — "freeMode" volumes show Free
///   row3  usage bar (used fraction, warn &gt;=75%)
///   row4  write/read arrow glyphs (ElegantIcons, left/right, red when active)
///   row5  write/read rate text (AutoScale, inset within the arrow row)
/// Then two shared half-width history graphs: write (left half) / read (right half),
/// one series per drive, autoscale.
///
/// Which volumes appear is a per-widget choice (option "volumes"); the collector publishes every
/// local volume it finds (hardware plan H1). With no selection the panel shows everything the
/// collector published, so a fresh install is not an empty box.
/// </summary>
public static class DrivesPanel
{
    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();
        var t = ctx.Theme;

        // Title band: "DRIVES" (styleTitle — Bold 9pt, centered, upper, colorTitle).
        p.TitleElements.Add(new TextEl
        {
            Text = c => c.TitleOr("DRIVES"),
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
        });

        var letters = SelectedVolumes(ctx);

        foreach (char letter in letters)
        {
            char d = letter;                 // capture per-iteration

            // ---- Row 1: label (center) + temp (right) ----
            // Note: accent-tinting of the "(X:)" prefix (per-drive colorDrive) is omitted — the
            // engine has no per-drive Theme token and no inline multi-color; whole label = colorText.
            p.Elements.Add(new TextEl
            {
                Text = c => $"({d}:) {c.Metrics.Text(MetricNames.DriveLabel(d))}",
                Style = TextStyle.Bold8,
                Align = TextAlign.Center,
                X = t.CenterAlign,
                ColorFn = c => c.Color($"label.{d}", "text"),
                WidthClip = 125,             // ContentWidth-65
                FixedH = 11,
                Advance = 0,                 // pitch (37) carried by prevBottom; first drive => TopMarginFormula
            });
            p.Elements.Add(new TextEl
            {
                // elevated-only sensor: on failure show N/A in text2 rather than hiding the row.
                // The unit goes with the number — "--°C" (and "N/A °C") reads as a temperature
                // that happens to be unprintable, which is not what is being said.
                Text = c => c.Na(MetricNames.DriveTempC(d), c.TempText),
                ColorFn = c => c.Metrics.TryValue(MetricNames.DriveTempC(d), out double tv)
                    ? CpuRamPanelImpl.WarnColor(tv, c.Warn($"temp.{d}"))
                    : "text2",
                VisibleWhen = c => c.Shows($"temp.{d}") && c.Shows("temp"),
                Style = TextStyle.Text8,
                Align = TextAlign.Right,
                SameRow = true,
                FixedH = 11,
            });

            // ---- Row 2: Used/Free (left) + Total (right) ----
            p.Elements.Add(new TextEl
            {
                // freeMode is read per draw, never captured: the catalog does not mark it
                // structural, so flipping a letter does not rebuild the element tree and a
                // build-time capture left the row saying "Used:" until something else forced a
                // rebuild — which is exactly "show free space does nothing".
                Text = c => ShowsFree(c, d)
                    // Free needs both halves; Used needs only its own.
                    ? $"Free: {c.Na(MetricNames.DriveTotalB(d), MetricNames.DriveUsedB(d), (total, used) => $"{ValueFormat.AutoScale(total - used)}B")}"
                    : $"{c.Label("used", "Used:")} {c.Na(MetricNames.DriveUsedB(d), v => $"{ValueFormat.AutoScale(v)}B")}",
                VisibleWhen = c => c.Shows("used"),
                Style = TextStyle.Bold8,
                Align = TextAlign.Left,
                ColorFn = c => c.Color("used", "text"),
                FixedH = 11,
                Advance = 0,
            });
            p.Elements.Add(new TextEl
            {
                Text = c => $"{c.Label("total", "Total:")} {c.Na(MetricNames.DriveTotalB(d), v => $"{ValueFormat.AutoScale(v)}B")}",
                VisibleWhen = c => c.Shows("total"),
                Style = TextStyle.Bold8,
                Align = TextAlign.Right,
                ColorFn = c => c.Color("total", "text"),
                SameRow = true,
                FixedH = 11,
            });

            // ---- Row 3: usage bar (used fraction; warn >=75%) ----
            // Free-mode draws the identical "used portion from the left" visual & warn point.
            p.Elements.Add(new BarEl
            {
                Value = c =>
                {
                    double total = c.Metrics.Value(MetricNames.DriveTotalB(d));
                    return total > 0 ? c.Metrics.Value(MetricNames.DriveUsedB(d)) / total : 0;
                },
                FillColorFn = c =>
                {
                    double total = c.Metrics.Value(MetricNames.DriveTotalB(d));
                    double pct = total > 0 ? c.Metrics.Value(MetricNames.DriveUsedB(d)) * 100 / total : 0;
                    var warn = c.Warn("used");
                    return warn.Length > 0 && pct >= warn[^1] ? "barWarn" : c.Color($"used.{d}", "bar");
                },
                VisibleWhen = c => c.Shows("used") || c.Shows("total"),
                Advance = 0,
            });

            // ---- Row 4: write arrow (down, left) + read arrow (up, right), ElegantIcons 12pt ----
            p.Elements.Add(new TextEl
            {
                Text = _ => "7",        // ElegantIcons 0x37 = down arrow (writing)
                Style = new TextStyle(12, false, "ElegantIcons"),
                Align = TextAlign.Left,
                X = t.ContentMargin,
                ColorFn = c => c.Metrics.Value(MetricNames.DriveWriteBps(d)) > 0 ? "red" : "inactiveButton",
                VisibleWhen = c => c.Shows("write"),
                FixedH = 14,
                Advance = 0,
            });
            p.Elements.Add(new TextEl
            {
                Text = _ => "6",        // ElegantIcons 0x36 = up arrow (reading)
                Style = new TextStyle(12, false, "ElegantIcons"),
                Align = TextAlign.Right,
                X = t.RightAlign,
                ColorFn = c => c.Metrics.Value(MetricNames.DriveReadBps(d)) > 0 ? "red" : "inactiveButton",
                VisibleWhen = c => c.Shows("read"),
                SameRow = true,
                FixedH = 14,
            });

            // ---- Row 5: write rate (left, inset) + read rate (right, inset) ----
            // Bare AutoScale output (no trailing "B"), colorText2, offset +2 within the arrow row.
            p.Elements.Add(new TextEl
            {
                Text = c => c.Na(MetricNames.DriveWriteBps(d), v => ValueFormat.AutoScale(v)),
                Style = TextStyle.Text8,
                Align = TextAlign.Left,
                X = t.ContentMargin + 25,    // 32
                Color = "text2",
                VisibleWhen = c => c.Shows("write"),
                SameRow = true,
                SameRowOffset = 2,
                FixedH = 10,
            });
            p.Elements.Add(new TextEl
            {
                Text = c => c.Na(MetricNames.DriveReadBps(d), v => ValueFormat.AutoScale(v)),
                Style = TextStyle.Text8,
                Align = TextAlign.Right,
                X = t.RightAlign - 25,       // 172
                Color = "text2",
                VisibleWhen = c => c.Shows("read"),
                SameRow = true,
                FixedH = 10,
            });
        }

        // ---- Bottom shared graphs: write history (left half) / read history (right half) ----
        // One series per drive; all "histogram" (no per-drive Theme accent token), autoscale.
        var writeSeries = new List<GraphSeries>();
        var readSeries = new List<GraphSeries>();
        foreach (char letter in letters)
        {
            char d = letter;
            writeSeries.Add(new GraphSeries { Color = ctx.Color("write", "histogram"), Ring = ctx.NewRing(), Sample = c => c.NaSample(MetricNames.DriveWriteBps(d)) });
            readSeries.Add(new GraphSeries { Color = ctx.Color("read", "histogram"), Ring = ctx.NewRing(), Sample = c => c.NaSample(MetricNames.DriveReadBps(d)) });
        }

        // Either history graph is toggled by its metric setting (metrics.write.graph / read.graph).
        // The first shown graph leads the row (Advance=4); the second shares it (SameRow).
        bool showWrite = ctx.Graphs("write");
        bool showRead = ctx.Graphs("read");
        if (showWrite)
        {
            p.Elements.Add(new GraphEl
            {
                X = t.ContentMargin, W = 94, H = ctx.GraphHeight,   // StyleHalfLengthGraphLeft: X=7, W=(ContentWidth-2)/2=94
                Start = GraphStart.Left,
                BgColor = "emptyBar",
                HistoryS = ctx.GraphHistoryS,
                Style = ctx.GraphStyle,
                Series = writeSeries,
                Advance = 4,                              // BottomMargin+1
            });
        }
        if (showRead)
        {
            p.Elements.Add(new GraphEl
            {
                X = t.ContentMargin + 96, W = 94, H = ctx.GraphHeight, // StyleHalfLengthGraphRight: X=7+94+2=103
                Start = GraphStart.Right,
                BgColor = "emptyBar",
                HistoryS = ctx.GraphHistoryS,
                Style = ctx.GraphStyle,
                Series = readSeries,
                SameRow = showWrite,                      // share the write graph's row when both shown
                Advance = 4,                              // else lead its own row
            });
        }

        return p;
    }

    /// <summary>
    /// Does this volume's row show free space instead of used? Answered live, per draw, because
    /// the option is not structural and so never costs a rebuild. Scans the raw "C,D,E" list
    /// rather than splitting it — this runs in both the measure and the draw pass of every drive
    /// row — and matches <see cref="SelectedVolumes"/>: an entry's first character is the letter.
    /// </summary>
    private static bool ShowsFree(PanelContext ctx, char drive)
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
    private static List<char> SelectedVolumes(PanelContext ctx)
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
}
