using Halo.Shared.Panels;

namespace Halo.Shared.Skins;

/// <summary>
/// Azur Archive: Azur Lane's port cards for structure, Blue Archive's tablet for ornament, the
/// Millennium cast on the cards (Azur Archive design spec). Metadata only — the renderer is
/// <c>Halo.Widgets.Skins.AzurArchive</c>.
///
/// <para>Presets are written in full, core tokens and extras alike, so a preset switch never leaves
/// a token at another preset's value. Values come from the approved mockups (<c>azur.css</c>),
/// nudged where the contrast gate asked for it — darker golds and reds for anything drawn as
/// text.</para>
/// </summary>
public static class AzurArchiveSkinInfo
{
    public const string Id = "azur-archive";

    /// <summary>The cast a card can show (§9.2), in the order Settings lists them: the two hosts,
    /// the roster in card order, then the extra Millennium students no card defaults to.</summary>
    public static readonly string[] Cast =
    [
        "arona", "plana",
        "yuuka", "midori", "momoi", "aris", "asuna", "akane", "chihiro", "kotama", "utaha", "toki", "noa", "koyuki",
        "yuzu", "kotori", "maki", "neru", "karin", "hare", "hibiki",
    ];

    private static readonly (string Token, string Group, string Description)[] Extras =
    [
        ("tabFill", "Skin extras", "Night Watch's header tab, Momo Pink's header band, state tags"),
        ("tabText", "Skin extras", "Title and sub-label drawn on the tab or band"),
        ("subLabel", "Skin extras", "Small sheared caption under the title"),
        ("rule", "Skin extras", "Header underline, chip outlines, dividers"),
        ("hatch", "Skin extras", "Second stripe of the critical hatch"),
        ("critHatch", "Skin extras", "Dark stripe behind the text of a critical tag"),
        ("shadow", "Skin extras", "Card drop shadow"),
        ("texture", "Skin extras", "Dotted grid in the header corner"),
        ("mosaic", "Skin extras", "Triangle mosaic in the bottom corner"),
        ("bandFill", "Skin extras", "Section band and MomoTalk icon background"),
        ("bandTick", "Skin extras", "Tick at the left of a section band"),
        ("barEnd", "Skin extras", "Far end of the bar gradient"),
        ("faint", "Skin extras", "Core numbers in the grid, decorative captions"),
        ("diamondFill", "Skin extras", "Inside of the status diamond"),
        ("warnText", "Skin extras", "Text on a warning tag"),
        ("critText", "Skin extras", "Text on a critical tag"),
        ("pillFill", "Skin extras", "Power panel's energy pill"),
        ("pillText", "Skin extras", "Reading inside the energy pill"),
        ("keyFps", "Skin extras", "FPS key bar and emblem"),
        ("keyFpsText", "Skin extras", "App name on the FPS card's tag"),
        ("netUpFill", "Skin extras", "Network card's upload bubble"),
        ("netUpText", "Skin extras", "Reading and caption in the upload bubble"),
        ("keyPower", "Skin extras", "Power key bar and emblem"),
        ("keyDrives", "Skin extras", "Drives key bar and emblem"),
        ("keyFans", "Skin extras", "Fans key bar and emblem"),
        ("keyTop", "Skin extras", "Top processes key bar and emblem"),
        ("receiptFill", "Skin extras", "Seminar receipt paper"),
        ("receiptInk", "Skin extras", "Seminar receipt text"),
        ("receiptMuted", "Skin extras", "Seminar receipt headings and rules"),
        ("receiptAccent", "Skin extras", "Seminar receipt's top entry"),
        ("stamp", "Skin extras", "AUDITED stamp"),
        ("signFill", "Skin extras", "Manjuu's sign"),
        ("signInk", "Skin extras", "Text on Manjuu's sign"),
        ("signBorder", "Skin extras", "Manjuu's sign frame and strings"),
        ("mascotFx", "Skin extras", "zZz and other drawn effects"),
        ("lime", "Skin extras", "Headroom / positive delta"),
        ("talkHeader", "Skin extras", "MomoTalk header"),
        ("talkHeaderText", "Skin extras", "Title and heart on the MomoTalk header"),
        ("talkBody", "Skin extras", "MomoTalk message area"),
        ("talkText", "Skin extras", "MomoTalk text"),
        ("talkBubble", "Skin extras", "MomoTalk message bubble"),
        ("talkBubbleText", "Skin extras", "Text in a MomoTalk bubble"),
        ("badge", "Skin extras", "Unread badge"),
    ];

    /// <summary>
    /// What the contrast gate checks for this skin. The text pairs are the core ones plus the
    /// skin's own text-on-surface pairs (tech plan §8). Bars and graph lines are not held to 3:1 on
    /// the card: they sit on their own track, and every warn step also changes a glyph, a texture
    /// and the face (design rule 7), so no state is told by colour alone. The warn ramp is held to
    /// 3:1 because the hero number is painted with it.
    /// </summary>
    private static readonly ContrastPair[] Pairs =
    [
        new("title", "bgTop", 4.5, AllWallpapers: true),
        new("subLabel", "bgTop", 4.5, AllWallpapers: true),
        new("text", "bgBody", 4.5, AllWallpapers: true),
        new("text2", "bgBody", 4.5, AllWallpapers: true),
        new("maxLabelGray", "bgBody", 4.5, AllWallpapers: true),
        new("redText", "bgBody", 4.5),
        new("tabText", "tabFill", 4.5),
        // the FPS app tag and the upload bubble are drawn on their own fills, not the tab. Port Day
        // and Shittim keep white on their bright blue tag (2.62:1) by choice: the look was reviewed
        // and kept on 2026-10-04, and the tag only repeats the game's name.
        new("keyFpsText", "keyFps", 4.5, ExemptPresets: ["port-day", "shittim"]),
        new("netUpText", "netUpFill", 4.5),
        new("warnText", "barWarn", 4.5),
        new("critText", "red", 4.5),
        new("critText", "critHatch", 4.5),
        new("pillText", "pillFill", 4.5),
        new("receiptInk", "receiptFill", 4.5),
        new("signInk", "signFill", 4.5),
        new("talkText", "talkBody", 4.5),
        new("talkBubbleText", "talkBubble", 4.5),
        new("talkHeaderText", "talkHeader", 4.5),
        new("activeTitle", "bgBody", 3),
        new("devWarn4", "bgBody", 3),
        new("devWarn5", "bgBody", 3),
    ];

    private static SkinInfo? _info;

    /// <summary>
    /// The skin. Built on first read rather than in a field initialiser because it and
    /// <see cref="SkinCatalog"/> read each other: the catalogue's <c>All</c> lists this, and this
    /// borrows the catalogue's core tokens. Touching the catalogue first means whichever class a
    /// caller reaches first, exactly one instance is built and <c>All</c> holds that one.
    /// </summary>
    public static SkinInfo Info
    {
        get
        {
            _ = SkinCatalog.All;
            return _info ??= Build();
        }
    }

    private static SkinInfo Build() => new(
        Id: Id,
        Name: "Azur Archive",
        Description: "Cards inspired by Azur Lane's port screens, with Blue Archive's Millennium School characters.",
        Attribution: "Inspired by Azur Lane and Blue Archive",
        Tokens: [.. SkinCatalog.CoreTokens, .. Extras.Select(e => new TokenSpec(e.Token, e.Group, e.Description, Core: false))],
        Presets:
        [
            Preset("port-day", "Port Day", header: "light", colors: """
                title=#1F2D44 activeTitle=#A87200 text=#1F2D44 text2=#55657C maxLabelGray=#55657C
                bar=#18B7F3 histogram=#7C8CF8 netDown=#3DB8E8 netUp=#4B8FE0 red=#D3203A redText=#BE1530
                emptyBar=#D3DFEC bgTop=#F7FAFDF0 bgBody=#EFF5FBF0 solidLabel=#4F6FAE14 inactiveButton=#9AA6B8
                barWarn=#FFC34A cpuTemp=#E0603A cpuUsage=#18B7F3 ramUsage=#F2B12E gpuTemp=#E0603A
                gpuUsage=#9A6FE8 gpuMemUsage=#F2B12E gpuFan=#28A8F5 devWarn1=#6F7E99 devWarn2=#1290CC
                devWarn3=#8455D6 devWarn4=#A87200 devWarn5=#BE1530 staleBadge=#D3203A stroke=#4F6FAE42
                tabFill=#4F6FAE tabText=#FFFFFF subLabel=#55657C rule=#4F6FAE59 hatch=#FF8A96 critHatch=#B00F27 shadow=#18305C38
                texture=#4F6FAE38 mosaic=#18B7F31A bandFill=#E3EEF8 bandTick=#18B7F3 barEnd=#0CD3FF faint=#7F8DA4
                diamondFill=#FFFFFF warnText=#3A2A00 critText=#FFFFFF pillFill=#1F2D44E6 pillText=#FFFFFF
                keyFps=#28A8F5 keyPower=#F5A524 keyDrives=#3FB9A0 keyFans=#4CC6B8 keyTop=#F2C230
                keyFpsText=#FFFFFF netUpFill=#3F78BC netUpText=#FFFFFF
                receiptFill=#FFFEFB receiptInk=#2E2A24 receiptMuted=#857C69 receiptAccent=#A87200 stamp=#D3203A99
                signFill=#FFFFFF signInk=#6B4A28 signBorder=#A87A4A mascotFx=#8D9BB0 lime=#3A9E28
                talkHeader=#FB91A5 talkBody=#FFFFFF talkText=#3D4246 badge=#F94414
                talkBubble=#4C5B6F talkBubbleText=#FFFFFF talkHeaderText=#3A1622
                """),
            Preset("night-watch", "Night Watch", header: "tab", host: "plana", colors: """
                title=#E8EEF8 activeTitle=#F0B429 text=#E8EEF8 text2=#A3B2CA maxLabelGray=#A3B2CA
                bar=#0CD3FF histogram=#8E9BFF netDown=#3DC8F0 netUp=#7FB2F5 red=#D42A3A redText=#FF7884
                emptyBar=#2E3A55 bgTop=#1C2436F0 bgBody=#171D2CF0 solidLabel=#5571AC38 inactiveButton=#6F7C93
                barWarn=#E1A516 cpuTemp=#FF7A5C cpuUsage=#0CD3FF ramUsage=#E1A516 gpuTemp=#FF7A5C
                gpuUsage=#B794F6 gpuMemUsage=#E1A516 gpuFan=#3FB4FF devWarn1=#8EA0BC devWarn2=#0CD3FF
                devWarn3=#B794F6 devWarn4=#F0B429 devWarn5=#FF4D5E staleBadge=#FF4D5E stroke=#8CAADC4D
                tabFill=#5571AC tabText=#FFFFFF subLabel=#A3B2CA rule=#7896D266 hatch=#FF9AA5 critHatch=#B00F27 shadow=#00000070
                texture=#8CAAE638 mosaic=#0CD3FF17 bandFill=#5571AC42 bandTick=#0CD3FF barEnd=#5BE3FF faint=#7D8BA3
                diamondFill=#1B2233 warnText=#2A1E00 critText=#FFFFFF pillFill=#0C101AE6 pillText=#FFFFFF
                keyFps=#3FB4FF keyPower=#F5A524 keyDrives=#3FC9AE keyFans=#4CD6C6 keyTop=#F2C230
                keyFpsText=#171D2C netUpFill=#7FB2F5 netUpText=#000000
                receiptFill=#FFFEFB receiptInk=#2E2A24 receiptMuted=#857C69 receiptAccent=#A87200 stamp=#D3203A99
                signFill=#2B3550 signInk=#E8D4B8 signBorder=#A87A4A mascotFx=#7D8BA3 lime=#94FF63
                talkHeader=#FB91A5 talkBody=#222B40 talkText=#E8EEF8 badge=#F94414
                talkBubble=#3A4864 talkBubbleText=#FFFFFF talkHeaderText=#3A1622
                """),
            Preset("shittim", "Shittim", header: "light", colors: """
                title=#24497A activeTitle=#8A6200 text=#24497A text2=#355B83 maxLabelGray=#355B83
                bar=#28A8F5 histogram=#6B7BEA netDown=#1F9AD6 netUp=#3B7FD0 red=#C41830 redText=#B0122A
                emptyBar=#3060922E bgTop=#E7F9FDF2 bgBody=#C6E7FDF2 solidLabel=#FFFFFF73 inactiveButton=#8AA2BC
                barWarn=#FFC34A cpuTemp=#D2502E cpuUsage=#28A8F5 ramUsage=#E8A41E gpuTemp=#D2502E
                gpuUsage=#8E6AE0 gpuMemUsage=#E8A41E gpuFan=#1E90E0 devWarn1=#55759A devWarn2=#0E7DBD
                devWarn3=#7348C8 devWarn4=#8A6200 devWarn5=#B0122A staleBadge=#C41830 stroke=#30609240
                tabFill=#2A78B8 tabText=#FFFFFF subLabel=#355B83 rule=#28A8F573 hatch=#FF8A96 critHatch=#B00F27 shadow=#18305C33
                texture=#3060923D mosaic=#FFFFFF66 bandFill=#FFFFFF99 bandTick=#28A8F5 barEnd=#59C8FF faint=#5F82A8
                diamondFill=#FFFFFF warnText=#3A2A00 critText=#FFFFFF pillFill=#1F3F63E6 pillText=#FFFFFF
                keyFps=#28A8F5 keyPower=#F5A524 keyDrives=#2FAE94 keyFans=#3DB8AA keyTop=#E8B820
                keyFpsText=#FFFFFF netUpFill=#3878C5 netUpText=#FFFFFF
                receiptFill=#FFFEFB receiptInk=#2E2A24 receiptMuted=#857C69 receiptAccent=#A87200 stamp=#D3203A99
                signFill=#FFFFFF signInk=#6B4A28 signBorder=#A87A4A mascotFx=#6F90B2 lime=#2E8A1E
                talkHeader=#FB91A5 talkBody=#FFFFFF talkText=#3D4246 badge=#F94414
                talkBubble=#4C5B6F talkBubbleText=#FFFFFF talkHeaderText=#3A1622
                """),
            Preset("momo-pink", "Momo Pink", header: "band", colors: """
                title=#3D4246 activeTitle=#9C6800 text=#3D4246 text2=#59636D maxLabelGray=#59636D
                bar=#3BB5E8 histogram=#7C8CF8 netDown=#3BB5E8 netUp=#4C5B6F red=#D3203A redText=#BE1530
                emptyBar=#D8DFE4 bgTop=#F2F7F8F5 bgBody=#EEF3F5F5 solidLabel=#4C5B6F12 inactiveButton=#98A1AA
                barWarn=#FFC34A cpuTemp=#E0603A cpuUsage=#3BB5E8 ramUsage=#F2B12E gpuTemp=#E0603A
                gpuUsage=#A47DEF gpuMemUsage=#F2B12E gpuFan=#3BB5E8 devWarn1=#6F7B87 devWarn2=#1A8DC0
                devWarn3=#8455D6 devWarn4=#9C6800 devWarn5=#BE1530 staleBadge=#D3203A stroke=#4C5B6F38
                tabFill=#FB91A5 tabText=#2B2F33 subLabel=#59636D rule=#4C5B6F59 hatch=#FF8A96 critHatch=#B00F27 shadow=#3D424633
                texture=#FB91A54D mosaic=#FB91A51F bandFill=#E7EDF0 bandTick=#FB91A5 barEnd=#6BCBF0 faint=#7F8994
                diamondFill=#FFFFFF warnText=#3A2A00 critText=#FFFFFF pillFill=#3D4246E6 pillText=#FFFFFF
                keyFps=#3BB5E8 keyPower=#F5A524 keyDrives=#3FB9A0 keyFans=#4CC6B8 keyTop=#F2C230
                keyFpsText=#2B2F33 netUpFill=#4C5B6F netUpText=#FFFFFF
                receiptFill=#FFFEFB receiptInk=#2E2A24 receiptMuted=#857C69 receiptAccent=#A87200 stamp=#D3203A99
                signFill=#FFFFFF signInk=#6B4A28 signBorder=#A87A4A mascotFx=#98A1AA lime=#3A9E28
                talkHeader=#FB91A5 talkBody=#FFFFFF talkText=#3D4246 badge=#F94414
                talkBubble=#4C5B6F talkBubbleText=#FFFFFF talkHeaderText=#3A1622
                """),
            Preset("high-contrast", "High contrast", header: "light", strokeWidth: "2", texture: "false", highContrast: true, colors: """
                title=#FFFFFF activeTitle=#FFFF00 text=#FFFFFF text2=#FFFFFF maxLabelGray=#FFFFFF
                bar=#00FFFF histogram=#00FFFF netDown=#00FFFF netUp=#00FF00 red=#FF4040 redText=#FF6060
                emptyBar=#444444 bgTop=#000000 bgBody=#000000 solidLabel=#00000000 inactiveButton=#C0C0C0
                barWarn=#FFFF00 cpuTemp=#FF6060 cpuUsage=#00FFFF ramUsage=#FFFF00 gpuTemp=#FF6060
                gpuUsage=#00FFFF gpuMemUsage=#FFFF00 gpuFan=#00FFFF devWarn1=#00FFFF devWarn2=#00FF00
                devWarn3=#FFFF00 devWarn4=#FFA500 devWarn5=#FF6060 staleBadge=#FFFF00 stroke=#FFFFFF
                tabFill=#FFFF00 tabText=#000000 subLabel=#FFFFFF rule=#FFFFFF hatch=#000000 critHatch=#FF9090 shadow=#00000000
                texture=#00000000 mosaic=#00000000 bandFill=#000000 bandTick=#00FFFF barEnd=#00FFFF faint=#C0C0C0
                diamondFill=#000000 warnText=#000000 critText=#000000 pillFill=#000000 pillText=#FFFFFF
                keyFps=#00FFFF keyPower=#00FFFF keyDrives=#00FFFF keyFans=#00FFFF keyTop=#00FFFF
                keyFpsText=#000000 netUpFill=#00FF00 netUpText=#000000
                receiptFill=#000000 receiptInk=#FFFFFF receiptMuted=#C0C0C0 receiptAccent=#FFFF00 stamp=#FF4040
                signFill=#000000 signInk=#FFFFFF signBorder=#FFFFFF mascotFx=#FFFFFF lime=#3FF23F
                talkHeader=#FFFF00 talkBody=#000000 talkText=#FFFFFF badge=#FF4040
                talkBubble=#FFFFFF talkBubbleText=#000000 talkHeaderText=#000000
                """),
        ],
        Options:
        [
            new OptionSpec("character", OptionKind.Enum, "auto", "Character",
                "Who sits on the card. Auto is the panel's own cast member: Yuuka on CPU / RAM, Midori on GPU, "
                + "Momoi on FPS, Aris on latency, Asuna on power, Akane on drives, Chihiro on network, Utaha on fans, "
                + "Toki on the clock, Noa and Koyuki on the top-process receipts.",
                Choices: ["auto", .. Cast, "none"]),
            new OptionSpec("character2", OptionKind.Enum, "auto", "Second character",
                "Who sits at the right end of the network card's traffic row (Kotama on auto). The other cards have one seat.",
                Choices: ["auto", .. Cast, "none"], Types: ["network"]),
            new OptionSpec("host", OptionKind.Enum, "auto", "Companion host",
                "Who hosts the companion card. Auto is Arona by day and Plana by night.",
                Choices: ["auto", "arona", "plana"]),
            new OptionSpec("talk", OptionKind.Bool, "true", "Character lines",
                "Short lines in the characters' voices: the companion's MomoTalk thread and the auditor's remark on the receipt."),
            new OptionSpec("addressAs", OptionKind.Text, "Sensei", "Address the user as",
                "What the characters call the user. Empty leaves the name out."),
            new OptionSpec("texture", OptionKind.Bool, "true", "Texture",
                "The dotted grid and triangle mosaic in the card corners."),
            new OptionSpec("footer", OptionKind.Bool, "false", "Footer",
                "A small \"HALO ✦ <panel>\" ticket line along the bottom of the card.", Structural: true),
            new OptionSpec("gameArt", OptionKind.Bool, "true", "Game art",
                "Off draws every card without character art, the same way it looks when the art folder is removed."),
            new OptionSpec("halo", OptionKind.Bool, "true", "Halo",
                "The halo above each character's face, and its slow turn at full motion."),
            new OptionSpec("header", OptionKind.Enum, "light", "Header",
                "light: dark title on the card. tab: title on a solid slanted tab. band: a full-width coloured band.",
                Choices: ["light", "tab", "band"]),
            new OptionSpec("strokeWidth", OptionKind.Double, "1", "Outline width",
                "Width of the frame around the card, in pixels at the default scale. 0 draws no frame.", Range: "0..4"),
        ],
        FontFiles:
        [
            "BarlowCondensed-Light.ttf", "BarlowCondensed-Medium.ttf", "BarlowCondensed-SemiBold.ttf",
            "BarlowCondensed-Bold.ttf", "MPLUSRounded1c-Medium.ttf", "MPLUSRounded1c-ExtraBold.ttf",
            "Oxanium-SemiBold.ttf",
        ],
        Slots:
        [
            .. Cast.Select(c => new AssetSlot(c, $"{char.ToUpperInvariant(c[0])}{c[1..]}: card face, plus <name>-<mood>.png moods")),
            new AssetSlot("manjuu", "Manjuu, who naps behind the no-signal sign"),
        ],
        FontFamily: "Rounded Mplus 1c",
        ContrastPairs: Pairs,
        // card, ink, bars, and the accent that tells the presets apart (Night Watch's tab, Momo
        // Pink's band); bgTop is near bgBody on every preset, so it would be a wasted swatch
        SwatchTokens: ["bgBody", "title", "bar", "tabFill"]);

    private static PresetSpec Preset(string id, string name, string header, string colors,
        string strokeWidth = "1", string texture = "true", bool highContrast = false, string host = "auto")
        => new(id, name,
            colors.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('='))
                .ToDictionary(kv => kv[0], kv => Hex(id, kv[0], kv[1]), StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["header"] = header, ["strokeWidth"] = strokeWidth, ["texture"] = texture, ["host"] = host,
            },
            HighContrast: highContrast);

    /// <summary>#RRGGBB gets an opaque alpha, so the table above can leave it off.</summary>
    private static string Hex(string preset, string token, string hex)
    {
        string full = hex.Length == 7 ? hex + "FF" : hex;
        return ThemeTokens.TryParse(full, out _, out _, out _, out _)
            ? full : throw new FormatException($"preset {preset}: bad colour '{hex}' for {token}");
    }
}
