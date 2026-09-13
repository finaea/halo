using Halo.Metrics;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// Network panel per tools\extracted\network.json: external/internal IP rows, the three
/// centre-labelled data rows (⏷SPEED⏶ down/up rate, ⏷PEAK⏶ session peak, ⏷SUM⏶ session totals),
/// and two half-width autoscaling traffic line-graphs (download blue left, upload green right)
/// with ElegantIcons down/up arrow glyphs overlaid at the graph corners. SSID/Signal rows are
/// disabled in the source config and intentionally omitted.
///
/// The "units" option switches every rate and total between bytes and bits.
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

    /// <summary>Bits mode keeps the 1024-step AutoScale the rest of the panel uses; only the
    /// multiplier and the unit change (bytes → bits is ×8).</summary>
    private static bool Bits(PanelContext c) => c.Option("units") == "bits";

    private static string Rate(PanelContext c, double bytesPerSecond)
        => Bits(c)
            ? ValueFormat.AutoScale(bytesPerSecond * 8, 1) + "bit/s"
            : ValueFormat.AutoScale(bytesPerSecond, 1) + "B/s";

    private static string Total(PanelContext c, double bytes)
        => Bits(c)
            ? ValueFormat.AutoScale(bytes * 8, 1) + "bit"
            : ValueFormat.AutoScale(bytes, 1) + "B";

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
        p.Elements.Add(new TextEl
        {
            Text = c => c.Label("ipExternal", "External IP:"),
            VisibleWhen = c => c.Shows("ipExternal"),
            Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.Text(MetricNames.NetIpExternal, "N/A"),
            VisibleWhen = c => c.Shows("ipExternal"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            ColorFn = c => c.Color("ipExternal", "text"),
            WidthClip = 120,
            SameRow = true,
            FixedH = 11,
        });

        // Internal IP row (styleFirstLineText → Bold8/text), 1px gap (RowSpacing).
        p.Elements.Add(new TextEl
        {
            Text = c => c.Label("ipInternal", "Internal IP:"),
            VisibleWhen = c => c.Shows("ipInternal"),
            Style = TextStyle.Bold8, Align = TextAlign.Left, Color = "text", FixedH = 11, Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => c.Metrics.Text(MetricNames.NetIpInternal, "N/A"),
            VisibleWhen = c => c.Shows("ipInternal"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            ColorFn = c => c.Color("ipInternal", "text"),
            WidthClip = 120,
            SameRow = true,
            FixedH = 11,
        });

        // ⏷SPEED⏶ row: download rate (left, full-width solidLabel pill, stylePrimaryText → Bold8/text),
        // centred SPEED label (7pt/text), upload rate (right, Bold8/text).
        p.Elements.Add(new TextEl
        {
            Text = c => Rate(c, c.Metrics.Value(MetricNames.NetDownBps)),
            VisibleWhen = c => c.Shows("down"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Left,
            ColorFn = c => c.Color("down", "text"),
            SolidColor = "solidLabel",
            SolidW = t.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl
        {
            Text = _ => SpeedLabel, Style = CenterLabel, Align = TextAlign.Center, Color = "text",
            VisibleWhen = c => c.Shows("down") || c.Shows("up"),
            SameRow = true, FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => Rate(c, c.Metrics.Value(MetricNames.NetUpBps)),
            VisibleWhen = c => c.Shows("up"),
            Style = TextStyle.Bold8,
            Align = TextAlign.Right,
            ColorFn = c => c.Color("up", "text"),
            SameRow = true,
            FixedH = 11,
        });

        // ⏷PEAK⏶ row: session peak down/up (styleSecondaryText → Text8/text2).
        p.Elements.Add(new TextEl
        {
            Text = c => Rate(c, c.Metrics.Value(MetricNames.NetDownBps + MaxSuffix)),
            VisibleWhen = c => c.Shows("peak"),
            Style = TextStyle.Text8,
            Align = TextAlign.Left,
            ColorFn = c => c.Color("peak", "text2"),
            SolidColor = "solidLabel",
            SolidW = t.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl
        {
            Text = _ => PeakLabel, Style = CenterLabel, Align = TextAlign.Center, Color = "text",
            VisibleWhen = c => c.Shows("peak"), SameRow = true, FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => Rate(c, c.Metrics.Value(MetricNames.NetUpBps + MaxSuffix)),
            VisibleWhen = c => c.Shows("peak"),
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            ColorFn = c => c.Color("peak", "text2"),
            SameRow = true,
            FixedH = 11,
        });

        // ⏷SUM⏶ row: cumulative session totals (styleSecondaryText → Text8/text2).
        p.Elements.Add(new TextEl
        {
            Text = c => Total(c, c.Metrics.Value(MetricNames.NetDownTotalB)),
            VisibleWhen = c => c.Shows("sum"),
            Style = TextStyle.Text8,
            Align = TextAlign.Left,
            ColorFn = c => c.Color("sum", "text2"),
            SolidColor = "solidLabel",
            SolidW = t.ContentWidth,
            SolidH = 11,
            FixedH = 11,
            Advance = t.RowSpacing,
        });
        p.Elements.Add(new TextEl
        {
            Text = _ => SumLabel, Style = CenterLabel, Align = TextAlign.Center, Color = "text",
            VisibleWhen = c => c.Shows("sum"), SameRow = true, FixedH = 11,
        });
        p.Elements.Add(new TextEl
        {
            Text = c => Total(c, c.Metrics.Value(MetricNames.NetUpTotalB)),
            VisibleWhen = c => c.Shows("sum"),
            Style = TextStyle.Text8,
            Align = TextAlign.Right,
            ColorFn = c => c.Color("sum", "text2"),
            SameRow = true,
            FixedH = 11,
        });

        // Two half-width traffic graphs (Line meters), autoscaling (no FixedMax).
        double halfW = (t.ContentWidth - 14) / 2;            // 88
        double dlX = t.ContentMargin;                        // 7
        double ulX = t.ContentMargin + halfW + 14;           // 109

        // download graph (blue, GraphStart Left). Graph-row anchor: (BottomMargin-2)R gap.
        p.Elements.Add(new GraphEl
        {
            X = dlX,
            W = halfW,
            H = ctx.GraphHeight,
            Start = GraphStart.Left,
            BgColor = "emptyBar",
            HistoryS = ctx.GraphHistoryS,
            Style = ctx.GraphStyle,
            VisibleWhen = c => c.Graphs("down"),
            Advance = t.BottomMargin - 2,
            Series =
            {
                new GraphSeries { Color = ctx.Color("down", "netDown"), Ring = ctx.NewRing(), Sample = c => c.Metrics.Value(MetricNames.NetDownBps) },
            },
        });
        // upload graph (green, GraphStart Right), same row, 14px right of the download graph.
        p.Elements.Add(new GraphEl
        {
            X = ulX,
            W = halfW,
            H = ctx.GraphHeight,
            Start = GraphStart.Right,
            BgColor = "emptyBar",
            HistoryS = ctx.GraphHistoryS,
            Style = ctx.GraphStyle,
            VisibleWhen = c => c.Graphs("up"),
            SameRow = true,
            Advance = t.BottomMargin - 2,
            Series =
            {
                new GraphSeries { Color = ctx.Color("up", "netUp"), Ring = ctx.NewRing(), Sample = c => c.Metrics.Value(MetricNames.NetUpBps) },
            },
        });

        // ElegantIcons arrow glyphs overlaid at the graph corners (meterDLArrow/meterULArrow):
        // Y = graph Y - 3, coloured by live traffic (netDown/netUp when >0, else inactiveButton).
        p.Elements.Add(new TextEl
        {
            Text = _ => "7",   // ElegantIcons glyph 0x37 (down arrow)
            Style = ArrowStyle,
            Align = TextAlign.Left,
            ColorFn = c => c.Metrics.Value(MetricNames.NetDownBps) > 0 ? c.Color("down", "netDown") : "inactiveButton",
            VisibleWhen = c => c.Graphs("down") || c.Graphs("up"),
            SameRow = true,
            SameRowOffset = -3,
            FixedH = 14,
        });
        p.Elements.Add(new TextEl
        {
            Text = _ => "6",   // ElegantIcons glyph 0x36 (up arrow)
            Style = ArrowStyle,
            Align = TextAlign.Right,
            ColorFn = c => c.Metrics.Value(MetricNames.NetUpBps) > 0 ? c.Color("up", "netUp") : "inactiveButton",
            VisibleWhen = c => c.Graphs("down") || c.Graphs("up"),
            SameRow = true,
            FixedH = 14,
        });

        return p;
    }
}
