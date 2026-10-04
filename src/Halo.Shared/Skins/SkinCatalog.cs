using Halo.Shared.Panels;

namespace Halo.Shared.Skins;

/// <summary>One colour token a skin defines. <see cref="Core"/> tokens are the cross-skin contract
/// (every skin defines all of them, with the same meaning); the rest are that skin's own chrome
/// extras.</summary>
public sealed record TokenSpec(string Token, string Group, string Description, bool Core);

/// <summary>A named starting look for a skin: a full palette plus, optionally, defaults for the
/// skin's options (user options still win over these). <see cref="ContrastExempt"/> is for a
/// faithful legacy palette that is the parity baseline and so cannot be contrast-corrected.</summary>
public sealed record PresetSpec(
    string Id,
    string Name,
    IReadOnlyDictionary<string, string> Colors,
    IReadOnlyDictionary<string, string>? Options = null,
    bool HighContrast = false,
    bool ContrastExempt = false);

/// <summary>A named image a skin can draw, with a vector fallback when the file is missing.</summary>
public sealed record AssetSlot(string Id, string Description);

/// <summary>
/// A foreground token the skin draws on a surface token, and the contrast every non-exempt preset
/// must reach between them (skin system tech plan §8): the surface composited over a wallpaper,
/// the foreground composited over that. <see cref="AllWallpapers"/> = black, #808080 and white;
/// otherwise grey only. Declared by the skin because only the skin knows what sits on what.
/// </summary>
public sealed record ContrastPair(string Foreground, string Surface, double MinRatio, bool AllWallpapers = false);

/// <summary>Everything about a skin that is not rendering code: its tokens, presets, options, fonts
/// and image slots. The renderer and the Settings app both read it. <see cref="FontFamily"/> is
/// the family a widget gets when neither it nor the global appearance names one.
/// <see cref="ContrastPairs"/> is what the preset contrast test checks. <see cref="SwatchTokens"/>
/// are the four colours Settings shows beside each preset: the ones covering most of the skin's
/// card, never an alert or warn colour, so a chip shows what the preset looks like.</summary>
public sealed record SkinInfo(
    string Id,
    string Name,
    string Description,
    string Attribution,
    IReadOnlyList<TokenSpec> Tokens,
    IReadOnlyList<PresetSpec> Presets,
    IReadOnlyList<OptionSpec> Options,
    IReadOnlyList<string> FontFiles,
    IReadOnlyList<AssetSlot> Slots,
    string FontFamily,
    double DefaultWidth = 206,
    IReadOnlyList<ContrastPair>? ContrastPairs = null,
    IReadOnlyList<string>? SwatchTokens = null)
{
    /// <summary>The preset a skin starts on: the first one declared.</summary>
    public PresetSpec DefaultPreset => Presets[0];
}

/// <summary>
/// The skins Halo can draw, declared once in code — the same pattern as <see cref="PanelCatalog"/>,
/// so the renderer and the Settings app read one source and there is no parse-failure path.
/// Rendering lives in Halo.Widgets (<c>ISkin</c>); this is only the metadata.
/// </summary>
public static class SkinCatalog
{
    public const string RainformerId = "rainformer";

    /// <summary>Settings-UI group for each core token. The ids are the ones already in users'
    /// config files and must never change.</summary>
    private static readonly Dictionary<string, string> CoreGroups = new(StringComparer.Ordinal)
    {
        ["bgTop"] = "Surfaces", ["bgBody"] = "Surfaces", ["solidLabel"] = "Surfaces", ["emptyBar"] = "Surfaces",
        ["stroke"] = "Surfaces",
        ["title"] = "Text", ["activeTitle"] = "Text", ["text"] = "Text", ["text2"] = "Text",
        ["redText"] = "Text", ["maxLabelGray"] = "Text", ["inactiveButton"] = "Text",
        ["bar"] = "Data", ["histogram"] = "Data", ["netDown"] = "Data", ["netUp"] = "Data",
        ["cpuTemp"] = "Data", ["cpuUsage"] = "Data", ["ramUsage"] = "Data",
        ["gpuTemp"] = "Data", ["gpuUsage"] = "Data", ["gpuMemUsage"] = "Data", ["gpuFan"] = "Data",
        ["red"] = "Warnings", ["barWarn"] = "Warnings", ["staleBadge"] = "Warnings",
        ["devWarn1"] = "Warnings", ["devWarn2"] = "Warnings", ["devWarn3"] = "Warnings",
        ["devWarn4"] = "Warnings", ["devWarn5"] = "Warnings",
    };

    /// <summary>The core contract, in <see cref="ThemeTokens.Defaults"/> order.</summary>
    public static IReadOnlyList<TokenSpec> CoreTokens { get; } =
        ThemeTokens.Defaults.Select(d => new TokenSpec(d.Token, CoreGroups[d.Token], d.Description, Core: true)).ToArray();

    /// <summary>
    /// The preset research's tiered standard (§4.3) on the core tokens, as Rainformer lays them out:
    /// body text 4.5:1 over every wallpaper — translucency is what breaks it; alert text 4.5:1 on
    /// grey; the highlighted title, graphics and the text-drawn warn ramp / idle glyphs 3:1 on grey.
    /// devWarn1..5 are bold 8 pt text, but forcing 4.5:1 on them turns "critical" pastel, so they
    /// are held to 3:1 on purpose. Another skin reuses whichever of these hold for its own layout.
    /// </summary>
    public static IReadOnlyList<ContrastPair> CoreContrastPairs { get; } =
    [
        new("title", "bgTop", 4.5, AllWallpapers: true),
        new("text", "bgBody", 4.5, AllWallpapers: true),
        new("text2", "bgBody", 4.5, AllWallpapers: true),
        new("maxLabelGray", "bgBody", 4.5, AllWallpapers: true),
        new("redText", "bgBody", 4.5),
        new("activeTitle", "bgTop", 3),
        .. new[]
        {
            "bar", "histogram", "netDown", "netUp", "red", "barWarn", "cpuTemp", "cpuUsage", "ramUsage",
            "gpuTemp", "gpuUsage", "gpuMemUsage", "gpuFan", "inactiveButton",
            "devWarn1", "devWarn2", "devWarn3", "devWarn4", "devWarn5",
        }.Select(token => new ContrastPair(token, "bgBody", 3)),
    ];

    /// <summary>Rainformer: the original look, transcribed from the Rainmeter skin. Its default
    /// preset is <see cref="ThemeTokens.Defaults"/> byte for byte — it is also the golden-test
    /// baseline and what a v2 config migrates onto, so it is never contrast-nudged.</summary>
    public static SkinInfo Rainformer { get; } = new(
        Id: RainformerId,
        Name: "Rainformer",
        Description: "Flat Rainmeter classic: a tinted title band over a translucent grey body.",
        Attribution: "Based on Rainformer",
        Tokens: CoreTokens,
        Presets:
        [
            new PresetSpec("rainformer-light", "Rainformer Light",
                ThemeTokens.Defaults.ToDictionary(d => d.Token, d => d.Color, StringComparer.Ordinal),
                ContrastExempt: true),
            // The rest are research §7.4 (tokens + stroke); the contrast test holds them to it.
            // Rainformer Dark is the factory `r` set from Variables.inc and needs its 1 px frame to
            // be itself. The research only corrected it on a grey wallpaper, so six values move
            // here, the least the gate allows: text2 and maxLabelGray go opaque (CD → FF, 3.5 → 4.5:1
            // over white), and the factory CC0000 red — devWarn5, red, cpuTemp, gpuTemp — lifts to
            // E00000 (2.6 → 3.0:1 on grey), still a step darker than devWarn4's pure red.
            Preset("rainformer-dark", "Rainformer Dark", strokeWidth: "1", colors: """
                title=#FFFFFFFF activeTitle=#FF8000FF text=#FFFFFFCD text2=#B2BEB5FF bar=#4DFFC9FF
                histogram=#B0C4DEFA netDown=#3399FFCD netUp=#33FF00CD red=#E00000FF redText=#E98F8FCD
                emptyBar=#FFFFFF19 bgTop=#000000FF bgBody=#000000B4 solidLabel=#3C3C3CFF inactiveButton=#787878FF
                barWarn=#DC143CFF cpuTemp=#E00000FF cpuUsage=#B0C4DEFF ramUsage=#66CC00FF gpuTemp=#E00000FF
                gpuUsage=#B0C4DEFF gpuMemUsage=#66CC00FF gpuFan=#00BFFFFF maxLabelGray=#B2BEB5FF devWarn1=#A6F3FFFF
                devWarn2=#FFFF66FF devWarn3=#FF9933FF devWarn4=#FF0000FF devWarn5=#E00000FF staleBadge=#FF5050DC
                stroke=#ACACAC64
                """),
            // Palettes v2: each adapts one or two coolors.co palettes (sources in
            // THIRD-PARTY-NOTICES.md). The palette gives the hue; surface alpha and ink lightness
            // are fitted to the contrast gate, so light cards keep the pastel on the surfaces and
            // draw data in a deeper cousin of it. PresetUniquenessTests keeps them apart.
            Preset("sakura-dusk", "Sakura Dusk", strokeWidth: "1", colors: """
                title=#FFE5ECFF activeTitle=#FFC48AFF text=#FFF0F4E6 text2=#E3C8D2FF bar=#FF8FABFF
                histogram=#FFB3C6FA netDown=#BDB2FFCD netUp=#9BE3C4CD red=#FF5A5FFF redText=#FF8A8EFF
                emptyBar=#FFFFFF19 bgTop=#8E4468E8 bgBody=#3A2236D4 solidLabel=#2A1727FF inactiveButton=#B08CA0FF
                barWarn=#FF5A5FFF cpuTemp=#FF5A5FFF cpuUsage=#FFB3C6FF ramUsage=#9BE3C4FF gpuTemp=#FF5A5FFF
                gpuUsage=#FFB3C6FF gpuMemUsage=#9BE3C4FF gpuFan=#E4C1F9FF maxLabelGray=#D4B8C4FF devWarn1=#A9DEF9FF
                devWarn2=#FFE29AFF devWarn3=#FFA96BFF devWarn4=#FF6B5FFF devWarn5=#FF2D55FF staleBadge=#FF5A5FDC
                stroke=#FF8FAB64
                """),
            Preset("candy-pastel", "Candy Pastel", strokeWidth: "1", colors: """
                title=#4A2A45FF activeTitle=#B8560BFF text=#4A2A45F2 text2=#74476AFF bar=#DF5199FF
                histogram=#9F6BD9FA netDown=#2B8ECDFF netUp=#359872FF red=#D41F4DFF redText=#C2123EFF
                emptyBar=#4A2A4526 bgTop=#F9C6DDEE bgBody=#FFEFF6EE solidLabel=#FFFFFFFF inactiveButton=#9A7C94FF
                barWarn=#D41F4DFF cpuTemp=#D41F4DFF cpuUsage=#9F6BD9FF ramUsage=#359872FF gpuTemp=#D41F4DFF
                gpuUsage=#9F6BD9FF gpuMemUsage=#359872FF gpuFan=#2F91A1FF maxLabelGray=#6E4A64FF devWarn1=#2B8ECDFF
                devWarn2=#AA7E00FF devWarn3=#D6640BFF devWarn4=#D41F4DFF devWarn5=#9E0A2AFF staleBadge=#D41F4DDC
                stroke=#E0559C64
                """),
            Preset("peach-sorbet", "Peach Sorbet", strokeWidth: "1", colors: """
                title=#4A2F2AFF activeTitle=#B8501EFF text=#4A2F2AF2 text2=#7A4A3FFF bar=#DC5A37FF
                histogram=#D4632BFF netDown=#498AAFFF netUp=#508F6EFF red=#C21E2EFF redText=#B3121FFF
                emptyBar=#4A2F2A26 bgTop=#FFD3B8EE bgBody=#FFEBDDEE solidLabel=#FFFFFFFF inactiveButton=#9E7D77FF
                barWarn=#C21E2EFF cpuTemp=#C21E2EFF cpuUsage=#D4632BFF ramUsage=#508F6EFF gpuTemp=#C21E2EFF
                gpuUsage=#D4632BFF gpuMemUsage=#508F6EFF gpuFan=#3B9090FF maxLabelGray=#6E463CFF devWarn1=#498AAFFF
                devWarn2=#A67D00FF devWarn3=#D9600FFF devWarn4=#C21E2EFF devWarn5=#8E0A1CFF staleBadge=#C21E2EDC
                stroke=#E2724F64
                """),
            Preset("sunrise-glow", "Sunrise Glow", strokeWidth: "1", colors: """
                title=#FFFCF7FF activeTitle=#FCCA46FF text=#FFF6E8E6 text2=#E6D8C0FF bar=#FF8F45FF
                histogram=#FCCA46FA netDown=#7FC5B2CD netUp=#A1C181CD red=#FF4560FF redText=#FF8FA0FF
                emptyBar=#FFFFFF19 bgTop=#B84E16F2 bgBody=#17303FD0 solidLabel=#0F2230FF inactiveButton=#7F9AA6FF
                barWarn=#FF4560FF cpuTemp=#FF4560FF cpuUsage=#FCCA46FF ramUsage=#A1C181FF gpuTemp=#FF4560FF
                gpuUsage=#FCCA46FF gpuMemUsage=#A1C181FF gpuFan=#7FC5B2FF maxLabelGray=#C7D4D2FF devWarn1=#7FC5B2FF
                devWarn2=#FCCA46FF devWarn3=#FF8F45FF devWarn4=#FF6A4DFF devWarn5=#FF2E49FF staleBadge=#FF4560DC
                stroke=#FF8F4564
                """),
            Preset("sunset-horizon", "Sunset Horizon", strokeWidth: "1", colors: """
                title=#FFF7F0FF activeTitle=#FFC15AFF text=#FFF1E6E6 text2=#EBCBBCFF bar=#EAAC8BFF
                histogram=#EAAC8BFA netDown=#9FB9E0CD netUp=#F3C97ACD red=#FF4D5EFF redText=#FF8C98FF
                emptyBar=#FFFFFF19 bgTop=#9C5568F2 bgBody=#2E3558E0 solidLabel=#222845FF inactiveButton=#A39AB0FF
                barWarn=#FF4D5EFF cpuTemp=#FF4D5EFF cpuUsage=#EAAC8BFF ramUsage=#F3C97AFF gpuTemp=#FF4D5EFF
                gpuUsage=#EAAC8BFF gpuMemUsage=#F3C97AFF gpuFan=#D3A0D6FF maxLabelGray=#D9BFB4FF devWarn1=#9FB9E0FF
                devWarn2=#FFD66BFF devWarn3=#FF9B5CFF devWarn4=#FF7A59FF devWarn5=#FF3F56FF staleBadge=#FF4D5EDC
                stroke=#EAAC8B64
                """),
            Preset("sage-forest", "Sage Forest", strokeWidth: "1", colors: """
                title=#EEF2EAFF activeTitle=#EBB35AFF text=#EEF2EAE6 text2=#CAD2C5FF bar=#9CC5A3FF
                histogram=#84A98CFA netDown=#8FC5C2CD netUp=#B5D99CCD red=#E8636AFF redText=#F59096FF
                emptyBar=#FFFFFF19 bgTop=#3E6256E4 bgBody=#243D33E4 solidLabel=#1A2E26FF inactiveButton=#8C9C98FF
                barWarn=#E8636AFF cpuTemp=#E8636AFF cpuUsage=#84A98CFF ramUsage=#B5D99CFF gpuTemp=#E8636AFF
                gpuUsage=#84A98CFF gpuMemUsage=#B5D99CFF gpuFan=#A8D8C9FF maxLabelGray=#B7C2B4FF devWarn1=#8FC5C2FF
                devWarn2=#F2D16BFF devWarn3=#F0A04BFF devWarn4=#E8636AFF devWarn5=#FF404CFF staleBadge=#E8636ADC
                stroke=#84A98C64
                """),
            Preset("mint-cream", "Mint Cream", strokeWidth: "1", colors: """
                title=#1E3A32FF activeTitle=#B0560FFF text=#1E3A32F2 text2=#3F6355FF bar=#3F8F73FF
                histogram=#46917DFF netDown=#3F8FA8FF netUp=#58943CFF red=#C4343CFF redText=#B22830FF
                emptyBar=#1E3A3226 bgTop=#BFDDD3EE bgBody=#E4F4EAEE solidLabel=#FFFFFFFF inactiveButton=#698A7DFF
                barWarn=#C4343CFF cpuTemp=#C4343CFF cpuUsage=#46917DFF ramUsage=#58943CFF gpuTemp=#C4343CFF
                gpuUsage=#46917DFF gpuMemUsage=#58943CFF gpuFan=#2C908EFF maxLabelGray=#46665AFF devWarn1=#3F8FA8FF
                devWarn2=#A67D00FF devWarn3=#D9600FFF devWarn4=#C4343CFF devWarn5=#8E0A1CFF staleBadge=#C4343CDC
                stroke=#3F8F7364
                """),
            Preset("coffee-house", "Coffee House", strokeWidth: "1", colors: """
                title=#F7EADAFF activeTitle=#FF9F45FF text=#F3E6D6E6 text2=#DDB892FF bar=#DDA466FF
                histogram=#E6CCB2FA netDown=#C9B08FCD netUp=#A9C28ACD red=#E8533DFF redText=#F58A78FF
                emptyBar=#FFFFFF19 bgTop=#7F5539EC bgBody=#3A2618E0 solidLabel=#2A1A0FFF inactiveButton=#A58B73FF
                barWarn=#E8533DFF cpuTemp=#E8533DFF cpuUsage=#E6CCB2FF ramUsage=#A9C28AFF gpuTemp=#E8533DFF
                gpuUsage=#E6CCB2FF gpuMemUsage=#A9C28AFF gpuFan=#D6A17AFF maxLabelGray=#C9AE8FFF devWarn1=#A9C9C0FF
                devWarn2=#F5CE62FF devWarn3=#FF9A3DFF devWarn4=#E8533DFF devWarn5=#FF2A1FFF staleBadge=#E8533DDC
                stroke=#DDB89264
                """),
            Preset("coastal-dusk", "Coastal Dusk", strokeWidth: "1", colors: """
                title=#E0FBFCFF activeTitle=#F4AF9CFF text=#E8FBFCE6 text2=#B4D3E4FF bar=#7FB8E0FF
                histogram=#98C1D9FA netDown=#6FC3E8CD netUp=#8FD9C4CD red=#FF5F5FFF redText=#FF8F8FFF
                emptyBar=#FFFFFF19 bgTop=#3D6A9AF0 bgBody=#1F3350DC solidLabel=#17263CFF inactiveButton=#8FA3B8FF
                barWarn=#FF5F5FFF cpuTemp=#FF5F5FFF cpuUsage=#98C1D9FF ramUsage=#8FD9C4FF gpuTemp=#FF5F5FFF
                gpuUsage=#98C1D9FF gpuMemUsage=#8FD9C4FF gpuFan=#7FD6E8FF maxLabelGray=#A9C4D6FF devWarn1=#98C1D9FF
                devWarn2=#FFD27AFF devWarn3=#FF9A5CFF devWarn4=#FF5F5FFF devWarn5=#FF3343FF staleBadge=#FF5F5FDC
                stroke=#98C1D964
                """),
            Preset("nightfall-violet", "Nightfall Violet", strokeWidth: "1", colors: """
                title=#F3E5FFFF activeTitle=#FFB86BFF text=#F3E5FFE6 text2=#D9B8F5FF bar=#C77DFFFF
                histogram=#B26BEFFA netDown=#8FA8FFCD netUp=#6EE7C8CD red=#FF4D6DFF redText=#FF8FA3FF
                emptyBar=#FFFFFF19 bgTop=#3C096CCC bgBody=#1B0636D0 solidLabel=#10002BFF inactiveButton=#9A7FBDFF
                barWarn=#FF4D6DFF cpuTemp=#FF4D6DFF cpuUsage=#B26BEFFF ramUsage=#6EE7C8FF gpuTemp=#FF4D6DFF
                gpuUsage=#B26BEFFF gpuMemUsage=#6EE7C8FF gpuFan=#E0AAFFFF maxLabelGray=#C9A8E6FF devWarn1=#8FA8FFFF
                devWarn2=#FFE066FF devWarn3=#FF9A4DFF devWarn4=#FF4D6DFF devWarn5=#FF1F4BFF staleBadge=#FF4D6DDC
                stroke=#C77DFF64
                """),
            Preset("lavender-haze", "Lavender Haze", strokeWidth: "1", colors: """
                title=#33254FFF activeTitle=#B45309FF text=#33254FF2 text2=#5B4A86FF bar=#7B5BD6FF
                histogram=#8B6BE2FF netDown=#5A7BE0FF netUp=#3A8F7EFF red=#C4244AFF redText=#B0183CFF
                emptyBar=#33254F26 bgTop=#E7C6FFEE bgBody=#F1E8FFEE solidLabel=#FFFFFFFF inactiveButton=#8879AEFF
                barWarn=#C4244AFF cpuTemp=#C4244AFF cpuUsage=#8B6BE2FF ramUsage=#3A8F7EFF gpuTemp=#C4244AFF
                gpuUsage=#8B6BE2FF gpuMemUsage=#3A8F7EFF gpuFan=#C053CEFF maxLabelGray=#55447CFF devWarn1=#5A7BE0FF
                devWarn2=#A17900FF devWarn3=#D45E0FFF devWarn4=#C4244AFF devWarn5=#8E0A2AFF staleBadge=#C4244ADC
                stroke=#7B5BD664
                """),
            Preset("high-contrast", "High contrast", strokeWidth: "2", colors: """
                title=#FFFFFFFF activeTitle=#FFFF00FF text=#FFFFFFFF text2=#00FFFFFF bar=#00FFFFFF
                histogram=#00FFFFFF netDown=#00FFFFFF netUp=#00FF00FF red=#FF6060FF redText=#FF6060FF
                emptyBar=#767676FF bgTop=#000000FF bgBody=#000000FF solidLabel=#000000FF inactiveButton=#C0C0C0FF
                barWarn=#FFFF00FF cpuTemp=#FF6060FF cpuUsage=#00FFFFFF ramUsage=#00FF00FF gpuTemp=#FF6060FF
                gpuUsage=#00FFFFFF gpuMemUsage=#00FF00FF gpuFan=#FFFF00FF maxLabelGray=#C0C0C0FF devWarn1=#00FFFFFF
                devWarn2=#00FF00FF devWarn3=#FFFF00FF devWarn4=#FFA500FF devWarn5=#FF6060FF staleBadge=#FFFF00FF
                stroke=#FFFFFFFF
                """, highContrast: true),
        ],
        Options:
        [
            new OptionSpec("cornerRadius", OptionKind.Double, "4", "Corner radius",
                "Roundness of the card corners, in logical pixels.", Range: "0..20"),
            new OptionSpec("strokeWidth", OptionKind.Double, "0", "Outline width",
                "Width of the frame around the card, in logical pixels. 0 draws no frame.", Range: "0..4"),
        ],
        FontFiles: [],
        Slots: [],
        FontFamily: "Trebuchet MS",
        ContrastPairs: CoreContrastPairs,
        // title band, body, the text on it, the bars
        SwatchTokens: ["bgTop", "bgBody", "text", "bar"]);

    public static IReadOnlyList<SkinInfo> All { get; } = [Rainformer, AzurArchiveSkinInfo.Info];

    /// <summary>The skin with this id, or null when this Halo does not know it.</summary>
    public static SkinInfo? Find(string? id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>A preset written as whitespace-separated <c>token=#RRGGBBAA</c> pairs — 31 named
    /// pairs read better than 31 dictionary initialisers, and a typo fails the type initialiser
    /// (so every test) instead of shipping.</summary>
    private static PresetSpec Preset(string id, string name, string strokeWidth, string colors, bool highContrast = false)
        => new(id, name,
            colors.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('='))
                .ToDictionary(kv => kv[0], kv => ThemeTokens.TryParse(kv[1], out _, out _, out _, out _)
                    ? kv[1] : throw new FormatException($"preset {id}: bad colour '{kv[1]}' for {kv[0]}"), StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["strokeWidth"] = strokeWidth },
            HighContrast: highContrast);

    /// <summary>The preset with this id in <paramref name="skin"/>, or null when it has none by that name.</summary>
    public static PresetSpec? FindPreset(SkinInfo skin, string? presetId)
        => presetId == null ? null : skin.Presets.FirstOrDefault(p => p.Id == presetId);
}
