using Halo.Widgets.Render;

namespace Halo.Widgets.PanelModels;

/// <summary>
/// Warn <b>state</b>, separate from warn colour (tech plan §3.4): L1..L5 are the five steps of
/// <see cref="PanelData.WarnColor"/>, so <c>devWarn{n}</c> is still the colour, but a skin that
/// swaps a glyph or a face on "hot" asks for the step, not the hex.
/// </summary>
public enum WarnLevel { None, L1, L2, L3, L4, L5 }

/// <summary>Why a card has no reading to show — what Azur Archive's Manjuu sign says (design §9.5).
/// Every one of them comes from a state the panels already compute.</summary>
public enum NoData
{
    None,
    /// <summary>The collector is stale or gone: every reading on every card is N/A.</summary>
    Collector,
    /// <summary>The card's headline reading is N/A while the collector is live.</summary>
    Sensor,
    /// <summary>The FPS pipeline has no presenting app.</summary>
    NoApp,
}

/// <summary>
/// A formatted reading. <see cref="Unit"/> is empty whenever <see cref="Text"/> is <c>N/A</c>, so a
/// skin that sets the unit smaller than the number cannot bring back the "N/A °C" the freshness
/// contract forbids (<see cref="PanelContext.Na(string, Func{double, string})"/>).
/// </summary>
public readonly record struct Val(string Text, string Unit = "")
{
    public static readonly Val Na = new("N/A");

    /// <summary>Not shown at all (the user hid it) — distinct from N/A, which is shown. Use this,
    /// never <c>default</c>: a default struct has a null unit.</summary>
    public static readonly Val Absent = new(null!, "");

    public bool IsNa => Text == "N/A";

    public override string ToString() => Unit.Length == 0 ? Text : Text + " " + Unit;
}

/// <summary>A small glyph a row can lead with, and the state it is drawn in.</summary>
public enum RowGlyph { None, Propeller }

/// <summary>Stopped, turning, or flat out — a fan's state for its glyph. A state, never a spin.</summary>
public enum SpinState { Stopped, Cruising, Full }

/// <summary>
/// One semantic piece of a panel. A model lists them in order; a skin's block layout turns each into
/// one composite element (graphs excepted: <see cref="GraphBlock"/> carries a real
/// <see cref="GraphEl"/>). Every accessor is evaluated per tick against the live context.
/// </summary>
public abstract record Block
{
    /// <summary>Hidden blocks take no space, like a hidden Rainformer row.</summary>
    public Func<PanelContext, bool>? Visible { get; init; }
}

/// <summary>The headline reading. <see cref="Caption"/> sits above it ("MAX: 61", "PC LATENCY");
/// <see cref="Warn"/> is the reading's own step.</summary>
public sealed record HeroBlock(Func<PanelContext, Val> Value) : Block
{
    public Func<PanelContext, string>? CaptionLabel { get; init; }
    /// <summary>Value after the caption label, e.g. the session max. Null = label only.</summary>
    public Func<PanelContext, Val>? CaptionValue { get; init; }
    public Func<PanelContext, WarnLevel> Warn { get; init; } = _ => WarnLevel.None;
    /// <summary>A small caption beside the unit ("CPU + GPU" on the power pill).</summary>
    public string? Note { get; init; }
    public HeroKind Kind { get; init; } = HeroKind.Number;
}

public enum HeroKind { Number, Pill }

/// <summary>A labelled reading, optionally with a bar under it and a MAX column.</summary>
public sealed record StatBlock(string Key, Func<PanelContext, string> Label, Func<PanelContext, Val> Value) : Block
{
    /// <summary>Muted words after the label ("budget", "cruising").</summary>
    public Func<PanelContext, string>? Detail { get; init; }
    /// <summary>0..1 for the bar under the row; null = no bar. NaN = no reading (empty track).</summary>
    public Func<PanelContext, double>? Fraction { get; init; }
    /// <summary>Is the bar past its warn point?</summary>
    public Func<PanelContext, bool>? BarWarn { get; init; }
    /// <summary>Colour token of the row's diamond bullet (and of its bar, where a skin colours them).</summary>
    public Func<PanelContext, string>? Token { get; init; }
    public Func<PanelContext, Val>? Max { get; init; }
    public RowGlyph Glyph { get; init; }
    public Func<PanelContext, SpinState>? Spin { get; init; }
    /// <summary>Muted label, no bullet — a fact rather than a reading (an IP address).</summary>
    public bool Plain { get; init; }
}

/// <summary>Two stats side by side.</summary>
public sealed record StatPairBlock(StatBlock Left, StatBlock Right) : Block;

/// <summary>Small outlined label/value boxes in a row (CLOCK · CPU FAN).</summary>
public sealed record ChipsBlock(IReadOnlyList<Chip> Chips) : Block
{
    /// <summary>Label above the value instead of beside it.</summary>
    public bool Stacked { get; init; }
}

public sealed record Chip(string Key, Func<PanelContext, string> Label, Func<PanelContext, Val> Value)
{
    public Func<PanelContext, bool>? Visible { get; init; }
}

/// <summary>A section heading, with an optional right-hand note ("16 threads", "rolling 20 s").</summary>
public sealed record HeadingBlock(Func<PanelContext, string> Text) : Block
{
    public Func<PanelContext, string>? Right { get; init; }
}

/// <summary>A grid of small labelled bars — the per-core view.</summary>
public sealed record GridBlock(IReadOnlyList<GridCell> Cells, int Columns) : Block;

public sealed record GridCell(string Label, Func<PanelContext, Val> Value, Func<PanelContext, double> Fraction, bool IsHeading = false);

/// <summary>A history or frame graph. The element itself is built by the model so it keeps the
/// sampling it needs (stream filter, reset key, NaN samples); a skin only sets its look.</summary>
public sealed record GraphBlock(GraphEl Graph) : Block
{
    /// <summary>The series that gets the skin's full treatment (fill, dots); the rest are drawn plainer.</summary>
    public GraphSeries? Primary { get; init; }
}

/// <summary>Incoming and outgoing traffic, as a pair of readings.</summary>
public sealed record TrafficBlock(StatBlock Down, StatBlock Up) : Block;

/// <summary>One storage volume.</summary>
public sealed record VolumeBlock(char Letter) : Block
{
    public required Func<PanelContext, string> Label { get; init; }
    public required Func<PanelContext, Val> Used { get; init; }
    public required Func<PanelContext, Val> Total { get; init; }
    public required Func<PanelContext, double> Fraction { get; init; }
    public required Func<PanelContext, Val> Temp { get; init; }
    public required Func<PanelContext, WarnLevel> TempWarn { get; init; }
    public required Func<PanelContext, Val> Read { get; init; }
    public required Func<PanelContext, Val> Write { get; init; }
    public required Func<PanelContext, bool> BarWarn { get; init; }
}

/// <summary>A ranked list (top processes).</summary>
public sealed record ListBlock(IReadOnlyList<ListRow> Rows) : Block
{
    public required string PrimaryHeading { get; init; }
    public required string SecondaryHeading { get; init; }
    public Func<PanelContext, Val>? Count { get; init; }
}

public sealed record ListRow(int Rank, Func<PanelContext, string> Name, Func<PanelContext, Val> Primary,
    Func<PanelContext, Val> Secondary, Func<PanelContext, bool> Over);

/// <summary>
/// What a panel shows, independent of how it looks: header text, the card's warn state, why it has
/// no data, and its blocks in order. Built per panel instance from <see cref="PanelContext"/>, so
/// structural options (gpuIndex, volumes, channels…) are already applied.
/// </summary>
public sealed class PanelModel
{
    public required string Type { get; init; }
    public required Func<PanelContext, string> Title { get; init; }
    /// <summary>The tracked caption under the title while the card has data.</summary>
    public required Func<PanelContext, string> Sub { get; init; }
    /// <summary>The card's own warn step — it decides the glyph, tag, key bar and face.</summary>
    public Func<PanelContext, WarnLevel> State { get; init; } = _ => WarnLevel.None;
    /// <summary>Why there is no headline reading; <see cref="NoData.Collector"/> is decided for every
    /// card by <see cref="PanelContext.Stale"/> and need not be returned here.</summary>
    public Func<PanelContext, NoData> Missing { get; init; } = _ => NoData.None;
    /// <summary>What "NO SIGNAL · …" names when the headline sensor is gone.</summary>
    public string MissingWhat { get; init; } = "SENSOR";
    /// <summary>Tag words for warn (L4) and critical (L5).</summary>
    public string WarnTag { get; init; } = "RUNNING HOT";
    public string CritTag { get; init; } = "CRITICAL";
    public List<Block> Blocks { get; } = new();
}
