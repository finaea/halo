using System.Globalization;
using Halo.Metrics;
using Halo.Shared.Config;
using Halo.Shared.Panels;
using Halo.Widgets.PanelModels;

namespace Halo.Widgets;

/// <summary>The machine's mood as a whole, highest priority first in <see cref="SystemMood.Candidate"/>.</summary>
public enum MoodState { Asleep, Idle, Busy, Gaming, Hot, Critical }

/// <summary>Who a message is about. A skin maps a topic to the character who owns it.
/// <see cref="Greeting"/> is the time of day (the day's first message, late-night nudges),
/// <see cref="Date"/> a holiday or a birthday, <see cref="Chatter"/> small talk on a quiet machine.</summary>
public enum MoodTopic { System, Ram, Fps, Fans, Network, Uptime, Poke, Greeting, Date, Chatter }

/// <summary>
/// One chat-worthy happening. <see cref="Key"/> names the line ("hot", "high", "quiet"…) and
/// <see cref="Arg"/> carries its numbers or names, '|'-separated and invariant-formatted, so the copy
/// deck that turns it into words lives in the skin and this stays skin-agnostic. <see cref="Who"/>
/// names a sender only where the event itself is about someone (a poke, a birthday); otherwise the
/// skin decides. <see cref="Pair"/>: 0 = on its own, 1 = opens a two-message exchange, 2 = the
/// reply to it — same topic, key and arg, posted <see cref="SystemMood.ReplyS"/> later.
/// </summary>
public sealed record MoodMessage(long Seq, MoodTopic Topic, string Key, string Arg, DateTime At, string? Who = null, int Pair = 0);

/// <summary>
/// The "fun" signal (skin tech plan §5): one per process, computed once per master tick right after
/// <c>MetricCache.Tick</c>, read by every widget through <see cref="Render.PanelContext.Mood"/>.
/// Skin-agnostic — Rainformer's companion only prints the state, Azur Archive puts faces and a
/// MomoTalk thread on it.
///
/// <para><b>Nothing here invents a threshold.</b> Hot and Critical are warn steps 4 and 5 of the
/// temperature rows (the user's thresholds on an enabled widget, else the catalog's), Busy is the
/// CPU usage warn point, a frametime spike is a frame slower than the FPS row's lowest step. The
/// two spec numbers are the design's own: RAM ≥ 90 % (design §9.5) and the 1/7/30-day milestones.</para>
///
/// <para><b>Rate limits are level-triggered.</b> Each topic remembers what it last <i>announced</i>
/// and posts only when the live condition differs from that and both the topic's cooldown and the
/// global gap have passed. A reading that flaps across a line therefore produces at most one
/// message per cooldown, and the last thing said always ends up matching the machine — a dropped
/// edge would leave "over budget" standing after the RAM came back. Faces follow the announced
/// value too, so they cannot flap either. Only frametime spikes and network bursts are edges; a
/// missed one is simply not news any more.</para>
///
/// <para><b>Asleep is silent.</b> With the collector gone there is no reading to talk about, so the
/// thread is cleared and nothing is posted until it is back — never an "all good" with no data.</para>
///
/// <para><b>Small talk never holds up news.</b> The day's greeting, a poke and a reply do not start
/// the global gap, so a reading that changes at start-up is not kept waiting behind a "good
/// morning". Chatter only fills silence: Idle, nothing said for <see cref="ChatterQuietS"/>, and its
/// own <see cref="ChatterS"/> cooldown.</para>
/// </summary>
public sealed class SystemMood
{
    // ---- rate limits (seconds) ----
    public const double GlobalGapS = 8;
    public const double TopicCooldownS = 60;
    public const double SpikeCooldownS = 120;
    public const double BurstCooldownS = 300;
    /// <summary>A pokes' own spacing: the user asked, so the global gap does not apply.</summary>
    public const double PokeCooldownS = 2;

    // ---- timing of the states and reactions ----
    /// <summary>A new state must hold this long before it is adopted, so a load hovering on the
    /// Busy line does not swap the host's face every tick. Asleep (and waking) skip it.</summary>
    public const double DwellS = 3;
    /// <summary>How long zero traffic must last before it counts as "sustained".</summary>
    public const double QuietAfterS = 60;
    /// <summary>A new session peak right after start is every reading; bursts wait this long.</summary>
    public const double WarmupS = 60;
    /// <summary>How long a one-shot reaction (relieved, happy) stays on a face.</summary>
    public const double ReactS = 30;
    public const double PokeS = 4;

    /// <summary>Design §9.5's line for the memory budget.</summary>
    public const double RamHighPct = 90;
    private static readonly double[] MilestonesS = [86400, 7 * 86400, 30 * 86400];
    /// <summary>Messages kept for the thread: the zone shows the newest few, and the wheel scrolls
    /// back through the rest.</summary>
    public const int Keep = 30;

    // ---- the talk around the readings ----
    /// <summary>Pokes on one character closer than this make a streak; a longer pause resets it.</summary>
    public const double PokeStreakS = 12;
    /// <summary>A reply's delay after the message it answers.</summary>
    public const double ReplyS = 3.5;
    /// <summary>Every this-many-th exchange-capable event (spike, RAM high, hot, burst) gets a reply.</summary>
    public const int DuetEvery = 3;
    /// <summary>A game counts as over once nothing has presented for this long (alt-tab is not an end).</summary>
    public const double GameEndS = 10;
    /// <summary>"GG" only after a session at least this long; a quick launcher check is not a game.</summary>
    public const double GameOverMinS = 20 * 60;
    /// <summary>Chatter's own cooldown, and how long the thread must have been silent first.</summary>
    public const double ChatterS = 25 * 60, ChatterQuietS = 5 * 60;
    /// <summary>Late night is 23:00–04:59, at most one nudge per clock hour.</summary>
    public static bool LateHour(int hour) => hour >= 23 || hour < 5;

    private static readonly long Qpf = System.Diagnostics.Stopwatch.Frequency;

    private readonly Func<IReadOnlyList<WidgetInstance>> _widgets;
    private readonly IReadOnlyDictionary<string, string[]> _birthdays;
    private readonly List<MoodMessage> _messages = new();
    private long _seq;
    private long _now;
    private DateTime _wall;

    /// <param name="widgets">Every widget, for the warn thresholds the user set.</param>
    /// <param name="birthdays">"MM-dd" → who has a birthday that day (a skin's cast); none by default.</param>
    public SystemMood(Func<IReadOnlyList<WidgetInstance>>? widgets = null, IReadOnlyDictionary<string, string[]>? birthdays = null)
    {
        _widgets = widgets ?? (() => []);
        _birthdays = birthdays ?? new Dictionary<string, string[]>();
    }

    /// <summary>Every widget, as the mood sees them — a skin reads who sits on which card.</summary>
    public IReadOnlyList<WidgetInstance> Widgets => _widgets();

    /// <summary>False until the first <see cref="Update"/>; a context with no mood behind it (a
    /// test, a tool) then shows every card at rest rather than reacting to a made-up state.</summary>
    public bool Known { get; private set; }
    public MoodState State { get; private set; } = MoodState.Asleep;

    /// <summary>The part that made it Hot/Critical ("CPU", "GPU", "GPU 2") and its °C.</summary>
    public string HotPart { get; private set; } = "";
    public double HotC { get; private set; }
    /// <summary>A 3D app is presenting right now, whatever the state says — a hot GPU outranks
    /// Gaming in <see cref="State"/>, but the game is still running.</summary>
    public bool Presenting { get; private set; }

    /// <summary>The presenting app while <see cref="MoodState.Gaming"/>.</summary>
    public string App { get; private set; } = "";

    /// <summary>Announced (rate-limited) conditions — what the faces show.</summary>
    public bool RamHigh => _ram.Announced;
    public bool NetQuiet => _quiet.Announced;

    public IReadOnlyList<MoodMessage> Messages => _messages;
    /// <summary>Messages since the user last looked (<see cref="Glance"/>).</summary>
    public int Unread { get; private set; }
    /// <summary>When the newest message was posted (QPC), or 0.</summary>
    public long LastPostQpc { get; private set; }

    // ---- per-topic state ----
    private sealed class Topic
    {
        public bool Announced;
        public long Last = long.MinValue / 2;
    }

    private readonly Topic _ram = new(), _quiet = new(), _fans = new(), _sys = new(), _spike = new(), _burst = new(), _uptime = new(),
        _late = new(), _gg = new(), _chatter = new();
    private MoodState _sysAnnounced;
    private int _milestone = -1;
    private bool _spikeOn, _burstOn;
    private long _zeroSince = -1, _awakeSince, _lastAny = long.MinValue / 2, _lastPoke = long.MinValue / 2;
    private bool _awake, _sawAsleep;
    private MoodState _pending;
    private long _pendingSince;
    private long _reliefUntil, _happyUntil, _pokeUntil;
    private string _pokeWho = "";
    private readonly Dictionary<string, (int Count, long Last)> _streaks = new(StringComparer.Ordinal);
    private DateOnly _greeted;
    private (DateOnly, int) _lateNudged = (default, -1);
    private long _gameSince = -1, _gameSeen;
    private string _gameApp = "";
    private string? _ggArg;
    private int _duets;
    private readonly List<(long Due, MoodMessage Msg)> _replies = new();

    /// <summary>The state in a few plain words, the same in every skin: "ALL SYSTEMS NOMINAL",
    /// "GAMING · CYBERPUNK2077.EXE", "GPU RUNNING HOT". Empty before the first update.</summary>
    public string Status => !Known ? "" : State switch
    {
        MoodState.Asleep => "WAITING FOR COLLECTOR",
        MoodState.Critical => $"{HotPart} CRITICAL",
        MoodState.Hot => $"{HotPart} RUNNING HOT",
        MoodState.Gaming => App.Length > 0 ? "GAMING · " + App.ToUpperInvariant() : "GAMING",
        MoodState.Busy => "WORKING HARD",
        _ => "ALL SYSTEMS NOMINAL",
    };

    // ---- reactions a skin asks about ----

    public bool RamRelief(long nowQpc) => nowQpc < _reliefUntil;
    public bool Celebrating(long nowQpc) => nowQpc < _happyUntil;
    public bool Poked(string who, long nowQpc) => nowQpc < _pokeUntil && who == _pokeWho;

    /// <summary>The user looked at the thread: the unread badge goes.</summary>
    public void Glance() => Unread = 0;

    /// <summary>
    /// A click without drag on <paramref name="who"/> (her face, or one of her bubbles): she looks
    /// surprised for a moment and says a random line from her deck — a crosser one the more she is
    /// poked in a row. Arg is <c>seed|tier</c>. Not while asleep — nobody is there to answer.
    /// </summary>
    public void Poke(string who, long nowQpc)
    {
        if (!Known || State == MoodState.Asleep || nowQpc - _lastPoke < S(PokeCooldownS)) return;
        _lastPoke = nowQpc;
        _pokeWho = who;
        _pokeUntil = nowQpc + S(PokeS);
        _now = nowQpc;
        var (count, last) = _streaks.GetValueOrDefault(who);
        count = nowQpc - last > S(PokeStreakS) ? 1 : count + 1;
        _streaks[who] = (count, nowQpc);
        Post(MoodTopic.Poke, "poke", $"{Random.Shared.Next(1000).ToString(CultureInfo.InvariantCulture)}|{PokeTier(count)}", who: who);
    }

    /// <summary>The n-th poke in a row → tier 1 (normal, 1st–2nd), 2 (annoyed, 3rd–5th),
    /// 3 (flustered, 6th–9th), 4 (gives up, 10th on).</summary>
    public static int PokeTier(int n) => n switch { <= 2 => 1, <= 5 => 2, <= 9 => 3, _ => 4 };

    // ---- the tick ----

    public void Update(IMetricSource m, long nowQpc, DateTime now)
    {
        _now = nowQpc;
        _wall = now;
        Known = true;

        if (m.Stale)
        {
            // silent: the thread goes with the data, and nothing is said until it is back
            if (State != MoodState.Asleep) { _messages.Clear(); Unread = 0; }
            State = MoodState.Asleep;
            _awake = false;
            _sawAsleep = true;
            _replies.Clear();
            _gameSince = -1;
            _ggArg = null;
            return;
        }

        var candidate = Candidate(m);
        if (!_awake)
        {
            // waking (or the first tick): adopt the state as it is and say nothing about levels
            // that were already true, except that the collector is back
            _awake = true;
            _awakeSince = nowQpc;
            State = candidate;
            _pending = candidate;
            _sysAnnounced = Category(candidate);
            _quiet.Announced = false;
            _zeroSince = -1;
            _milestone = Milestone(m);
            _spikeOn = _burstOn = false;
            _chatter.Last = nowQpc;
            if (_sawAsleep) Post(MoodTopic.System, "back", "");
        }
        else if (candidate != State)
        {
            if (candidate != _pending) { _pending = candidate; _pendingSince = nowQpc; }
            else if (nowQpc - _pendingSince >= S(DwellS)) State = candidate;
        }
        else _pending = State;

        Greet();
        Announce(m);
        Game();
        Chatter();
        while (_replies.Count > 0 && _replies[0].Due <= _now)
        {
            var r = _replies[0].Msg;
            _replies.RemoveAt(0);
            Post(r.Topic, r.Key, r.Arg, who: r.Who, pair: 2);
        }
    }

    // ---- the clock: the day's first message, late nights ----

    /// <summary>
    /// Once per process per date — the first awake tick and every date rollover: a birthday (the
    /// students born today, a second one answering the first), else a holiday, else a greeting for
    /// the time of day. At night that greeting is the hour's late-night nudge, and every later late
    /// hour gets one more, rate-limited like any other topic.
    /// </summary>
    private void Greet()
    {
        var today = DateOnly.FromDateTime(_wall);
        int hour = _wall.Hour;
        if (today != _greeted)
        {
            _greeted = today;
            string md = _wall.ToString("MM-dd", CultureInfo.InvariantCulture);
            if (_birthdays.TryGetValue(md, out var born) && born.Length > 0)
            {
                Post(MoodTopic.Date, "birthday", md, who: born[0], pair: born.Length > 1 ? 1 : 0, gap: false);
                if (born.Length > 1) Reply(MoodTopic.Date, "birthday", md, born[1]);
                return;
            }
            string? holiday = md switch
            {
                "01-01" => "newyear",
                "12-24" or "12-25" => "xmas",
                "10-31" => "halloween",
                _ => null,
            };
            if (holiday != null) { Post(MoodTopic.Date, holiday, "", gap: false); return; }
            string part = LateHour(hour) ? "late" : hour < 12 ? "morning" : hour < 18 ? "afternoon" : "evening";
            Post(MoodTopic.Greeting, part, _wall.ToString("HH:mm", CultureInfo.InvariantCulture), part == "late" ? _late : null, gap: false);
            if (part == "late") _lateNudged = (today, hour);
            return;
        }
        if (LateHour(hour) && _lateNudged != (today, hour) && CanPost(_late, 0))
        {
            _lateNudged = (today, hour);
            Post(MoodTopic.Greeting, "late", _wall.ToString("HH:mm", CultureInfo.InvariantCulture), _late);
        }
    }

    // ---- a game ending ----

    /// <summary>"GG" with the play time once a game of at least <see cref="GameOverMinS"/> stops
    /// presenting for <see cref="GameEndS"/>. Arg is <c>app|minutes</c>.</summary>
    private void Game()
    {
        if (Presenting)
        {
            if (_gameSince < 0) _gameSince = _now;
            _gameSeen = _now;
            if (App.Length > 0) _gameApp = Tidy(App);
        }
        else if (_gameSince >= 0 && _now - _gameSeen >= S(GameEndS))
        {
            double played = (_gameSeen - _gameSince) / (double)Qpf;
            if (played >= GameOverMinS)
                _ggArg = $"{_gameApp}|{Math.Round(played / 60).ToString("0", CultureInfo.InvariantCulture)}";
            _gameSince = -1;
            _gameApp = "";
        }
        if (_ggArg != null && CanPost(_gg, 0))
        {
            Post(MoodTopic.Fps, "gameover", _ggArg, _gg);
            _ggArg = null;
        }
    }

    /// <summary>A process name as people say it: "Cyberpunk2077.exe" → "Cyberpunk2077".</summary>
    public static string Tidy(string app)
        => app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;

    // ---- small talk ----

    private void Chatter()
    {
        if (State != MoodState.Idle || _now - LastPostQpc < S(ChatterQuietS) || !CanPost(_chatter, ChatterS)) return;
        Post(MoodTopic.Chatter, "idle", "", _chatter);
    }

    /// <summary>Asleep > Critical > Hot > Gaming > Busy > Idle.</summary>
    private MoodState Candidate(IMetricSource m)
    {
        var level = WarnLevel.None;
        string part = "";
        double hottest = 0;
        void Temp(string metric, string type, string label)
        {
            if (!m.TryValue(metric, out double c)) return;
            var l = Models.Level(c, Warn(type, "temp"));
            if (l > level || (l == level && c > hottest)) { level = l; part = label; hottest = c; }
        }
        Temp(MetricNames.CpuPackageTempC, "cpu-ram", "CPU");
        int gpus = (int)Math.Clamp(m.Value(MetricNames.GpuCount, 0), 0, 16);
        for (int i = 0; i < gpus; i++) Temp(MetricNames.GpuTempC(i), "gpu", i == 0 ? "GPU" : $"GPU {i + 1}");
        if (level >= WarnLevel.L4) { HotPart = part; HotC = hottest; }

        bool gaming = m.TryValue(MetricNames.FpsPresented, out _, maxAgeS: 3);
        Presenting = gaming;
        App = gaming ? m.Text(MetricNames.FpsAppName) : "";

        if (level == WarnLevel.L5) return MoodState.Critical;
        if (level == WarnLevel.L4) return MoodState.Hot;
        if (gaming) return MoodState.Gaming;
        return m.TryValue(MetricNames.CpuTotalPct, out double cpu) && PanelData.Over(cpu, Warn("cpu-ram", "usage"))
            ? MoodState.Busy : MoodState.Idle;
    }

    /// <summary>What the host talks about: calm (Idle and Busy alike), gaming, hot, critical.</summary>
    private static MoodState Category(MoodState s) => s == MoodState.Busy ? MoodState.Idle : s;

    private void Announce(IMetricSource m)
    {
        // the host: gaming started, it got hot, it got critical, it cooled down
        var cat = Category(State);
        if (cat != _sysAnnounced)
        {
            bool critical = cat == MoodState.Critical;
            if (critical || CanPost(_sys, TopicCooldownS))
            {
                string? key = cat switch
                {
                    MoodState.Critical => "critical",
                    MoodState.Hot => "hot",
                    MoodState.Gaming => "gaming",
                    // back to calm only says something after heat; a game ending is not news
                    _ => _sysAnnounced is MoodState.Hot or MoodState.Critical ? "cool" : null,
                };
                string arg = cat is MoodState.Hot or MoodState.Critical
                    ? $"{HotPart}|{HotC.ToString("0", CultureInfo.InvariantCulture)}"
                    : cat == MoodState.Gaming ? Tidy(App) : "";
                if (key != null) Post(MoodTopic.System, key, arg, _sys);
                _sysAnnounced = cat;
            }
        }

        // Yuuka: the memory budget, both ways
        if (m.TryValue(MetricNames.RamPct, out double ram) && (ram >= RamHighPct) != _ram.Announced && CanPost(_ram, TopicCooldownS))
        {
            _ram.Announced = ram >= RamHighPct;
            Post(MoodTopic.Ram, _ram.Announced ? "high" : "back", ram.ToString("0", CultureInfo.InvariantCulture), _ram);
            if (!_ram.Announced) _reliefUntil = _now + S(ReactS);
        }

        // Momoi: a frame slower than the FPS row's lowest step, while a game is running
        var fpsWarn = Warn("fps", "fps");
        bool spike = State == MoodState.Gaming && fpsWarn.Length > 0 && fpsWarn[0] > 0
            && m.TryValue(MetricNames.FpsFrametimePresentedWorstMs, out double worst, maxAgeS: 3) && worst >= 1000 / fpsWarn[0];
        if (spike && !_spikeOn && CanPost(_spike, SpikeCooldownS))
            Post(MoodTopic.Fps, "spike", m.Value(MetricNames.FpsFrametimePresentedWorstMs).ToString("0", CultureInfo.InvariantCulture), _spike);
        _spikeOn = spike;

        // Utaha: a fan flat out (the same 90 % duty the Fans card calls "full"). Saying it once is
        // enough; slowing down again is not news.
        bool full = false;
        string fanName = "";
        int fans = (int)Math.Clamp(m.Value(MetricNames.FanCount, 0), 0, 32);
        for (int i = 0; i < fans && !full; i++)
            if (m.TryValue(MetricNames.FanControlPct(i), out double duty) && Models.SpinOf(-1, duty) == SpinState.Full)
            {
                full = true;
                fanName = m.Text(MetricNames.FanName(i));
            }
        if (!full) _fans.Announced = false;
        else if (!_fans.Announced && CanPost(_fans, TopicCooldownS))
        {
            _fans.Announced = true;
            Post(MoodTopic.Fans, "full", fanName, _fans);
        }

        // Chihiro: sustained, valid zero traffic (a real reading — a dead adapter reads N/A and
        // never gets here), and bursts that set a new session peak
        bool zero = m.TryValue(MetricNames.NetDownBps, out double down) && m.TryValue(MetricNames.NetUpBps, out double up) && down == 0 && up == 0;
        if (!zero) { _zeroSince = -1; _quiet.Announced = false; }
        else
        {
            if (_zeroSince < 0) _zeroSince = _now;
            if (!_quiet.Announced && _now - _zeroSince >= S(QuietAfterS) && CanPost(_quiet, TopicCooldownS))
            {
                _quiet.Announced = true;
                Post(MoodTopic.Network, "quiet", "", _quiet);
            }
        }
        bool burst = _now - _awakeSince >= S(WarmupS)
            && m.TryValue(MetricNames.NetDownBps, out double d2) && m.TryValue(MetricNames.NetDownBps + MetricNames.MaxSuffix, out double peak)
            && peak > 0 && d2 >= peak;
        if (burst && !_burstOn && CanPost(_burst, BurstCooldownS))
            Post(MoodTopic.Network, "burst", m.Value(MetricNames.NetDownBps).ToString("0", CultureInfo.InvariantCulture), _burst);
        _burstOn = burst;

        // the clock's student: uptime milestones crossed while watching (a machine that was already up a week
        // when Halo started has nothing new to record)
        int ms = Milestone(m);
        if (ms > _milestone && CanPost(_uptime, TopicCooldownS))
        {
            _milestone = ms;
            Post(MoodTopic.Uptime, "milestone", (MilestonesS[ms] / 86400).ToString("0", CultureInfo.InvariantCulture), _uptime);
            _happyUntil = _now + S(ReactS);
        }
        else if (ms < _milestone) _milestone = ms;
    }

    private static int Milestone(IMetricSource m)
    {
        if (!m.TryValue(MetricNames.SysUptimeS, out double up)) return -1;
        int i = -1;
        while (i + 1 < MilestonesS.Length && up >= MilestonesS[i + 1]) i++;
        return i;
    }

    // ---- posting ----

    private bool CanPost(Topic t, double cooldownS)
        => _now - _lastAny >= S(GlobalGapS) && _now - t.Last >= S(cooldownS);

    /// <summary>The events another character may answer (design: two-student cross-talk).</summary>
    private static bool Duet(MoodTopic topic, string key) => (topic, key) is
        (MoodTopic.Fps, "spike") or (MoodTopic.Ram, "high") or (MoodTopic.System, "hot") or (MoodTopic.Network, "burst");

    /// <summary>Warnings that raise the stakes. A reply still queued from before one would land
    /// after it and push it out of the chat area, so posting one drops them.</summary>
    private static bool Escalates(MoodTopic topic, string key) => (topic, key) is
        (MoodTopic.System, "critical") or (MoodTopic.System, "hot") or (MoodTopic.Ram, "high") or (MoodTopic.Fans, "full");

    /// <summary>Urgent posts (critical, the collector coming back) skip <see cref="CanPost"/> at the
    /// call site. Every post starts the global gap except pokes, replies and the day's greeting
    /// (<paramref name="gap"/> false). Every <see cref="DuetEvery"/>-th duet-capable event opens an
    /// exchange: the reply is queued for <see cref="ReplyS"/> later. An escalating warning first
    /// drops replies still queued from earlier events (<see cref="Escalates"/>).</summary>
    private void Post(MoodTopic topic, string key, string arg, Topic? t = null, string? who = null, int pair = 0, bool gap = true)
    {
        if (pair == 0 && Escalates(topic, key)) _replies.Clear();
        if (pair == 0 && Duet(topic, key) && _duets++ % DuetEvery == 0)
        {
            pair = 1;
            Reply(topic, key, arg, null);
        }
        _messages.Add(new MoodMessage(++_seq, topic, key, arg, _wall, who, pair));
        if (_messages.Count > Keep) _messages.RemoveAt(0);
        Unread++;
        LastPostQpc = _now;
        if (gap && topic != MoodTopic.Poke && pair != 2) _lastAny = _now;
        if (t != null) t.Last = _now;
    }

    private void Reply(MoodTopic topic, string key, string arg, string? who)
        => _replies.Add((_now + S(ReplyS), new MoodMessage(0, topic, key, arg, _wall, who, 2)));

    private static long S(double seconds) => (long)(seconds * Qpf);

    /// <summary>The user's warn thresholds on the first enabled widget of that type that set them,
    /// else the catalog's — the same answer that widget's own card gives.</summary>
    private double[] Warn(string type, string key)
    {
        foreach (var w in _widgets())
            if (w.Enabled && w.Type == type && w.Metrics.GetValueOrDefault(key)?.Warn is { Length: > 0 } user)
                return user;
        return PanelCatalog.Find(type)?.Metric(key)?.WarnDefaults ?? [];
    }
}
