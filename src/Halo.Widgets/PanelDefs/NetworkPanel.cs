using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// Network panel per tools\extracted\network.json: external/internal IP rows, the three
/// centre-labelled data rows (⏷SPEED⏶ down/up rate, ⏷PEAK⏶ session peak, ⏷SUM⏶ session totals),
/// and two half-width autoscaling traffic line-graphs (download blue left, upload green right)
/// with ElegantIcons down/up arrow glyphs overlaid at the graph corners. SSID/Signal rows are
/// disabled in the source config and intentionally omitted.
/// </summary>
public static class NetworkPanel
{
    // ⏷ = U+23F7 (down triangle), ⏶ = U+23F6 (up triangle) — verbatim from the UTF-16LE source.
    private const string SpeedLabel = "⏷SPEED⏶";
    private const string PeakLabel = "⏷PEAK⏶";
    private const string SumLabel = "⏷SUM⏶";

    // styleSecondaryText flourish labels render at 7pt, non-bold.
    private static readonly TextStyle CenterLabel = new(7, false);
    // ElegantIcons arrow glyphs (StyleArrowDown/StyleArrowUp): 12pt, non-bold, private font.
    private static readonly TextStyle ArrowStyle = new(12, false, "ElegantIcons");

    private const string MaxSuffix = MetricNames.MaxSuffix;

    public static Panel Build(PanelContext ctx)
    {
        var p = new Panel();
        var t = ctx.Theme;

        // title band: "NETWORK" (styleTitle → Bold9, centred, upper)
        p.TitleElements.Add(new TextEl
        {
            Text = c => c.TitleOr("NETWORK"),
            Upper = true,
            Style = TextStyle.Bold9,
            Align = TextAlign.Center,
            Color = "title",
        });

        // External IP row (styleFirstLineText → Bold8/text). Anchors the content column at Y=32.
        p.Elements.Add(new TextEl { Text = _ => "External IP:", Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", FixedH = 11 });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.Text(MetricNames.NetIpExternal, "N/A"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            Color = "text",
            WidthClip = 120,
            SameRow = true,
            FixedH = 11,
        });

        // Internal IP row (styleFirstLineText → Bold8/text), 1px gap (RowSpacing).
        p.Elements.Add(new TextEl { Text = _ => "Internal IP:", Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", FixedH = 11, Advance = t.RowSpacing });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.Text(MetricNames.NetIpInternal, "N/A"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            Color = "text",
            WidthClip = 120,
            SameRow = true,
            FixedH = 11,
        });

        // ⏷SPEED⏶ row: download rate (left, full-width solidLabel pill, stylePrimaryText → Bold8/text),
        // centred SPEED label (7pt/text), upload rate (right, Bold8/text). AutoScale F1 + "B/s".
        p.Elements.Add(new TextEl
        {
            Text = c => ValueFormat.AutoScale(c.Metrics.Value(MetricNames.NetDownBps), 1) + "B/s",
            Style = TextStyle.Bold8,
            Align = TextAlign.Left,
            Color = "text",
            SolidColor = "solidLabel",
            SolidW = t.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl { Text = _ => SpeedLabel, Style = CenterLabel, Align = TextAlign.Center, Color = "text", SameRow = true, FixedH = 11 });
        p.Elements.Add(new TextEl
        {
            Text = c => ValueFormat.AutoScale(c.Metrics.Value(MetricNames.NetUpBps), 1) + "B/s",
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            Color = "text",
            SameRow = true,
            FixedH = 11,
        });

        // ⏷PEAK⏶ row: session peak down/up (styleSecondaryText → Text8/text2). AutoScale F1 + "B/s".
        p.Elements.Add(new TextEl
        {
            Text = c => ValueFormat.AutoScale(c.Metrics.Value(MetricNames.NetDownBps + MaxSuffix), 1) + "B/s",
            Style = TextStyle.Text8,
            Align = TextAlign.Left,
            Color = "text2",
            SolidColor = "solidLabel",
            SolidW = t.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl { Text = _ => PeakLabel, Style = CenterLabel, Align = TextAlign.Center, Color = "text", SameRow = true, FixedH = 11 });
        p.Elements.Add(new TextEl
        {
            Text = c => ValueFormat.AutoScale(c.Metrics.Value(MetricNames.NetUpBps + MaxSuffix), 1) + "B/s",
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            Color = "text2",
            SameRow = true,
            FixedH = 11,
        });

        // ⏷SUM⏶ row: cumulative session totals (styleSecondaryText → Text8/text2). AutoScale F1 + "B".
        p.Elements.Add(new TextEl
        {
            Text = c => ValueFormat.AutoScale(c.Metrics.Value(MetricNames.NetDownTotalB), 1) + "B",
            Style = TextStyle.Text8,
            Align = TextAlign.Left,
            Color = "text2",
            SolidColor = "solidLabel",
            SolidW = t.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl { Text = _ => SumLabel, Style = CenterLabel, Align = TextAlign.Center, Color = "text", SameRow = true, FixedH = 11 });
        p.Elements.Add(new TextEl
        {
            Text = c => ValueFormat.AutoScale(c.Metrics.Value(MetricNames.NetUpTotalB), 1) + "B",
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            Color = "text2",
            SameRow = true,
            FixedH = 11,
        });

        // Two half-width traffic graphs (Line meters), 1 Hz, autoscaling (no FixedMax).
        double halfW = (t.ContentWidth - 14) / 2;            // 88
        double dlX = t.ContentMargin;                        // 7
        double ulX = t.ContentMargin + halfW + 14;           // 109

        // download graph (blue, GraphStart Left). Graph-row anchor: (BottomMargin-2)R gap.
        p.Elements.Add(new GraphEl
        {
            X = dlX,
            W = halfW,
            H = 25,
            Start = GraphStart.Left,
            BgColor = "emptyBar",
            SampleRateHz = 5,
            Advance = t.BottomMargin - 2,
            Series =
            {
                new GraphSeries { Color = "netDown", Ring = new HistoryRing((int)halfW), Sample = c => c.Metrics.Value(MetricNames.NetDownBps) },
            },
        });
        // upload graph (green, GraphStart Right), same row, 14px right of the download graph.
        p.Elements.Add(new GraphEl
        {
            X = ulX,
            W = halfW,
            H = 25,
            Start = GraphStart.Right,
            BgColor = "emptyBar",
            SampleRateHz = 5,
            SameRow = true,
            Series =
            {
                new GraphSeries { Color = "netUp", Ring = new HistoryRing((int)halfW), Sample = c => c.Metrics.Value(MetricNames.NetUpBps) },
            },
        });

        // ElegantIcons arrow glyphs overlaid at the graph corners (meterDLArrow/meterULArrow):
        // Y = graph Y - 3, coloured by live traffic (netDown/netUp when >0, else inactiveButton).
        p.Elements.Add(new TextEl
        {
            Text = _ => "7",   // ElegantIcons glyph 0x37 (down arrow)
            Style = ArrowStyle,
            Align = TextAlign.Left,
            ColorFn = c => c.Metrics.Value(MetricNames.NetDownBps) > 0 ? "netDown" : "inactiveButton",
            SameRow = true,
            SameRowOffset = -3,
            FixedH = 14,
        });
        p.Elements.Add(new TextEl
        {
            Text = _ => "6",   // ElegantIcons glyph 0x36 (up arrow)
            Style = ArrowStyle,
            Align = TextAlign.Right,
            ColorFn = c => c.Metrics.Value(MetricNames.NetUpBps) > 0 ? "netUp" : "inactiveButton",
            SameRow = true,
            FixedH = 14,
        });

        return p;
    }
}
