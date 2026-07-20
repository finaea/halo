using Halo.Shared.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// DRIVES panel per tools\extracted\drives.json. Iterates the active drive letters
/// (ctx.Settings.DriveLetters) and stacks one 5-sub-row block per drive at a 37-unit pitch:
///   row1  label "(C:) &lt;vol&gt;" (center) + temp "NN°C" (right, staged warn colors)
///   row2  "Used: &lt;auto&gt;B" (left) + "Total: &lt;auto&gt;B" (right)   — drive E is Free-mode
///   row3  usage bar (used fraction, warn &gt;=75%)
///   row4  write/read arrow glyphs (ElegantIcons, left/right, red when active)
///   row5  write/read rate text (AutoScale, inset within the arrow row)
/// Then two shared half-width history graphs: write (left half) / read (right half),
/// one series per active drive, 1 Hz, autoscale.
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
            Text = c => c.Options.GetValueOrDefault("title", "").Length > 0 ? c.Options["title"] : "DRIVES",
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
        });

        // Active drive letters (config default: C,D,E,F,G,H,I).
        var letters = new List<char>();
        foreach (var s in ctx.Settings.DriveLetters)
            if (!string.IsNullOrEmpty(s)) letters.Add(char.ToUpperInvariant(s[0]));

        foreach (char letter in letters)
        {
            char d = letter;                 // capture per-iteration
            bool freeMode = d == 'E';        // per-config: only E shows "Free:" (spaceInUseDrive5=0)

            // ---- Row 1: label (center) + temp (right) ----
            // Note: accent-tinting of the "(X:)" prefix (per-drive colorDrive) is omitted — the
            // engine has no per-drive Theme token and no inline multi-color; whole label = colorText.
            p.Elements.Add(new TextEl
            {
                Text = c => $"({d}:) {c.Metrics.Text(MetricNames.DriveLabel(d))}",
                Style = TextStyle.Bold8,
                Align = TextAlign.Center,
                X = t.CenterAlign,
                Color = "text",
                WidthClip = 125,             // ContentWidth-65
                FixedH = 11,
                Advance = 0,                 // pitch (37) carried by prevBottom; first drive => TopMarginFormula
            });
            p.Elements.Add(new TextEl
            {
                // elevated-only sensor: on failure show "--°C" in text2 rather than hiding the row
                Text = c => c.Metrics.TryValue(MetricNames.DriveTempC(d), out double tv)
                    ? $"{ValueFormat.Int0(tv)}°C"
                    : "--°C",
                ColorFn = c => c.Metrics.TryValue(MetricNames.DriveTempC(d), out double tv)
                    ? CpuRamPanelImpl.WarnColor(tv, 35, 45, 55, 65)
                    : "text2",
                Style = TextStyle.Text8,
                Align = TextAlign.Right,
                SameRow = true,
                FixedH = 11,
            });

            // ---- Row 2: Used/Free (left) + Total (right) ----
            p.Elements.Add(new TextEl
            {
                Text = c =>
                {
                    double used = c.Metrics.Value(MetricNames.DriveUsedB(d));
                    double total = c.Metrics.Value(MetricNames.DriveTotalB(d));
                    return freeMode
                        ? $"Free: {ValueFormat.AutoScale(total - used)}B"
                        : $"Used: {ValueFormat.AutoScale(used)}B";
                },
                Style = TextStyle.Bold8,
                Align = TextAlign.Left,
                Color = "text",
                FixedH = 11,
                Advance = 0,
            });
            p.Elements.Add(new TextEl
            {
                Text = c => $"Total: {ValueFormat.AutoScale(c.Metrics.Value(MetricNames.DriveTotalB(d)))}B",
                Style = TextStyle.Bold8,
                Align = TextAlign.Right,
                Color = "text",
                SameRow = true,
                FixedH = 11,
            });

            // ---- Row 3: usage bar (used fraction; warn >=75%) ----
            // Free-mode (E) draws the identical "used portion from the left" visual & warn point.
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
                    return pct >= 75 ? "barWarn" : "bar";
                },
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
                SameRow = true,
                FixedH = 14,
            });

            // ---- Row 5: write rate (left, inset) + read rate (right, inset) ----
            // Bare AutoScale output (no trailing "B"), colorText2, offset +2 within the arrow row.
            p.Elements.Add(new TextEl
            {
                Text = c => ValueFormat.AutoScale(c.Metrics.Value(MetricNames.DriveWriteBps(d))),
                Style = TextStyle.Text8,
                Align = TextAlign.Left,
                X = t.ContentMargin + 25,    // 32
                Color = "text2",
                SameRow = true,
                SameRowOffset = 2,
                FixedH = 10,
            });
            p.Elements.Add(new TextEl
            {
                Text = c => ValueFormat.AutoScale(c.Metrics.Value(MetricNames.DriveReadBps(d))),
                Style = TextStyle.Text8,
                Align = TextAlign.Right,
                X = t.RightAlign - 25,       // 172
                Color = "text2",
                SameRow = true,
                FixedH = 10,
            });
        }

        // ---- Bottom shared graphs: write history (left half) / read history (right half) ----
        // One series per active drive; all "histogram" (no per-drive Theme accent token); 1 Hz, autoscale.
        var writeSeries = new List<GraphSeries>();
        var readSeries = new List<GraphSeries>();
        foreach (char letter in letters)
        {
            char d = letter;
            writeSeries.Add(new GraphSeries { Color = "histogram", Ring = new HistoryRing(94), Sample = c => c.Metrics.Value(MetricNames.DriveWriteBps(d)) });
            readSeries.Add(new GraphSeries { Color = "histogram", Ring = new HistoryRing(94), Sample = c => c.Metrics.Value(MetricNames.DriveReadBps(d)) });
        }

        // Either history graph is toggleable via Options graphDriveWrite/graphDriveRead (default on).
        // The first shown graph leads the row (Advance=4); the second shares it (SameRow).
        bool showWrite = ctx.GraphLineVisible("graphDriveWrite");
        bool showRead = ctx.GraphLineVisible("graphDriveRead");
        if (showWrite)
        {
            p.Elements.Add(new GraphEl
            {
                X = t.ContentMargin, W = 94, H = 25,     // StyleHalfLengthGraphLeft: X=7, W=(ContentWidth-2)/2=94
                Start = GraphStart.Left,
                BgColor = "emptyBar",
                SampleRateHz = 1,
                Series = writeSeries,
                Advance = 4,                              // BottomMargin+1
            });
        }
        if (showRead)
        {
            p.Elements.Add(new GraphEl
            {
                X = t.ContentMargin + 96, W = 94, H = 25, // StyleHalfLengthGraphRight: X=7+94+2=103
                Start = GraphStart.Right,
                BgColor = "emptyBar",
                SampleRateHz = 1,
                Series = readSeries,
                SameRow = showWrite,                      // share the write graph's row when both shown
                Advance = 4,                              // else lead its own row
            });
        }

        return p;
    }
}
