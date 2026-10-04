using Halo.Shared.Skins;
using Halo.Widgets.PanelModels;
using Halo.Widgets.Render;
using static Halo.Widgets.Skins.AzurArchive.Az;

namespace Halo.Widgets.Skins.AzurArchive;

/// <summary>
/// Azur Archive (design spec; approved mockups in the epic's <c>azur-archive-mockups</c>). Panels come
/// from <see cref="Models"/> through a block layout — one composite element per block, graphs as
/// plain top-level <see cref="GraphEl"/>s with an <see cref="AzurGraphVisual"/> — except FPS, the
/// clock, the top-process receipt and the companion, which are laid out by hand.
/// </summary>
public sealed class AzurArchiveSkin : ISkin
{
    public static AzurArchiveSkin Instance { get; } = new();

    private AzurArchiveSkin() { }

    public SkinInfo Info => AzurArchiveSkinInfo.Info;

    public SkinGeometry Geometry(Theme t) => new(InsetX, HeaderH, Width(t), U(22));

    /// <summary>
    /// Who has a birthday when, "MM-dd" → student ids — SchaleDB <c>students.min.json</c>
    /// <c>BirthDay</c>, fetched 2026-10-04. Any student in it posts on her day, whether or not a
    /// card shows her; twins share one date and the second answers the first. Arona and Plana are
    /// not students there and have no sourced date, so they are not in it.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Birthdays { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["01-05"] = ["kotama"], ["02-02"] = ["karin"], ["02-14"] = ["koyuki"], ["03-14"] = ["yuuka"],
        ["03-24"] = ["asuna"], ["03-25"] = ["aris"], ["04-01"] = ["akane"], ["04-02"] = ["hibiki"],
        ["04-13"] = ["noa"], ["04-19"] = ["hare"], ["04-26"] = ["chihiro"], ["08-01"] = ["maki"],
        ["08-12"] = ["yuzu"], ["08-16"] = ["toki"], ["08-17"] = ["neru"], ["11-13"] = ["utaha"],
        ["12-08"] = ["momoi", "midori"], ["12-31"] = ["kotori"],
    };

    /// <summary>The §9.2 roster (cast expansion, story D13): one student per card type, and her face
    /// at rest. The network card's second seat is <see cref="TrafficEl.SecondDefault"/>.</summary>
    internal static (string Who, string Mood) Roster(string type) => type switch
    {
        "cpu-ram" => ("yuuka", "working"),
        "gpu" => ("midori", "working"),
        "fps" => ("momoi", "excited"),
        "latency" => ("aris", ""),
        "power" => ("asuna", "relaxed"),
        "drives" => ("akane", ""),
        "network" => ("chihiro", ""),
        "fans" => ("utaha", ""),
        "clock" => ("toki", "happy"),
        "topcpu" => ("noa", "relaxed"),
        "topram" => ("koyuki", "relaxed"),
        "companion" => ("arona", ""),
        _ => ("yuuka", ""),
    };

    public Panel? Build(string type, PanelContext ctx)
    {
        // the companion's model is this skin's own: its title is the host's name
        var model = type == "companion" ? AzurCompanion.Model() : Models.Build(type, ctx);
        if (model == null) return null;
        var (who, mood) = Roster(type);
        var card = new AzurCard { Model = model, Character = who, NormalMood = mood };
        var p = new Panel { Chrome = new AzurChrome(header: type != "clock"), Entrance = true, Loops = true };

        switch (type)
        {
            case "fps": Fps(p, card, ctx); break;
            case "clock": Clock(p, card); break;
            case "topcpu" or "topram": Receipt(p, card, type == "topram"); break;
            case "companion": Start(p, card); p.Elements.Add(new CompanionEl(card)); break;
            default: Blocks(p, card, ctx); break;
        }

        if (type != "clock" && AzurChrome.Option(ctx.Theme, "footer") == "true")
            p.Elements.Add(new FooterEl(card, FooterName(type)));
        return p;
    }

    private static string FooterName(string type) => type switch
    {
        "cpu-ram" => "CPU",
        "fps" => "FRAMES",
        "topcpu" or "topram" => "ROSTER",
        _ => type.ToUpperInvariant(),
    };

    private static void Start(Panel p, AzurCard card)
    {
        p.TitleElements.Add(new HeaderEl(card) { AbsY = InsetTop });
        p.Elements.Add(new SpacerEl { AbsY = ContentTop, H = 0, Advance = 0 });
    }

    // ---- the block layout ----

    private static void Blocks(Panel p, AzurCard card, PanelContext ctx)
    {
        Start(p, card);
        string type = card.Model.Type;
        bool castPlaced = false;
        int statIndex = 0;

        foreach (var block in card.Model.Blocks)
        {
            Element el = block switch
            {
                HeroBlock h => new HeroEl(card, h),
                StatBlock s => new StatEl(card, s) { BigGlyph = type == "fans", RowH = type == "fans" ? U(24) : U(22) },
                StatPairBlock sp => new StatPairEl(card, sp),
                ChipsBlock ch => new ChipsEl(card, ch),
                HeadingBlock hd => new BandEl(card, hd),
                GridBlock g => new GridEl(card, g),
                VolumeBlock v => new VolumeEl(card, v),
                TrafficBlock tr => new TrafficEl(card, tr),
                GraphBlock gb => Graph(gb, card, type == "latency", ctx.Theme),
                _ => throw new InvalidOperationException($"no Azur element for {block.GetType().Name}"),
            };
            if (block.Visible is { } vis && el is not GraphEl) el.VisibleWhen = vis;
            p.Elements.Add(el);

            // the network card seats two, one at each end of its traffic row
            if (!castPlaced && block is TrafficBlock)
            {
                p.Elements.Add(new CastEl(card)
                {
                    Bottom = TrafficEl.FaceBottom, Size = TrafficEl.Face, LeftX = Left, ShowNap = false, VisibleWhen = el.VisibleWhen,
                });
                p.Elements.Add(new CastEl(card)
                {
                    Bottom = TrafficEl.FaceBottom, Size = TrafficEl.Face, Option = "character2", Default = TrafficEl.SecondDefault,
                    VisibleWhen = el.VisibleWhen,
                });
                castPlaced = true;
            }

            // the character: beside the hero, or beside the first rows of a card without one
            if (!castPlaced)
            {
                float? bottom = block switch
                {
                    HeroBlock { Kind: HeroKind.Pill } => U(57),
                    HeroBlock => U(67),
                    VolumeBlock => U(5 + 48),
                    StatBlock when type == "fans" => U(48),
                    _ => null,
                };
                if (bottom != null)
                {
                    p.Elements.Add(new CastEl(card) { Bottom = bottom.Value, VisibleWhen = el.VisibleWhen });
                    castPlaced = true;
                }
            }
            if (el is VolumeEl { } vEl && p.Elements.OfType<VolumeEl>().Count() == 1) vEl.RightInset = U(58);
            if (el is StatEl sEl && type == "fans" && statIndex++ < 2) sEl.RightInset = U(62);
        }

        // Nothing to list (no volumes or fan channels discovered — the collector is down, or this
        // machine has none): keep room for Manjuu's sign so the card still says why it is empty.
        if (card.Model.Blocks.Count == 0)
        {
            var none = new StatBlock("none", _ => type == "drives" ? "VOLUMES" : "CHANNELS", _ => Val.Na);
            p.Elements.Add(new StatEl(card, none) { RightInset = U(104) });
            p.Elements.Add(new SpacerEl { H = U(34), Advance = 0 });
            p.Elements.Add(new CastEl(card) { Bottom = U(32) });
        }
    }

    private static GraphEl Graph(GraphBlock gb, AzurCard card, bool histogram, Theme t)
    {
        var g = gb.Graph;
        g.X = Left;
        g.W = Width(t);
        g.Advance = U(6);
        g.Visual = new AzurGraphVisual { Primary = gb.Primary, Histogram = histogram, Critical = () => card.Crit };
        return g;
    }

    // ---- hand layouts ----

    private static void Fps(Panel p, AzurCard card, PanelContext ctx)
    {
        Start(p, card);
        var blocks = card.Model.Blocks;
        var hero = blocks.OfType<HeroBlock>().First();
        var stats = blocks.OfType<StatBlock>().ToDictionary(s => s.Key);
        p.Elements.Add(new FpsHeroEl(card, hero, [stats["low1"], stats["low01"], stats["frametime"], stats["worst"]]));
        p.Elements.Add(new FpsTagsEl(card, stats["app"], stats["dlss"]));
        var graph = Graph(blocks.OfType<GraphBlock>().First(), card, histogram: false, ctx.Theme);
        graph.Advance = U(12);
        p.Elements.Add(graph);
        p.Elements.Add(new FrameLineEl(card, graph, stats["low1ms"]) { VisibleWhen = graph.VisibleWhen });
    }

    private static void Clock(Panel p, AzurCard card)
    {
        p.Elements.Add(new ClockEl(card, card.Model.Blocks.OfType<StatBlock>().First()) { AbsY = InsetTop });
    }

    private static void Receipt(Panel p, AzurCard card, bool byRam)
    {
        Start(p, card);
        p.Elements.Add(new ReceiptEl(card, card.Model.Blocks.OfType<ListBlock>().First(), byRam));
    }
}
