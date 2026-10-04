using System.Globalization;
using Halo.Metrics;
using Halo.Widgets.PanelModels;
using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Halo.Widgets.Skins.AzurArchive.Az;

namespace Halo.Widgets.Skins.AzurArchive;

/// <summary>
/// The companion card (design §8 ★, §9): the host — Arona by day, Plana by night or on Night Watch —
/// in her mood's expression, <c>BOND · UPTIME</c> with the mood's status under it, and a MomoTalk
/// group thread where each student speaks about her own panel.
/// </summary>
internal static class AzurCompanion
{
    public static PanelModel Model() => new()
    {
        Type = "companion",
        Title = c => c.TitleOr(AzurTalk.Name(Host(c)).ToUpperInvariant()),
        Sub = _ => "COMPANION · SHITTIM CHEST",
        // The card has no reading of its own, so its status diamond follows the machine's mood
        // (design rule 7): Hot → !, Critical → ▲, Asleep → – like any card without its collector.
        State = c => !c.Mood.Known ? WarnLevel.None : c.Mood.State switch
        {
            MoodState.Critical => WarnLevel.L5,
            MoodState.Hot => WarnLevel.L4,
            _ => WarnLevel.None,
        },
        Missing = c => c.Mood.Known && c.Mood.State == MoodState.Asleep ? NoData.Collector : NoData.None,
    };

    /// <summary>The <c>host</c> option; auto hands over at the clock card's own sunrise and sunset
    /// (6:00 and 18:00), so the two never disagree about whether it is day.</summary>
    public static string Host(PanelContext c) => AzurChrome.Option(c.Theme, "host") switch
    {
        "arona" => "arona",
        "plana" => "plana",
        _ => c.Now.Hour is >= 6 and < 18 ? "arona" : "plana",
    };

    /// <summary>The host's face for the machine's mood (the mockups' approval table). Plana's Idle
    /// is her asleep pose without the zZz — her "sleepy" reads as neutral (Jack, answer 4).</summary>
    public static string HostMood(SystemMood m, string host, long now)
    {
        if (!m.Known) return "";
        if (m.State == MoodState.Asleep) return "asleep";
        if (m.Poked(host, now)) return "surprised";
        return m.State switch
        {
            MoodState.Critical => "panicking",
            MoodState.Hot => "worried",
            _ when m.Celebrating(now) => "happy",
            MoodState.Gaming => "cheering",
            MoodState.Busy => "focused",
            _ => host == "plana" ? "asleep" : "sleepy",
        };
    }
}

/// <summary>
/// Everything under the companion's header, in one element of <b>fixed height</b>: messages come and
/// go inside the MomoTalk zone, clipped to it, and never move the window — a card that grew would
/// drag the column packer and snapping with it (design §8). With <c>talk</c> off the zone is not
/// drawn at all; that is a setting, not a message, so it may change the height.
/// </summary>
internal sealed class CompanionEl(AzurCard card) : AzEl(card), IPokeTarget, IScrollTarget
{
    /// <summary>The thread falls back to the host's idle line once nothing has been said for this long.</summary>
    public const double QuietS = 300;

    private static readonly float RowH = U(102), TalkHeadH = U(22), TalkBodyH = U(104), TalkBottom = U(12);

    /// <summary>One line of the thread; <paramref name="Seq"/> is its message's, 0 for the idle line and
    /// system notes, which are not messages.</summary>
    private readonly record struct Item(string? Who, string Name, string Text, long Seq = 0);

    private readonly List<Item> _items = new();
    /// <summary>Where each sender's bubble (and icon) was last drawn, newest last — a click there
    /// pokes her, the same as a click on her face.</summary>
    private readonly List<(Rect Box, string Who)> _bubbles = new();
    private string _host = "arona", _mood = "", _status = "", _big = "", _small = "";
    private bool _talk, _bond, _asleep;
    private int _unread;
    private MoodState _state;
    private Rect _bust;
    private readonly Fade<Host> _hostLook = new();
    private readonly TalkScroll _scroll = new();
    private readonly List<long> _seqs = new();

    private readonly record struct Host(string Who, string Mood, bool Asleep);

    public static float FixedHeight(bool talk) => talk ? RowH + TalkHeadH + TalkBodyH + TalkBottom : RowH + U(4);

    protected override string Resolve(PanelContext c)
    {
        var m = c.Mood;
        _host = AzurCompanion.Host(c);
        _mood = AzurCompanion.HostMood(m, _host, c.NowQpc);
        _asleep = c.Stale || m.State == MoodState.Asleep && m.Known;
        _state = m.Known ? m.State : MoodState.Idle;
        _status = c.Stale ? "WAITING FOR COLLECTOR" : m.Known ? m.Status : "";
        _talk = AzurChrome.Option(c.Theme, "talk") != "false";
        _unread = _talk && !_asleep ? m.Unread : 0;

        // BOND · UPTIME is a reading like any other: no fresh uptime, no number (freshness contract)
        _bond = !c.Stale && c.Metrics.TryValue(MetricNames.SysUptimeS, out _);
        if (_bond)
        {
            long s = (long)c.Metrics.Value(MetricNames.SysUptimeS);
            (_big, _small) = s >= 86400 ? ($"{s / 86400}", $"d {s % 86400 / 3600:00}h") : ($"{s / 3600}", $"h {s % 3600 / 60:00}m");
        }
        else (_big, _small) = ("N/A", "");

        _hostLook.Step(c, new Host(_host, _mood, _asleep));
        _items.Clear();
        if (_talk) Thread(c, m);
        _seqs.Clear();
        foreach (var it in _items) _seqs.Add(it.Seq);
        _scroll.Sync(_seqs, c.NowQpc);
        return $"{_host}|{_mood}|{_status}|{_big}{_small}|{_unread}|{_talk}|{AzurCast.ArtOn(c.Theme)}|{_scroll.Offset}|{string.Join("¦", _items)}";
    }

    private void Thread(PanelContext c, SystemMood m)
    {
        string addr = AzurChrome.Option(c.Theme, "addressAs").Trim();
        var seats = AzurTalk.Seats.From(c.Settings, m.Widgets);
        bool art = AzurCast.ArtOn(c.Theme);
        if (_asleep)
        {
            // silent: a time stamp and a system note, never a line claiming all is well
            _items.Add(new(null, "", "— " + c.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + " —"));
            _items.Add(new(null, "", $"collector stopped · {AzurTalk.Name(_host)} is asleep"));
            return;
        }
        bool recent = m.Messages.Count > 0 && (c.NowQpc - m.LastPostQpc) < QuietS * System.Diagnostics.Stopwatch.Frequency;
        if (recent)
            foreach (var msg in m.Messages)
            {
                string who = AzurTalk.Sender(msg, _host, seats);
                if (AzurTalk.Line(msg, _host, addr, seats, art) is { } line) _items.Add(new(who, AzurTalk.Name(who), line, msg.Seq));
            }
        if (_items.Count == 0) _items.Add(new(_host, AzurTalk.Name(_host), AzurTalk.IdleLine(_state, _host, addr)));
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = FixedHeight(_talk);

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        var t = rc.Theme;
        float top = (float)Y;
        bool settled = !_hostLook.Moving(ctx);
        _hostLook.Draw(rc, ctx, (host, current) => DrawHost(rc, ctx, t, top, host, current, current && settled));
        DrawBond(rc, top);
        if (_talk) DrawTalk(rc, t, top + RowH);
    }

    // ---- host ----

    private void DrawHost(RenderContext rc, PanelContext ctx, Theme t, float top, Host host, bool current, bool live)
    {
        float size = U(104), right = CardX + CardW(t) - U(6);
        var b = new Rect(right - size, top + RowH - size, size, size);
        if (current) _bust = b;
        if (!AzurCast.ArtOn(t) || AzurCast.Slot(host.Who, host.Mood) is not { } slot) { if (current) _bust = default; return; }

        // faded out at the bottom so she stands behind the thread rather than on top of it; at rest
        // asleep the whole bust dims — her halo is baked into the art, and BA's halo fades when its
        // owner is out cold (design §9.5)
        float dim = host.Asleep ? 0.55f : 1f;
        void Bust(RenderContext r) => DrawBaked(r, "bust:" + slot, b, k =>
        {
            var fade = k.LinearGradient(new Color4(0, 0, 0, 1), new Color4(0, 0, 0, 0), new(0, b.Top + b.Height * 0.78f), new(0, b.Bottom));
            k.DC.PushLayer(new LayerParameters1 { ContentBounds = b, OpacityBrush = fade, Opacity = 1, MaskTransform = System.Numerics.Matrix3x2.Identity }, null!);
            AzurCast.Assets.Draw(k, slot, AzurCast.Bundled(slot), b);
            k.DC.PopLayer();
        }, dim);
        // the companion's one loop (motion full): she breathes — the cached bust rises and settles
        // on its own visual (design §9), so it costs no repaint here
        if (!(live && AmbientLoop.Offer(ctx, new AmbientLoop { Kind = AmbientKind.Bob, Key = $"bust:{slot}|{dim}", Bounds = b, Draw = Bust })))
            Bust(rc);

        if (host.Asleep)
        {
            var z = Bar(15, FontWeight.Bold, 1, tab: false);
            var zs = Bar(11, FontWeight.Bold, 1, tab: false);
            var zc = C(rc, "mascotFx");
            float zx = b.Right - U(34), zb = b.Top + U(34);
            zx += Text(rc, "z", zs, zc, zx, zb);
            zx += Text(rc, "z", z, zc, zx, zb - U(3));
            Text(rc, "Z", z, zc, zx, zb - U(7));
        }
    }

    // ---- BOND · UPTIME + status ----

    private void DrawBond(RenderContext rc, float top)
    {
        float x = Left + U(2);
        Text(rc, "BOND · UPTIME", Caption, C(rc, "text2"), x, top + U(31));
        var num = Bar(40, FontWeight.Light);
        float base_ = top + U(71);
        if (_bond)
        {
            AzurIcons.Emblem(rc, "heart", x, base_ - U(23), U(24), C(rc, "talkHeader"));
            x += U(24) + U(6);
            x += Text(rc, _big, num, C(rc, "text"), x, base_) + U(2);
            Text(rc, _small, HeroUnit, C(rc, "text2"), x, base_);
        }
        else Text(rc, _big, num, C(rc, "inactiveButton"), x, base_);

        string tone = _state == MoodState.Critical ? "devWarn5" : _state == MoodState.Hot ? "devWarn4" : "text2";
        Text(rc, _status, Caption, C(rc, tone), Left + U(2), top + U(86));
    }

    // ---- MomoTalk ----

    private void DrawTalk(RenderContext rc, Theme t, float top)
    {
        float x = Left, w = Width(t);
        var box = new Rect(x, top, w, TalkHeadH + TalkBodyH);
        var body = new Rect(x, top + TalkHeadH, w, TalkBodyH);
        rc.DC.PushLayer(new LayerParameters1 { ContentBounds = box, GeometricMask = RoundRect(rc, box, U(8)), Opacity = 1, MaskTransform = System.Numerics.Matrix3x2.Identity }, null!);
        rc.DC.FillRectangle(new Rect(x, top, w, TalkHeadH), rc.Brush(C(rc, "talkHeader")));
        rc.DC.FillRectangle(body, rc.Brush(C(rc, "talkBody")));
        rc.DC.PopLayer();

        // header: heart, "MomoTalk", the unread badge
        var ink = C(rc, "talkHeaderText");
        AzurIcons.Emblem(rc, "heart", x + U(9), top + U(4.5), U(13), ink);
        Text(rc, "MomoTalk", Mp(11, FontWeight.ExtraBold), ink, x + U(9) + U(13) + U(5), top + U(15));
        if (_unread > 0)
        {
            string badge = _unread > 9 ? "9+" : _unread.ToString(CultureInfo.InvariantCulture);
            var bf = Mp(9.5, FontWeight.ExtraBold);
            float bw = Math.Max(U(15), W(rc, badge, bf) + U(12)), bh = U(15), bx = x + w - U(9) - bw, by = top + U(3.5);
            rc.DC.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(bx, by, bw, bh), RadiusX = bh / 2, RadiusY = bh / 2 }, rc.Brush(C(rc, "badge")));
            Text(rc, badge, bf, new Color4(1, 1, 1, 1), bx + bw / 2, by + U(11), TextAlign.Center);
        }

        // The thread like a real chat: laid out from the bottom (newest) up and filling the zone, so
        // the oldest message in view is cut by the zone's top edge rather than left off whole, with
        // a short fade there so the cut reads as "more above". Scrolled back by the wheel, the whole
        // thread slides down by the offset (logical units), and a NEWER pill takes it back.
        _bubbles.Clear();
        _zone = body;
        _newer = default;
        float left = x + U(9), right = x + w - U(9), pad = U(7);
        int n = _items.Count;
        var lines = new List<string>[n];
        var heights = new float[n];
        float content = 0;
        for (int i = 0; i < n; i++)
        {
            lines[i] = Lines(rc, _items[i], left, right);
            heights[i] = ItemHeight(_items[i], lines[i]);
            content += heights[i] + (i > 0 ? Gap : 0);
        }
        _scroll.Layout(Math.Max(0, content - (body.Height - 2 * pad)), k =>
        {
            float h = 0;
            for (int i = n - 1; i >= Math.Max(0, n - k); i--) h += heights[i] + Gap;
            return h;
        });

        rc.DC.PushAxisAlignedClip(body, AntialiasMode.PerPrimitive);
        float y = body.Bottom - pad + _scroll.Offset;
        for (int i = n - 1; i >= 0; i--)
        {
            float itemTop = y - heights[i];
            if (itemTop < body.Bottom) DrawItem(rc, _items[i], lines[i], left, itemTop);
            if (itemTop <= body.Top) break;
            y = itemTop - Gap;
        }
        // the fade only where something is actually cut off above
        if (content - _scroll.Offset > body.Height - 2 * pad)
        {
            var bg = C(rc, "talkBody");
            var fade = rc.LinearGradient(bg, new Color4(bg.R, bg.G, bg.B, 0), new(0, body.Top), new(0, body.Top + U(10)));
            rc.DC.FillRectangle(new Rect(body.Left, body.Top, body.Width, U(10)), fade);
        }
        if (_scroll.Offset > 0) _newer = DrawNewer(rc, body);
        rc.DC.PopAxisAlignedClip();
    }

    private static readonly float Gap = U(6);

    /// <summary>The "newer" pill while scrolled back: bottom right of the zone, a down chevron, over
    /// whatever message sits there. A click on it returns to newest (<see cref="Poke"/>).</summary>
    private static Rect DrawNewer(RenderContext rc, Rect body)
    {
        var f = Mp(8.5, FontWeight.ExtraBold);
        float tw = W(rc, "NEWER", f), ph = U(14), pw = tw + U(8) + U(8) + U(8);
        var pill = new Rect(body.Right - U(7) - pw, body.Bottom - U(5) - ph, pw, ph);
        rc.DC.FillRoundedRectangle(new RoundedRectangle { Rect = pill, RadiusX = ph / 2, RadiusY = ph / 2 }, rc.Brush(C(rc, "talkHeader")));
        var ink = C(rc, "talkHeaderText");
        Text(rc, "NEWER", f, ink, pill.Left + U(7), pill.Top + U(10));
        float cx = pill.Right - U(9), cy = pill.Top + ph / 2 + U(1);
        var brush = rc.Brush(ink);
        rc.DC.DrawLine(new(cx - U(3), cy - U(2)), new(cx, cy + U(1)), brush, U(1.4));
        rc.DC.DrawLine(new(cx, cy + U(1)), new(cx + U(3), cy - U(2)), brush, U(1.4));
        return pill;
    }

    private static readonly TextStyle NameFont = Mp(9.5, FontWeight.ExtraBold);
    private static readonly TextStyle BubbleFont = Mp(11, FontWeight.Medium);
    private static readonly TextStyle SysFont = Ox(8.5, 0.5f);
    private static readonly float LineH = U(14.85);

    /// <summary>A message's bubble lines (a system note has none).</summary>
    private static List<string> Lines(RenderContext rc, Item it, float left, float right)
        => it.Who == null ? [] : Wrap(rc, it.Text, Math.Min(U(246), right - (left + U(30)) - U(16)));

    private static float ItemHeight(Item it, List<string> lines)
        => it.Who == null ? U(12) : U(13.5) + lines.Count * LineH + U(10);

    /// <summary>Draw one item with its top at <paramref name="top"/>.</summary>
    private void DrawItem(RenderContext rc, Item it, List<string> lines, float left, float top)
    {
        float right = left + (Width(rc.Theme) - U(18));
        if (it.Who == null)
        {
            Text(rc, it.Text, SysFont, C(rc, "faint"), (left + right) / 2, top + U(9), TextAlign.Center);
            return;
        }

        float bx = left + U(30), textW = 0;
        foreach (var l in lines) textW = Math.Max(textW, W(rc, l, BubbleFont));
        AzurCast.Icon(rc, it.Who, new Rect(left, top, U(24), U(24)));
        Text(rc, it.Name, NameFont, C(rc, "talkText"), bx, top + U(9));
        var bubble = new Rect(bx, top + U(13.5), textW + U(16), lines.Count * LineH + U(10));
        rc.DC.FillRoundedRectangle(new RoundedRectangle { Rect = bubble, RadiusX = U(7), RadiusY = U(7) }, rc.Brush(C(rc, "talkBubble")));
        // the icon through the bubble, kept to the visible zone: a message cut by the zone's edge
        // (the top, or the bottom while scrolled back) answers only where it shows
        float hitTop = Math.Max(top, _zone.Top), hitBottom = Math.Min(bubble.Bottom, _zone.Bottom);
        _bubbles.Add((new Rect(left, hitTop, bubble.Right - left, Math.Max(0, hitBottom - hitTop)), it.Who));
        for (int i = 0; i < lines.Count; i++)
            Text(rc, lines[i], BubbleFont, C(rc, "talkBubbleText"), bubble.Left + U(8), bubble.Top + U(5) + U(11.6) + i * LineH);
    }

    /// <summary>Greedy word wrap. Done by hand rather than with a DirectWrite max width because the
    /// layout cache is keyed by text and style only, so a wrapped layout would leak into other draws.</summary>
    private static List<string> Wrap(RenderContext rc, string text, float maxW)
    {
        var lines = new List<string>();
        string line = "";
        foreach (string word in text.Split(' '))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && W(rc, next, BubbleFont) > maxW) { lines.Add(line); line = word; }
            else line = next;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    /// <summary>Any click on the card counts as a glance at the thread (the unread badge goes); a
    /// click on the NEWER pill returns the thread to newest, and a click on the host, or on a
    /// message, is a poke on whoever is there.</summary>
    public bool Poke(PanelContext ctx, double x, double y)
    {
        if (y < Y || y > Y + Height) return false;
        if (Hit(_newer, x, y)) _scroll.ToNewest();
        else if (Hit(_bust, x, y)) ctx.Mood.Poke(_host, ctx.NowQpc);
        else if (BubbleAt(x, y) is { } who) ctx.Mood.Poke(who, ctx.NowQpc);
        ctx.Mood.Glance();
        return true;
    }

    private Rect _zone, _newer;

    /// <summary>The wheel over the MomoTalk zone scrolls the thread, a bubble line per notch.</summary>
    public bool Wheel(PanelContext ctx, double x, double y, int notches)
        => _talk && Hit(_zone, x, y) && _scroll.Wheel(notches, LineH, ctx.NowQpc);

    /// <summary>The sender of the message drawn at (<paramref name="x"/>, <paramref name="y"/>), or null.</summary>
    internal string? BubbleAt(double x, double y)
    {
        foreach (var (box, who) in _bubbles)
            if (Hit(box, x, y)) return who;
        return null;
    }

    private static bool Hit(Rect r, double x, double y)
        => r.Width > 0 && r.Height > 0 && x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;
}

/// <summary>
/// How far the MomoTalk thread is scrolled back, in logical units (0 = newest at the bottom), and
/// how far it can go (<see cref="Max"/>, the thread's height beyond the zone, from the last layout).
/// Apart from the element so it can be tested. A new message while scrolled back moves the offset up
/// by its height, so the view stays on what the user was reading; the unread badge counts it. The
/// view returns to newest <see cref="ReturnS"/> after the last wheel turn, on a wheel-down past the
/// end, or on a click on the NEWER pill.
/// </summary>
internal sealed class TalkScroll
{
    public const double ReturnS = 20;

    public float Offset { get; private set; }
    public float Max { get; private set; }
    private long _lastWheel;
    private long _newest = -1;      // seq of the newest message seen; -1 = nothing yet
    private int _fresh;             // messages that arrived while scrolled back, not yet laid out

    /// <summary>A wheel turn over the zone, <paramref name="step"/> units a notch: positive goes back
    /// (older). Clamped to [0, <see cref="Max"/>]. True = the view moved.</summary>
    public bool Wheel(int notches, float step, long nowQpc)
    {
        _lastWheel = nowQpc;
        float next = Math.Clamp(Offset + notches * step, 0, Max);
        if (next == Offset) return false;
        Offset = next;
        return true;
    }

    /// <summary>Back to newest. True = the view moved.</summary>
    public bool ToNewest()
    {
        _fresh = 0;
        if (Offset == 0) return false;
        Offset = 0;
        return true;
    }

    /// <summary>Follow the thread as it is now: <paramref name="seqs"/> per item, oldest first (0 for a
    /// line that is not a message).</summary>
    public void Sync(IReadOnlyList<long> seqs, long nowQpc)
    {
        if (Offset > 0)
            for (int i = seqs.Count - 1; i >= 0 && seqs[i] > _newest; i--)
                if (seqs[i] > 0) _fresh++;
        foreach (long s in seqs) _newest = Math.Max(_newest, s);
        if (Offset > 0 && nowQpc - _lastWheel >= (long)(ReturnS * System.Diagnostics.Stopwatch.Frequency)) ToNewest();
    }

    /// <summary>The layout's answer, once per draw: how far the thread can scroll, and the height
    /// (gap included) of the newest <c>k</c> items, so messages that came in while scrolled back
    /// push the offset up by exactly their height.</summary>
    public void Layout(float max, Func<int, float> newestHeight)
    {
        if (_fresh > 0 && Offset > 0) Offset += newestHeight(_fresh);
        _fresh = 0;
        Max = max;
        Offset = Math.Clamp(Offset, 0, max);
    }
}
