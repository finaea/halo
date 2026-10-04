using Halo.Shared.Config;
using Halo.Shared.Panels;
using Halo.Widgets;
using Halo.Widgets.Harness;
using Halo.Widgets.PanelModels;
using Halo.Widgets.Render;
using Halo.Widgets.Skins;
using Halo.Widgets.Skins.AzurArchive;

namespace Halo.Tests;

/// <summary>Panel models, card states and the cast's mood chain. Pure: no Dx, no disk beyond fixtures.</summary>
public sealed class AzurArchiveModelTests
{
    private static PanelContext Ctx(string type, string fixture, string? fixturePath = null)
    {
        var metrics = FixtureMetrics.Load(fixturePath ?? fixture);
        var settings = new AppSettings();
        settings.Appearance.Skin = "azur-archive";
        var widget = new WidgetInstance { Id = "t-" + type, Type = type };
        var theme = Theme.Resolve(settings.Appearance, widget, 1.7);
        var ctx = new PanelContext { Metrics = metrics, Theme = theme, Settings = settings, Widget = widget, Type = PanelCatalog.Find(type) };
        metrics.Advance(1.0, 1000);
        ctx.Stale = metrics.Stale;
        ctx.TickIndex = 1;
        ctx.NowQpc = 1000;
        return ctx;
    }

    /// <summary>A companion context whose mood has been fed a fixture to its last point
    /// (the same loop SystemMoodTests uses); <paramref name="fixture"/> null = a mood never fed.</summary>
    private static PanelContext MoodCtx(string? fixture)
    {
        var ctx = Ctx("companion", "idle");
        var mood = new SystemMood();
        if (fixture != null)
        {
            long tick = System.Diagnostics.Stopwatch.Frequency / 5;
            var f = FixtureMetrics.Load(fixture);
            int ticks = f.DurationS is { } d ? (int)Math.Ceiling(d / 0.2) : PanelRenderer.Ticks;
            for (int k = 0; k < ticks; k++)
            {
                f.Advance((double)k / (ticks - 1), k * tick);
                mood.Update(f, k * tick, new DateTime(2026, 3, 14, 12, 0, 0).AddSeconds(k * 0.2));
            }
        }
        return new PanelContext
        {
            Metrics = ctx.Metrics, Theme = ctx.Theme, Settings = ctx.Settings, Widget = ctx.Widget,
            Type = ctx.Type, Mood = mood, TickIndex = 1, NowQpc = 1000,
        };
    }

    [Theory]
    [InlineData("hot", MoodState.Critical, WarnLevel.L5, NoData.None)]
    [InlineData("mood-hot", MoodState.Hot, WarnLevel.L4, NoData.None)]
    [InlineData("idle", MoodState.Idle, WarnLevel.None, NoData.None)]
    [InlineData("mood-busy", MoodState.Busy, WarnLevel.None, NoData.None)]
    [InlineData("mood-gaming", MoodState.Gaming, WarnLevel.None, NoData.None)]
    [InlineData("na", MoodState.Asleep, WarnLevel.None, NoData.Collector)]
    public void Companion_status_follows_the_mood(string fixture, MoodState mood, WarnLevel level, NoData missing)
    {
        var ctx = MoodCtx(fixture);
        Assert.True(ctx.Mood.Known);
        Assert.Equal(mood, ctx.Mood.State);
        var model = AzurCompanion.Model();
        Assert.Equal(missing, model.Missing(ctx));
        Assert.Equal(level, model.State(ctx));

        // through the card: a missing reading claims no level (freshness contract)
        var card = new AzurCard { Model = model, Character = "arona", NormalMood = "normal" }.Eval(ctx);
        Assert.Equal(level, card.Level);
        Assert.Equal(missing, card.Missing);
        Assert.Equal(level == WarnLevel.L5, card.Crit);
        Assert.Equal(level == WarnLevel.L4, card.Warn);
    }

    [Fact]
    public void Companion_with_no_known_mood_claims_nothing()
    {
        var ctx = MoodCtx(null);
        Assert.False(ctx.Mood.Known);
        var model = AzurCompanion.Model();
        Assert.Equal(WarnLevel.None, model.State(ctx));
        Assert.Equal(NoData.None, model.Missing(ctx));
    }

    [Fact]
    public void Every_azur_preset_defines_critHatch_and_the_crit_pair_is_declared()
    {
        var skin = Halo.Shared.Skins.AzurArchiveSkinInfo.Info;
        Assert.Contains(skin.Tokens, t => t.Token == "critHatch");
        Assert.Contains(skin.ContrastPairs!, p => p.Foreground == "critText" && p.Surface == "critHatch" && p.MinRatio == 4.5);
        foreach (var p in skin.Presets)
            Assert.True(p.Colors.ContainsKey("critHatch"), p.Id);
    }

    private static HeroBlock Hero(PanelModel m) => m.Blocks.OfType<HeroBlock>().First();

    public static IEnumerable<object[]> Types() => PanelFactory.KnownTypes.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(Types))]
    public void Every_known_type_builds_a_model(string type)
        => Assert.NotNull(Models.Build(type, Ctx(type, "idle")));

    // ---- heroes ----

    [Fact]
    public void Cpu_hero_is_NA_with_empty_unit_when_temp_is_NA()
    {
        var ctx = Ctx("cpu-ram", "partial");
        var v = Hero(Models.Build("cpu-ram", ctx)!).Value(ctx);
        Assert.True(v.IsNa);
        Assert.Equal("", v.Unit);
    }

    [Fact]
    public void Cpu_hero_caption_is_MAX_of_the_package_temp_max()
    {
        var ctx = Ctx("cpu-ram", "gaming");
        var hero = Hero(Models.Build("cpu-ram", ctx)!);
        Assert.Equal("MAX:", hero.CaptionLabel!(ctx));
        Assert.Equal("76", hero.CaptionValue!(ctx).Text); // gaming.json cpu.package.temp.c.max
    }

    [Fact]
    public void Power_hero_sums_cpu_and_gpu()
    {
        var ctx = Ctx("power", "gaming");
        var v = Hero(Models.Build("power", ctx)!).Value(ctx);
        Assert.Equal("274", v.Text); // 88 + 186
        Assert.Equal("W", v.Unit);
    }

    [Theory]
    [InlineData("cpu.package.power.w")]
    [InlineData("gpu.0.power.w")]
    public void Power_hero_is_NA_unless_both_read(string missing)
    {
        string path = Path.Combine(Path.GetTempPath(), $"halo-fx-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ \"extends\": \"gaming\", \"na\": [\"" + missing + "\"] }");
        try
        {
            var ctx = Ctx("power", "gaming", path);
            Assert.True(Hero(Models.Build("power", ctx)!).Value(ctx).IsNa);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Fps_hero_has_no_MAX_caption()
    {
        var ctx = Ctx("fps", "gaming");
        var hero = Hero(Models.Build("fps", ctx)!);
        Assert.Null(hero.CaptionLabel);
        Assert.Null(hero.CaptionValue);
    }

    // ---- levels, spin, names ----

    [Theory]
    [InlineData(10)] [InlineData(50)] [InlineData(55)] [InlineData(60)] [InlineData(65)]
    [InlineData(70)] [InlineData(75)] [InlineData(80)] [InlineData(95)]
    public void Level_matches_WarnColor_step_for_step(double v)
    {
        double[] t = [50, 60, 70, 80];
        var expected = PanelData.WarnColor(v, t) switch
        {
            "devWarn1" => WarnLevel.L1, "devWarn2" => WarnLevel.L2, "devWarn3" => WarnLevel.L3,
            "devWarn4" => WarnLevel.L4, _ => WarnLevel.L5,
        };
        Assert.Equal(expected, Models.Level(v, t));
    }

    [Theory]
    [InlineData(144, WarnLevel.L1)]
    [InlineData(60, WarnLevel.L1)]   // >= t[1]
    [InlineData(59, WarnLevel.L4)]   // < t[1]
    [InlineData(30, WarnLevel.L4)]   // >= t[0]
    [InlineData(29, WarnLevel.L5)]   // < t[0]
    public void FpsLevel_steps(double fps, WarnLevel want)
        => Assert.Equal(want, Models.FpsLevel(fps, [30, 60, 90, 120]));

    [Theory]
    [InlineData(0, 40, SpinState.Stopped)]    // 0 rpm is stopped even with duty > 0
    [InlineData(1200, 90, SpinState.Full)]
    [InlineData(1200, 89, SpinState.Cruising)]
    [InlineData(1200, 50, SpinState.Cruising)]
    public void SpinOf_states(double rpm, double pct, SpinState want)
        => Assert.Equal(want, Models.SpinOf(rpm, pct));

    // ---- card states (Manjuu sign) ----

    private static AzurCard Card(string type, string fixture, string mood = "")
    {
        var ctx = Ctx(type, fixture);
        return new AzurCard { Model = Models.Build(type, ctx)!, Character = "yuuka", NormalMood = mood }.Eval(ctx);
    }

    [Fact]
    public void Stale_collector_marks_card_Missing_Collector_with_no_level()
    {
        var c = Card("cpu-ram", "na");
        Assert.Equal(NoData.Collector, c.Missing);
        Assert.True(c.Down);
        Assert.Equal(WarnLevel.None, c.Level);
    }

    [Fact] public void Partial_cpu_card_is_Missing_Sensor() => Assert.Equal(NoData.Sensor, Card("cpu-ram", "partial").Missing);
    [Fact] public void Idle_fps_card_is_Missing_NoApp() => Assert.Equal(NoData.NoApp, Card("fps", "idle").Missing);
    [Fact] public void Gaming_cpu_is_L4() => Assert.Equal(WarnLevel.L4, Card("cpu-ram", "gaming").Level);
    [Fact] public void Hot_cpu_is_L5() => Assert.Equal(WarnLevel.L5, Card("cpu-ram", "hot").Level);
    [Fact] public void Lowfps_fps_is_L4() => Assert.Equal(WarnLevel.L4, Card("fps", "lowfps").Level);
    [Fact] public void Stutter_fps_is_L5() => Assert.Equal(WarnLevel.L5, Card("fps", "stutter").Level);

    [Theory]
    [MemberData(nameof(Types))]
    public void Missing_card_never_reports_a_warn_level(string type)
    {
        foreach (string fx in new[] { "na", "partial", "idle", "gaming", "hot", "lowfps", "stutter" })
        {
            var c = Card(type, fx);
            if (c.Missing != NoData.None) Assert.Equal(WarnLevel.None, c.Level);
        }
    }

    // ---- mood chain ----

    /// <summary>A card's face with no mood behind it (a context that never ticked one).</summary>
    private static (string, AzurIcons.Overlay) MoodOf(AzurCard card) => AzurCast.Mood(card, null, Ctx("cpu-ram", "idle"));

    [Fact]
    public void Mood_crit_is_cross_and_spiral()
        => Assert.Equal(("cross", AzurIcons.Overlay.Spiral), MoodOf(Card("cpu-ram", "hot")));

    [Fact]
    public void Mood_warn_is_hot_and_sweat()
        => Assert.Equal(("hot", AzurIcons.Overlay.Sweat), MoodOf(Card("cpu-ram", "gaming")));

    [Fact]
    public void Mood_normal_uses_the_resting_mood_and_star_only_when_excited()
    {
        Assert.Equal(("happy", AzurIcons.Overlay.None), MoodOf(Card("cpu-ram", "idle", "happy")));
        Assert.Equal(("excited", AzurIcons.Overlay.Star), MoodOf(Card("cpu-ram", "idle", "excited")));
    }

    [Fact]
    public void Slot_is_null_when_neither_mood_nor_base_file_exists()
        => Assert.Null(AzurCast.Slot("no-such-student-xyz", "hot"));

    // ---- generic titles ----

    private const string VendorFixture =
        "{ \"extends\": \"idle\", \"text\": { \"cpu.name\": \"AMD Ryzen 7 7700X 8-Core Processor\", \"gpu.0.name\": \"NVIDIA GeForce RTX 5070 Ti\" } }";

    private static void WithVendorFixture(string type, Action<PanelContext> check, string? customTitle = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"halo-fx-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, VendorFixture);
        try
        {
            var ctx = Ctx(type, "idle", path);
            if (customTitle != null) ctx.Widget.Title = customTitle;
            check(ctx);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("cpu-ram", "CPU", "CENTRAL PROCESSING UNIT")]
    [InlineData("gpu", "GPU", "GRAPHICS PROCESSING UNIT")]
    public void Default_title_and_subtitle_are_fixed_whatever_the_name_metrics_say(string type, string title, string sub)
        => WithVendorFixture(type, ctx =>
        {
            var m = Models.Build(type, ctx)!;
            Assert.Equal(title, m.Title(ctx));
            Assert.Equal(sub, m.Sub(ctx));
        });

    [Theory]
    [InlineData("cpu-ram", "CENTRAL PROCESSING UNIT")]
    [InlineData("gpu", "GRAPHICS PROCESSING UNIT")]
    public void Custom_title_wins_and_subtitle_stays_fixed(string type, string sub)
        => WithVendorFixture(type, ctx =>
        {
            var m = Models.Build(type, ctx)!;
            Assert.Equal("MY CPU", m.Title(ctx));
            Assert.Equal(sub, m.Sub(ctx));
        }, "MY CPU");

    // ---- settings polish ----

    [Fact]
    public void Azur_character2_mirrors_character()
    {
        var opts = Halo.Shared.Skins.AzurArchiveSkinInfo.Info.Options;
        var a = opts.Single(o => o.Key == "character");
        var b = opts.Single(o => o.Key == "character2");
        Assert.Equal(a.Choices, b.Choices);
        Assert.Equal(a.Kind, b.Kind);
    }

    [Fact]
    public void Azur_character2_is_network_only()
    {
        var opts = Halo.Shared.Skins.AzurArchiveSkinInfo.Info.Options;
        Assert.Equal(["network"], opts.Single(o => o.Key == "character2").Types!);
        Assert.Null(opts.Single(o => o.Key == "character").Types);
    }

    private static readonly (string Type, string Who, string Mood)[] RosterTable =
    [
        ("cpu-ram", "yuuka", "working"), ("gpu", "midori", "working"), ("fps", "momoi", "excited"),
        ("latency", "aris", ""), ("power", "asuna", "relaxed"), ("drives", "akane", ""),
        ("network", "chihiro", ""), ("fans", "utaha", ""), ("clock", "toki", "happy"),
        ("topcpu", "noa", "relaxed"), ("topram", "koyuki", "relaxed"), ("companion", "arona", ""),
    ];

    [Fact]
    public void Roster_pins_the_cast_table_and_stays_inside_the_cast()
    {
        var cast = Halo.Shared.Skins.AzurArchiveSkinInfo.Cast;
        foreach (var (type, who, mood) in RosterTable)
            Assert.Equal((who, mood), AzurArchiveSkin.Roster(type));
        foreach (var (type, _, _) in RosterTable) Assert.Contains(AzurArchiveSkin.Roster(type).Who, cast);
        Assert.Equal("kotama", TrafficEl.SecondDefault);
        Assert.Contains(TrafficEl.SecondDefault, cast);
    }

    [Fact]
    public void Every_cast_id_and_roster_mood_resolves_art()
    {
        // art-free checkout (after a takedown): nothing to resolve
        if (AzurCast.Slot("arona", "") == null) return;
        foreach (string id in Halo.Shared.Skins.AzurArchiveSkinInfo.Cast)
            Assert.Equal(id, AzurCast.Slot(id, ""));
        foreach (var (_, who, mood) in RosterTable.Where(r => r.Mood != ""))
            Assert.Equal(who + "-" + mood, AzurCast.Slot(who, mood));
    }

    [Fact]
    public void Every_cast_id_has_its_own_halo_colour()
    {
        var fallback = AzurCast.HaloOf("no-such-id");
        Assert.Equal(fallback, AzurCast.HaloOf("arona"));
        foreach (string id in Halo.Shared.Skins.AzurArchiveSkinInfo.Cast.Where(i => i != "arona"))
            Assert.NotEqual(fallback, AzurCast.HaloOf(id));
    }

    private static double Lum(Vortice.Mathematics.Color4 c)
    {
        static double Lin(float v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double Ratio(Vortice.Mathematics.Color4 a, Vortice.Mathematics.Color4 b)
    {
        double x = Lum(a) + 0.05, y = Lum(b) + 0.05;
        return Math.Max(x, y) / Math.Min(x, y);
    }

    [Fact]
    public void InitialInk_is_the_better_of_white_and_dark_and_readable_on_every_halo_disc()
    {
        var white = new Vortice.Mathematics.Color4(1, 1, 1, 1);
        var dark = new Vortice.Mathematics.Color4(0x1E / 255f, 0x24 / 255f, 0x30 / 255f, 1);
        foreach (string id in Halo.Shared.Skins.AzurArchiveSkinInfo.Cast)
        {
            var disc = AzurCast.HaloOf(id);
            var ink = AzurCast.InitialInk(disc);
            Assert.True(ink == white || ink == dark, id);
            Assert.True(Ratio(ink, disc) >= 3.0, $"{id}: {Ratio(ink, disc):F2}");
            Assert.True(Ratio(ink, disc) >= Math.Max(Ratio(white, disc), Ratio(dark, disc)) - 1e-6, id);
        }
    }
}
