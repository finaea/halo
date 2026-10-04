using System.IO;
using Halo.Shared.Config;
using Halo.Shared.Skins;
using Xunit;

namespace Halo.Tests;

/// <summary>Ticket 12b: colour tweaks are remembered per preset. <see cref="SkinSettings.SwitchPreset"/>
/// parks the active preset's tweaks in <c>PresetColors</c>; only Settings reads that, the renderer
/// never does.</summary>
public sealed class PresetTweakStashTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Halo.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private static Dictionary<string, string> Tweak(string token, string hex)
        => new(StringComparer.Ordinal) { [token] = hex };

    [Fact]
    public void AToBToA_KeepsEachPresetsTweaks()
    {
        var s = new SkinSettings { Preset = "a", Colors = Tweak("bgBody", "#111111FF") };

        s.SwitchPreset("b");
        Assert.Equal("b", s.Preset);
        Assert.Empty(s.Colors);
        s.Colors["bgBody"] = "#222222FF";

        s.SwitchPreset("a");
        Assert.Equal("#111111FF", s.Colors["bgBody"]);
        s.SwitchPreset("b");
        Assert.Equal("#222222FF", s.Colors["bgBody"]);
    }

    [Fact]
    public void ResetToPreset_ClearsOnlyTheActiveOne()
    {
        var s = new SkinSettings { Preset = "a", Colors = Tweak("bgBody", "#111111FF") };
        s.SwitchPreset("b");
        s.Colors["bgBody"] = "#222222FF";

        s.Colors.Clear(); // what the view model does

        Assert.Equal("#111111FF", s.PresetColors!["a"]["bgBody"]);
        s.SwitchPreset("a");
        Assert.Equal("#111111FF", s.Colors["bgBody"]);
        Assert.Null(s.PresetColors); // b was empty, so nothing is parked now
    }

    [Fact]
    public void Widget_InheritAndOwnPreset_ParkSeparately()
    {
        var s = new SkinSettings { Preset = null, Colors = Tweak("bgBody", "#111111FF") };

        s.SwitchPreset("x");
        Assert.Empty(s.Colors);
        Assert.True(s.PresetColors!.ContainsKey(SkinSettings.InheritSlot));
        s.Colors["bgBody"] = "#333333FF";

        s.SwitchPreset(null);
        Assert.Null(s.Preset);
        Assert.Equal("#111111FF", s.Colors["bgBody"]);
        Assert.Equal("#333333FF", s.PresetColors!["x"]["bgBody"]);

        s.SwitchPreset("x");
        Assert.Equal("#333333FF", s.Colors["bgBody"]);
        Assert.Equal("#111111FF", s.PresetColors![SkinSettings.InheritSlot]["bgBody"]);
    }

    [Fact]
    public void Global_NullPresetAndDefaultSlot_AreTheSameSlot()
    {
        var s = new SkinSettings { Preset = null, Colors = Tweak("bgBody", "#111111FF") };

        s.SwitchPreset("def", defaultSlot: "def");

        Assert.Equal("def", s.Preset);
        Assert.Equal("#111111FF", s.Colors["bgBody"]);
        Assert.Null(s.PresetColors);
    }

    [Fact]
    public void Global_LeavingNullPreset_ParksUnderDefaultSlot()
    {
        var s = new SkinSettings { Preset = null, Colors = Tweak("bgBody", "#111111FF") };

        s.SwitchPreset("other", defaultSlot: "def");

        Assert.Equal("#111111FF", s.PresetColors!["def"]["bgBody"]);
        Assert.False(s.PresetColors.ContainsKey(SkinSettings.InheritSlot));
    }

    [Fact]
    public void RepickingActivePreset_KeepsColors()
    {
        var s = new SkinSettings { Preset = "a", Colors = Tweak("bgBody", "#111111FF") };

        s.SwitchPreset("a");

        Assert.Equal("#111111FF", s.Colors["bgBody"]);
        Assert.Null(s.PresetColors);
    }

    [Fact]
    public void Derived_BeatsStash_AndDropsThatSlot_OldTweaksStayParked()
    {
        var s = new SkinSettings { Preset = "a", Colors = Tweak("bgBody", "#111111FF") };
        s.SwitchPreset("hc");
        s.Colors["bgBody"] = "#444444FF";
        s.SwitchPreset("a");                       // hc's tweaks are parked now
        Assert.True(s.PresetColors!.ContainsKey("hc"));

        s.SwitchPreset("hc", derived: Tweak("bgBody", "#000000FF"));

        Assert.Equal("#000000FF", s.Colors["bgBody"]);
        Assert.Equal("#111111FF", s.PresetColors!["a"]["bgBody"]);
        Assert.False(s.PresetColors.ContainsKey("hc"));
    }

    [Fact]
    public void EmptyColorsWhenLeaving_RemovesOldSlot_AndNullsPresetColors()
    {
        var s = new SkinSettings { Preset = "a", Colors = Tweak("bgBody", "#111111FF") };
        s.SwitchPreset("b");
        s.SwitchPreset("a");
        s.Colors.Clear();
        s.SwitchPreset("b");   // leaving "a" with nothing

        Assert.Null(s.PresetColors);
    }

    [Fact]
    public void IsEmpty_IsFalseWhileOnlyPresetColorsHasContent()
    {
        var s = new SkinSettings { Preset = null };
        s.PresetColors = new() { ["a"] = Tweak("bgBody", "#111111FF") };

        Assert.False(s.IsEmpty);
        s.PresetColors = null;
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var s = new SkinSettings { Preset = "b" };
        s.PresetColors = new() { ["a"] = Tweak("bgBody", "#111111FF") };

        var c = s.Clone();
        c.PresetColors!["a"]["bgBody"] = "#999999FF";
        c.PresetColors["z"] = Tweak("x", "#000000FF");

        Assert.Equal("#111111FF", s.PresetColors["a"]["bgBody"]);
        Assert.False(s.PresetColors.ContainsKey("z"));
    }

    [Fact]
    public void RoundTrip_GlobalAndWidgetPresetColors_Survive_UnderCamelCaseKey()
    {
        var skin = SkinCatalog.Rainformer;
        using (var store = new ConfigStore(_dir, watch: false))
        {
            store.Settings.Appearance.Skins[skin.Id] = new SkinSettings
            {
                Preset = skin.Presets[0].Id,
                PresetColors = new() { [skin.Presets[1].Id] = Tweak("bgBody", "#123456FF") },
            };
            store.Widgets.Widgets.Add(new WidgetInstance
            {
                Id = "w", Type = "cpuram",
                Appearance = new WidgetAppearance
                {
                    Skins = new()
                    {
                        [skin.Id] = new SkinSettings
                        {
                            Preset = skin.Presets[1].Id,
                            PresetColors = new() { [SkinSettings.InheritSlot] = Tweak("bgBody", "#654321FF") },
                        },
                    },
                },
            });
            store.SaveSettings();
            store.SaveWidgets();
        }

        Assert.Contains("\"presetColors\"", File.ReadAllText(Path.Combine(_dir, ConfigStore.SettingsFile)));
        Assert.Contains("\"presetColors\"", File.ReadAllText(Path.Combine(_dir, ConfigStore.WidgetsFile)));

        using var fresh = new ConfigStore(_dir, watch: false);
        Assert.Equal("#123456FF",
            fresh.Settings.Appearance.Skins[skin.Id].PresetColors![skin.Presets[1].Id]["bgBody"]);
        var w = fresh.Widgets.Widgets.Single(x => x.Id == "w");
        Assert.Equal("#654321FF",
            w.Appearance.Skins![skin.Id].PresetColors![SkinSettings.InheritSlot]["bgBody"]);
    }

    [Fact]
    public void Palette_IgnoresPresetColors()
    {
        var skin = SkinCatalog.Rainformer;
        var x = skin.Presets[1];
        var global = new AppearanceSettings
        {
            Skins = new()
            {
                [skin.Id] = new SkinSettings
                {
                    Preset = skin.Presets[0].Id,
                    PresetColors = new() { [x.Id] = Tweak("bgBody", "#ABCDEFFF") },
                },
            },
        };
        var widget = new WidgetAppearance
        {
            Skins = new() { [skin.Id] = new SkinSettings { Preset = x.Id } },
        };

        var inherited = SkinPalette.InheritedByWidget(global, widget, skin);
        Assert.Equal(x.Colors["bgBody"], inherited["bgBody"]);

        var g = SkinPalette.Global(global, skin);
        Assert.Equal(skin.Presets[0].Colors["bgBody"], g["bgBody"]);
    }
}
