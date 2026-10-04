using System.Globalization;
using System.Text.Json;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Shared.Skins;
using Halo.Widgets.PanelModels;

namespace Halo.Widgets.Skins.AzurArchive;

/// <summary>
/// The copy deck (design §9.3–9.5): who speaks for each <see cref="MoodTopic"/>, and our own short
/// lines in each character's voice. Written for this skin, all ages, and about what a character
/// does, not how she looks. <c>{S}</c> is the <c>addressAs</c> name; when it is empty the name and
/// its comma drop out, so every line still reads. Ellipses are three periods: the bundled M PLUS
/// faces draw U+2026 as a centred, Japanese-style ellipsis, which reads oddly in English text.
///
/// <para><b>Decks are keyed by sender, slot.</b> A line is looked up as <c>(who, slot)</c>, then
/// <c>("*", slot)</c> — so a student added to the cast speaks the moment she sits on a card, and a
/// personal deck is only extra entries. The variant is <c>Seq % count</c> (a poke's random seed for
/// pokes), never <see cref="Random"/>: <see cref="Line"/> runs on every resolve and must give the
/// same answer each time.</para>
///
/// <para><b>Real game lines are a supplement.</b> A few official English lines live in the optional
/// <c>game-art\talk\lines.json</c> pack (CREDITS.md has a row each) and join a slot's deck only
/// while <c>gameArt</c> is on. Deleting <c>game-art\</c> removes them with the art.</para>
/// </summary>
internal static class AzurTalk
{
    public static string Name(string who) => who.Length == 0 ? "" : char.ToUpperInvariant(who[0]) + who[1..];

    // ---- who speaks ----

    /// <summary>
    /// Who sits on which card, read from the config the way <c>Theme.Resolve</c> merges the
    /// <c>character</c> option (widget over global), else the roster default. A seat set to
    /// "none" or to an id outside the cast still has the roster student speak for it — a topic
    /// always has an owner.
    /// </summary>
    internal sealed class Seats
    {
        public static readonly Seats Roster = new(null, []);

        private readonly AppSettings? _settings;
        private readonly IReadOnlyList<WidgetInstance> _widgets;

        private Seats(AppSettings? settings, IReadOnlyList<WidgetInstance> widgets)
        {
            _settings = settings;
            _widgets = widgets;
        }

        public static Seats From(AppSettings settings, IReadOnlyList<WidgetInstance> widgets) => new(settings, widgets);

        /// <summary>The student on the first enabled card of <paramref name="type"/>.</summary>
        public string Of(string type)
        {
            foreach (var w in _widgets)
                if (w.Enabled && w.Type == type) return Pick(w, "character", AzurArchiveSkin.Roster(type).Who);
            return AzurArchiveSkin.Roster(type).Who;
        }

        /// <summary>Everyone sitting on an enabled card, the host first — who chatter and late-night
        /// nudges are drawn from. Just the host when no widget is known.</summary>
        public IReadOnlyList<string> Present(string host)
        {
            var list = new List<string> { host };
            foreach (var w in _widgets)
            {
                if (!w.Enabled || w.Type == "companion") continue;
                Add(Pick(w, "character", AzurArchiveSkin.Roster(w.Type).Who));
                if (w.Type == "network") Add(Pick(w, "character2", TrafficEl.SecondDefault));
            }
            return list;

            void Add(string who) { if (!list.Contains(who)) list.Add(who); }
        }

        private string Pick(WidgetInstance w, string option, string fallback)
        {
            string? pick = w.Appearance?.Skins?.GetValueOrDefault(AzurArchiveSkinInfo.Id)?.Options?.GetValueOrDefault(option)
                ?? _settings?.Appearance.Skins.GetValueOrDefault(AzurArchiveSkinInfo.Id)?.Options?.GetValueOrDefault(option);
            return pick != null && Array.IndexOf(AzurArchiveSkinInfo.Cast, pick) >= 0 ? pick : fallback;
        }
    }

    /// <summary>The message's sender: whoever the event names, else the student on the topic's card
    /// (the host for the machine as a whole), else for a reply the character the pair table names.</summary>
    public static string Sender(MoodMessage m, string host, Seats? seats = null)
    {
        seats ??= Seats.Roster;
        if (m.Who != null) return m.Who;
        if (m.Pair == 2)
        {
            string opener = Sender(m with { Pair = 1 }, host, seats);
            string reply = (m.Topic, m.Key) switch
            {
                (MoodTopic.System, _) => host == "plana" ? "arona" : "plana",
                (MoodTopic.Fps, _) => seats.Of("latency"),
                (MoodTopic.Ram, _) => seats.Of("topram"),
                (MoodTopic.Network, _) => seats.Of("cpu-ram"),
                _ => host,
            };
            return reply == opener ? (opener == host ? (host == "plana" ? "arona" : "plana") : host) : reply;
        }
        return m.Topic switch
        {
            MoodTopic.Ram => seats.Of("cpu-ram"),
            MoodTopic.Fps => seats.Of("fps"),
            MoodTopic.Fans => seats.Of("fans"),
            MoodTopic.Network => seats.Of("network"),
            MoodTopic.Uptime => seats.Of("clock"),
            MoodTopic.Chatter => Draw(seats.Present(host), m.Seq),
            MoodTopic.Greeting when m.Key == "late" => Draw(seats.Present(host), m.Seq),
            _ => host,
        };
    }

    private static string Draw(IReadOnlyList<string> who, long seq) => who[(int)(seq % who.Count)];

    // ---- what is said ----

    /// <summary>The line for a message, or null when no deck has one (the message is skipped).
    /// <paramref name="art"/> is the <c>gameArt</c> option: off, the real-line pack is never read.</summary>
    public static string? Line(MoodMessage m, string host, string addr, Seats? seats = null, bool art = false)
    {
        seats ??= Seats.Roster;
        string who = Sender(m, host, seats);
        string[] arg = m.Arg.Split('|');
        string slot = Slot(m, arg);
        var deck = Deck(who, slot, art);
        if (deck.Count == 0) return null;
        long pick = m.Seq;
        if (m.Topic == MoodTopic.Poke && long.TryParse(arg[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seed)) pick = seed;
        string line = deck[(int)(Math.Abs(pick) % deck.Count)];

        string opener = m.Pair == 2 ? Name(Sender(m with { Pair = 1 }, host, seats)) : "";
        line = Fill(line, m, arg, opener);
        return Address(line, addr);
    }

    /// <summary>The deck slot a message reads from.</summary>
    internal static string Slot(MoodMessage m, string[] arg)
    {
        string pair = m.Pair switch { 1 => ".duet", 2 => ".reply", _ => "" };
        return (m.Topic, m.Key) switch
        {
            (MoodTopic.System, "gaming") when m.Arg.Length == 0 => "system.gaming.anon",
            (MoodTopic.Fps, "gameover") when Arg(arg, 0).Length == 0 => "fps.gameover.anon",
            (MoodTopic.Uptime, _) => m.Arg == "1" ? "uptime.day1" : "uptime.days",
            (MoodTopic.Poke, _) => "poke." + (int.TryParse(Arg(arg, 1), out int tier) ? Math.Clamp(tier, 1, 4) : 1),
            // a birthday's opener is her ordinary birthday line; only the twin's answer differs
            (MoodTopic.Date, "birthday") => m.Pair == 2 ? "date.birthday.reply" : "date.birthday",
            _ => $"{Prefix(m.Topic)}.{m.Key}{pair}",
        };
    }

    private static string Prefix(MoodTopic t) => t switch
    {
        MoodTopic.Network => "net",
        MoodTopic.Greeting => "greet",
        _ => t.ToString().ToLowerInvariant(),
    };

    /// <summary>Our deck for (who, slot), else the neutral one, plus the real lines for that slot
    /// while the art is on.</summary>
    internal static IReadOnlyList<string> Deck(string who, string slot, bool art)
    {
        string[] ours = Lines.GetValueOrDefault(who + ":" + slot) ?? Lines.GetValueOrDefault("*:" + slot) ?? [];
        if (!art || !Pack.Value.TryGetValue(who + ":" + slot, out var real)) return ours;
        return [.. ours, .. real];
    }

    private static string Fill(string line, MoodMessage m, string[] arg, string opener)
    {
        string a0 = Arg(arg, 0), a1 = Arg(arg, 1);
        string s = line
            .Replace("{part}", a0.Length > 0 ? a0 : "machine")
            .Replace("{c}", a1)
            .Replace("{n}", a0)
            .Replace("{ms}", a0)
            .Replace("{rate}", Rate(a0))
            .Replace("{app}", a0)
            .Replace("{min}", a1)
            .Replace("{fan}", a0.Length > 0 ? a0 : "the fan")
            .Replace("{time}", a0)
            .Replace("{who}", opener);
        return s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;
    }

    /// <summary>What the host says when nothing has happened for a while — never during Asleep,
    /// which has no reading to be calm about.</summary>
    public static string IdleLine(MoodState s, string host, string addr)
    {
        bool plana = host == "plana";
        string line = s switch
        {
            MoodState.Busy => plana ? "Load is high. Monitoring, {S}." : "You're working hard, {S}! I'll keep watch.",
            MoodState.Gaming => plana ? "Game in progress. Good luck, {S}." : "Go go, {S}! I'm cheering for you!",
            MoodState.Hot => plana ? "Temperatures elevated, {S}." : "It's getting warm in here, {S}...",
            MoodState.Critical => plana ? "{S}. Please check the cooling." : "{S}!! It's way too hot!!",
            _ => plana ? "All quiet. I'll rest my eyes, {S}." : "{S}, there's nothing to do... can I nap?",
        };
        return Address(line, addr);
    }

    private static string Arg(string[] arg, int i) => i < arg.Length ? arg[i] : "";

    private static string Rate(string bps)
    {
        if (!double.TryParse(bps, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return "";
        var (n, u) = Models.Bytes(v, suffix: "/s");
        return n + " " + u;
    }

    /// <summary>Fill in the name, or take it out with the punctuation that framed it.</summary>
    public static string Address(string line, string addr)
    {
        if (addr.Length > 0) return line.Replace("{S}", addr);
        string s = line.Replace("{S}, ", "").Replace(", {S}", "").Replace("{S}. ", "").Replace("{S}!! ", "").Replace("{S}! ", "").Replace(" {S}", "").Replace("{S}", "");
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
    }

    // ---- the real-line pack ----

    /// <summary>The pack's file, relative to the skin's assets folder.</summary>
    public const string PackFile = "game-art/talk/lines.json";

    /// <summary><c>{ "lines": [ { "who", "slot", "text", ... } ] }</c>, read once. Missing or
    /// unreadable = no real lines, the same as with the art off.</summary>
    internal static readonly Lazy<Dictionary<string, string[]>> Pack = new(() => LoadPack(Path.Combine(Paths.AssetsDir, "skins", AzurArchiveSkinInfo.Id, PackFile)));

    internal static Dictionary<string, string[]> LoadPack(string path)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var e in doc.RootElement.GetProperty("lines").EnumerateArray())
                {
                    string key = e.GetProperty("who").GetString() + ":" + e.GetProperty("slot").GetString();
                    if (!map.TryGetValue(key, out var l)) map[key] = l = new();
                    l.Add(e.GetProperty("text").GetString() ?? "");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.For("azur-talk").Warn($"could not read {path} ({ex.GetType().Name}: {ex.Message}); no real lines");
            map.Clear();
        }
        return map.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
    }

    // ---- our lines ----

    /// <summary>"who:slot" → variants. "*" is the neutral voice every slot has. Lines are ≤ 70
    /// characters (two bubble lines); a duet or reply line is one bubble line (≤ 35), because the
    /// fixed zone shows two messages together only when each is one line.</summary>
    internal static readonly Dictionary<string, string[]> Lines = Build();

    private static Dictionary<string, string[]> Build()
    {
        var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void A(string who, string slot, params string[] lines) => d[who + ":" + slot] = lines;

        // ---- the machine as a whole: the host ----
        A("*", "system.back", "{S}, the collector's back! I can see everything again.", "Reconnected! I missed the numbers, {S}.",
            "Phew, there you are! Everything's showing again.", "Welcome back, collector! ...and you, {S}!");
        A("plana", "system.back", "Collector reconnected. Monitoring resumed, {S}.", "Link restored. Telemetry nominal.",
            "Collector online. I was beginning to worry. Slightly.");
        A("*", "system.gaming", "{S}, {app} is running! I'll watch the temperatures.", "{app}! Go go, {S}! I'll watch the fans.",
            "Game time! I'll be right here if it gets hot.", "Have fun with {app}, {S}! I'll handle the boring numbers.");
        A("plana", "system.gaming", "{app} detected. Enjoy your game, {S}.", "{app} launched. Monitoring thermals.",
            "Game session started. Good luck.", "{app}. I will log the frame times. For fairness.");
        A("*", "system.gaming.anon", "{S}, a game started! I'll keep an eye on the temperatures.", "Game time! I'll be right here if it gets hot.",
            "Have fun, {S}! I'll handle the boring numbers.");
        A("plana", "system.gaming.anon", "Game detected. Enjoy, {S}.", "Game session started. Good luck.");
        A("*", "system.hot", "{S}, the {part} is at {c} °C... it's getting warm in here.", "It's getting toasty! {part}, {c} °C.",
            "{S}, could we open a window? {part} is at {c} °C.");
        A("plana", "system.hot", "{S}. {part} is at {c} °C. Opening a window is recommended.", "Thermal watch: {part}, {c} °C.",
            "{part} at {c} °C. A short break for the machine is advised.");
        A("*", "system.hot.duet", "{S}, the {part} is getting warm!", "{part} at {c} °C! Toasty!");
        A("plana", "system.hot.duet", "{part} at {c} °C. Noted.", "Thermal watch: {part}, {c} °C.");
        A("*", "system.hot.reply", "I'll fan the fans, {S}!", "Ice cream break, then?");
        A("plana", "system.hot.reply", "Fans have been advised.", "Cooling is aware. Probably.");
        A("*", "system.critical", "{S}!! The {part} is at {c} °C! That's way too hot!!", "{S}!! {part} {c} °C! Please slow down!",
            "Emergency! {part} is at {c} °C! Can we stop for a moment?!");
        A("plana", "system.critical", "{S}. {part} at {c} °C. This is not a drill.", "Critical temperature: {part}, {c} °C. Act now.",
            "{part} {c} °C. Reduce the load, {S}.");
        A("*", "system.cool", "Phew... everything's cooled down, {S}.", "Cool again! I can breathe now.", "Temperatures are back to normal! You did great.");
        A("plana", "system.cool", "Temperatures are normal again, {S}.", "Thermals stable. Well done.");

        // ---- RAM (the CPU / RAM card) ----
        A("*", "ram.high", "Memory is at {n} %, {S}. That's a lot.", "{n} % of RAM in use. Something's hungry.", "RAM at {n} %. Maybe close a tab or two?");
        A("yuuka", "ram.high", "{S}! Memory is at {n} %. That's over budget.", "Memory at {n} %. Which tab was it? Which one?!",
            "{S}, we can't keep spending like this. {n} % of RAM!", "RAM at {n} %. I'm opening an audit.");
        A("*", "ram.high.duet", "Memory is at {n} %!", "RAM at {n} %. Hm.");
        A("yuuka", "ram.high.duet", "Memory at {n} %. Auditing!", "{n} % of RAM?! Over budget!");
        A("*", "ram.high.reply", "Wasn't me. I think.", "Someone's auditing, then.");
        A("koyuki", "ram.high.reply", "Nihaha... wasn't me. Probably.", "I didn't open that many. Maybe.");
        A("noa", "ram.high.reply", "Noted. {who} is auditing.", "Recorded. Heehee.");
        A("arona", "ram.high.reply", "{who}'s going to audit us...", "I'll close my tabs, {who}!");
        A("plana", "ram.high.reply", "Audit acknowledged.", "{who} is correct. Again.");
        A("*", "ram.back", "Memory's back to normal, {S}.", "RAM freed. Much better.", "Plenty of room again.");
        A("yuuka", "ram.back", "Back within budget. I'll file this as responsible spending, {S}.", "Memory freed. The books balance again.",
            "Under budget. See? Frugality works, {S}.");

        // ---- FPS ----
        A("*", "fps.spike", "Whoa, a {ms} ms frame! Did the game just trip, {S}?", "A {ms} ms hitch. That one stung.", "{ms} ms frame! Hold on, {S}!");
        A("momoi", "fps.spike", "Whoa, a {ms} ms frame! Did the game just trip, {S}?", "Lag spike! {ms} ms! That one's not on me!",
            "{ms} ms?! Even games have bad days.", "{S}, was that a save point or a stutter? ({ms} ms)");
        A("*", "fps.spike.duet", "Lag spike! {ms} ms!", "{ms} ms frame! Ouch!");
        A("momoi", "fps.spike.duet", "Lag spike! {ms} ms!", "{ms} ms?! Unfair!");
        A("*", "fps.spike.reply", "Not the game's fault. Probably.", "It happens. Keep going.");
        A("midori", "fps.spike.reply", "...Not the game's fault. Probably.", "Momoi, it's fine. Keep playing.");
        A("aris", "fps.spike.reply", "Aris saw it! A lag monster!", "Lag debuff! Aris will heal you!");
        A("*", "fps.gameover", "GG! {min} minutes of {app}, {S}.", "That's {min} minutes of {app}. Good game!", "Welcome back from {app}, {S}!");
        A("momoi", "fps.gameover", "GG! {min} minutes of {app}, {S}.", "That's a wrap on {app}! Rematch?", "{min} minutes! That's a whole speedrun, {S}!");
        A("*", "fps.gameover.anon", "GG, {S}! {min} minutes well played.", "Good game! That was {min} minutes.");
        A("momoi", "fps.gameover.anon", "GG, {S}! {min} minutes!", "Game over! {min} minutes. Rematch?");

        // ---- fans ----
        A("*", "fans.full", "{fan} is at full speed, {S}.", "{fan} is flat out. It's working hard.", "{fan} at max. Cooling is on it.");
        A("utaha", "fans.full", "{fan} is at full speed. It's fine. It was built for this.", "{fan} at max. Music to my ears.",
            "{fan} hit the limit. A bigger heatsink would be fun to build.", "Fan at full power. I'd call that enthusiastic engineering.");

        // ---- network ----
        A("*", "net.quiet", "No traffic for a minute. All quiet.", "The network's resting, {S}.", "Not a single packet. Peaceful.");
        A("chihiro", "net.quiet", "No packets. Finally, some peace.", "Zero traffic for a minute. Suspiciously quiet.",
            "Network's silent. Either all good, or someone unplugged it.");
        A("kotama", "net.quiet", "Silence on the line. I'm still listening.", "Nothing to intercept. How boring.");
        A("*", "net.burst", "Download just hit a new peak, {rate}.", "{rate}! Something big is coming in, {S}.", "New download record: {rate}.");
        A("chihiro", "net.burst", "Download just hit a new peak, {rate}. Somebody's busy, {S}.", "{rate} just now. A big download, or a big secret?",
            "Whoa, {rate}! Who's pulling that much data?", "{rate} incoming. Logging it. Out of habit.");
        A("kotama", "net.burst", "{rate} on the line! I heard every byte.", "A {rate} peak. Very interesting traffic, {S}.");
        A("*", "net.burst.duet", "{rate} down. Who's that?", "New peak: {rate}!");
        A("chihiro", "net.burst.duet", "{rate} down. Who's that?", "{rate}. Big download.");
        A("*", "net.burst.reply", "Is it in the budget?", "That's a lot of data.");
        A("yuuka", "net.burst.reply", "Is it in the budget?", "Who approved that download?");

        // ---- uptime (the clock card) ----
        A("*", "uptime.day1", "One day without a restart, {S}.", "A full day of uptime. Nice and steady.");
        A("*", "uptime.days", "{n} days without a restart, {S}.", "{n} days of uptime. Impressive.", "{n} days and still going!");
        A("noa", "uptime.day1", "Recorded. One day without a restart, {S}.", "Day one in the logs. A tidy start.");
        A("noa", "uptime.days", "Recorded. {n} days without a restart, {S}.", "Entry for today: {n} days of uptime. Impressive.",
            "{n} days. I'll mark the calendar, {S}.", "Heehee... {n} days. I'd call that devotion.");
        A("toki", "uptime.day1", "One day of uptime. Time is my specialty. Peace, peace.", "Twenty-four hours, exactly as timed, {S}.");
        A("toki", "uptime.days", "{n} days without a restart. I kept time. V.", "{n} days, {S}. A maid never loses count.",
            "Uptime: {n} days. Requesting a reward.");

        // ---- greetings and late nights ----
        A("*", "greet.morning", "Good morning, {S}! Did you sleep well?", "Morning! Today's going to be a good one!");
        A("*", "greet.afternoon", "Good afternoon, {S}! Time for a snack?", "Afternoon! I'm ready when you are.");
        A("*", "greet.evening", "Good evening, {S}! You worked hard today.", "Evening! Shall we wrap things up?");
        A("plana", "greet.morning", "Good morning. Systems are ready, {S}.", "Morning. All sensors reporting.");
        A("plana", "greet.afternoon", "Good afternoon, {S}. Monitoring is active.", "Afternoon. All sensors reporting.");
        A("plana", "greet.evening", "Good evening. All sensors reporting, {S}.", "Evening. Standing by.");
        A("*", "greet.late", "It's {time}, {S}. Shouldn't you be asleep?", "{time} already... get some rest, {S}.");
        A("arona", "greet.late", "{S}, it's really late... go to sleep.", "It's {time}! Even Arona needs a nap, {S}.");
        A("plana", "greet.late", "{S}. It is past midnight. Sleep is advised.", "{time}. Rest is recommended, {S}.");
        A("yuuka", "greet.late", "Sleep counts as a budget item, {S}.", "It's {time}. Overtime is not approved.");
        A("noa", "greet.late", "It's {time}. I'll note it as overtime, {S}.", "{time}. Shall I log you as asleep?");
        A("chihiro", "greet.late", "Night shift again? Same here.", "{time}. The servers are quiet. You should be too.");
        A("momoi", "greet.late", "Just one more round? It's {time}!", "All-nighter, {S}? I won't tell Yuuka.");
        A("midori", "greet.late", "It's {time}... I should sleep too.", "Um, {S}... it's really late.");
        A("aris", "greet.late", "It's {time}! Aris says: rest at the inn!", "Night falls. HP restores when you sleep, {S}.");
        A("asuna", "greet.late", "Still up at {time}? Me too! Hehe.", "Night mission? I'm in, {S}!");
        A("akane", "greet.late", "It's {time}. Shall I tidy up so you can rest?", "{S}, a proper bedtime is a clean habit.");
        A("kotama", "greet.late", "{time}. Your keyboard is the only sound I hear.", "Late-night signals are the clearest.");
        A("utaha", "greet.late", "{time}. Best hours for inventing, honestly.", "Even machines need cooldown, {S}.");
        A("toki", "greet.late", "It is {time}. Bedtime, {S}.", "{time}. Maid's orders: sleep.");
        A("koyuki", "greet.late", "Nihaha, {time}! Let's stay up!", "Shh, {S}... it's {time}. Don't tell Yuuka.");

        // ---- holidays and birthdays ----
        A("*", "date.newyear", "Happy New Year, {S}! Let's do our best again!");
        A("plana", "date.newyear", "Happy New Year, {S}. A new log begins.");
        A("*", "date.xmas", "Merry Christmas, {S}! Is there strawberry cake?");
        A("plana", "date.xmas", "Merry Christmas, {S}. Cake is recommended.");
        A("*", "date.halloween", "Trick or treat, {S}! ...do I get strawberry milk?");
        A("plana", "date.halloween", "Halloween. Treats are logical, {S}.");
        A("*", "date.birthday", "It's my birthday today, {S}!", "Today's my birthday. Cake, maybe?");
        A("yuuka", "date.birthday", "It's my birthday. I budgeted for cake, {S}.");
        A("noa", "date.birthday", "Today's my birthday. I've scheduled cake, {S}.");
        A("koyuki", "date.birthday", "Nihaha! It's my birthday! Presents, {S}?");
        A("momoi", "date.birthday", "It's our birthday, {S}!");
        A("midori", "date.birthday", "Um... it's our birthday, {S}.");
        A("*", "date.birthday.reply", "Happy birthday to us!");
        A("momoi", "date.birthday.reply", "...You too, Midori!");
        A("midori", "date.birthday.reply", "...You too, Momoi.");
        A("aris", "date.birthday", "Pan-paka-paaan! Aris levelled up today!");
        A("asuna", "date.birthday", "It's my birthday! Let's party, {S}!");
        A("akane", "date.birthday", "My birthday. Shall I prepare a... blast?");
        A("chihiro", "date.birthday", "My birthday... I forgot to log it.");
        A("kotama", "date.birthday", "My birthday. I heard you planning. Heehee.");
        A("utaha", "date.birthday", "My birthday? It slipped my mind. Cake sounds good.");
        A("toki", "date.birthday", "Today is my birthday. A reward, please. V.");

        // ---- small talk ----
        A("*", "chatter.idle", "All quiet over here, {S}.", "Nothing to report. Just saying hi.", "Still here! The numbers look fine.");
        A("arona", "chatter.idle", "All quiet. Want some strawberry milk, {S}?", "I counted all the fans. Still there!",
            "Zzz... oh! I'm awake! Everything's fine, {S}.");
        A("plana", "chatter.idle", "Standing by. No anomalies.", "Idle state. Boredom is not an anomaly.", "No anomalies. It is... pleasant.");
        A("yuuka", "chatter.idle", "Quiet day. Perfect for the accounts.", "{S}, did you file those receipts?");
        A("noa", "chatter.idle", "I've reorganised the logs. Twice.", "Today's log is very tidy, {S}.");
        A("chihiro", "chatter.idle", "Did anyone lock their screen? ...Okay.", "Patching something. Don't mind me.");
        A("momoi", "chatter.idle", "{S}, wanna play something?", "I'm so bored I could write a whole game.");
        A("midori", "chatter.idle", "I'm sketching the widgets. Is that weird?", "Quiet days are good for drawing.");
        A("aris", "chatter.idle", "Aris is grinding XP while you rest!", "No monsters in sight. Aris is on patrol.");
        A("asuna", "chatter.idle", "Bored! Got a mission for me, {S}?", "Hmm hmm hmm. Nothing to clean!");
        A("akane", "chatter.idle", "Everything is spotless. As it should be.", "Tea, {S}? I'll tidy up afterwards.");
        A("kotama", "chatter.idle", "Listening to the quiet. It's soothing.", "I bugged the clock. It just ticks.");
        A("utaha", "chatter.idle", "I'm drafting a fan with eight blades.", "Want to see a blueprint, {S}?");
        A("toki", "chatter.idle", "Standing by, {S}. Peace, peace.", "No orders? I'll keep time, then.");
        A("koyuki", "chatter.idle", "Nihaha, guess what I'm doing? Nothing!", "I found a password! ...Just kidding.");

        // ---- pokes: tier 1 normal, 2 annoyed, 3 flustered, 4 gives up ----
        A("*", "poke.1", "Hm? Yes, {S}?", "Did you need something?", "Oh! Hi, {S}.");
        A("*", "poke.2", "Again, {S}?", "That's a few pokes now.");
        A("*", "poke.3", "Okay, okay, I'm listening!", "{S}, that's a lot of pokes!");
        A("*", "poke.4", "...I give up. Poke away.", "You win, {S}. Happy?");

        A("arona", "poke.1", "Eh?! {S}, that tickles!", "Ah, {S}! Did you need something?", "I'm awake, I'm awake!", "Hyaa! Did I fall asleep?",
            "{S}, do you want strawberry milk?");
        A("arona", "poke.2", "Ehh?! Again?! {S}, that's three!", "{S}, are you testing me?!");
        A("arona", "poke.3", "Stop, stop, my halo is spinning!", "I'm ticklish, {S}!");
        A("arona", "poke.4", "Okay, you win! You win! Please!", "...I'm napping now. Do not disturb.");
        A("plana", "poke.1", "...Yes, {S}?", "Poke detected. Purpose unclear.", "I am not a button, {S}.", "Contact registered.");
        A("plana", "poke.2", "Repeat poke detected. Reason?", "{S}. This is the third time.");
        A("plana", "poke.3", "I'm confused. This behaviour is illogical.", "Please stop, or I may malfunction.");
        A("plana", "poke.4", "Poke limit reached. Filing a complaint with Arona.", "...Is this a game to you, {S}?");
        A("yuuka", "poke.1", "{S}! I'm in the middle of the accounts!", "That poke isn't in the budget, {S}.", "Hm? Do you need a receipt?",
            "Is this about the schedule?");
        A("yuuka", "poke.2", "{S}, every poke is a line item now.", "That's three interruptions. Noted.");
        A("yuuka", "poke.3", "That's seven interruptions! I'm itemising them!", "{S}! My calculations!");
        A("yuuka", "poke.4", "I'm sending the invoice to your desk, {S}.", "Fine. Poking is now a paid service.");
        A("momoi", "poke.1", "Hey! I almost lost my combo!", "{S}! Wanna play the next round?", "Hehe, gotcha first!", "Player two has entered!");
        A("momoi", "poke.2", "Combo! Hit me again!", "Oh, a cheat code?");
        A("momoi", "poke.3", "Okay, okay, that's definitely a cheat code!", "Button mashing won't save you, {S}!");
        A("momoi", "poke.4", "You win the mini-game! Reward: ...nothing, sorry!", "Achievement unlocked: Pest. Hehe.");
        A("midori", "poke.1", "Ah... {S}? I was drawing.", "Um... is it my turn?", "Momoi, was that you?", "U-um, yes?");
        A("midori", "poke.2", "Um... again?", "M-my line went crooked...");
        A("midori", "poke.3", "I'll draw you being annoying.", "{S}, please... I'm shading.");
        A("midori", "poke.4", "Momoi, help...", "...I'm drawing you as a slime now.");
        A("aris", "poke.1", "Aris received a poke! What is the quest?", "A wild {S} appeared!", "Pan-paka-paaan! Aris is here!");
        A("aris", "poke.2", "Poke attack again! Aris takes 1 damage!", "Is this a hidden event, {S}?");
        A("aris", "poke.3", "Aris's HP is low! Retreating!", "Combo detected! Aris must counter!");
        A("aris", "poke.4", "Aris has fainted... Game over...", "Aris gives up! {S} wins the battle!");
        A("asuna", "poke.1", "Hehe, hi! Need a hand, {S}?", "Hi hi! What's up, {S}?", "Oh? A mission?");
        A("asuna", "poke.2", "Again? Hehe, I like this game!", "Tag! You're it, {S}!");
        A("asuna", "poke.3", "Okay, my turn to poke back!", "You're really into this, huh?");
        A("asuna", "poke.4", "Hehe, I'm out of energy! ...Just kidding!", "You win! Let's go get snacks!");
        A("akane", "poke.1", "Yes, {S}? Is something out of place?", "Oh my. How may I help?", "I was just tidying up.");
        A("akane", "poke.2", "Again? Shall I sweep you away, {S}?", "Please, {S}. I'm being patient.");
        A("akane", "poke.3", "I may have to clean this up... explosively.", "My patience has a fuse, {S}.");
        A("akane", "poke.4", "Fine. Consider yourself... cleared.", "Boom. ...Just a figure of speech, {S}.");
        A("utaha", "poke.1", "Careful, {S}. Precision equipment.", "Want to see my latest invention?", "Please don't touch the propeller.",
            "Is something broken?");
        A("utaha", "poke.2", "Please stop. Calibration is delicate.", "That's a second test input. Logged.");
        A("utaha", "poke.3", "Poke count high. Calibration ruined.", "I'll have to rebuild this from scratch.");
        A("utaha", "poke.4", "I'll build a 'do not touch' sign for you.", "Fine. I'll invent a poke-proof shield.");
        A("chihiro", "poke.1", "Ping received, {S}.", "Was that a packet or a poke?", "Logged. Timestamped. Filed.", "Who's asking?");
        A("chihiro", "poke.2", "Rate limit: one poke per second.", "Repeated requests detected.");
        A("chihiro", "poke.3", "That's a DDoS, {S}.", "Firewall engaged. Pokes dropped.");
        A("chihiro", "poke.4", "Blocked. ...Just kidding. Mostly.", "I'm routing your pokes to /dev/null.");
        A("kotama", "poke.1", "Shh! I'm listening to something.", "Oh, {S}. I heard you coming.", "You're on my frequency now.");
        A("kotama", "poke.2", "Every poke is recorded, {S}.", "Interference again? Hmm.");
        A("kotama", "poke.3", "Too much noise! I can't hear the signal!", "{S}, you're jamming my receiver!");
        A("kotama", "poke.4", "Fine. I'm wiretapping you back.", "Signal lost. Going off-air.");
        A("toki", "poke.1", "Yes, {S}. Orders?", "Maid on standby. V.", "Toki is here. Peace, peace.");
        A("toki", "poke.2", "A second poke. Noted.", "Are these orders, {S}? Unclear.");
        A("toki", "poke.3", "I am a maid, not a button. Peace, peace.", "Poke count rising. Still unbothered.");
        A("toki", "poke.4", "Very well. I'll accept a reward instead.", "You win, {S}. Peace, peace.");
        A("noa", "poke.1", "Noted in the records, {S}.", "My, {S}. How bold.", "I'll remember that one.", "Heehee. May I help, {S}?");
        A("noa", "poke.2", "Noted. And noted. And... noted.", "I'm counting, {S}.");
        A("noa", "poke.3", "My notebook is filling with pokes, {S}.", "Shall I start a new page just for you?");
        A("noa", "poke.4", "Entry: the pokes would not stop. Heehee.", "I'll show this log to Yuuka, {S}.");
        A("koyuki", "poke.1", "Nihaha! Wasn't me!", "Huh? I didn't touch anything!", "{S}! Got any passwords?");
        A("koyuki", "poke.2", "Nihaha, again? You like me!", "Stop it, that tickles! Nihaha!");
        A("koyuki", "poke.3", "I'm escaping! You'll never catch me!", "Okay, I'm telling Yuuka on you!");
        A("koyuki", "poke.4", "Fine, you caught me. Nihaha...", "I surrender! ...Was that a trap?");
        return d;
    }
}
