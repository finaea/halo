using System.IO;
using Halo.Shared.Config;
using Halo.Shared.Skins;
using Xunit;

namespace Halo.Tests;

/// <summary>Ticket 12c: a widget's monitor, position and on/off are remembered per skin.
/// <see cref="WidgetInstance.SwitchSkin"/> parks the live placement in <c>Placements</c>; only
/// Settings reads that, the widgets process never does.</summary>
public sealed class SkinPlacementStashTests : IDisposable
{
    private const string R = SkinCatalog.RainformerId;
    private const string A = AzurArchiveSkinInfo.Id;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Halo.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private static WidgetInstance Widget(string id, string monitor = "M1", int x = 10, int y = 20, bool enabled = true)
        => new() { Id = id, Type = "cpuram", Monitor = monitor, X = x, Y = y, Enabled = enabled };

    [Fact]
    public void RainformerToAzurToRainformer_RestoresBothLayouts_IncludingEnabledAndMonitor()
    {
        var w = Widget("w", "M1", 10, 20, enabled: true);

        w.SwitchSkin(R, A);
        w.Monitor = "M2"; w.X = 300; w.Y = 400; w.Enabled = false;

        w.SwitchSkin(A, R);
        Assert.Equal(("M1", 10, 20, true), (w.Monitor, w.X, w.Y, w.Enabled));

        w.SwitchSkin(R, A);
        Assert.Equal(("M2", 300, 400, false), (w.Monitor, w.X, w.Y, w.Enabled));
    }

    [Fact]
    public void FirstVisit_KeepsLivePositionAndEnabled_AndParksTheOldSkin()
    {
        var w = Widget("w", "M1", 10, 20, enabled: false);

        w.SwitchSkin(R, A);

        Assert.Equal(("M1", 10, 20, false), (w.Monitor, w.X, w.Y, w.Enabled));
        Assert.NotNull(w.Placements);
        var parked = Assert.Single(w.Placements!);
        Assert.Equal(R, parked.Key);
        Assert.Equal(("M1", 10, 20, false), (parked.Value.Monitor, parked.Value.X, parked.Value.Y, parked.Value.Enabled));
    }

    [Fact]
    public void SwitchGlobalSkin_MovesInheritAndUnknownOwnSkin_ButNotKnownOwnSkin()
    {
        var inherit = Widget("inherit", x: 1);
        var unknown = Widget("unknown", x: 2);
        unknown.Appearance.Skin = "no-such-skin";
        var own = Widget("own", x: 3);
        own.Appearance.Skin = R;
        var cfg = new WidgetsConfig { Widgets = { inherit, unknown, own } };

        cfg.SwitchGlobalSkin(R, A);
        inherit.X = 100; unknown.X = 200; own.X = 300;   // moved under Azur
        cfg.SwitchGlobalSkin(A, R);

        Assert.Equal(1, inherit.X);                       // restored
        Assert.Equal(2, unknown.X);                       // restored
        Assert.Equal(300, own.X);                         // never switched
        Assert.Null(own.Placements);
        Assert.Equal(100, inherit.Placements![A].X);
        Assert.Equal(200, unknown.Placements![A].X);
    }

    [Fact]
    public void PerWidgetSwitch_LeavesOtherWidgetsUntouched()
    {
        var a = Widget("a", x: 1);
        var b = Widget("b", x: 2);

        a.SwitchSkin(R, A);
        a.X = 111;
        a.SwitchSkin(A, R);

        Assert.Equal(1, a.X);
        Assert.Equal(("M1", 2, 20, true), (b.Monitor, b.X, b.Y, b.Enabled));
        Assert.Null(b.Placements);
    }

    [Fact]
    public void SameSkinFromAndTo_ChangesNothing()
    {
        var w = Widget("w");

        w.SwitchSkin(R, R);

        Assert.Null(w.Placements);
        Assert.Equal(("M1", 10, 20, true), (w.Monitor, w.X, w.Y, w.Enabled));
    }

    [Fact]
    public void EnabledToggledUnderOneSkin_OnlyAffectsThatSkin()
    {
        var w = Widget("w", enabled: true);

        w.SwitchSkin(R, A);
        w.Enabled = false;                 // turned off under Azur only
        w.SwitchSkin(A, R);
        Assert.True(w.Enabled);

        w.SwitchSkin(R, A);
        Assert.False(w.Enabled);
    }

    [Fact]
    public void SwitchingBackToBackAndForth_LeavesNoDanglingEntryForTheLiveSkin()
    {
        var w = Widget("w");

        w.SwitchSkin(R, A);
        w.SwitchSkin(A, R);

        // Azur is parked, Rainformer is live: exactly one entry, and it is not the live skin.
        Assert.Equal(new[] { A }, w.Placements!.Keys);
    }

    [Fact]
    public void EffectiveSkin_OwnThenGlobalThenRainformer_UnknownCountsAsUnset()
    {
        var own = new WidgetAppearance { Skin = A };
        Assert.Equal(A, own.EffectiveSkin(R));

        var inherit = new WidgetAppearance();
        Assert.Equal(A, inherit.EffectiveSkin(A));
        Assert.Equal(R, inherit.EffectiveSkin(null));

        var unknownOwn = new WidgetAppearance { Skin = "nope" };
        Assert.Equal(A, unknownOwn.EffectiveSkin(A));
        Assert.Equal(R, unknownOwn.EffectiveSkin("also-nope"));
        Assert.Equal(R, unknownOwn.EffectiveSkin(null));
    }

    [Fact]
    public void RoundTrip_Placements_Survive_AndAbsentOnesWriteNoKey()
    {
        var w = Widget("w", "M1", 10, 20, enabled: true);
        w.SwitchSkin(R, A);
        w.Monitor = "M2"; w.X = 300; w.Y = 400; w.Enabled = false;
        var plain = Widget("plain");

        using (var store = new ConfigStore(_dir, watch: false))
        {
            store.Widgets.Widgets.Add(w);
            store.SaveWidgets();
        }
        Assert.Contains("\"placements\"", File.ReadAllText(Path.Combine(_dir, ConfigStore.WidgetsFile)));

        using (var fresh = new ConfigStore(_dir, watch: false))
        {
            var got = fresh.Widgets.Widgets.Single(x => x.Id == "w");
            Assert.Equal(("M2", 300, 400, false), (got.Monitor, got.X, got.Y, got.Enabled));
            var p = got.Placements![R];
            Assert.Equal(("M1", 10, 20, true), (p.Monitor, p.X, p.Y, p.Enabled));
        }

        string dir2 = Path.Combine(_dir, "plain");
        using (var store = new ConfigStore(dir2, watch: false))
        {
            store.Widgets.Widgets.Add(plain);
            store.SaveWidgets();
        }
        Assert.DoesNotContain("placements", File.ReadAllText(Path.Combine(dir2, ConfigStore.WidgetsFile)));
    }

    [Fact]
    public void SkinPlacement_Clone_IsIndependent()
    {
        var p = new SkinPlacement { Monitor = "M", X = 1, Y = 2, Enabled = false };
        var c = p.Clone();
        c.X = 9;
        Assert.Equal(1, p.X);
        Assert.Equal(("M", 2, false), (c.Monitor, c.Y, c.Enabled));
    }
}
