using Halo.Shared.Config;
using Halo.Shared.Panels;
using Halo.Shared.Skins;
using Halo.Widgets;
using Halo.Widgets.Harness;
using Halo.Widgets.Render;
using Halo.Widgets.Skins;
using Halo.Widgets.Skins.Rainformer;

namespace Halo.Tests;

/// <summary>The skin seam: catalog metadata, registry fallback, and that Rainformer attaches its chrome.
/// In the log collection because <see cref="SkinRegistry"/> and panel builds log.</summary>
[Collection(LogTestCollection.Name)]
public sealed class SkinCatalogTests
{
    private static readonly SkinInfo Rf = SkinCatalog.Rainformer;

    [Fact]
    public void Default_preset_is_ThemeTokens_Defaults_byte_for_byte()
    {
        var colors = Rf.DefaultPreset.Colors;
        Assert.Equal("rainformer-light", Rf.DefaultPreset.Id);
        Assert.Equal(ThemeTokens.Defaults.Length, colors.Count);
        foreach (var d in ThemeTokens.Defaults)
            Assert.Equal(d.Color, colors[d.Token]);
    }

    [Fact]
    public void CoreTokens_match_Defaults_in_order()
    {
        Assert.Equal(31,SkinCatalog.CoreTokens.Count);
        Assert.Equal(ThemeTokens.Defaults.Length, SkinCatalog.CoreTokens.Count);
        for (int i = 0; i < ThemeTokens.Defaults.Length; i++)
        {
            var spec = SkinCatalog.CoreTokens[i];
            var d = ThemeTokens.Defaults[i];
            Assert.Equal(d.Token, spec.Token);
            Assert.Equal(d.Description, spec.Description);
            Assert.True(spec.Core);
            Assert.False(string.IsNullOrWhiteSpace(spec.Group));
        }
    }

    [Fact]
    public void Preset_covers_every_token_and_is_contrast_exempt()
    {
        var specs = Rf.Tokens.Select(t => t.Token).ToHashSet(StringComparer.Ordinal);
        foreach (var token in Rf.DefaultPreset.Colors.Keys)
            Assert.Contains(token, specs);
        foreach (var core in SkinCatalog.CoreTokens)
            Assert.True(Rf.DefaultPreset.Colors.ContainsKey(core.Token), core.Token);
        Assert.True(Rf.DefaultPreset.ContrastExempt);
    }

    [Fact]
    public void Rainformer_declares_its_options()
    {
        Assert.Equal("4", Rf.Options.Single(o => o.Key == "cornerRadius").Default);
        // "Text size" never reached the renderer, so it is no longer offered.
        Assert.DoesNotContain(Rf.Options, o => o.Key == "textSizePt");
    }

    [Fact]
    public void Find_returns_known_skin_and_null_otherwise()
    {
        Assert.Same(Rf, SkinCatalog.Find("rainformer"));
        Assert.Null(SkinCatalog.Find("nope"));
        Assert.Null(SkinCatalog.Find(null));
    }

    [Fact]
    public void Registry_falls_back_to_Rainformer()
    {
        Assert.Same(RainformerSkin.Instance, SkinRegistry.For("rainformer"));
        Assert.Same(RainformerSkin.Instance, SkinRegistry.For("does-not-exist"));
        Assert.Same(RainformerSkin.Instance, SkinRegistry.For(null));
    }

    [Fact]
    public void Build_sets_chrome_for_known_types_and_null_for_unknown()
    {
        foreach (var type in RainformerSkin.KnownTypes)
        {
            var ctx = MakeContext(type);
            using var panel = RainformerSkin.Instance.Build(type, ctx);
            Assert.NotNull(panel);
            Assert.NotNull(panel!.Chrome);
        }
        Assert.Null(RainformerSkin.Instance.Build("bogus", MakeContext("clock")));
    }

    private static PanelContext MakeContext(string type)
    {
        var widget = new WidgetInstance { Id = "t-" + type, Type = type };
        var settings = new AppSettings();
        var theme = Theme.Resolve(settings.Appearance, widget, 1.7);
        theme.Dpi = 96;
        return new PanelContext
        {
            Metrics = FixtureMetrics.Load("idle"),
            Theme = theme,
            Settings = settings,
            Widget = widget,
            Type = PanelCatalog.Find(type),
        };
    }

    private static readonly string[] AlertTokens =
        ["red", "redText", "barWarn", "staleBadge", "devWarn1", "devWarn2", "devWarn3", "devWarn4", "devWarn5",
         "critHatch", "hatch", "warnText", "critText"];

    [Fact]
    public void Every_skin_declares_four_calm_swatch_tokens_it_defines()
    {
        foreach (var skin in SkinCatalog.All)
        {
            Assert.NotNull(skin.SwatchTokens);
            Assert.Equal(4, skin.SwatchTokens!.Count);
            var defined = skin.Tokens.Select(t => t.Token).ToHashSet(StringComparer.Ordinal);
            foreach (string t in skin.SwatchTokens)
            {
                Assert.True(defined.Contains(t), skin.Id + ": undefined swatch token " + t);
                Assert.DoesNotContain(t, AlertTokens);
                foreach (var preset in skin.Presets)
                    Assert.True(preset.Colors.ContainsKey(t), skin.Id + "/" + preset.Id + " lacks " + t);
            }
        }
    }

    [Theory]
    [InlineData(30, 30)] [InlineData(15, 15)] [InlineData(24, 24)] [InlineData(60, 60)]
    [InlineData(0, 30)] [InlineData(-1, 30)] [InlineData(25, 30)] [InlineData(1000, 30)]
    public void MotionFps_is_held_to_the_choices(int input, int expected)
        => Assert.Equal(expected, AppearanceSettings.ValidMotionFps(input));

    [Fact]
    public void MotionFps_defaults_to_30_and_round_trips_as_motionFps()
    {
        Assert.Equal(30, new AppearanceSettings().MotionFps);
        string dir = Directory.CreateTempSubdirectory("halo-fps").FullName;
        try
        {
            using (var store = new ConfigStore(dir, watch: false))
            {
                store.Settings.Appearance.MotionFps = 60;
                store.SaveSettings();
            }
            Assert.Contains("\"motionFps\": 60", File.ReadAllText(Path.Combine(dir, ConfigStore.SettingsFile)));
            using var again = new ConfigStore(dir, watch: false);
            Assert.Equal(60, again.Settings.Appearance.MotionFps);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Azur_network_card_is_no_taller_than_216_px()
    {
        var dx = new Dx(Halo.Shared.Paths.FontsDir, warp: true);
        var img = PanelRenderer.Render(dx, new RenderRequest("network", "idle", Skin: Halo.Shared.Skins.AzurArchiveSkinInfo.Id));
        Assert.True(img.Height <= 216, "height " + img.Height);
    }
}
