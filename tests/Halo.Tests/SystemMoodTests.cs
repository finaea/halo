using System.Diagnostics;
using Halo.Metrics;
using Halo.Widgets;
using Halo.Widgets.Harness;

namespace Halo.Tests;

/// <summary>SystemMood driven directly: states, dwell, events, rate limits and the Asleep silence.
/// Pure — a fixture or a tiny override source, and a synthetic QPC at 0.2 s per tick.</summary>
public sealed class SystemMoodTests
{
    private static readonly long Tick = Stopwatch.Frequency / 5;
    private static readonly DateTime Wall = new(2026, 3, 14, 12, 0, 0);

    /// <summary>The idle fixture at its last point, with readings overridden and the collector
    /// switchable — events need a value that holds still, not a fixture's series.</summary>
    private sealed class Fake : IMetricSource
    {
        private readonly FixtureMetrics _f = FixtureMetrics.Load("idle");
        public readonly Dictionary<string, double> Set = new();
        public readonly Dictionary<string, string> Texts = new();
        public bool Down;
        public Fake() => _f.Advance(1.0, 1000);
        public bool Stale => Down;
        public double Value(string n, double d = 0) => Set.TryGetValue(n, out double v) ? v : _f.Value(n, d);
        public bool TryValue(string n, out double v, double maxAgeS = double.MaxValue)
            => Set.TryGetValue(n, out v) || _f.TryValue(n, out v, maxAgeS);
        public bool Has(string n) => Set.ContainsKey(n) || _f.Has(n);
        public string Text(string n, string d = "") => Texts.TryGetValue(n, out var t) ? t : _f.Text(n, d);
        public ReadOnlySpan<FrameEntry> NewFrames => default;
        public IReadOnlyList<MetricInfo> Describe() => _f.Describe();
    }

    /// <summary>Feeds ticks and keeps every message ever posted (Messages only holds the last 30).</summary>
    private sealed class Run
    {
        public readonly SystemMood Mood;
        public readonly List<(long Qpc, MoodMessage M)> Posts = new();
        public long K;
        /// <summary>Added to the wall clock — a jump forward (a date rollover) without waiting for it.</summary>
        public TimeSpan Skip;
        private readonly DateTime _start;
        private long _seen;

        public Run(DateTime? start = null, SystemMood? mood = null)
        {
            _start = start ?? Wall;
            Mood = mood ?? new SystemMood();
        }

        public void Feed(IMetricSource m, double seconds) => Feed(m, seconds, null);

        public void Feed(IMetricSource m, double seconds, Action<long>? before)
        {
            int n = (int)Math.Round(seconds / 0.2);
            for (int i = 0; i < n; i++, K++)
            {
                before?.Invoke(K);
                Mood.Update(m, K * Tick, _start.Add(Skip).AddSeconds(K * 0.2));
                foreach (var msg in Mood.Messages.Where(x => x.Seq > _seen).ToList())
                {
                    Posts.Add((K * Tick, msg));
                    _seen = msg.Seq;
                }
            }
        }
    }

    private static MoodState Final(string fixture)
    {
        var f = FixtureMetrics.Load(fixture);
        var mood = new SystemMood();
        int ticks = f.DurationS is { } d ? (int)Math.Ceiling(d / 0.2) : PanelRenderer.Ticks;
        for (int k = 0; k < ticks; k++)
        {
            long qpc = k * Tick;
            f.Advance((double)k / (ticks - 1), qpc);
            mood.Update(f, qpc, Wall.AddSeconds(k * 0.2));
        }
        return mood.State;
    }

    // ---- 1. states ----

    [Theory]
    [InlineData("idle", MoodState.Idle)]
    [InlineData("mood-busy", MoodState.Busy)]
    [InlineData("mood-gaming", MoodState.Gaming)]
    [InlineData("mood-hot", MoodState.Hot)]
    [InlineData("hot", MoodState.Critical)]
    [InlineData("na", MoodState.Asleep)]
    public void Fixture_ends_in_its_state(string fixture, MoodState expected)
        => Assert.Equal(expected, Final(fixture));

    [Fact]
    public void Hot_names_the_gpu()
    {
        var f = FixtureMetrics.Load("mood-hot");
        var mood = new SystemMood();
        for (int k = 0; k < PanelRenderer.Ticks; k++)
        {
            f.Advance((double)k / (PanelRenderer.Ticks - 1), k * Tick);
            mood.Update(f, k * Tick, Wall);
        }
        Assert.Equal(MoodState.Hot, mood.State);
        Assert.Equal("GPU", mood.HotPart);
        Assert.Contains("GPU", mood.Status);
    }

    [Fact]
    public void A_flip_shorter_than_the_dwell_is_not_adopted()
    {
        var fake = new Fake();
        var run = new Run();
        run.Feed(fake, 5);
        Assert.Equal(MoodState.Idle, run.Mood.State);

        fake.Set["cpu.total.pct"] = 99;
        run.Feed(fake, 1.4);                       // < 3 s
        Assert.Equal(MoodState.Idle, run.Mood.State);
        fake.Set["cpu.total.pct"] = 2;
        run.Feed(fake, 5);
        Assert.Equal(MoodState.Idle, run.Mood.State);

        fake.Set["cpu.total.pct"] = 99;
        run.Feed(fake, 4);                         // held past the dwell
        Assert.Equal(MoodState.Busy, run.Mood.State);
    }

    // ---- 2. events ----

    [Fact]
    public void Ram_posts_going_over_and_coming_back_and_opens_a_relief_window()
    {
        var fake = new Fake();
        var run = new Run();
        fake.Set["ram.pct"] = 60;
        run.Feed(fake, 2);
        fake.Set["ram.pct"] = 93;
        run.Feed(fake, 1);
        Assert.Contains(run.Posts, p => p.M is { Topic: MoodTopic.Ram, Key: "high" });
        Assert.True(run.Mood.RamHigh);

        fake.Set["ram.pct"] = 70;
        run.Feed(fake, 70);                        // past the 60 s topic cooldown
        var back = Assert.Single(run.Posts, p => p.M is { Topic: MoodTopic.Ram, Key: "back" });
        Assert.False(run.Mood.RamHigh);
        Assert.True(run.Mood.RamRelief(back.Qpc + Tick));
        Assert.False(run.Mood.RamRelief(back.Qpc + 31 * Stopwatch.Frequency));
    }

    [Fact]
    public void Quiet_network_posts_only_after_sixty_seconds()
    {
        var fake = new Fake();
        var run = new Run();
        fake.Set["net.down.bps"] = 0;
        fake.Set["net.up.bps"] = 0;
        run.Feed(fake, 55);
        Assert.DoesNotContain(run.Posts, p => p.M.Key == "quiet");
        run.Feed(fake, 10);
        Assert.Single(run.Posts, p => p.M is { Topic: MoodTopic.Network, Key: "quiet" });
        Assert.True(run.Mood.NetQuiet);
    }

    [Fact]
    public void Uptime_milestone_posts_once()
    {
        var fake = new Fake();
        var run = new Run();
        fake.Set["sys.uptime.s"] = 604_700;
        run.Feed(fake, 2);
        Assert.DoesNotContain(run.Posts, p => p.M.Topic == MoodTopic.Uptime);
        fake.Set["sys.uptime.s"] = 604_900;
        run.Feed(fake, 20);
        var m = Assert.Single(run.Posts, p => p.M.Topic == MoodTopic.Uptime);
        Assert.Equal("7", m.M.Arg);
        Assert.True(run.Mood.Celebrating(run.K * Tick));
    }

    // ---- 3. rate limits ----

    [Fact]
    public void Flapping_ram_is_rate_limited_and_ends_consistent()
    {
        var f = FixtureMetrics.Load("mood-ram-flap");
        var run = new Run();
        int ticks = (int)Math.Ceiling(f.DurationS!.Value / 0.2);
        run.Feed(f, ticks * 0.2, k => f.Advance((double)k / (ticks - 1), k * Tick));

        // a RAM-high duet's reply (Pair 2) is a second Ram post 3.5 s after the opener, by design
        var ram = run.Posts.Where(p => p.M.Topic == MoodTopic.Ram && p.M.Pair != 2).ToList();
        Assert.NotEmpty(ram);
        Assert.True(ram.Count <= (int)Math.Ceiling(300 / SystemMood.TopicCooldownS) + 1, $"{ram.Count} ram messages");
        for (int i = 1; i < ram.Count; i++)
            Assert.True(ram[i].Qpc - ram[i - 1].Qpc >= (long)(SystemMood.TopicCooldownS * Stopwatch.Frequency),
                "ram posts closer than the topic cooldown");

        // pokes, replies and the day's greeting never start the global gap, so they are not held to it
        var all = run.Posts.Where(p => p.M.Topic != MoodTopic.Poke && p.M.Pair != 2 && p.M.Topic != MoodTopic.Greeting).ToList();
        for (int i = 1; i < all.Count; i++)
            Assert.True(all[i].Qpc - all[i - 1].Qpc >= (long)(SystemMood.GlobalGapS * Stopwatch.Frequency),
                "posts closer than the global gap");

        // level-triggered: what was last said matches the flag the faces read
        Assert.Equal(ram[^1].M.Key == "high", run.Mood.RamHigh);
    }

    // ---- 4. asleep ----

    [Fact]
    public void Asleep_is_silent_clears_the_thread_and_wakes_with_one_back()
    {
        var fake = new Fake();
        var run = new Run();
        fake.Set["ram.pct"] = 93;
        run.Feed(fake, 3);
        Assert.NotEmpty(run.Mood.Messages);

        fake.Down = true;
        run.Feed(fake, 1);
        Assert.Equal(MoodState.Asleep, run.Mood.State);
        Assert.Empty(run.Mood.Messages);
        Assert.Equal(0, run.Mood.Unread);

        int before = run.Posts.Count;
        fake.Set["ram.pct"] = 50;
        run.Feed(fake, 20);
        run.Mood.Poke("arona", run.K * Tick);
        Assert.Empty(run.Mood.Messages);
        Assert.Equal(before, run.Posts.Count);

        fake.Down = false;
        run.Feed(fake, 0.2);
        var msg = Assert.Single(run.Mood.Messages);
        Assert.Equal(MoodTopic.System, msg.Topic);
        Assert.Equal("back", msg.Key);
    }

    [Fact]
    public void Poke_while_awake_posts_a_poke()
    {
        var fake = new Fake();
        var run = new Run();
        run.Feed(fake, 2);
        run.Mood.Poke("arona", run.K * Tick);
        Assert.Contains(run.Mood.Messages, m => m.Topic == MoodTopic.Poke && m.Who == "arona");
        Assert.True(run.Mood.Poked("arona", run.K * Tick));
    }

    // ---- 5. poke streaks ----

    private static int TierOf(MoodMessage m) => int.Parse(m.Arg.Split('|')[1]);

    private static List<int> PokeTiers(Run run) => run.Posts.Where(p => p.M.Topic == MoodTopic.Poke).Select(p => TierOf(p.M)).ToList();

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 2)]
    [InlineData(6, 3)]
    [InlineData(9, 3)]
    [InlineData(10, 4)]
    [InlineData(50, 4)]
    public void PokeTier_maps_the_streak_count(int n, int tier) => Assert.Equal(tier, SystemMood.PokeTier(n));

    [Fact]
    public void Poke_streak_climbs_the_tiers_and_resets_after_a_quiet_spell()
    {
        var run = new Run();
        var fake = new Fake();
        run.Feed(fake, 2);
        for (int i = 0; i < 10; i++)
        {
            run.Mood.Poke("arona", run.K * Tick);
            run.Feed(fake, 2.2);
        }
        Assert.Equal([1, 1, 2, 2, 2, 3, 3, 3, 3, 4], PokeTiers(run));

        run.Feed(fake, SystemMood.PokeStreakS + 1);      // the quiet spell
        run.Mood.Poke("arona", run.K * Tick);
        run.Feed(fake, 0.2);
        Assert.Equal(1, PokeTiers(run)[^1]);
    }

    [Fact]
    public void Poke_streaks_are_per_character()
    {
        var run = new Run();
        var fake = new Fake();
        run.Feed(fake, 2);
        for (int i = 0; i < 4; i++) { run.Mood.Poke("arona", run.K * Tick); run.Feed(fake, 2.2); }
        run.Mood.Poke("plana", run.K * Tick);
        run.Feed(fake, 0.2);
        Assert.Equal(1, PokeTiers(run)[^1]);
    }

    [Fact]
    public void A_poke_inside_the_cooldown_is_ignored_and_the_arg_is_seed_and_tier()
    {
        var run = new Run();
        var fake = new Fake();
        run.Feed(fake, 2);
        run.Mood.Poke("arona", run.K * Tick);
        run.Feed(fake, 1.0);                              // < PokeCooldownS
        run.Mood.Poke("arona", run.K * Tick);
        run.Feed(fake, 0.2);
        var poke = Assert.Single(run.Posts, p => p.M.Topic == MoodTopic.Poke);
        var parts = poke.M.Arg.Split('|');
        Assert.Equal(2, parts.Length);
        Assert.True(int.TryParse(parts[0], out _));
        Assert.Equal("1", parts[1]);
        Assert.Equal("arona", poke.M.Who);
    }

    // ---- 6. greetings, late nights ----

    private static List<MoodMessage> Of(Run run, MoodTopic topic) => run.Posts.Where(p => p.M.Topic == topic).Select(p => p.M).ToList();

    [Theory]
    [InlineData(8, "morning")]
    [InlineData(11, "morning")]
    [InlineData(12, "afternoon")]
    [InlineData(17, "afternoon")]
    [InlineData(18, "evening")]
    [InlineData(22, "evening")]
    [InlineData(23, "late")]
    [InlineData(0, "late")]
    [InlineData(4, "late")]
    [InlineData(5, "morning")]
    public void First_awake_tick_greets_for_the_hour(int hour, string key)
    {
        var run = new Run(new DateTime(2026, 3, 15, hour, 5, 0));
        run.Feed(new Fake(), 1);
        var g = Assert.Single(Of(run, MoodTopic.Greeting));
        Assert.Equal(key, g.Key);
        Assert.Equal($"{hour:00}:05", g.Arg);
    }

    [Fact]
    public void Greeting_comes_once_per_date_and_again_on_rollover()
    {
        var run = new Run(new DateTime(2026, 3, 15, 20, 0, 0));
        var fake = new Fake();
        run.Feed(fake, 600);
        Assert.Equal(["evening"], Of(run, MoodTopic.Greeting).Select(m => m.Key));

        run.Skip = TimeSpan.FromHours(12);                // 08:10 the next day
        run.Feed(fake, 5);
        run.Feed(fake, 600);
        Assert.Equal(["evening", "morning"], Of(run, MoodTopic.Greeting).Select(m => m.Key));
    }

    [Fact]
    public void Late_night_nudges_at_most_once_per_clock_hour()
    {
        var run = new Run(new DateTime(2026, 3, 15, 1, 10, 0));
        var fake = new Fake();
        run.Feed(fake, 40 * 60);                          // 01:10 → 01:50, still hour 1
        Assert.Single(Of(run, MoodTopic.Greeting), m => m.Key == "late");
        run.Feed(fake, 20 * 60);                          // into hour 2
        Assert.Equal(2, Of(run, MoodTopic.Greeting).Count(m => m.Key == "late"));
    }

    [Fact]
    public void No_late_nudge_once_the_night_is_over()
    {
        var run = new Run(new DateTime(2026, 3, 15, 4, 30, 0));
        run.Feed(new Fake(), 40 * 60);                    // across 05:00
        Assert.Single(Of(run, MoodTopic.Greeting));
    }

    [Fact]
    public void Greetings_and_pokes_do_not_start_the_global_gap()
    {
        var run = new Run();
        var fake = new Fake();
        fake.Set["ram.pct"] = 60;
        run.Feed(fake, 1);
        Assert.Single(Of(run, MoodTopic.Greeting));
        fake.Set["ram.pct"] = 93;                         // news right behind the greeting
        run.Feed(fake, 0.4);
        Assert.Contains(run.Posts, p => p.M is { Topic: MoodTopic.Ram, Key: "high" });
    }

    // ---- 7. birthdays and holidays ----

    private static SystemMood Born(string md, params string[] who) => new(null, new Dictionary<string, string[]> { [md] = who });

    [Fact]
    public void A_birthday_posts_once_for_the_student()
    {
        var run = new Run(Wall, Born("03-14", "yuuka"));
        run.Feed(new Fake(), 600);
        var b = Assert.Single(Of(run, MoodTopic.Date));
        Assert.Equal(("birthday", "yuuka", 0), (b.Key, b.Who, b.Pair));
        Assert.Empty(Of(run, MoodTopic.Greeting));         // the birthday is the day's first message
    }

    [Fact]
    public void Another_date_has_no_birthday()
    {
        var run = new Run(Wall.AddDays(1), Born("03-14", "yuuka"));
        run.Feed(new Fake(), 5);
        Assert.Empty(Of(run, MoodTopic.Date));
        Assert.Single(Of(run, MoodTopic.Greeting));
    }

    [Fact]
    public void Twin_birthdays_open_with_the_first_and_the_second_answers_later()
    {
        var run = new Run(Wall, Born("03-14", "momoi", "midori"));
        run.Feed(new Fake(), 10);
        var posts = run.Posts.Where(p => p.M.Topic == MoodTopic.Date).ToList();
        Assert.Equal(2, posts.Count);
        Assert.Equal(("momoi", 1), (posts[0].M.Who, posts[0].M.Pair));
        Assert.Equal(("midori", 2), (posts[1].M.Who, posts[1].M.Pair));
        Assert.Equal("birthday", posts[1].M.Key);
        double gapS = (posts[1].Qpc - posts[0].Qpc) / (double)Stopwatch.Frequency;
        Assert.InRange(gapS, SystemMood.ReplyS, SystemMood.ReplyS + 0.45);
    }

    [Theory]
    [InlineData(1, 1, "newyear")]
    [InlineData(12, 24, "xmas")]
    [InlineData(12, 25, "xmas")]
    [InlineData(10, 31, "halloween")]
    public void Holidays_post_once(int month, int day, string key)
    {
        var run = new Run(new DateTime(2026, month, day, 9, 0, 0));
        run.Feed(new Fake(), 600);
        var d = Assert.Single(Of(run, MoodTopic.Date));
        Assert.Equal(key, d.Key);
        Assert.Empty(Of(run, MoodTopic.Greeting));
    }

    [Fact]
    public void A_birthday_outranks_a_holiday()
    {
        var run = new Run(new DateTime(2026, 12, 25, 9, 0, 0), Born("12-25", "noa"));
        run.Feed(new Fake(), 5);
        Assert.Equal("birthday", Assert.Single(Of(run, MoodTopic.Date)).Key);
    }

    // ---- 8. game over ----

    private static void Play(Fake fake, string app)
    {
        fake.Set[MetricNames.FpsPresented] = 60;
        fake.Texts[MetricNames.FpsAppName] = app;
    }

    [Fact]
    public void A_long_game_ends_with_gg_the_minutes_and_a_tidy_name()
    {
        var run = new Run();
        var fake = new Fake();
        run.Feed(fake, 2);
        Play(fake, "Cyberpunk2077.exe");
        run.Feed(fake, 21 * 60);
        Assert.DoesNotContain(run.Posts, p => p.M.Key == "gameover");     // still playing
        fake.Set.Remove(MetricNames.FpsPresented);
        run.Feed(fake, SystemMood.GameEndS + 5);
        var gg = Assert.Single(run.Posts, p => p.M is { Topic: MoodTopic.Fps, Key: "gameover" });
        Assert.Equal("Cyberpunk2077|21", gg.M.Arg);
    }

    [Fact]
    public void A_short_game_is_silent()
    {
        var run = new Run();
        var fake = new Fake();
        run.Feed(fake, 2);
        Play(fake, "launcher.exe");
        run.Feed(fake, 10 * 60);
        fake.Set.Remove(MetricNames.FpsPresented);
        run.Feed(fake, 60);
        Assert.DoesNotContain(run.Posts, p => p.M.Key == "gameover");
    }

    [Fact]
    public void An_alt_tab_shorter_than_the_end_delay_is_not_the_end()
    {
        var run = new Run();
        var fake = new Fake();
        run.Feed(fake, 2);
        Play(fake, "game.exe");
        run.Feed(fake, 21 * 60);
        fake.Set.Remove(MetricNames.FpsPresented);
        run.Feed(fake, SystemMood.GameEndS - 4);
        Play(fake, "game.exe");
        run.Feed(fake, 30);
        Assert.DoesNotContain(run.Posts, p => p.M.Key == "gameover");
    }

    [Theory]
    [InlineData("Game.exe", "Game")]
    [InlineData("Game.EXE", "Game")]
    [InlineData("Game", "Game")]
    [InlineData("exe", "exe")]
    public void Tidy_strips_the_exe(string raw, string tidy) => Assert.Equal(tidy, SystemMood.Tidy(raw));

    // ---- 9. chatter ----

    [Fact]
    public void Chatter_waits_for_its_cooldown_after_waking_and_keeps_its_spacing()
    {
        var run = new Run();
        var fake = new Fake();
        run.Feed(fake, SystemMood.ChatterS - 60);
        Assert.Empty(Of(run, MoodTopic.Chatter));
        run.Feed(fake, 90 * 60);
        var chat = run.Posts.Where(p => p.M.Topic == MoodTopic.Chatter).ToList();
        Assert.True(chat.Count >= 2, $"{chat.Count} chatter posts");
        Assert.True(chat[0].Qpc >= (long)(SystemMood.ChatterS * Stopwatch.Frequency), "first chatter before the cooldown");
        for (int i = 1; i < chat.Count; i++)
            Assert.True(chat[i].Qpc - chat[i - 1].Qpc >= (long)(SystemMood.ChatterS * Stopwatch.Frequency), "chatter closer than its cooldown");
        Assert.All(chat, c => Assert.Equal("idle", c.M.Key));
    }

    [Fact]
    public void Chatter_only_fills_an_idle_machine()
    {
        var busy = new Run();
        var fake = new Fake();
        fake.Set["cpu.total.pct"] = 99;
        busy.Feed(fake, 60 * 60);
        Assert.Equal(MoodState.Busy, busy.Mood.State);
        Assert.Empty(Of(busy, MoodTopic.Chatter));

        var gaming = new Run();
        var g = new Fake();
        Play(g, "game.exe");
        gaming.Feed(g, 60 * 60);
        Assert.Empty(Of(gaming, MoodTopic.Chatter));
    }

    // ---- 10. cross-talk ----

    [Fact]
    public void The_first_duet_event_posts_an_opener_and_a_reply_later_and_the_third_again()
    {
        var run = new Run();
        var fake = new Fake();
        fake.Set["ram.pct"] = 60;
        run.Feed(fake, 2);
        fake.Set["ram.pct"] = 93;
        run.Feed(fake, 10);
        var ram = run.Posts.Where(p => p.M.Topic == MoodTopic.Ram).ToList();
        Assert.Equal([1, 2], ram.Select(p => p.M.Pair));
        Assert.Equal((ram[0].M.Key, ram[0].M.Arg), (ram[1].M.Key, ram[1].M.Arg));
        double gapS = (ram[1].Qpc - ram[0].Qpc) / (double)Stopwatch.Frequency;
        Assert.InRange(gapS, SystemMood.ReplyS, SystemMood.ReplyS + 0.45);

        // the next duet-capable event (the 2nd high) stands alone, so there is still just the one reply
        fake.Set["ram.pct"] = 70;
        run.Feed(fake, 70);
        fake.Set["ram.pct"] = 93;
        run.Feed(fake, 70);
        Assert.Equal(1, run.Posts.Count(p => p.M is { Topic: MoodTopic.Ram, Pair: 2 }));
        Assert.Equal(0, run.Posts.Last(p => p.M is { Topic: MoodTopic.Ram, Key: "high" }).M.Pair);
    }

    [Fact]
    public void A_reply_is_dropped_when_the_collector_goes_away()
    {
        var run = new Run();
        var fake = new Fake();
        fake.Set["ram.pct"] = 60;
        run.Feed(fake, 2);
        fake.Set["ram.pct"] = 93;
        run.Feed(fake, 1);                                // opener out, reply still pending
        Assert.Contains(run.Posts, p => p.M is { Topic: MoodTopic.Ram, Pair: 1 });
        fake.Down = true;
        run.Feed(fake, 1);
        fake.Down = false;
        run.Feed(fake, 15);
        Assert.DoesNotContain(run.Posts, p => p.M.Pair == 2);
    }

    [Fact]
    public void A_critical_warning_drops_a_queued_reply_so_it_is_the_last_thing_said()
    {
        var run = new Run();
        var fake = new Fake();
        fake.Set["cpu.package.temp.c"] = 52;
        run.Feed(fake, 30);
        fake.Set["cpu.package.temp.c"] = 75;
        while (!run.Posts.Any(p => p.M is { Topic: MoodTopic.System, Key: "hot" }) && run.K < 400) run.Feed(fake, 0.2);
        Assert.Contains(run.Posts, p => p.M is { Key: "hot", Pair: 1 });      // the first duet-capable event opens one

        fake.Set["cpu.package.temp.c"] = 95;                                  // critical lands inside the reply's 3.5 s
        run.Feed(fake, 10);                                                   // well past where the reply would have fired

        Assert.Equal(MoodState.Critical, run.Mood.State);
        Assert.Contains(run.Posts, p => p.M is { Key: "critical" });
        Assert.DoesNotContain(run.Posts, p => p.M.Pair == 2);
        Assert.Equal("critical", run.Posts[^1].M.Key);
    }

    [Fact]
    public void A_reply_still_lands_after_an_event_that_does_not_escalate()
    {
        var run = new Run();
        var fake = new Fake();
        fake.Set["cpu.package.temp.c"] = 52;
        run.Feed(fake, 30);
        fake.Set["cpu.package.temp.c"] = 75;
        run.Feed(fake, 15);
        Assert.Equal([1, 2], run.Posts.Where(p => p.M.Key == "hot").Select(p => p.M.Pair));
    }
}
