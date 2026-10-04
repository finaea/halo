using Halo.Shared.Config;
using Halo.Shared.Panels;
using Halo.Shared.Skins;
using Halo.Widgets;
using Vortice.Mathematics;
using Xunit;

namespace Halo.Tests;

/// <summary>
/// <see cref="Theme.Resolve"/> (skin / preset / tweak / option / font precedence) and
/// <see cref="Theme.RebuildReason"/>. Only one skin exists, so a "different skin" is simulated by
/// setting <see cref="Theme.SkinId"/> on a hand-built Theme.
/// </summary>
public sealed class ThemeResolveTests
{
    private const string Rf = "rainformer";
    private const string Light = "rainformer-light";

    private static Color4 Hex(string hex)
    {
        Assert.True(ThemeTokens.TryParse(hex, out byte r, out byte g, out byte b, out byte a));
        return new Color4(r / 255f, g / 255f, b / 255f, a / 255f);
    }

    private static AppearanceSettings Global(Action<SkinSettings>? configure = null)
    {
        var a = new AppearanceSettings();
        if (configure != null) configure(a.SkinFor(Rf));
        return a;
    }

    private static WidgetInstance Widget(Action<WidgetAppearance> configure)
    {
        var w = new WidgetInstance { Id = "w", Type = "cpuram" };
        configure(w.Appearance);
        return w;
    }

    private static Theme Resolve(AppearanceSettings g, WidgetInstance? w = null) => Theme.Resolve(g, w, 1.7);

    [Fact]
    public void Defaults_ResolveToTheBuiltInPalette()
    {
        var t = Resolve(new AppearanceSettings());

        Assert.Equal(Rf, t.SkinId);
        Assert.Equal("Trebuchet MS", t.FontFamily);
        Assert.Equal(4, t.CornerRadius);
        foreach (var (token, color, _) in ThemeTokens.Defaults)
            Assert.Equal(Hex(color), t.Color(token));
        Assert.Equal(ThemeTokens.Defaults.Length, t.Colors.Count);
    }

    [Fact]
    public void GlobalTweak_Applies()
    {
        var t = Resolve(Global(s => s.Colors["bgBody"] = "#112233FF"));

        Assert.Equal(Hex("#112233FF"), t.Color("bgBody"));
        Assert.Equal(Hex("#A3B2E6B4"), t.Color("bgTop"));
    }

    [Fact]
    public void WidgetTweak_BeatsGlobalTweak()
    {
        var g = Global(s => { s.Colors["bgBody"] = "#112233FF"; s.Colors["bgTop"] = "#010101FF"; });
        var w = Widget(a => a.SkinFor(Rf).Colors["bgBody"] = "#445566FF");

        var t = Resolve(g, w);

        Assert.Equal(Hex("#445566FF"), t.Color("bgBody"));
        Assert.Equal(Hex("#010101FF"), t.Color("bgTop"));   // still inherits the other global tweak
    }

    [Fact]
    public void MetricColour_ComesFromTheWidgetMetricSetting()
    {
        var w = Widget(_ => { });
        w.Metrics["usage"] = new MetricSetting { Color = "#ABCDEF80" };

        var t = Resolve(new AppearanceSettings(), w);

        Assert.True(t.HasColor("metric:usage"));
        Assert.Equal(Hex("#ABCDEF80"), t.Color("metric:usage"));
    }

    [Fact]
    public void WidgetPickingAPreset_StopsInheritingGlobalTweaks()
    {
        var g = Global(s => s.Colors["bgBody"] = "#112233FF");
        var w = Widget(a =>
        {
            var s = a.SkinFor(Rf);
            s.Preset = Light;
            s.Colors["bgTop"] = "#445566FF";
        });

        var t = Resolve(g, w);

        Assert.Equal(Hex("#E6E6E6B4"), t.Color("bgBody"));   // global tweak not applied
        Assert.Equal(Hex("#445566FF"), t.Color("bgTop"));    // widget's own tweak is
    }

    [Fact]
    public void WidgetWithAnUnknownPreset_CountsAsNotPicked()
    {
        var g = Global(s => s.Colors["bgBody"] = "#112233FF");
        var w = Widget(a => a.SkinFor(Rf).Preset = "no-such-preset");

        Assert.Equal(Hex("#112233FF"), Resolve(g, w).Color("bgBody"));
    }

    // ---- SkinPalette: what the Settings app shows as a widget's inherited swatch. It is the
    // value "Use global" off persists, so it has to match what Resolve draws. ----

    [Fact]
    public void InheritedSwatch_WidgetWithOwnPreset_IgnoresGlobalTweaks()
    {
        // The review repro: global bgBody tweak, widget picked rainformer-light, no widget colours.
        var g = Global(s => s.Colors["bgBody"] = "#112233FF");
        var w = Widget(a => a.SkinFor(Rf).Preset = Light);

        var inherited = SkinPalette.InheritedByWidget(g, w.Appearance, SkinCatalog.Rainformer);

        Assert.Equal("#E6E6E6B4", inherited["bgBody"]);
        Assert.Equal(Resolve(g, w).Color("bgBody"), Hex(inherited["bgBody"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("no-such-preset")]
    public void InheritedSwatch_WidgetWithoutAKnownPreset_FollowsGlobalTweaks(string? preset)
    {
        var g = Global(s => s.Colors["bgBody"] = "#112233FF");
        var w = Widget(a => a.SkinFor(Rf).Preset = preset);

        var inherited = SkinPalette.InheritedByWidget(g, w.Appearance, SkinCatalog.Rainformer);

        Assert.Equal("#112233FF", inherited["bgBody"]);
        Assert.Equal(Resolve(g, w).Color("bgBody"), Hex(inherited["bgBody"]));
    }

    // ---- preset switching: the Rainformer presets and the stroke token / strokeWidth option ----

    private static void AssertPalette(string presetId, Theme t)
    {
        var preset = SkinCatalog.FindPreset(SkinCatalog.Rainformer, presetId)!;
        foreach (var (token, hex) in preset.Colors)
            Assert.Equal(Hex(hex), t.Color(token));
    }

    [Fact]
    public void GlobalPreset_SakuraDusk_GivesItsColoursAndStroke()
    {
        var t = Resolve(Global(s => s.Preset = "sakura-dusk"));

        AssertPalette("sakura-dusk", t);
        Assert.Equal(1, t.StrokeWidth);
    }

    [Fact]
    public void DefaultPreset_HasNoStroke_AndTheLightStrokeColour()
    {
        var t = Resolve(new AppearanceSettings());

        Assert.Equal(0, t.StrokeWidth);
        Assert.Equal(Hex("#608ACB64"), t.Color("stroke"));
    }

    [Fact]
    public void UserStrokeWidth_BeatsThePresetsStrokeWidth()
    {
        var g = Global(s => { s.Preset = "rainformer-dark"; s.Options["strokeWidth"] = "0"; });

        Assert.Equal(0, Resolve(g).StrokeWidth);
        Assert.Equal(1, Resolve(Global(s => s.Preset = "rainformer-dark")).StrokeWidth);
    }

    [Fact]
    public void WidgetWithOwnPreset_GetsItsColours_NotTheGlobalTweaks_ButItsOwn()
    {
        var g = Global(s => { s.Preset = "sakura-dusk"; s.Colors["bar"] = "#123456FF"; });
        var w = Widget(a =>
        {
            var s = a.SkinFor(Rf);
            s.Preset = "coastal-dusk";
            s.Colors["text"] = "#ABCDEFFF";
        });

        var t = Resolve(g, w);

        Assert.Equal(Hex("#7FB8E0FF"), t.Color("bar"));          // coastal-dusk's, not the global #123456FF
        Assert.Equal(Hex("#ABCDEFFF"), t.Color("text"));         // the widget's own tweak
        Assert.Equal(Hex("#E0FBFCFF"), t.Color("title"));        // coastal-dusk, not sakura-dusk
        Assert.Equal(Hex("#F4AF9CFF"), t.Color("activeTitle"));

        var inherited = SkinPalette.InheritedByWidget(g, w.Appearance, SkinCatalog.Rainformer);
        Assert.Equal("#7FB8E0FF", inherited["bar"]);
        Assert.Equal(Hex(inherited["title"]), t.Color("title"));
    }

    [Fact]
    public void WidgetWithNoPreset_FollowsGlobalPresetAndTweaks()
    {
        var g = Global(s => { s.Preset = "sakura-dusk"; s.Colors["bar"] = "#123456FF"; });
        var w = Widget(_ => { });

        var t = Resolve(g, w);

        Assert.Equal(Hex("#123456FF"), t.Color("bar"));
        Assert.Equal(Hex("#FFE5ECFF"), t.Color("title"));
        Assert.Equal(1, t.StrokeWidth);

        var inherited = SkinPalette.InheritedByWidget(g, w.Appearance, SkinCatalog.Rainformer);
        foreach (var (token, hex) in inherited)
            Assert.Equal(Hex(hex), t.Color(token));
    }

    [Fact]
    public void OptionBaseline_PresetOptionsOverSkinDefaults()
    {
        var dark = SkinCatalog.FindPreset(SkinCatalog.Rainformer, "rainformer-dark")!;

        var o = SkinPalette.OptionBaseline(SkinCatalog.Rainformer, dark);

        Assert.Equal("1", o["strokeWidth"]);
        Assert.Equal("4", o["cornerRadius"]);
    }

    [Fact]
    public void OptionBaseline_UnderAWidgetsOwnPreset_AgreesWithResolve()
    {
        var g = Global(s => s.Options["cornerRadius"] = "8");
        var w = Widget(a => a.SkinFor(Rf).Preset = "coastal-dusk");

        var preset = SkinPalette.WidgetPreset(g, w.Appearance, SkinCatalog.Rainformer);
        var baseline = SkinPalette.OptionBaseline(SkinCatalog.Rainformer, preset, g);

        Assert.Equal("coastal-dusk", preset.Id);
        Assert.Equal("8", baseline["cornerRadius"]);   // global options still apply under a widget preset
        Assert.Equal("1", baseline["strokeWidth"]);
        Assert.Equal(Resolve(g, w).SkinOptions, baseline);
    }

    [Fact]
    public void WidgetPreset_UnknownWidgetPreset_FallsBackToGlobal()
    {
        var g = Global(s => s.Preset = "sakura-dusk");
        var w = Widget(a => a.SkinFor(Rf).Preset = "no-such-preset");

        var preset = SkinPalette.WidgetPreset(g, w.Appearance, SkinCatalog.Rainformer);

        Assert.Equal("sakura-dusk", preset.Id);
        Assert.Equal(Hex(preset.Colors["bar"]), Resolve(g, w).Color("bar"));
    }

    [Fact]
    public void UnknownGlobalPreset_FallsBackToTheSkinDefault()
    {
        var t = Resolve(Global(s => s.Preset = "no-such-preset"));

        AssertPalette(SkinCatalog.Rainformer.DefaultPreset.Id, t);
        Assert.Equal(0, t.StrokeWidth);
    }


    [Fact]
    public void RemovedPresetId_InGlobalSettings_FallsBackToTheSkinDefault()
    {
        // "nord" shipped once and was removed; a stored id must not break or recolour anything.
        var t = Resolve(Global(s => s.Preset = "nord"));

        AssertPalette(SkinCatalog.Rainformer.DefaultPreset.Id, t);
        Assert.Equal(Light, SkinCatalog.Rainformer.DefaultPreset.Id);
        Assert.Equal(0, t.StrokeWidth);
    }

    [Fact]
    public void RemovedPresetId_OnAWidget_InheritsTheGlobalLook()
    {
        // Decided: a widget naming a removed preset inherits the global look (preset + tweaks) rather
        // than jumping to the skin default, so it still matches the rest of the layout.
        var w = Widget(a => a.SkinFor(Rf).Preset = "nord");

        // Global is on a real preset: the widget follows it, not the removed one.
        var g = Global(s => s.Preset = "sakura-dusk");
        AssertPalette("sakura-dusk", Resolve(g, w));
        Assert.Equal("sakura-dusk", SkinPalette.WidgetPreset(g, w.Appearance, SkinCatalog.Rainformer).Id);

        // Global also on a removed id: both land on the skin default.
        var g2 = Global(s => s.Preset = "nord");
        AssertPalette(Light, Resolve(g2, w));
        Assert.Equal(Light, SkinPalette.WidgetPreset(g2, w.Appearance, SkinCatalog.Rainformer).Id);

        // Global tweaks follow too, and the Settings swatches agree with the renderer.
        var g3 = Global(s => { s.Preset = "sakura-dusk"; s.Colors["bgBody"] = "#112233FF"; });
        Assert.Equal(Hex("#112233FF"), Resolve(g3, w).Color("bgBody"));
        var inherited = SkinPalette.InheritedByWidget(g3, w.Appearance, SkinCatalog.Rainformer);
        Assert.Equal("#112233FF", inherited["bgBody"]);
        Assert.Equal(Resolve(g3, w).Color("bgBody"), Hex(inherited["bgBody"]));
    }

    [Fact]
    public void UnknownSkin_FallsBackToRainformer_AndUnknownTokensAreIgnored()
    {
        var g = Global(s => s.Colors["notAToken"] = "#112233FF");
        g.Skin = "future-skin";
        var w = Widget(a => { a.Skin = "another-future-skin"; a.SkinFor(Rf).Colors["alsoNotAToken"] = "#112233FF"; });

        var t = Resolve(g, w);

        Assert.Equal(Rf, t.SkinId);
        Assert.False(t.HasColor("notAToken"));
        Assert.False(t.HasColor("alsoNotAToken"));
        Assert.Equal(Resolve(new AppearanceSettings()).Colors.Count, t.Colors.Count);

        g.Skin = "future-skin";
        Assert.Equal(Rf, Resolve(g).SkinId);
    }

    [Fact]
    public void Options_WidgetBeatsGlobal_AndUnknownKeysAreIgnored()
    {
        var g = Global(s => { s.Options["cornerRadius"] = "9"; s.Options["bogus"] = "1"; });
        Assert.Equal(9, Resolve(g).CornerRadius);

        var w = Widget(a => a.SkinFor(Rf).Options["cornerRadius"] = "2");
        var t = Resolve(g, w);

        Assert.Equal(2, t.CornerRadius);
        Assert.False(t.SkinOptions.ContainsKey("bogus"));
    }

    [Fact]
    public void Options_RetiredTextSizePt_IsIgnoredButOthersStillApply()
    {
        var g = Global(s => { s.Options["textSizePt"] = "10"; s.Options["cornerRadius"] = "9"; });

        var t = Resolve(g);

        Assert.False(t.SkinOptions.ContainsKey("textSizePt"));
        Assert.Equal(9, t.CornerRadius);
    }

    [Fact]
    public void Font_WidgetThenGlobalThenSkin()
    {
        Assert.Equal("Trebuchet MS", Resolve(new AppearanceSettings()).FontFamily);

        var g = new AppearanceSettings { FontFamily = "Segoe UI" };
        Assert.Equal("Segoe UI", Resolve(g).FontFamily);

        var w = Widget(a => a.FontFamily = "Consolas");
        Assert.Equal("Consolas", Resolve(g, w).FontFamily);
    }

    // ---- RebuildReason ----

    [Fact]
    public void RebuildReason_SameConfig_IsNull()
    {
        var g = Global(s => s.Colors["bgBody"] = "#112233FF");
        Assert.Null(Theme.RebuildReason(Resolve(g), Resolve(g)));
    }

    [Fact]
    public void RebuildReason_GlobalFontChange_Rebuilds()
    {
        var live = Resolve(new AppearanceSettings());
        var next = Resolve(new AppearanceSettings { FontFamily = "Segoe UI" });

        Assert.NotNull(Theme.RebuildReason(live, next));
    }

    [Fact]
    public void RebuildReason_WidgetFontOverride_Rebuilds()
    {
        var g = new AppearanceSettings();
        var live = Resolve(g);
        var next = Resolve(g, Widget(a => a.FontFamily = "Consolas"));

        Assert.NotNull(Theme.RebuildReason(live, next));
    }

    [Fact]
    public void RebuildReason_ColourOnlyChange_IsNull()
    {
        var live = Resolve(new AppearanceSettings());
        var next = Resolve(Global(s => s.Colors["bgBody"] = "#112233FF"));

        Assert.Null(Theme.RebuildReason(live, next));
    }

    [Fact]
    public void RebuildReason_CornerRadiusChange_IsNotStructural()
    {
        var live = Resolve(new AppearanceSettings());
        var next = Resolve(Global(s => s.Options["cornerRadius"] = "9"));

        Assert.Null(Theme.RebuildReason(live, next));
    }

    [Fact]
    public void RebuildReason_DifferentSkin_Rebuilds()
    {
        // Only one skin exists, so set SkinId by hand: stands in for a global skin switch or a
        // per-widget skin override, both of which change the resolved SkinId.
        var live = Resolve(new AppearanceSettings());
        var next = Resolve(new AppearanceSettings());
        next.SkinId = "other-skin";

        Assert.NotNull(Theme.RebuildReason(live, next));
    }

    [Fact]
    public void CopyFrom_CopiesSkinOptions()
    {
        var src = Resolve(Global(s => s.Options["cornerRadius"] = "9"));
        var dst = Resolve(new AppearanceSettings());

        dst.CopyFrom(src);

        Assert.Equal("9", dst.SkinOptions["cornerRadius"]);
        Assert.Equal(src.SkinOptions, dst.SkinOptions);
        Assert.Equal(9, dst.CornerRadius);
    }
}
