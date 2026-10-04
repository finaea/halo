using Halo.Shared;
using Halo.Shared.Skins;
using Halo.Widgets;
using Halo.Widgets.Harness;
using Halo.Widgets.Skins;
using Halo.Widgets.Skins.AzurArchive;

namespace Halo.Tests;

/// <summary>Azur Archive renders through WARP without throwing: every type, both extreme fixtures,
/// every preset, and with game art switched off. Smoke tests, not goldens.</summary>
[Collection(LogTestCollection.Name)]
public sealed class AzurArchiveRenderTests
{
    private static readonly Lazy<Dx> SharedDx = new(() => new Dx(Paths.FontsDir, warp: true));

    public static IEnumerable<object[]> Cases() =>
        from type in PanelFactory.KnownTypes
        from fixture in new[] { "idle", "na" }
        from preset in AzurArchiveSkinInfo.Info.Presets.Select(p => p.Id)
        select new object[] { type, fixture, preset };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Renders_without_throwing(string type, string fixture, string preset)
    {
        var img = PanelRenderer.Render(SharedDx.Value, new RenderRequest(type, fixture, Skin: AzurArchiveSkinInfo.Id, Preset: preset));
        Assert.True(img.Width > 0 && img.Height > 0);
    }

    [Theory]
    [InlineData("cpu-ram", "gaming")]
    [InlineData("fps", "idle")]
    [InlineData("fans", "na")]
    public void Renders_with_game_art_off(string type, string fixture)
    {
        string dir = Directory.CreateTempSubdirectory("halo-azur").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                "{ \"schemaVersion\": 3, \"appearance\": { \"skin\": \"azur-archive\", \"skins\": { \"azur-archive\": { \"options\": { \"gameArt\": \"false\" } } } } }");
            File.WriteAllText(Path.Combine(dir, "widgets.json"),
                "{ \"schemaVersion\": 3, \"widgets\": [ { \"id\": \"w1\", \"type\": \"" + type + "\" } ] }");
            var img = PanelRenderer.Render(SharedDx.Value, new RenderRequest(null, fixture, ConfigDir: dir, WidgetId: "w1"));
            Assert.True(img.Width > 0 && img.Height > 0);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ArtFree_beats_a_widgets_own_gameArt_under_a_rainformer_global()
    {
        if (AzurCast.Slot("yuuka", "") == null) return;   // art-free checkout: nothing to suppress
        string Render(string gameArt, bool artFree)
        {
            string dir = Directory.CreateTempSubdirectory("halo-azur").FullName;
            try
            {
                File.WriteAllText(Path.Combine(dir, "settings.json"),
                    "{ \"schemaVersion\": 3, \"appearance\": { \"skin\": \"rainformer\" } }");
                File.WriteAllText(Path.Combine(dir, "widgets.json"),
                    "{ \"schemaVersion\": 3, \"widgets\": [ { \"id\": \"w1\", \"type\": \"cpu-ram\", \"appearance\": { \"skin\": \"azur-archive\", " +
                    "\"skins\": { \"azur-archive\": { \"options\": { \"gameArt\": \"" + gameArt + "\" } } } } } ] }");
                var img = PanelRenderer.Render(SharedDx.Value, new RenderRequest(null, "idle", ConfigDir: dir, WidgetId: "w1", ArtFree: artFree));
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(img.Pixels));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        string withArt = Render("true", artFree: false);
        string off = Render("false", artFree: false);
        Assert.NotEqual(off, withArt);                       // control: the art does show here
        Assert.Equal(off, Render("true", artFree: true));
    }

    private static string RenderHalo(string type, string globalHalo, string? widgetHalo)
    {
        string dir = Directory.CreateTempSubdirectory("halo-azur").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                "{ \"schemaVersion\": 3, \"appearance\": { \"skin\": \"azur-archive\", \"skins\": { \"azur-archive\": { \"options\": { \"halo\": \"" + globalHalo + "\" } } } } }");
            string wa = widgetHalo == null ? "" : ", \"appearance\": { \"skins\": { \"azur-archive\": { \"options\": { \"halo\": \"" + widgetHalo + "\" } } } }";
            File.WriteAllText(Path.Combine(dir, "widgets.json"),
                "{ \"schemaVersion\": 3, \"widgets\": [ { \"id\": \"w1\", \"type\": \"" + type + "\"" + wa + " } ] }");
            var img = PanelRenderer.Render(SharedDx.Value, new RenderRequest(null, "idle", ConfigDir: dir, WidgetId: "w1"));
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(img.Pixels));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Halo_option_changes_the_face_render_globally_and_per_widget()
    {
        if (AzurCast.Slot("yuuka", "") == null) return;   // art-free checkout: no face, no halo to compare
        string on = RenderHalo("cpu-ram", "true", null);
        Assert.NotEqual(on, RenderHalo("cpu-ram", "false", null));          // global off draws no ring
        Assert.Equal(RenderHalo("cpu-ram", "false", null), RenderHalo("cpu-ram", "true", "false"));   // per-widget off, same picture
        Assert.Equal(on, RenderHalo("cpu-ram", "false", "true"));           // per-widget on beats global off
    }

    [Fact]
    public void Halo_option_leaves_the_clock_render_alone()
    {
        string on = RenderHalo("clock", "true", null);
        Assert.Equal(on, RenderHalo("clock", "false", null));               // the clock never had a ring
    }

    // ---- the clock card's peek stays inside the card ----

    private static PanelImage RenderClock(string character, string preset, double scale, bool art)
    {
        string dir = Directory.CreateTempSubdirectory("halo-azur").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                "{ \"schemaVersion\": 3, \"appearance\": { \"skin\": \"azur-archive\", \"skins\": { \"azur-archive\": { \"preset\": \"" + preset + "\" } } } }");
            string opts = "\"gameArt\": \"" + (art ? "true" : "false") + "\"" + (character.Length > 0 ? ", \"character\": \"" + character + "\"" : "");
            File.WriteAllText(Path.Combine(dir, "widgets.json"),
                "{ \"schemaVersion\": 3, \"widgets\": [ { \"id\": \"w1\", \"type\": \"clock\", \"appearance\": { \"skins\": { \"azur-archive\": { \"options\": { " + opts + " } } } } } ] }");
            return PanelRenderer.Render(SharedDx.Value, new RenderRequest(null, "idle", Scale: scale, ConfigDir: dir, WidgetId: "w1", Backdrop: "none"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>Distance outside the card's rounded rect, in pixels (negative inside).</summary>
    private static double OutsideCard(double px, double py, double scale, int pxH, double cardW)
    {
        double l = Az.CardX * scale, t = Az.InsetTop * scale, r = l + cardW * scale, b = pxH - Az.InsetBottom * scale;
        double rad = Az.Radius * scale, cx = (l + r) / 2, cy = (t + b) / 2, hx = (r - l) / 2, hy = (b - t) / 2;
        double qx = Math.Abs(px - cx) - (hx - rad), qy = Math.Abs(py - cy) - (hy - rad);
        return Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - rad;
    }

    [Theory]
    [InlineData("", "port-day", 1.7)]
    [InlineData("toki", "port-day", 1.7)]
    [InlineData("arona", "port-day", 1.7)]
    [InlineData("hibiki", "night-watch", 1.7)]
    [InlineData("yuuka", "night-watch", 1.7)]
    [InlineData("", "night-watch", 2.125)]
    [InlineData("toki", "port-day", 2.125)]
    [InlineData("arona", "night-watch", 2.125)]
    [InlineData("hibiki", "port-day", 2.125)]
    [InlineData("yuuka", "port-day", 2.125)]
    public void The_clock_peek_adds_no_pixels_outside_the_card(string character, string preset, double scale)
    {
        if (AzurCast.Slot("toki", "") == null) return;   // art-free checkout: no peek to clip
        var withArt = RenderClock(character, preset, scale, art: true);
        var without = RenderClock(character, preset, scale, art: false);
        Assert.Equal((without.Width, without.Height), (withArt.Width, withArt.Height));

        double cardW = (withArt.Width / scale) - 2 * Az.InsetX;   // BgWidth is the unscaled panel width
        int diffInside = 0;
        for (int y = 0; y < withArt.Height; y++)
            for (int x = 0; x < withArt.Width; x++)
            {
                int o = (y * withArt.Width + x) * 4;
                bool same = true;
                for (int k = 0; k < 4; k++) same &= withArt.Pixels[o + k] == without.Pixels[o + k];
                if (same) continue;
                // 1.5 px of slack for antialiasing and the ceil() on the pixel height
                if (OutsideCard(x + 0.5, y + 0.5, scale, withArt.Height, cardW) > 1.5)
                    Assert.Fail($"{character}/{preset}/{scale}: peek changed pixel ({x},{y}) outside the card");
                diffInside++;
            }
        Assert.True(diffInside > 0, "control: the peek should draw something inside the card");

        // the gap: inside the card but within ClockEl.PeekGap of its top and bottom edge the peek
        // draws nothing, so those rows match the art-free render (±2 per channel for antialiasing)
        double cardL = Az.CardX * scale, cardR = cardL + cardW * scale;
        double cardT = Az.InsetTop * scale, cardB = withArt.Height - Az.InsetBottom * scale;
        double gapPx = Az.U(ClockEl.PeekGap) * scale;
        int x0 = (int)Math.Ceiling(cardR - Az.U(60) * scale), x1 = (int)Math.Floor(cardR - Az.Radius * scale);
        var bands = new[] { (Edge: "top", From: cardT + 1, To: cardT + gapPx - 1), (Edge: "bottom", From: cardB - gapPx + 1, To: cardB - 1) };
        int bandRows = 0;
        foreach (var (edge, from, to) in bands)
        {
            if (to - from < 1) continue;   // under a pixel at this scale
            for (int y = 0; y < withArt.Height; y++)
            {
                if (y + 0.5 <= from || y + 0.5 >= to) continue;
                bandRows++;
                for (int x = x0; x < x1; x++)
                {
                    int o = (y * withArt.Width + x) * 4;
                    for (int k = 0; k < 4; k++)
                        if (Math.Abs(withArt.Pixels[o + k] - without.Pixels[o + k]) > 2)
                            Assert.Fail($"{character}/{preset}/{scale}: peek drew into the {edge} gap at ({x},{y}) channel {k}");
                }
            }
        }
        Assert.True(bandRows > 0, "control: at least one gap row should have been checked");
    }
}
