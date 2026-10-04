using Halo.Shared;
using Halo.Shared.Skins;
using Halo.Metrics;
using Halo.Shared.Config;
using Halo.Shared.Panels;
using Halo.Widgets;
using Halo.Widgets.Render;
using Halo.Widgets.Harness;
using Halo.Widgets.Skins;
using Halo.Widgets.Skins.AzurArchive;

namespace Halo.Tests;

/// <summary>The companion card keeps one size whatever the mood, the copy deck has a line for every
/// message and drops the name cleanly, and a click is told from a drag.</summary>
[Collection(LogTestCollection.Name)]
public sealed class AzurCompanionTests
{
    private static readonly Lazy<Dx> SharedDx = new(() => new Dx(Paths.FontsDir, warp: true));

    [Fact]
    public void Companion_render_is_the_same_size_in_every_mood()
    {
        var sizes = new[] { "idle", "mood-thread", "mood-hot", "na", "mood-poke-arona" }
            .Select(f =>
            {
                var img = PanelRenderer.Render(SharedDx.Value, new RenderRequest("companion", f, Skin: AzurArchiveSkinInfo.Id));
                return (f, img.Width, img.Height);
            }).ToList();
        Assert.All(sizes, s => Assert.Equal((sizes[0].Width, sizes[0].Height), (s.Width, s.Height)));
    }

    [Fact]
    public void Talk_mode_is_taller_than_the_compact_card()
        => Assert.True(CompanionEl.FixedHeight(true) > CompanionEl.FixedHeight(false));

    [Fact]
    public void Address_drops_the_name_when_empty_and_substitutes_otherwise()
    {
        Assert.Equal("Hi.", AzurTalk.Address("{S}, hi.", ""));
        Assert.Equal("Commander, hi.", AzurTalk.Address("{S}, hi.", "Commander"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Commander")]
    public void Every_message_has_a_line_with_no_placeholder_left(string addr)
    {
        var keys = new (MoodTopic, string, string)[]
        {
            (MoodTopic.System, "back", ""), (MoodTopic.System, "gaming", ""), (MoodTopic.System, "gaming", "game.exe"),
            (MoodTopic.System, "hot", "GPU|72"), (MoodTopic.System, "critical", "CPU|95"), (MoodTopic.System, "cool", ""),
            (MoodTopic.Ram, "high", "93"), (MoodTopic.Ram, "back", "70"),
            (MoodTopic.Fps, "spike", "60"), (MoodTopic.Fans, "full", "Pump"), (MoodTopic.Fans, "full", ""),
            (MoodTopic.Network, "quiet", ""), (MoodTopic.Network, "burst", "1200000"),
            (MoodTopic.Uptime, "milestone", "1"), (MoodTopic.Uptime, "milestone", "7"),
            (MoodTopic.Poke, "poke", "123|3"),
        };
        foreach (var host in new[] { "arona", "plana" })
        {
            foreach (var (topic, key, arg) in keys)
            {
                var m = new MoodMessage(1, topic, key, arg, DateTime.Now, topic == MoodTopic.Poke ? host : null);
                string? line = AzurTalk.Line(m, host, addr);
                Assert.True(line != null, $"no line for {topic}/{key}");
                Assert.DoesNotContain("{S}", line);
                Assert.False(string.IsNullOrWhiteSpace(AzurTalk.Sender(m, host)));
            }
            foreach (var s in Enum.GetValues<MoodState>().Where(s => s != MoodState.Asleep))
                Assert.DoesNotContain("{S}", AzurTalk.IdleLine(s, host, addr));
        }
    }

    // ---- tapping a bubble ----

    [Fact]
    public void A_click_on_a_drawn_bubble_pokes_its_sender_and_a_click_beside_it_does_not()
    {
        var settings = new AppSettings();
        settings.Appearance.Skin = AzurArchiveSkinInfo.Id;
        var widget = new WidgetInstance { Id = "t", Type = "companion" };
        var widgets = new[] { widget };
        var metrics = FixtureMetrics.Load("mood-crosstalk");
        var mood = new SystemMood(() => widgets, AzurArchiveSkin.Birthdays);
        var theme = Theme.Resolve(settings.Appearance, widget, 1.7);
        theme.Dpi = 96;
        var ctx = new PanelContext { Metrics = metrics, Mood = mood, Theme = theme, Settings = settings, Widget = widget, Type = PanelCatalog.Find("companion") };
        using var panel = PanelFactory.Create("companion", ctx)!;

        long step = (long)(PanelRenderer.TickS * System.Diagnostics.Stopwatch.Frequency), start = 1000L * System.Diagnostics.Stopwatch.Frequency;
        int ticks = metrics.DurationS is { } d ? Math.Max(PanelRenderer.Ticks, (int)Math.Ceiling(d / PanelRenderer.TickS)) : PanelRenderer.Ticks;
        var now = metrics.Now ?? PanelRenderer.PinnedNow;
        for (int k = 0; k < ticks; k++)
        {
            long qpc = start + k * step;
            metrics.Advance((double)k / (ticks - 1), qpc);
            mood.Update(metrics, qpc, now);
            ctx.Now = now; ctx.NowQpc = qpc; ctx.TickIndex++;
            panel.Update(ctx);
        }

        var dx = SharedDx.Value;
        using var dc = dx.D2DDevice.CreateDeviceContext(Vortice.Direct2D1.DeviceContextOptions.None);
        using var rc = new RenderContext(dc, dx.DWrite, dx.CustomFonts, theme);
        double h = panel.Layout(rc, ctx);
        int w = (int)Math.Ceiling(theme.BgWidth * theme.EffectiveScale), ph = Math.Max(8, (int)Math.Ceiling(h * theme.EffectiveScale));
        var fmt = new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var target = dc.CreateBitmap(new Vortice.Mathematics.SizeI(w, ph), IntPtr.Zero, 0,
            new Vortice.Direct2D1.BitmapProperties1(fmt, 96, 96, Vortice.Direct2D1.BitmapOptions.Target | Vortice.Direct2D1.BitmapOptions.CannotDraw));
        dc.Target = target;
        dc.SetDpi(96, 96);
        dc.BeginDraw();
        dc.Transform = System.Numerics.Matrix3x2.CreateScale((float)theme.BaseScale);
        panel.Draw(rc, ctx);
        dc.EndDraw();
        dc.Target = null;

        var el = panel.Elements.OfType<CompanionEl>().Single();
        var spoken = mood.Messages.Select(m => AzurTalk.Sender(m, "arona", AzurTalk.Seats.From(settings, widgets))).ToHashSet();
        Assert.NotEmpty(spoken);

        // scan the card for what answers a click: only senders of a message in the thread, and some
        var hits = new List<(double X, double Y, string Who)>();
        for (double y = el.Y; y < el.Y + el.Height; y += 2)
            for (double x = -20; x < 400; x += 2)
                if (el.BubbleAt(x, y) is { } who) hits.Add((x, y, who));
        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.Contains(hit.Who, spoken));

        // beside the thread: left of the card, and the header row above the zone
        Assert.Null(el.BubbleAt(-20, el.Y + el.Height / 2));
        Assert.Null(el.BubbleAt(hits[0].X, el.Y + 4));

        // a click inside a bubble pokes exactly that sender, and counts as a glance
        var (px, py, sender) = hits[^1];
        Assert.True(el.Poke(ctx, px, py));
        Assert.True(mood.Poked(sender, ctx.NowQpc));
        Assert.Equal(sender, mood.Messages[^1].Who);
        Assert.Equal(MoodTopic.Poke, mood.Messages[^1].Topic);
        Assert.Equal(0, mood.Unread);
    }

    // ---- the thread never goes quiet ----

    private static (Panel Panel, PanelContext Ctx, SystemMood Mood, long Qpc) Driven(string fixture)
    {
        var settings = new AppSettings();
        settings.Appearance.Skin = AzurArchiveSkinInfo.Id;
        var widget = new WidgetInstance { Id = "t", Type = "companion" };
        var widgets = new[] { widget };
        var metrics = FixtureMetrics.Load(fixture);
        var mood = new SystemMood(() => widgets, AzurArchiveSkin.Birthdays);
        var theme = Theme.Resolve(settings.Appearance, widget, 1.7);
        var ctx = new PanelContext { Metrics = metrics, Mood = mood, Theme = theme, Settings = settings, Widget = widget, Type = PanelCatalog.Find("companion") };
        var panel = PanelFactory.Create("companion", ctx)!;
        long step = (long)(PanelRenderer.TickS * System.Diagnostics.Stopwatch.Frequency), start = 1000L * System.Diagnostics.Stopwatch.Frequency;
        int ticks = metrics.DurationS is { } d ? Math.Max(PanelRenderer.Ticks, (int)Math.Ceiling(d / PanelRenderer.TickS)) : PanelRenderer.Ticks;
        var now = metrics.Now ?? PanelRenderer.PinnedNow;
        long qpc = start;
        for (int k = 0; k < ticks; k++)
        {
            qpc = start + k * step;
            metrics.Advance((double)k / (ticks - 1), qpc);
            mood.Update(metrics, qpc, now);
            ctx.Now = now; ctx.NowQpc = qpc; ctx.TickIndex++;
            panel.Update(ctx);
        }
        return (panel, ctx, mood, qpc);
    }

    [Fact]
    public void A_thread_whose_last_post_is_long_past_still_shows_its_messages()
    {
        var (panel, ctx, mood, qpc) = Driven("mood-crosstalk");
        using var _ = panel;
        Assert.NotEmpty(mood.Messages);

        ctx.NowQpc = qpc + (long)(10 * 60 * System.Diagnostics.Stopwatch.Frequency);   // > the old 5 min gate
        panel.Update(ctx);

        var lines = panel.Elements.OfType<CompanionEl>().Single().ThreadLines;
        Assert.Equal(mood.Messages.Count, lines.Count);
        Assert.DoesNotContain(lines, l => l.Text == AzurTalk.IdleLine(mood.State, AzurCompanion.Host(ctx), ""));
    }

    /// <summary>A companion panel over a mood that has never had <c>Update</c> called, so its thread
    /// is empty by construction. <see cref="Driven"/> cannot give that: it always posts the day's
    /// greeting first.</summary>
    private static (Panel Panel, PanelContext Ctx, SystemMood Mood) EmptyThread()
    {
        var settings = new AppSettings();
        settings.Appearance.Skin = AzurArchiveSkinInfo.Id;
        var widget = new WidgetInstance { Id = "t", Type = "companion" };
        var widgets = new[] { widget };
        var mood = new SystemMood(() => widgets, AzurArchiveSkin.Birthdays);
        var ctx = new PanelContext
        {
            Metrics = FixtureMetrics.Load("idle"), Mood = mood, Theme = Theme.Resolve(settings.Appearance, widget, 1.7),
            Settings = settings, Widget = widget, Type = PanelCatalog.Find("companion"),
            Now = PanelRenderer.PinnedNow, NowQpc = 1000L * System.Diagnostics.Stopwatch.Frequency,
        };
        var panel = PanelFactory.Create("companion", ctx)!;
        panel.Update(ctx);
        return (panel, ctx, mood);
    }

    [Fact]
    public void The_idle_line_shows_only_for_an_empty_thread()
    {
        var (panel, ctx, mood) = EmptyThread();
        using var _p = panel;
        Assert.Empty(mood.Messages);
        var only = Assert.Single(panel.Elements.OfType<CompanionEl>().Single().ThreadLines);
        Assert.Equal(AzurTalk.IdleLine(mood.State, AzurCompanion.Host(ctx), AzurChrome.Option(ctx.Theme, "addressAs").Trim()), only.Text);

        var busy = Driven("mood-crosstalk");
        using var _b = busy.Panel;
        Assert.NotEmpty(busy.Mood.Messages);
        Assert.DoesNotContain(busy.Panel.Elements.OfType<CompanionEl>().Single().ThreadLines,
            l => l.Text == AzurTalk.IdleLine(busy.Mood.State, AzurCompanion.Host(busy.Ctx), ""));
    }

    [Fact]
    public void A_host_owned_message_keeps_its_sender_and_text_when_the_host_changes()
    {
        var (panel, ctx, mood, _) = Driven("mood-hot");
        using var _ = panel;
        var el = panel.Elements.OfType<CompanionEl>().Single();
        Assert.Equal("arona", AzurCompanion.Host(ctx));   // fixture clock is daytime
        var before = el.ThreadLines;
        // precondition: some message would read differently under the other host, or the test proves nothing
        var seats = AzurTalk.Seats.From(ctx.Settings, mood.Widgets);
        Assert.Contains(mood.Messages, m => m.Who == null
            && (AzurTalk.Sender(m, "arona", seats) != AzurTalk.Sender(m, "plana", seats)
                || AzurTalk.Line(m, "arona", "", seats, false) != AzurTalk.Line(m, "plana", "", seats, false)));

        ctx.Now = ctx.Now.Date.AddHours(20);
        Assert.Equal("plana", AzurCompanion.Host(ctx));
        panel.Update(ctx);

        Assert.Equal(before, el.ThreadLines);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(3, -2, true)]
    [InlineData(5, 0, false)]
    public void PokeGesture_tells_a_click_from_a_drag(int dx, int dy, bool click)
        => Assert.Equal(click, PokeGesture.IsClick(dx, dy, 4, 4));

    // ---- the press latch (review fixup 1) ----

    [Fact]
    public void Jitter_inside_the_slop_never_moves_and_ends_as_a_click()
    {
        var g = new PokeGesture();
        g.Down(100, 100);
        Assert.False(g.Move(102, 99, 4, 4));    // false = the window does not follow
        Assert.False(g.Move(97, 103, 4, 4));
        Assert.True(g.Up());                     // a click: poke, nothing to save
    }

    [Fact]
    public void Crossing_the_slop_latches_a_drag_even_if_it_comes_back()
    {
        var g = new PokeGesture();
        g.Down(100, 100);
        Assert.True(g.Move(110, 100, 4, 4));
        Assert.True(g.Move(101, 100, 4, 4));     // back near the start: still a drag
        Assert.False(g.Up());                    // not a click: the drag path persists the position
        Assert.False(g.Pressed);
    }

    // ---- config renders read every widget's thresholds (review fixup 2) ----

    [Fact]
    public void Config_render_mood_uses_other_widgets_warn_thresholds()
    {
        string dir = Path.Combine(Path.GetTempPath(), "halo-mood-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "widgets.json"), """
                { "schemaVersion": 3, "widgets": [
                  { "id": "cpu-1", "type": "cpu-ram", "enabled": true, "metrics": { "temp": { "warn": [60, 70, 80, 90] } } },
                  { "id": "comp-1", "type": "companion", "enabled": true } ] }
                """);
            string hot85 = Path.Combine(dir, "cpu85.json"), hot75 = Path.Combine(dir, "cpu75.json");
            File.WriteAllText(hot85, """{ "extends": "idle", "series": { "cpu.package.temp.c": [85] } }""");
            File.WriteAllText(hot75, """{ "extends": "idle", "series": { "cpu.package.temp.c": [75] } }""");

            // 85 °C on the user's 60/70/80/90 is step 4: Hot, the same as 75 °C on the catalog's
            // 50/60/70/80 — and not the Critical that 85 °C would be on the catalog's
            var config = PanelRenderer.Render(SharedDx.Value, new RenderRequest(null, hot85, ConfigDir: dir, WidgetId: "comp-1"));
            var hot = PanelRenderer.Render(SharedDx.Value, new RenderRequest("companion", hot75));
            var critical = PanelRenderer.Render(SharedDx.Value, new RenderRequest("companion", hot85));
            Assert.Equal(hot.Pixels, config.Pixels);
            Assert.NotEqual(critical.Pixels, config.Pixels);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
