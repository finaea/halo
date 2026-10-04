using Halo.Shared.Skins;
using Halo.Widgets;
using Halo.Widgets.Skins.AzurArchive;

namespace Halo.Tests;

/// <summary>The MomoTalk copy deck: every slot a message can resolve to has a neutral ("*") deck, every
/// line reads cleanly with and without an addressAs name, lengths fit the bubbles, and the real-line
/// pack never leaks in with the art off.</summary>
[Collection(LogTestCollection.Name)]
public sealed class AzurTalkTests
{
    private static readonly string[] Senders = AzurArchiveSkinInfo.Cast;

    /// <summary>Every shape of message the mood can produce, with worst-case-ish arguments (an app name of
    /// 8 characters: the longest system.gaming line is 70 characters with one of ≤ 8 and "Sensei").</summary>
    private static IEnumerable<(MoodTopic Topic, string Key, string Arg, int Pair)> Shapes()
    {
        yield return (MoodTopic.System, "back", "", 0);
        yield return (MoodTopic.System, "gaming", "Valorant", 0);
        yield return (MoodTopic.System, "gaming", "", 0);
        foreach (int pair in new[] { 0, 1, 2 }) yield return (MoodTopic.System, "hot", "GPU 2|105", pair);
        yield return (MoodTopic.System, "critical", "GPU 2|105", 0);
        yield return (MoodTopic.System, "cool", "", 0);
        foreach (int pair in new[] { 0, 1, 2 }) yield return (MoodTopic.Ram, "high", "93", pair);
        yield return (MoodTopic.Ram, "back", "70", 0);
        foreach (int pair in new[] { 0, 1, 2 }) yield return (MoodTopic.Fps, "spike", "1200", pair);
        yield return (MoodTopic.Fps, "gameover", "Valorant|125", 0);
        yield return (MoodTopic.Fps, "gameover", "|125", 0);
        yield return (MoodTopic.Fans, "full", "Pump", 0);
        yield return (MoodTopic.Fans, "full", "", 0);
        yield return (MoodTopic.Network, "quiet", "", 0);
        foreach (int pair in new[] { 0, 1, 2 }) yield return (MoodTopic.Network, "burst", "1200000000", pair);
        yield return (MoodTopic.Uptime, "milestone", "1", 0);
        yield return (MoodTopic.Uptime, "milestone", "30", 0);
        foreach (int tier in new[] { 1, 2, 3, 4 }) yield return (MoodTopic.Poke, "poke", "{seed}|" + tier, 0);
        foreach (string part in new[] { "morning", "afternoon", "evening", "late" }) yield return (MoodTopic.Greeting, part, "03:00", 0);
        foreach (string day in new[] { "newyear", "xmas", "halloween" }) yield return (MoodTopic.Date, day, "", 0);
        yield return (MoodTopic.Date, "birthday", "12-08", 0);
        yield return (MoodTopic.Date, "birthday", "12-08", 1);
        yield return (MoodTopic.Date, "birthday", "12-08", 2);
        yield return (MoodTopic.Chatter, "idle", "", 0);
    }

    /// <summary>The shapes as messages, one per deck variant (Seq and poke seed 0–11), from <paramref name="who"/>.</summary>
    private static IEnumerable<MoodMessage> Messages(string who)
    {
        foreach (var (topic, key, arg, pair) in Shapes())
            for (int i = 0; i < 12; i++)
                yield return new MoodMessage(i, topic, key, arg.Replace("{seed}", i.ToString()), DateTime.Now, who, pair);
    }

    private static string RepoPackPath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Halo.sln")))
                return Path.Combine(d.FullName, "assets", "skins", AzurArchiveSkinInfo.Id, AzurTalk.PackFile);
        return "";
    }

    // ---- decks ----

    [Fact]
    public void Every_slot_a_message_can_use_has_a_neutral_deck()
    {
        var slots = Shapes().Select(s =>
        {
            var m = new MoodMessage(1, s.Topic, s.Key, s.Arg.Replace("{seed}", "1"), DateTime.Now, "arona", s.Pair);
            return AzurTalk.Slot(m, m.Arg.Split('|'));
        }).Distinct().ToList();
        // a twin's answer is the one slot only two students have
        foreach (string slot in slots.Where(s => s != "date.birthday.reply"))
            Assert.True(AzurTalk.Lines.ContainsKey("*:" + slot), $"no '*' deck for {slot}");
        Assert.True(AzurTalk.Lines.ContainsKey("momoi:date.birthday.reply") && AzurTalk.Lines.ContainsKey("midori:date.birthday.reply"));
    }

    [Fact]
    public void Every_sender_resolves_every_slot_through_the_fallback()
    {
        var slots = AzurTalk.Lines.Keys.Select(k => k[(k.IndexOf(':') + 1)..]).Where(s => AzurTalk.Lines.ContainsKey("*:" + s)).Distinct().ToList();
        Assert.NotEmpty(slots);
        foreach (string who in Senders)
            foreach (string slot in slots)
                Assert.True(AzurTalk.Deck(who, slot, art: false).Count > 0, $"{who} has no line for {slot}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Commander")]
    public void No_line_keeps_a_placeholder_a_lowercase_start_or_a_punctuation_artefact(string addr)
    {
        foreach (string who in Senders)
            foreach (var m in Messages(who))
            {
                string? line = AzurTalk.Line(m, "arona", addr);
                Assert.True(line != null, $"{who}: no line for {m.Topic}/{m.Key}");
                string ctx = $"{who} {m.Topic}/{m.Key}/{m.Pair} \"{line}\"";
                Assert.False(line.Contains('{') || line.Contains('}'), ctx);
                Assert.False(char.IsLower(line[0]), ctx);
                Assert.False(line.Contains(", .") || line.Contains(" ,") || line.Contains("  ") || line.Contains(",,"), ctx);
                Assert.Equal(line.Trim(), line);
            }
    }

    [Fact]
    public void Every_line_fits_two_bubble_lines_and_a_duet_or_reply_fits_one()
    {
        foreach (string who in Senders)
            foreach (var m in Messages(who))
            {
                string line = AzurTalk.Line(m, "arona", "Sensei")!;
                Assert.True(line.Length <= 70, $"{who} {m.Topic}/{m.Key}: {line.Length} chars: {line}");
                bool twin = m is { Topic: MoodTopic.Date, Key: "birthday" } && who is "momoi" or "midori";
                if (m.Pair != 0 && m.Topic != MoodTopic.Date || twin)   // only the twins ever open or answer a birthday
                    Assert.True(line.Length <= 35, $"{who} {m.Topic}/{m.Key}/{m.Pair}: {line.Length} chars: {line}");
            }
    }

    [Fact]
    public void Line_is_deterministic_for_the_same_message()
    {
        foreach (var m in Messages("arona").Concat(Messages("plana")))
            Assert.Equal(AzurTalk.Line(m, "arona", "Sensei"), AzurTalk.Line(m, "arona", "Sensei"));
        var chatter = new MoodMessage(7, MoodTopic.Chatter, "idle", "", DateTime.Now);
        Assert.Equal(AzurTalk.Line(chatter, "arona", "Sensei"), AzurTalk.Line(chatter with { At = DateTime.Now.AddHours(1) }, "arona", "Sensei"));
    }

    // ---- who speaks ----

    [Fact]
    public void A_named_sender_wins_and_a_reply_is_never_the_opener()
    {
        var named = new MoodMessage(1, MoodTopic.Date, "birthday", "03-14", DateTime.Now, "yuuka");
        Assert.Equal("yuuka", AzurTalk.Sender(named, "arona"));
        foreach (var (topic, key) in new[] { (MoodTopic.System, "hot"), (MoodTopic.Fps, "spike"), (MoodTopic.Ram, "high"), (MoodTopic.Network, "burst") })
            foreach (string host in new[] { "arona", "plana" })
            {
                var open = new MoodMessage(1, topic, key, "1", DateTime.Now, Pair: 1);
                var reply = open with { Pair = 2 };
                Assert.NotEqual(AzurTalk.Sender(open, host), AzurTalk.Sender(reply, host));
            }
    }

    [Fact]
    public void Wiring_puts_the_rostered_student_on_each_topic()
    {
        var uptime = new MoodMessage(1, MoodTopic.Uptime, "milestone", "1", DateTime.Now);
        Assert.Equal("toki", AzurTalk.Sender(uptime, "arona", AzurTalk.Seats.Roster));
        var ram = new MoodMessage(1, MoodTopic.Ram, "high", "93", DateTime.Now, Pair: 2);
        Assert.Equal("koyuki", AzurTalk.Sender(ram, "arona", AzurTalk.Seats.Roster));
        var fps = new MoodMessage(1, MoodTopic.Fps, "spike", "1200", DateTime.Now, Pair: 2);
        Assert.Equal("aris", AzurTalk.Sender(fps, "arona", AzurTalk.Seats.Roster));
        var net = new MoodMessage(1, MoodTopic.Network, "burst", "1200000000", DateTime.Now);
        Assert.Equal("chihiro", AzurTalk.Sender(net, "arona", AzurTalk.Seats.Roster));
    }

    [Fact]
    public void A_network_card_seats_kotama_by_default()
    {
        var seats = AzurTalk.Seats.From(new Halo.Shared.Config.AppSettings(),
            [new Halo.Shared.Config.WidgetInstance { Id = "n", Type = "network" }]);
        Assert.Contains("kotama", seats.Present("arona"));
        Assert.Contains("chihiro", seats.Present("arona"));
    }

    // ---- the real-line pack ----

    [Fact]
    public void With_the_art_off_no_line_is_ever_a_pack_line()
    {
        var pack = AzurTalk.LoadPack(RepoPackPath());       // empty on an art-free checkout: nothing to leak
        var real = pack.Values.SelectMany(v => v).ToHashSet();
        foreach (var key in pack.Keys)
        {
            int cut = key.IndexOf(':');
            var ours = AzurTalk.Deck(key[..cut], key[(cut + 1)..], art: false);
            Assert.DoesNotContain(ours, o => real.Contains(o));
        }
        var rendered = real.Select(t => AzurTalk.Address(t, "Sensei")).ToHashSet();
        foreach (string who in Senders)
            foreach (var m in Messages(who))
                Assert.DoesNotContain(AzurTalk.Line(m, "arona", "Sensei", null, art: false)!, rendered);
    }

    [Fact]
    public void Pack_lines_fit_a_bubble_and_read_cleanly()
    {
        foreach (var (key, texts) in AzurTalk.LoadPack(RepoPackPath()))
            foreach (string t in texts)
            {
                string line = AzurTalk.Address(t, "Sensei");
                Assert.True(line.Length <= 70, $"{key}: {line.Length} chars: {line}");
                foreach (string addr in new[] { "", "Commander" })
                {
                    string a = AzurTalk.Address(t, addr);
                    Assert.False(a.Contains(", .") || a.Contains(" ,") || a.Contains("  "), $"{key}: {a}");
                }
            }
    }

    [Fact]
    public void With_the_art_on_the_loaded_pack_joins_the_deck()
    {
        var pack = AzurTalk.Pack.Value;
        if (pack.Count == 0) return;                          // art-free output folder: no pack to join
        foreach (var (key, texts) in pack)
        {
            int cut = key.IndexOf(':');
            string who = key[..cut], slot = key[(cut + 1)..];
            var on = AzurTalk.Deck(who, slot, art: true);
            var off = AzurTalk.Deck(who, slot, art: false);
            Assert.Equal(off.Count + texts.Length, on.Count);
            Assert.All(texts, t => Assert.Contains(t, on));
        }
    }

    [Fact]
    public void The_built_in_deck_never_hardcodes_sensei()
    {
        // the name is {S}: addressAs decides what it reads as, a literal would ignore it
        foreach (var (slot, variants) in AzurTalk.Lines)
            foreach (string line in variants)
                Assert.False(line.Contains("Sensei", StringComparison.Ordinal), $"{slot}: {line}");

        foreach (var state in Enum.GetValues<MoodState>())
            foreach (string host in new[] { "arona", "plana" })
                Assert.DoesNotContain("Sensei", AzurTalk.IdleLine(state, host, ""), StringComparison.Ordinal);
    }
}
