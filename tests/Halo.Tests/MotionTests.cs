using System.Diagnostics;
using Halo.Metrics;
using Halo.Shared.Config;
using Halo.Shared.Panels;
using Halo.Shared.Skins;
using Halo.Widgets;
using Halo.Widgets.Harness;
using Halo.Widgets.Render;
using Halo.Widgets.Skins;
using Halo.Widgets.Skins.AzurArchive;

namespace Halo.Tests;

/// <summary>Motion level, the shared ease and Transition clock, the "data never animates" rule on a
/// real Azur panel, and the ambient loop's gates. Pure — no window, no swapchain.</summary>
[Collection(LogTestCollection.Name)]   // shares HaloShape's static cache with the shape tests
public sealed class MotionTests
{
    private static readonly long Tick = Stopwatch.Frequency / 5;
    private static readonly long Anim = (long)(Motion.TransitionS * Stopwatch.Frequency);
    private static readonly DateTime Wall = new(2026, 3, 14, 12, 0, 0);

    /// <summary>The idle fixture with readings overridden, so one value can change between ticks.</summary>
    private sealed class Fake : IMetricSource
    {
        private readonly FixtureMetrics _f = FixtureMetrics.Load("idle");
        public readonly Dictionary<string, double> Set = new();
        public readonly HashSet<string> Gone = new();
        public Fake() => _f.Advance(1.0, 1000);
        public bool Stale => false;
        public double Value(string n, double d = 0) => Set.TryGetValue(n, out double v) ? v : _f.Value(n, d);
        public bool TryValue(string n, out double v, double maxAgeS = double.MaxValue)
        {
            if (Gone.Contains(n)) { v = 0; return false; }
            return Set.TryGetValue(n, out v) || _f.TryValue(n, out v, maxAgeS);
        }
        public bool Has(string n) => Set.ContainsKey(n) || _f.Has(n);
        public string Text(string n, string d = "") => _f.Text(n, d);
        public ReadOnlySpan<FrameEntry> NewFrames => default;
        public IReadOnlyList<MetricInfo> Describe() => _f.Describe();
    }

    private static PanelContext Ctx(MotionLevel level, IMetricSource m, SystemMood? mood = null)
    {
        var settings = new AppSettings();
        settings.Appearance.Skin = AzurArchiveSkinInfo.Id;
        var widget = new WidgetInstance { Id = "t", Type = "cpu-ram" };
        var theme = Theme.Resolve(settings.Appearance, widget, 1.7);
        theme.Dpi = 96;
        return new PanelContext
        {
            Metrics = m, Theme = theme, Settings = settings, Widget = widget,
            Type = PanelCatalog.Find(widget.Type), Mood = mood ?? new SystemMood(), Motion = level,
        };
    }

    // ---- 1. level and curve ----

    [Theory]
    [InlineData("off", true, MotionLevel.Off)]
    [InlineData("subtle", true, MotionLevel.Subtle)]
    [InlineData("full", true, MotionLevel.Full)]
    [InlineData(" FULL ", true, MotionLevel.Full)]
    [InlineData("Off", true, MotionLevel.Off)]
    [InlineData("wobbly", true, MotionLevel.Subtle)]
    [InlineData("", true, MotionLevel.Subtle)]
    [InlineData(null, true, MotionLevel.Subtle)]
    [InlineData("full", false, MotionLevel.Off)]
    [InlineData("subtle", false, MotionLevel.Off)]
    [InlineData(null, false, MotionLevel.Off)]
    public void Resolve_follows_the_setting_and_windows_caps_it(string? setting, bool windows, MotionLevel want)
        => Assert.Equal(want, Motion.Resolve(setting, windows));

    [Fact]
    public void Ease_hits_both_ends_and_never_goes_backwards()
    {
        Assert.Equal(0, Motion.Ease(0));
        Assert.Equal(1, Motion.Ease(1));
        Assert.Equal(0, Motion.Ease(-0.5));
        Assert.Equal(1, Motion.Ease(1.5));
        double prev = 0;
        for (int i = 1; i <= 100; i++)
        {
            double e = Motion.Ease(i / 100.0);
            Assert.InRange(e, prev, 1);
            prev = e;
        }
        Assert.True(Motion.Ease(0.25) > 0.25);   // decelerate: ahead of linear early on
    }

    // ---- 2. Transition ----

    [Fact]
    public void First_key_is_not_a_transition()
    {
        var c = Ctx(MotionLevel.Subtle, new Fake());
        c.NowQpc = 5000;
        var t = new Transition();
        Assert.False(t.Step(c, "a"));
        Assert.Equal(0, c.AnimatingUntilQpc);
        Assert.Equal(1f, t.Progress(c));
        Assert.False(t.Step(c, "a"));           // same key again: still nothing
        Assert.Equal(0, c.AnimatingUntilQpc);
    }

    [Fact]
    public void Key_change_runs_the_clock_for_the_transition_length()
    {
        var c = Ctx(MotionLevel.Subtle, new Fake());
        var t = new Transition();
        c.NowQpc = c.AnimQpc = 10_000;
        t.Step(c, "a");
        c.NowQpc = c.AnimQpc = 20_000;
        Assert.True(t.Step(c, "b"));
        Assert.Equal("a", t.From);
        Assert.Equal(20_000 + Anim, c.AnimatingUntilQpc);

        Assert.Equal(0f, t.Progress(c));
        float prev = 0;
        for (int i = 1; i < 10; i++)
        {
            c.AnimQpc = 20_000 + Anim * i / 10;
            float p = t.Progress(c);
            Assert.InRange(p, prev, 1);
            prev = p;
        }
        c.AnimQpc = 20_000 + Anim;
        Assert.Equal(1f, t.Progress(c));
        Assert.True(t.Done(c));
        Assert.Null(t.From);
    }

    [Fact]
    public void Key_change_with_motion_off_asks_for_no_frames()
    {
        var c = Ctx(MotionLevel.Off, new Fake());
        var t = new Transition();
        c.NowQpc = c.AnimQpc = 10_000;
        t.Step(c, "a");
        c.NowQpc = 20_000;
        Assert.True(t.Step(c, "b"));             // the state did change...
        Assert.Equal(0, c.AnimatingUntilQpc);    // ...but nothing is scheduled
        Assert.Equal(1f, t.Progress(c));
        c.AnimateUntil(99_999);
        Assert.Equal(0, c.AnimatingUntilQpc);    // AnimateUntil is a no-op at Off
    }

    // ---- 3. the data rule, on a real Azur panel ----

    /// <summary>cpu-ram in azur-archive, ticked until the first keys are recorded; returns a function
    /// that ticks once more with the given CPU % and package temperature.</summary>
    private static (Func<double, double, bool> Tick, PanelContext Ctx, IDisposable Panel) Azur(MotionLevel level)
    {
        var m = new Fake();
        var ctx = Ctx(level, m);
        var mood = ctx.Mood;
        var panel = PanelFactory.Create("cpu-ram", ctx)!;
        long k = 0;
        bool Step(double cpuPct, double tempC)
        {
            m.Set[MetricNames.CpuTotalPct] = cpuPct;
            m.Set[MetricNames.CpuPackageTempC] = tempC;
            long qpc = 1000L * Stopwatch.Frequency + k * Tick;
            mood.Update(m, qpc, Wall.AddSeconds(k * 0.2));
            ctx.Now = Wall; ctx.NowQpc = ctx.AnimQpc = qpc; ctx.TickIndex++;
            k++;
            return panel.Update(ctx);
        }
        for (int i = 0; i < 5; i++) Step(20, 40);
        return (Step, ctx, panel);
    }

    [Fact]
    public void A_reading_inside_its_warn_band_is_dirty_but_asks_for_no_animation_frames()
    {
        var (step, ctx, panel) = Azur(MotionLevel.Subtle);
        using (panel)
        {
            Assert.Equal(0, ctx.AnimatingUntilQpc);
            Assert.True(step(25, 40));            // CPU 20 → 25 %: the number changes, so it repaints
            Assert.True(step(30, 41));            // temp 40 → 41 °C: same band (< 50)
            Assert.Equal(0, ctx.AnimatingUntilQpc);   // data snaps; no clock was started
        }
    }

    [Fact]
    public void Crossing_a_warn_step_starts_the_clock()
    {
        var (step, ctx, panel) = Azur(MotionLevel.Subtle);
        using (panel)
        {
            step(20, 75);                         // 40 → 75 °C is step 4 on 50/60/70/80
            Assert.True(ctx.AnimatingUntilQpc > 0);
            Assert.True(ctx.AnimatingUntilQpc <= ctx.NowQpc + Anim);
        }
    }

    [Fact]
    public void Crossing_the_quiet_levels_below_warn_starts_no_clock()
    {
        var (step, ctx, panel) = Azur(MotionLevel.Subtle);
        using (panel)
        {
            int before = ctx.TransitionStarts;
            foreach (double t in new[] { 55.0, 65, 55, 45 })   // L1 → L2 → L3 → L2 → L1, none is warn (75+)
            {
                step(20, t);
                Assert.Equal(0, ctx.AnimatingUntilQpc);
            }
            Assert.Equal(before, ctx.TransitionStarts);
        }
    }

    [Fact]
    public void Leaving_the_warn_step_starts_the_clock_too()
    {
        var (step, ctx, panel) = Azur(MotionLevel.Subtle);
        using (panel)
        {
            step(20, 75);
            for (int i = 0; i < 3; i++) step(20, 75);          // settle in warn
            int before = ctx.TransitionStarts;
            step(20, 45);
            Assert.True(ctx.TransitionStarts > before);   // header plus any other warn-keyed element
            Assert.True(ctx.AnimatingUntilQpc > 0);
        }
    }

    [Fact]
    public void Transition_starts_count_only_started_clocks()
    {
        var (step, ctx, panel) = Azur(MotionLevel.Off);
        using (panel)
        {
            step(20, 75);
            Assert.Equal(0, ctx.TransitionStarts);
        }
    }

    [Fact]
    public void Crossing_a_warn_step_with_motion_off_stays_quiet()
    {
        var (step, ctx, panel) = Azur(MotionLevel.Off);
        using (panel)
        {
            step(20, 75);
            Assert.Equal(0, ctx.AnimatingUntilQpc);
        }
    }

    // ---- 4. the ambient loop ----

    private static PanelContext AmbientCtx(MotionLevel level, Fake m, out SystemMood mood)
    {
        mood = new SystemMood();
        mood.Update(m, 1000L * Stopwatch.Frequency, Wall);   // Known = true; idle fixture, so not asleep
        return Ctx(level, m, mood);
    }

    private static AmbientLoop Loop() => new()
    {
        Kind = AmbientKind.Spin, Key = "k", Bounds = new Vortice.Mathematics.Rect(0, 0, 10, 4),
    };

    [Fact]
    public void Loops_run_only_at_full_motion()
    {
        var m = new Fake();
        Assert.False(AmbientLoop.On(AmbientCtx(MotionLevel.Off, m, out _)));
        Assert.False(AmbientLoop.On(AmbientCtx(MotionLevel.Subtle, m, out _)));
        Assert.True(AmbientLoop.On(AmbientCtx(MotionLevel.Full, m, out _)));
        Assert.False(AmbientLoop.On(Ctx(MotionLevel.Full, m)));   // a mood that never reported: not Known
    }

    [Fact]
    public void Loops_stop_while_a_game_is_presenting()
    {
        var m = new Fake();
        m.Set[MetricNames.FpsPresented] = 144;
        var c = AmbientCtx(MotionLevel.Full, m, out var mood);
        Assert.True(mood.Presenting);
        Assert.False(AmbientLoop.On(c));
        Assert.False(AmbientLoop.Offer(c, Loop()));
        Assert.Null(c.Ambient);
        Assert.Empty(c.Ambients);
    }

    [Fact]
    public void A_widget_gets_two_loops_and_the_third_is_refused()
    {
        Assert.Equal(2, AmbientLoop.MaxPerWidget);
        var c = AmbientCtx(MotionLevel.Full, new Fake(), out _);
        var first = Loop();
        var second = Loop();
        Assert.True(AmbientLoop.Offer(c, first));
        Assert.True(AmbientLoop.Offer(c, second));
        Assert.False(AmbientLoop.Offer(c, Loop()));
        Assert.Same(first, c.Ambient);
        Assert.Equal(2, c.Ambients.Count);
        Assert.Same(first, c.Ambients[0]);
        Assert.Same(second, c.Ambients[1]);

        foreach (var level in new[] { MotionLevel.Subtle, MotionLevel.Off })
        {
            var off = AmbientCtx(level, new Fake(), out _);
            Assert.False(AmbientLoop.Offer(off, Loop()));
            Assert.Null(off.Ambient);
            Assert.Empty(off.Ambients);
        }
    }

    [Theory]
    [InlineData(0, 32, 0)]    // inside the 5-point band above 30: stays
    [InlineData(0, 36, 1)]
    [InlineData(1, 27, 1)]    // inside the band below 30: stays
    [InlineData(1, 24, 0)]
    [InlineData(1, 72, 1)]
    [InlineData(1, 76, 2)]
    [InlineData(2, 67, 2)]
    [InlineData(2, 64, 1)]
    [InlineData(0, 90, 2)]
    [InlineData(2, 5, 0)]
    public void SpinStep_has_hysteresis_on_cpu_load(int current, double cpu, int want)
    {
        var m = new Fake();
        m.Set[MetricNames.CpuTotalPct] = cpu;
        Assert.Equal(want, AmbientLoop.SpinStep(m, current));
    }

    [Fact]
    public void Offer_refuses_while_the_entrance_runs_and_succeeds_once_it_ends()
    {
        var c = AmbientCtx(MotionLevel.Full, new Fake(), out _);
        c.Entering = true;
        Assert.False(AmbientLoop.Offer(c, Loop()));
        Assert.Null(c.Ambient);
        Assert.Empty(c.Ambients);
        c.Entering = false;
        var loop = Loop();
        Assert.True(AmbientLoop.Offer(c, loop));
        Assert.Same(loop, c.Ambient);
    }

    [Fact]
    public void Gate_is_open_only_at_full_motion_when_fresh_known_and_not_presenting()
    {
        var m = new Fake();
        AmbientCtx(MotionLevel.Full, m, out var mood);
        Assert.True(AmbientLoop.Gate(MotionLevel.Full, false, mood));
        Assert.False(AmbientLoop.Gate(MotionLevel.Off, false, mood));
        Assert.False(AmbientLoop.Gate(MotionLevel.Subtle, false, mood));
        Assert.False(AmbientLoop.Gate(MotionLevel.Full, true, mood));                 // stale
        Assert.False(AmbientLoop.Gate(MotionLevel.Full, false, new SystemMood()));    // never reported

        var game = new Fake();
        game.Set[MetricNames.FpsPresented] = 144;
        AmbientCtx(MotionLevel.Full, game, out var presenting);
        Assert.False(AmbientLoop.Gate(MotionLevel.Full, false, presenting));
    }

    // ---- 5. App.WaitMs ----

    [Theory]
    [MemberData(nameof(Frequencies))]
    public void WaitMs_rounds_up_to_a_whole_millisecond_and_is_bounded(long qpf)
    {
        const long now = 5_000_000;
        long Ms(double ms) => (long)Math.Round(ms * qpf / 1000.0);
        Assert.Equal(1u, App.WaitMs(now + Ms(0.4), now, qpf));
        Assert.Equal(2u, App.WaitMs(now + Ms(1.2), now, qpf));
        Assert.Equal(0u, App.WaitMs(now, now, qpf));
        Assert.Equal(0u, App.WaitMs(now - 1, now, qpf));
        Assert.Equal(100u, App.WaitMs(long.MaxValue, now, qpf));
        Assert.Equal(250u, App.WaitMs(now + 10 * qpf, now, qpf));
    }

    /// <summary>The real QPC frequency and a round one; they coincide on most Windows 10+ machines,
    /// and a duplicate theory row is skipped by xUnit, so only distinct values go in.</summary>
    public static TheoryData<long> Frequencies
    {
        get
        {
            var d = new TheoryData<long>();
            foreach (long f in new[] { Stopwatch.Frequency, 10_000_000L }.Distinct()) d.Add(f);
            return d;
        }
    }

    // ---- 6. capability flags ----

    private static PanelContext SkinCtx(string skin, string type, Action<AppSettings, WidgetInstance>? configure = null)
    {
        var settings = new AppSettings();
        settings.Appearance.Skin = skin;
        var widget = new WidgetInstance { Id = "t", Type = type };
        configure?.Invoke(settings, widget);
        var theme = Theme.Resolve(settings.Appearance, widget, 1.7);
        theme.Dpi = 96;
        return new PanelContext
        {
            Metrics = new Fake(), Theme = theme, Settings = settings, Widget = widget,
            Type = PanelCatalog.Find(type), Mood = new SystemMood(), Motion = MotionLevel.Full,
        };
    }

    [Fact]
    public void Rainformer_panels_never_loop_or_enter()
    {
        foreach (string type in PanelFactory.KnownTypes)
        {
            using var p = PanelFactory.Create(type, SkinCtx(SkinCatalog.RainformerId, type))!;
            Assert.False(p.Loops, type);
            Assert.False(p.Entrance, type);
        }
    }

    [Theory]
    [InlineData("cpu-ram")]
    [InlineData("companion")]
    public void Azur_panels_declare_loops_and_entrance(string type)
    {
        using var p = PanelFactory.Create(type, SkinCtx(AzurArchiveSkinInfo.Id, type))!;
        Assert.True(p.Loops, type);
        Assert.True(p.Entrance, type);
    }

    // ---- 7. drawing during an entrance ----

    private static readonly Lazy<Dx> SharedDx = new(() => new Dx(Halo.Shared.Paths.FontsDir, warp: true));

    /// <summary>Draws the panel once with the ambient slot cleared (as WidgetWindow does per render)
    /// and returns what it claimed.</summary>
    private static AmbientLoop? DrawAndGetAmbient(string type, bool entering, Action<AppSettings, WidgetInstance>? configure = null)
        => DrawAndGetAmbients(type, entering, configure).FirstOrDefault();

    /// <summary>Every loop the panel claimed in one draw, in offer order (up to AmbientLoop.MaxPerWidget).</summary>
    private static List<AmbientLoop> DrawAndGetAmbients(string type, bool entering, Action<AppSettings, WidgetInstance>? configure = null)
    {
        var ctx = SkinCtx(AzurArchiveSkinInfo.Id, type, configure);
        var m = (Fake)ctx.Metrics;
        ctx.Mood.Update(m, 1000L * Stopwatch.Frequency, Wall);
        ctx.Now = Wall; ctx.NowQpc = ctx.AnimQpc = 1000L * Stopwatch.Frequency; ctx.TickIndex++;
        using var panel = PanelFactory.Create(type, ctx)!;
        panel.Update(ctx);

        var dx = SharedDx.Value;
        using var dc = dx.D2DDevice.CreateDeviceContext(Vortice.Direct2D1.DeviceContextOptions.None);
        using var rc = new RenderContext(dc, dx.DWrite, dx.CustomFonts, ctx.Theme);
        double scale = ctx.Theme.EffectiveScale;
        double h = panel.Layout(rc, ctx);
        int w = (int)Math.Ceiling(ctx.Theme.BgWidth * scale), ph = Math.Max(8, (int)Math.Ceiling(h * scale));
        var fmt = new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var target = dc.CreateBitmap(new Vortice.Mathematics.SizeI(w, ph), IntPtr.Zero, 0,
            new Vortice.Direct2D1.BitmapProperties1(fmt, 96, 96, Vortice.Direct2D1.BitmapOptions.Target | Vortice.Direct2D1.BitmapOptions.CannotDraw));
        dc.Target = target;
        dc.SetDpi(96, 96);

        ctx.Ambients.Clear();
        ctx.Entering = entering;
        dc.BeginDraw();
        dc.Transform = System.Numerics.Matrix3x2.CreateScale((float)ctx.Theme.BaseScale);
        panel.Draw(rc, ctx);
        dc.EndDraw();
        dc.Target = null;
        return [.. ctx.Ambients];
    }

    [Theory]
    [InlineData("companion")]
    [InlineData("cpu-ram")]
    public void Drawing_during_an_entrance_claims_no_loop(string type)
        => Assert.Null(DrawAndGetAmbient(type, entering: true));

    [Theory]
    [InlineData("companion")]
    [InlineData("cpu-ram")]
    public void Drawing_after_the_entrance_claims_a_loop(string type)
    {
        // art-free checkout (after a takedown): no art, nothing to loop, nothing to claim
        if (AzurCast.Slot("arona", "") == null || AzurCast.Slot("yuuka", "") == null) return;
        Assert.NotNull(DrawAndGetAmbient(type, entering: false));   // control: the harness can see a claim
    }

    // ---- 8. tinted glint, no clock halo, the halo option ----

    private static bool ArtPresent => AzurCast.Slot("yuuka", "") != null && AzurCast.Slot("toki", "") != null;

    [Fact]
    public void Glint_is_the_halo_colour_lifted_toward_white_not_white()
    {
        var blue = new Vortice.Mathematics.Color4(0, 0, 1, 0.8f);
        var g = AmbientVisual.Glint(blue);
        Assert.True(g.R > blue.R && g.G > blue.G);                 // lifted
        Assert.Equal(1f, g.B, 3);                                   // blue already at the top
        Assert.False(g.R >= 1f && g.G >= 1f);                       // not white
        Assert.Equal(0.8f, g.A, 3);                                 // same alpha
        Assert.Equal(AmbientVisual.GlintLift, g.R, 3);
        var other = AmbientVisual.Glint(new Vortice.Mathematics.Color4(1, 0.5f, 0, 0.8f));
        Assert.NotEqual(g, other);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yuuka")]
    [InlineData("toki")]
    public void The_clock_card_never_offers_a_loop(string character)
    {
        Assert.Null(DrawAndGetAmbient("clock", entering: false, (_, w) =>
        {
            if (character.Length > 0) WidgetOpt(w, "character", character);
        }));
    }

    [Fact]
    public void A_face_card_can_offer_a_spin_so_the_clock_check_means_something()
    {
        if (!ArtPresent) return;
        Assert.Equal(AmbientKind.Spin, DrawAndGetAmbient("cpu-ram", entering: false)!.Kind);
    }

    [Fact]
    public void The_halo_option_is_a_bool_defaulting_on()
    {
        var o = AzurArchiveSkinInfo.Info.Options.Single(x => x.Key == "halo");
        Assert.Equal(OptionKind.Bool, o.Kind);
        Assert.Equal("true", o.Default);
    }

    private static void WidgetOpt(WidgetInstance w, string k, string v) => w.Appearance.SkinFor(AzurArchiveSkinInfo.Id).Options[k] = v;
    private static void GlobalHalo(AppSettings s, string v) => s.Appearance.SkinFor(AzurArchiveSkinInfo.Id).Options["halo"] = v;
    private static void WidgetHalo(WidgetInstance w, string v)
        => w.Appearance.SkinFor(AzurArchiveSkinInfo.Id).Options["halo"] = v;

    [Fact]
    public void Halo_off_globally_or_per_widget_offers_no_spin_and_per_widget_on_beats_global_off()
    {
        if (!ArtPresent) return;
        Assert.Null(DrawAndGetAmbient("cpu-ram", false, (s, _) => GlobalHalo(s, "false")));
        Assert.Null(DrawAndGetAmbient("cpu-ram", false, (_, w) => WidgetHalo(w, "false")));
        Assert.Null(DrawAndGetAmbient("cpu-ram", false, (s, w) => { GlobalHalo(s, "true"); WidgetHalo(w, "false"); }));
        Assert.Equal(AmbientKind.Spin, DrawAndGetAmbient("cpu-ram", false, (s, w) => { GlobalHalo(s, "false"); WidgetHalo(w, "true"); })!.Kind);
    }

    private static bool NetworkArtPresent => AzurCast.Slot("chihiro", "") != null && AzurCast.Slot("kotama", "") != null;

    [Fact]
    public void The_network_card_offers_a_spin_for_each_seat()
    {
        if (!NetworkArtPresent) return;
        var both = DrawAndGetAmbients("network", false);
        Assert.Equal(2, both.Count);
        Assert.All(both, l => Assert.Equal(AmbientKind.Spin, l.Kind));
        Assert.Contains(both, l => l.Key.Contains("chihiro"));
        Assert.Contains(both, l => l.Key.Contains("kotama"));

        Assert.Empty(DrawAndGetAmbients("network", false, (s, _) => GlobalHalo(s, "false")));
        Assert.Empty(DrawAndGetAmbients("network", false, (_, w) => WidgetHalo(w, "false")));

        var one = DrawAndGetAmbients("network", false, (_, w) => WidgetOpt(w, "character2", "none"));
        var l1 = Assert.Single(one);
        Assert.Contains("chihiro", l1.Key);

        Assert.Empty(DrawAndGetAmbients("network", true));          // entering: none
    }

    [Fact]
    public void Aris_card_holds_her_halo_still_while_yuuka_spins()
    {
        if (!ArtPresent) return;
        Assert.Null(DrawAndGetAmbient("cpu-ram", false, (_, w) => WidgetOpt(w, "character", "aris")));
        var y = DrawAndGetAmbient("cpu-ram", false, (_, w) => WidgetOpt(w, "character", "yuuka"));
        Assert.Equal(AmbientKind.Spin, y!.Kind);
        Assert.Contains("yuuka", y.Key);
    }

    [Theory]
    [InlineData("cpu-ram")]
    [InlineData("drives")]
    public void A_halo_never_reaches_above_the_header_rule_and_a_cramped_seat_leans_it_flatter(string type)
    {
        if (!ArtPresent) return;
        HaloShape.ResetForTests();
        try
        {
            float ceiling = Az.ContentTop + Az.U(1);
            foreach (string id in AzurArchiveSkinInfo.Cast)
            {
                var shape = HaloShape.For(id);
                if (shape == null) continue;
                var loop = DrawAndGetAmbient(type, false, (_, w) => WidgetOpt(w, "character", id));
                if (!shape.Spin) { Assert.Null(loop); continue; }
                Assert.True(loop != null, $"{type}/{id}");
                Assert.Equal(AmbientKind.Spin, loop!.Kind);
                Assert.True(loop.Bounds.Top >= ceiling - 0.01f, $"{type}/{id}: top {loop.Bounds.Top} < ceiling {ceiling}");
                Assert.True(loop.Radius > 0, $"{type}/{id}");
                // leaning flatter only ever raises the pitch
                Assert.True(loop.Pitch * 180 / MathF.PI >= shape.Pitch - 0.01f, $"{type}/{id}");
            }
        }
        finally { HaloShape.ResetForTests(); }
    }

    /// <summary>Calls DrawHalo for an unknown student (so HaloShape.Generic, no art or data needed) and
    /// returns the one Spin it offered.</summary>
    private static AmbientLoop GenericHalo(float ceilingAboveSlot)
    {
        var ctx = AmbientCtx(MotionLevel.Full, new Fake(), out _);
        var dx = SharedDx.Value;
        using var dc = dx.D2DDevice.CreateDeviceContext(Vortice.Direct2D1.DeviceContextOptions.None);
        using var rc = new RenderContext(dc, dx.DWrite, dx.CustomFonts, ctx.Theme);
        var slot = new Vortice.Mathematics.Rect(20, 60, Az.U(48), Az.U(48));
        AzurCast.DrawHalo(rc, "no-such-student", slot, 1, ctx, slot.Top - ceilingAboveSlot);
        return Assert.Single(ctx.Ambients);
    }

    [Fact]
    public void A_cramped_seat_leans_the_generic_halo_flatter_and_a_roomy_one_does_not()
    {
        float ceiling = 60 - Az.U(2);
        var cramped = GenericHalo(Az.U(2));
        Assert.Equal(AmbientKind.Spin, cramped.Kind);
        Assert.True(cramped.Pitch > HaloShape.Generic.PitchRad, $"offered {cramped.Pitch} rad, generic {HaloShape.Generic.PitchRad} rad");
        Assert.True(cramped.Bounds.Top >= ceiling - 0.01f, $"top {cramped.Bounds.Top} < ceiling {ceiling}");
        Assert.True(cramped.Radius > 0);

        var roomy = GenericHalo(Az.U(40));
        Assert.Equal(AmbientKind.Spin, roomy.Kind);
        Assert.Equal(HaloShape.Generic.PitchRad, roomy.Pitch, 4);
        Assert.True(roomy.Radius > 0);
    }

    [Fact]
    public void Halo_off_leaves_the_companions_bob_loop()
    {
        if (!ArtPresent) return;
        var on = DrawAndGetAmbient("companion", false);
        var off = DrawAndGetAmbient("companion", false, (s, _) => GlobalHalo(s, "false"));
        Assert.Equal(AmbientKind.Bob, on?.Kind);                    // control: the harness sees a Bob
        Assert.Equal(AmbientKind.Bob, off?.Kind);
    }

    [Fact]
    public void SpinStep_keeps_the_current_step_when_cpu_is_na()
    {
        var m = new Fake();
        m.Gone.Add(MetricNames.CpuTotalPct);
        Assert.Equal(2, AmbientLoop.SpinStep(m, 2));
        Assert.Equal(0, AmbientLoop.SpinStep(m, 0));
    }
}
