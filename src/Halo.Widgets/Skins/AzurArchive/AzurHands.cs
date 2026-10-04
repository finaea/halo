using System.Globalization;
using System.Numerics;
using Halo.Widgets.PanelModels;
using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Halo.Widgets.Skins.AzurArchive.Az;

namespace Halo.Widgets.Skins.AzurArchive;

/// <summary>
/// FPS hero (design §8 ★): a 58 px framerate with no <c>MAX:</c> — FPS publishes no session max —
/// and the lows, frametime and 1 s worst in a 2 × 2 grid beside it; Momoi, or Manjuu's NO 3D APP.
/// </summary>
internal sealed class FpsHeroEl(AzurCard card, HeroBlock hero, StatBlock[] side) : AzEl(card), IPokeTarget
{
    private Val _fps;
    private readonly (string Label, Val Value, bool Shown)[] _side = new (string, Val, bool)[side.Length];
    private string _who = "";
    private readonly Fade<CastEl.Look> _look = new();

    protected override string Resolve(PanelContext c)
    {
        _fps = hero.Visible?.Invoke(c) == false ? Val.Absent : hero.Value(c);
        for (int i = 0; i < side.Length; i++)
            _side[i] = (side[i].Label(c), side[i].Value(c), side[i].Visible?.Invoke(c) ?? true);
        _who = AzurCast.Who(c.Theme, Card.Character) ?? "";
        var (mood, fx) = AzurCast.Mood(Card, _who.Length > 0 ? _who : null, c);
        _look.Step(c, new CastEl.Look(_who.Length > 0 ? _who : null, mood, fx, Card.Missing));
        return $"{_fps}|{string.Join(",", _side)}|{_look.Current}|{Card.Level}|{AzurCast.FaceKey(c.Theme)}";
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = U(72);

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        float top = (float)Y, x = Left + U(2), right = Right(rc.Theme);
        float numW = 0;
        if (_fps.Text != null)
        {
            var num = Bar(58, FontWeight.Light);
            numW = Text(rc, _fps.Text, num, C(rc, AzurCard.HeroToken(Card.Level, _fps.IsNa || Card.Missing != NoData.None)), x, top + U(66));
        }

        // 2 × 2 captions: label above, value below, tabular; each column as wide as its widest
        bool nap = Card.Missing != NoData.None;
        float gap = nap ? U(10) : U(14);
        float gx = x + Math.Max(numW, U(40)) + (nap ? U(12) : U(16));
        var val = Bar(17, FontWeight.Medium, 0.1f);
        var unit = Bar(11, FontWeight.Medium);
        Span<float> colW = stackalloc float[2];
        for (int i = 0; i < _side.Length; i++)
            colW[i % 2] = Math.Max(colW[i % 2], Math.Max(W(rc, _side[i].Label, Caption), W(rc, _side[i].Value.Text ?? "", val) + U(14)));
        // Manjuu's sign is wider than a face: slide the grid left to clear it, never under the number
        float need = colW[0] + gap + colW[1], room = right - (nap ? U(90) : U(48)) - U(4);
        if (gx + need > room) gx = Math.Max(x + numW + U(6), room - need);
        int shown = 0;
        for (int i = 0; i < _side.Length; i++)
        {
            var (label, v, on) = _side[i];
            if (!on) continue;
            int col = shown % 2, row = shown / 2;
            shown++;
            float cx = gx + col * (colW[0] + gap), cy = top + U(13) + row * U(31);
            Text(rc, label, Caption, C(rc, "text2"), cx, cy + U(9));
            Reading(rc, v, val, unit, C(rc, v.IsNa ? "inactiveButton" : "text"), C(rc, "text2"), cx, cy + U(26), alignLeft: true, gap: 2);
        }

        float bottom = top + U(67);
        // six px into the card padding: the 2 × 2 grid needs the width more than the margin does
        _face = default;
        var slot = new Rect(right - U(48), bottom - U(48), U(48), U(48));
        bool settled = !_look.Moving(ctx);
        _look.Draw(rc, ctx, (look, current) =>
        {
            if (look.Missing != NoData.None) AzurCast.Nap(rc, look.Missing, right + U(6), bottom + U(2));
            else if (look.Who != null && AzurCast.Face(rc, look.Who, look.Mood, look.Fx, slot, loop: current && settled ? ctx : null) && current) _face = slot;
        });
    }

    private Rect _face;

    public bool Poke(PanelContext ctx, double x, double y) => AzurCast.Poke(ctx, _who, _face, x, y);
}

/// <summary>The app name in a skewed BA tag and the DLSS version in a ghost tag; idle, a ghost
/// "waiting for a game..." instead.</summary>
internal sealed class FpsTagsEl(AzurCard card, StatBlock app, StatBlock dlss) : AzEl(card)
{
    private string _app = "", _dlss = "";

    protected override string Resolve(PanelContext c)
    {
        var a = app.Visible?.Invoke(c) == false ? Val.Na : app.Value(c);
        var d = dlss.Visible?.Invoke(c) == false ? Val.Na : dlss.Value(c);
        _app = a.IsNa ? "" : a.Text;
        _dlss = d.IsNa || d.Text.Length == 0 ? "" : "DLSS " + d.Text;
        return _app + "|" + _dlss + "|" + Card.Missing;
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = U(24);

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        float x = Left + U(2), y = (float)Y + U(3);
        var font = Mp(10, FontWeight.ExtraBold);
        if (_app.Length > 0)
            x = Tag(rc, _app, font, x, y, solid: true) + U(5);
        string ghost = _dlss.Length > 0 ? _dlss
            : Card.Missing == NoData.Collector ? "—"
            : Card.Missing == NoData.NoApp ? "waiting for a game..." : "";
        if (ghost.Length > 0 && (_app.Length > 0 || Card.Missing != NoData.None))
            Tag(rc, ghost, font, x, y, solid: false);
    }

    /// <summary>A -20° parallelogram tag; text stays upright inside it (rule 3).</summary>
    private float Tag(RenderContext rc, string text, TextStyle font, float x, float y, bool solid)
    {
        float h = U(16), slant = h * 0.364f;
        float avail = Right(rc.Theme) - x - U(20);
        string s = text;
        while (s.Length > 4 && W(rc, s, font) > avail) s = s[..^2];
        if (s.Length < text.Length) s = s.TrimEnd('.') + "…";
        float w = W(rc, s, font) + U(21) + slant;
        var shape = Lean(rc, x, y, w, h, slant);
        if (solid) rc.DC.FillGeometry(shape, rc.Brush(C(rc, Card.Data("keyFps"))));
        else rc.DC.DrawGeometry(shape, rc.Brush(C(rc, "rule")), U(1));
        Text(rc, s, font, solid ? C(rc, "tabText") : C(rc, "text2"), x + slant / 2 + U(9), y + h / 2 + U(3.6));
        return x + w;
    }
}

/// <summary>
/// The dashed gold line across the frame graph at the 1 %-low <b>frametime</b> (1000 / low1 ms),
/// labelled above the graph's right end; hidden when the low is N/A. It shares the graph's row and
/// reads the graph's own scale, so the line sits where a bar of that length would end.
/// </summary>
internal sealed class FrameLineEl : AzEl
{
    private readonly GraphEl _graph;
    private readonly StatBlock _low;
    private Val _ms;

    public FrameLineEl(AzurCard card, GraphEl graph, StatBlock low) : base(card)
    {
        _graph = graph;
        _low = low;
        SameRow = true;
    }

    protected override string Resolve(PanelContext c)
    {
        _ms = _low.Value(c);
        return _ms.ToString();
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = 0;

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        if (_ms.IsNa || _ms.Text == null || _graph.Series.Count == 0 || !_graph.IsVisible(ctx)) return;
        var ring = _graph.Series[0].Ring;
        if (ring.Count < 2) return;
        double ms = double.Parse(_ms.Text, CultureInfo.InvariantCulture);
        double max = _graph.Series[0].FixedMax ?? Math.Max(1e-9, ring.Max());
        float x = (float)(_graph.X ?? Left), w = (float)(_graph.W ?? Width(rc.Theme));
        float gy = (float)_graph.Y, gh = (float)_graph.H;
        float y = gy + gh - (float)Math.Clamp(ms / max, 0, 1) * gh;
        var gold = rc.Brush(C(rc, "activeTitle"));
        rc.DC.DrawLine(new Vector2(x, y), new Vector2(x + w, y), gold, U(1.2), AzurGraphVisual.LongDash(rc));
        var val = Bar(11, FontWeight.Medium);
        float vw = W(rc, _ms.Text + " " + _ms.Unit, val);
        Text(rc, _ms.Text + " " + _ms.Unit, val, C(rc, "activeTitle"), x + w - U(2), gy - U(3), TextAlign.Right);
        Text(rc, "1% LOW", Ox(9, 0.4f), C(rc, "text2"), x + w - U(2) - vw - U(4), gy - U(3), TextAlign.Right);
    }
}

/// <summary>
/// The AL Port clock (design §8 ★): sun or moon by the hour, a big time, a divider, the date, the
/// weekday with a gold ✦ and the uptime; the clock's student (Toki) peeks in from the right edge,
/// faded, behind it all. No header — the clock is the header.
/// </summary>
internal sealed class ClockEl(AzurCard card, StatBlock uptime) : AzEl(card), IPokeTarget
{
    private DateTime _now;
    private Val _up;
    private string _who = "";
    private ID2D1Geometry? _cardClip;
    private (nint, float, float) _clipKey;
    private readonly Fade<Peek> _peek = new();

    private readonly record struct Peek(string Who, bool Poked);

    protected override string Resolve(PanelContext c)
    {
        _now = c.Now;
        _up = uptime.Visible?.Invoke(c) == false ? Val.Absent : uptime.Value(c);
        _who = AzurCast.Who(c.Theme, Card.Character) ?? "";
        _poked = _who.Length > 0 && c.Mood.Poked(_who, c.NowQpc);
        _peek.Step(c, new Peek(_who, _poked));
        return $"{_now:yyyyMMddHHmm}|{_up}|{_who}|{_poked}|{AzurCast.ArtOn(c.Theme)}";
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = U(80);

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        var t = rc.Theme;
        float top = (float)Y, mid = top + U(41);

        // the student first, so everything else reads over her: clipped to the card, faded in from the left
        _face = default;
        _peek.Draw(rc, ctx, (peek, current) => DrawPeek(rc, t, top, peek, current));

        // sun 6:00–17:59, else a crescent moon
        float sx = Left + U(17), sy = mid;
        var gold = new Color4(1, 0xD5 / 255f, 0x4A / 255f, 1);
        if (_now.Hour is >= 6 and < 18)
        {
            rc.DC.FillEllipse(new Ellipse(new(sx, sy), U(8), U(8)), rc.Brush(gold));
            rc.DC.DrawEllipse(new Ellipse(new(sx, sy), U(11), U(11)), rc.Brush(Alpha(gold, 0.45f)), U(2));
            var ray = rc.Brush(gold);
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI / 4;
                var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
                rc.DC.DrawLine(new Vector2(sx, sy) + d * U(13.5), new Vector2(sx, sy) + d * U(16), ray, U(2.4));
            }
        }
        else
        {
            var moon = new Color4(0xC9 / 255f, 0xD8 / 255f, 1, 1);
            rc.DC.FillEllipse(new Ellipse(new(sx, sy), U(10), U(10)), rc.Brush(moon));
            rc.DC.FillEllipse(new Ellipse(new(sx + U(5), sy - U(4)), U(8.5), U(8.5)), rc.Brush(C(rc, "bgTop")));
        }

        float x = Left + U(34) + U(10);
        var time = Bar(48, FontWeight.Bold, 0.5f);
        string hm = _now.ToString("h:mm", CultureInfo.InvariantCulture);
        float tw = Text(rc, hm, time, C(rc, "text"), x, mid + U(17));
        float ampmW = Text(rc, _now.Hour < 12 ? "AM" : "PM", Bar(16, FontWeight.SemiBold, tab: false), C(rc, "text2"), x + tw + U(3), mid + U(17));
        float dx = x + tw + U(3) + ampmW + U(12);
        rc.DC.FillRectangle(new Rect(dx, top + U(18), U(1), U(48)), rc.Brush(C(rc, "rule")));
        dx += U(12);
        Text(rc, _now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), Bar(17, FontWeight.SemiBold), C(rc, "text"), dx, top + U(33));
        float ww = Text(rc, _now.DayOfWeek.ToString(), Mp(11.5, FontWeight.ExtraBold), C(rc, "text2"), dx, top + U(49));
        Text(rc, "✦", Mp(11.5, FontWeight.ExtraBold), C(rc, "barWarn"), dx + ww + U(3), top + U(49));
        if (_up.Text != null)
        {
            float lw = Text(rc, "UPTIME", Caption, C(rc, "text2"), dx, top + U(66));
            Text(rc, _up.Text, CaptionVal, C(rc, _up.IsNa ? "inactiveButton" : "text"), dx + lw + U(4), top + U(66));
        }
    }

    private Rect _face;
    private bool _poked;

    public bool Poke(PanelContext ctx, double x, double y) => AzurCast.Poke(ctx, _who.Length > 0 ? _who : null, _face, x, y);

    /// <summary>Gap between the peek and the card's top and bottom edges, and the length of the
    /// soft fade at each end (design px). Equal at both ends, and subtle (Jack, 2026-10-04).</summary>
    internal const float PeekGap = 3, PeekFade = 7;

    /// <summary>The peeking student first, so everything else reads over her: nearly as tall as the
    /// card, a small gap from its top and bottom edges, her head fading in just below the top and
    /// her body fading out just above the bottom — the same short fade at both ends, so the face
    /// crop's own cut-off hair never shows as a line (Jack, 2026-10-04). Faded in from the left as
    /// well, some of her runs off the right edge so she stays clear of the date, and she is clipped
    /// to the card's exact shape, so nothing of her lands outside it. No halo: a ring over her
    /// crowded the clock (D17), so the clock card has no loop at all.</summary>
    private void DrawPeek(RenderContext rc, Theme t, float top, Peek peek, bool current)
    {
        if (peek.Who.Length == 0 || !AzurCast.ArtOn(t) || AzurCast.Slot(peek.Who, peek.Poked ? "surprised" : "happy") is not { } slot) return;
        var card = CardRect(t);
        float side = card.Height - 2 * U(PeekGap);
        // her left edge about 60 px in from the card's right: clear of the date column
        var img = new Rect(card.Right + U(24) - side, card.Top + U(PeekGap), side, side);
        if (current) _face = img;
        var across = rc.LinearGradient(new Color4(0, 0, 0, 0), new Color4(0, 0, 0, 1), new(img.Left, 0), new(img.Left + img.Width * 0.35f, 0));
        rc.DC.PushLayer(new LayerParameters1
        {
            ContentBounds = img,
            GeometricMask = CardClip(rc, card),
            OpacityBrush = across,
            Opacity = 1,
            MaskTransform = Matrix3x2.Identity,
        }, null!);
        // the top and bottom fades: a second layer, since a layer takes one opacity brush; the
        // clock repaints about once a minute, so the extra layer costs nothing that shows
        float f = U(PeekFade) / img.Height;
        using var stops = rc.DC.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0, Color = new Color4(0, 0, 0, 0) },
            new GradientStop { Position = f, Color = new Color4(0, 0, 0, 1) },
            new GradientStop { Position = 1 - f, Color = new Color4(0, 0, 0, 1) },
            new GradientStop { Position = 1, Color = new Color4(0, 0, 0, 0) },
        ]);
        using var down = rc.DC.CreateLinearGradientBrush(new LinearGradientBrushProperties(new(0, img.Top), new(0, img.Bottom)), stops);
        rc.DC.PushLayer(new LayerParameters1 { ContentBounds = img, OpacityBrush = down, Opacity = 1, MaskTransform = Matrix3x2.Identity }, null!);
        AzurCast.Assets.Draw(rc, slot, AzurCast.Bundled(slot), img);
        rc.DC.PopLayer();
        rc.DC.PopLayer();
    }

    /// <summary>The card the chrome draws round this panel. The clock is its panel's last
    /// element, so the panel's height is this element's bottom plus the layout's bottom margin
    /// (Panel.Layout); the old clip guessed it and ran 3 units past the card's rounded corner, which
    /// showed the picture's square corner outside the card.</summary>
    private Rect CardRect(Theme t) => AzurChrome.CardRect(t, Y + Height + t.BottomMargin + t.BgOffset + 2);

    private ID2D1Geometry CardClip(RenderContext rc, Rect card)
    {
        using var factory = rc.DC.Factory;
        var key = (factory.NativePointer, card.Top, card.Bottom);
        if (_cardClip == null || key != _clipKey)
        {
            _cardClip?.Dispose();
            _cardClip = factory.CreateRoundedRectangleGeometry(new RoundedRectangle { Rect = card, RadiusX = Radius, RadiusY = Radius });
            _clipKey = key;
        }
        return _cardClip;
    }
}

/// <summary>
/// Top processes as a Seminar receipt (design §8 ★ — the one panel that breaks the card template
/// on purpose): dotted rules, rank / process / two amount columns, #1 in gold, the process count,
/// an AUDITED stamp and one line from the auditor, which follows the <c>talk</c> option.
/// </summary>
internal sealed class ReceiptEl(AzurCard card, ListBlock list, bool byRam) : AzEl(card), IPokeTarget
{
    private readonly (string Name, Val A, Val B, bool Over)[] _rows = new (string, Val, Val, bool)[list.Rows.Count];
    private Val _count;
    private string _who = "", _line = "";

    protected override string Resolve(PanelContext c)
    {
        for (int i = 0; i < _rows.Length; i++)
        {
            var r = list.Rows[i];
            _rows[i] = (r.Name(c), r.Primary(c), r.Secondary(c), r.Over(c));
        }
        _count = list.Count?.Invoke(c) ?? Val.Absent;
        _who = AzurCast.Who(c.Theme, Card.Character) ?? "";
        bool talk = AzurChrome.Option(c.Theme, "talk") != "false";
        string who = AzurChrome.Option(c.Theme, "addressAs").Trim();
        string start = byRam ? "Every byte’s on the books" : "Every cycle’s accounted for";
        _line = !talk ? "" : who.Length > 0 ? $"“{start},\n{who}.”" : $"“{start}.”";
        _poked = _who.Length > 0 && c.Mood.Poked(_who, c.NowQpc);
        _auditor.Step(c, new CastEl.Look(_who, _poked ? "surprised" : "relaxed", AzurIcons.Overlay.None, Card.Down ? NoData.Collector : NoData.None));
        return $"{string.Join(";", _rows)}|{_count}|{_who}|{_line}|{_poked}|{AzurCast.FaceKey(c.Theme)}";
    }

    private float RowsH => _rows.Length * U(19);

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = U(8) + U(8 + 12 + 9.5f + 13) + RowsH + U(5.5f + 8 + 48 + 10) + U(4);

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        var t = rc.Theme;
        float x0 = Left, x1 = Right(t), top = (float)Y + U(8), bottom = (float)(Y + Height) - U(4);
        var paper = new Rect(x0, top, x1 - x0, bottom - top);
        rc.DC.FillRectangle(paper, rc.Brush(C(rc, "receiptFill")));
        rc.DC.DrawRectangle(paper, rc.Brush(Alpha(C(rc, "receiptMuted"), 0.35f)), U(1));

        var ink = C(rc, "receiptInk");
        var muted = C(rc, "receiptMuted");
        var accent = C(rc, "receiptAccent");
        var head = Ox(8.5, 0.6f);
        float x = x0 + U(12), r = x1 - U(12), y = top + U(8);

        Text(rc, "SEMINAR · EXPENSE REPORT", head, muted, x, y + U(8));
        if (_count.Text != null)
            Text(rc, _count.IsNa ? "N/A" : $"{_count.Text} PROCESSES", head, muted, r, y + U(8), TextAlign.Right);
        y += U(12);
        y = Dotted(rc, x, r, y + U(5), muted) + U(3);

        // columns: 18 | 1fr | 52 | 58
        float cA = r - U(58), cB = r;
        Text(rc, "#", Ox(8.5, 0.4f), muted, x, y + U(9));
        Text(rc, "PROCESS", Ox(8.5, 0.4f), muted, x + U(18), y + U(9));
        Text(rc, list.PrimaryHeading, Ox(8.5, 0.4f), muted, cA, y + U(9), TextAlign.Right);
        Text(rc, list.SecondaryHeading, Ox(8.5, 0.4f), muted, cB, y + U(9), TextAlign.Right);
        y += U(13);

        var amount = Bar(14, FontWeight.Medium);
        for (int i = 0; i < _rows.Length; i++)
        {
            var (name, a, b, over) = _rows[i];
            float baseY = y + U(14);
            bool empty = name == "---" && a.IsNa;
            var rowInk = over ? C(rc, "redText") : empty ? muted : i == 0 ? accent : ink;
            Text(rc, list.Rows[i].Rank.ToString(CultureInfo.InvariantCulture), Bar(13, FontWeight.Bold), rowInk, x, baseY);
            var nameFont = Mp(11, i == 0 ? FontWeight.ExtraBold : FontWeight.Medium);
            string n = name;
            float maxW = cA - U(52) - (x + U(18)) - U(4);
            while (n.Length > 3 && W(rc, n, nameFont) > maxW) n = n[..^1];
            if (n.Length < name.Length) n += "…";
            Text(rc, n, nameFont, rowInk, x + U(18), baseY);
            Text(rc, a.IsNa ? "N/A" : a.Text + Sep(a), amount, a.IsNa ? muted : rowInk, cA, baseY, TextAlign.Right);
            Text(rc, b.IsNa ? "N/A" : b.Text + Sep(b), amount, i == 0 && !b.IsNa ? accent : muted, cB, baseY, TextAlign.Right);
            y += U(19);
        }
        y = Dotted(rc, x, r, y + U(4), muted);

        // the auditor, her remark, the stamp — or, with the collector gone, Manjuu asleep: nobody
        // audits a receipt of N/A, and a cheerful line would claim all is well
        float fy = y + U(8);
        if (Card.Down)
        {
            AzurCast.Nap(rc, NoData.Collector, r, fy + U(50));
            return;
        }
        float lx = x;
        var face = new Rect(x, fy, U(48), U(48));
        _face = default;
        bool settled = !_auditor.Moving(ctx);
        bool drawn = false;
        _auditor.Draw(rc, ctx, (look, current) =>
        {
            if (look.Who is not { Length: > 0 } who || look.Missing != NoData.None) return;
            if (AzurCast.Face(rc, who, look.Mood, look.Fx, face, loop: current && settled ? ctx : null, ceiling: y + U(2)) && current) { _face = face; drawn = true; }
        });
        if (drawn) lx = x + U(48) + U(8);
        if (_line.Length > 0)
        {
            var lines = _line.Split('\n');
            var quote = Mp(10.5, FontWeight.Medium);
            float ly = fy + U(24) - (lines.Length - 1) * U(7) + U(4);
            foreach (var l in lines) { Text(rc, l, quote, Alpha(ink, 0.8f), lx, ly); ly += U(14); }
        }
        Stamp(rc, r - U(4) - U(34), bottom - U(22) - U(14), (_who.Length > 0 ? _who : "yuuka").ToUpperInvariant());
    }

    private Rect _face;
    private bool _poked;
    private readonly Fade<CastEl.Look> _auditor = new();

    public bool Poke(PanelContext ctx, double x, double y) => !Card.Down && AzurCast.Poke(ctx, _who.Length > 0 ? _who : null, _face, x, y);

    private static string Sep(Val v) => v.Unit.Length == 0 ? "" : v.Unit == "%" ? "%" : " " + v.Unit;

    /// <summary>A dotted rule, baked: some forty ellipses a rule, two rules a receipt, on every
    /// repaint were the receipt's dearest part.</summary>
    private static float Dotted(RenderContext rc, float x, float r, float y, Color4 c)
    {
        // a dot may end 1.5 past r, and antialiasing bleeds a little: pad so nothing is cut off
        var area = new Rect(x - U(1), y - U(2), r - x + U(3.5), U(4));
        DrawBaked(rc, "dots:" + c, area, b =>
        {
            var brush = b.Brush(c);
            for (float dx = x; dx < r; dx += U(4))
                b.DC.FillEllipse(new Ellipse(new Vector2(dx + U(0.75), y), U(0.75), U(0.75)), brush);
        });
        return y + U(1.5);
    }

    private static void Stamp(RenderContext rc, float cx, float cy, string who)
    {
        var c = rc.Theme.Color("stamp");
        var font = Ox(9, 0.8f);
        string l1 = "AUDITED", l2 = "BY " + who;
        float w = Math.Max(W(rc, l1, font), W(rc, l2, font)) + U(12), h = U(26);
        var saved = rc.DC.Transform;
        rc.DC.Transform = Matrix3x2.CreateRotation(-12 * MathF.PI / 180, new Vector2(cx, cy)) * saved;
        rc.DC.DrawRoundedRectangle(new RoundedRectangle { Rect = new Rect(cx - w / 2, cy - h / 2, w, h), RadiusX = U(3), RadiusY = U(3) }, rc.Brush(c), U(2));
        Text(rc, l1, font, c, cx, cy - U(1.5), TextAlign.Center);
        Text(rc, l2, font, c, cx, cy + U(8.5), TextAlign.Center);
        rc.DC.Transform = saved;
    }
}
