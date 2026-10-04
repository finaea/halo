using System.Numerics;
using Halo.Widgets.PanelModels;
using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Halo.Widgets.Skins.AzurArchive.Az;

namespace Halo.Widgets.Skins.AzurArchive;

/// <summary>
/// Base for the skin's composite elements: one element per block, measuring and drawing its own
/// label, value and bar (tech plan §3.4). <see cref="Update"/> resolves everything the element will
/// draw into fields and reports a change only when that text or state moved, so a card at rest
/// costs no repaint. Margins live inside <see cref="Element.Height"/>, so the flow advance is 0.
/// </summary>
internal abstract class AzEl : Element
{
    protected readonly AzurCard Card;
    private string _sig = "\u0000";

    /// <summary>Space kept clear at the right for the character beside this block.</summary>
    public float RightInset;

    protected AzEl(AzurCard card)
    {
        Card = card;
        Advance = 0;
    }

    public override bool Update(PanelContext ctx)
    {
        Card.Eval(ctx);
        string sig = Resolve(ctx);
        if (sig == _sig) return false;
        _sig = sig;
        return true;
    }

    /// <summary>Read the block's values for this tick; the returned string is what decides a repaint.</summary>
    protected abstract string Resolve(PanelContext ctx);

    protected static Color4 C(RenderContext rc, string token) => rc.Theme.Color(token);

    protected float R(RenderContext rc) => Right(rc.Theme) - RightInset;

    /// <summary>Track + fill with the 20° cut end and 25 % ticks; cyan, gold past the warn point,
    /// red hatch past it on a critical card (design §3 "Bar", rule 7).</summary>
    protected void DrawBar(RenderContext rc, float x, float y, float w, double frac, bool warn, float h = 0)
    {
        if (h <= 0) h = U(8);
        float cut = U(3);
        var t = rc.Theme;
        rc.DC.FillGeometry(CutRight(rc, x, y, w, h, cut), rc.Brush(t.Color("emptyBar")));
        if (!double.IsNaN(frac) && frac > 0 && !Card.Down)
        {
            float fw = (float)(w * Math.Clamp(frac, 0, 1));
            var fill = CutRight(rc, x, y, MathF.Round(fw * 4) / 4, h, cut);
            if (warn && Card.Crit)
                Hatch(rc, fill, t.Color("red"), t.Color("hatch"), U(4), U(2));
            else
                rc.DC.FillGeometry(fill, rc.LinearGradient(t.Color("bar"), t.Color(warn ? "barWarn" : "barEnd"), new(x, 0), new(x + w, 0)));
        }
        var tick = rc.Brush(Alpha(t.Color("bgTop"), 0.6f));
        for (int i = 1; i < 4; i++)
            rc.DC.FillRectangle(new Rect(x + w * i / 4 - U(0.4), y, U(0.8), h), tick);
    }
}

/// <summary>Key bar · title · sheared sub-label · state tag · emblem · status diamond · rule
/// (design §3 header, §4.1: light on Port Day, a solid tab on Night Watch, a band on Momo Pink).</summary>
internal sealed class HeaderEl(AzurCard card) : AzEl(card)
{
    private string _title = "", _sub = "";
    private readonly Transition _state = new();
    private ID2D1Geometry? _clip;
    private (nint, float) _clipKey;

    protected override string Resolve(PanelContext c)
    {
        _title = Card.Model.Title(c).ToUpperInvariant();
        _sub = Card.Down ? "NO SIGNAL · COLLECTOR"
            : Card.Missing == NoData.Sensor ? "NO SIGNAL · " + Card.Model.MissingWhat
            : Card.Model.Sub(c).ToUpperInvariant();
        // a warn tag or N/A arriving eases in; the title and sub-label are text and just change.
        // Keyed on what the header draws differently — L1..L3 look alike here, and a temperature
        // hovering on one of those lines restarted the clock ~120 times a minute for nothing.
        // (a card with no reading has no step, so Missing and a tag never coexist)
        _state.Step(c, Card.Crit ? "crit" : Card.Warn ? "warn" : Card.Missing.ToString());
        return $"{_title}|{_sub}|{Card.Level}|{Card.Missing}|{AzurChrome.Option(c.Theme, "header")}";
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = HeaderH;

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        var t = rc.Theme;
        string style = AzurChrome.Option(t, "header");
        bool onFill = style is "tab" or "band";
        float top = (float)Y, mid = top + HeaderH / 2;
        float x = Left;

        var titleC = C(rc, onFill ? "tabText" : "title");
        var subC = onFill ? Alpha(C(rc, "tabText"), 0.8f) : C(rc, "subLabel");
        float titleW = W(rc, _title, Title), subW = W(rc, _sub, Sub) + U(2);

        float tagX = x + titleW + U(10);
        if (style == "tab")
        {
            float tabW = Left - CardX + Math.Max(titleW, subW) + U(20);
            tagX = CardX + tabW + U(2);
            var shape = CutRight(rc, CardX, top, tabW, HeaderH, U(12));
            rc.DC.PushLayer(new LayerParameters1 { ContentBounds = new Rect(CardX, top, tabW, HeaderH), GeometricMask = TopClip(rc, t, top), Opacity = 1, MaskTransform = Matrix3x2.Identity }, null!);
            rc.DC.FillGeometry(shape, rc.Brush(C(rc, Card.Down ? "inactiveButton" : "tabFill")));
            rc.DC.PopLayer();
        }
        else
        {
            var key = new Rect(x, mid - U(11), U(4), U(22));
            if (style == "band")
                rc.DC.FillRoundedRectangle(new RoundedRectangle { Rect = key, RadiusX = U(1), RadiusY = U(1) }, rc.Brush(Alpha(C(rc, "diamondFill"), 0.9f)));
            else if (Card.Crit)
                Hatch(rc, CutRight(rc, key.Left, key.Top, key.Width, key.Height, 0), C(rc, "red"), C(rc, "hatch"), U(3), U(2));
            else
                rc.DC.FillRoundedRectangle(new RoundedRectangle { Rect = key, RadiusX = U(1), RadiusY = U(1) }, rc.Brush(C(rc, Card.KeyToken)));
            x += U(12);
        }

        float titleBase = top + U(20.5), subBase = top + U(32.2);
        Text(rc, _title, Title, titleC, x, titleBase);
        Text(rc, _sub, Sub, subC, x, subBase);

        // state tag: the solid slanted tab survives as the warn/critical label (rule 7)
        // it fades in on a step change, and at full motion slides in from the title as well
        float p = _state.Progress(ctx);
        if (Card.Warn || Card.Crit)
            Fade<int>.Layer(rc, p, () =>
            {
                string tag = Card.Crit ? Card.Model.CritTag : Card.Model.WarnTag;
                float tx = (style == "tab" ? tagX : x + titleW + U(10)) - (ctx.Motion == MotionLevel.Full ? (1 - p) * U(10) : 0);
                float th = U(15), tw = W(rc, tag, Tag) + U(17);
                var shape = CutRight(rc, tx, mid - th / 2 - U(3), tw, th, U(4));
                if (Card.Crit) Hatch(rc, shape, C(rc, "red"), C(rc, "critHatch"), U(5), U(4));
                else rc.DC.FillGeometry(shape, rc.Brush(C(rc, "barWarn")));
                Text(rc, tag, Tag, C(rc, Card.Crit ? "critText" : "warnText"), tx + U(7), mid - U(3) + U(3.4));
            });

        // right end: emblem, then the status diamond
        float dcx = Right(t) - U(3) - U(7.5) + U(1), r = U(15) * 0.7071f + U(0.8);
        var status = Card.Down || Card.Missing != NoData.None ? AzurIcons.Status.None
            : Card.Crit ? AzurIcons.Status.Crit : Card.Warn ? AzurIcons.Status.Warn : AzurIcons.Status.Ok;
        string ring = status switch
        {
            AzurIcons.Status.Crit => "red",
            AzurIcons.Status.Warn => "devWarn4",
            AzurIcons.Status.None => "inactiveButton",
            _ => style == "band" ? "diamondFill" : "tabFill",
        };
        var dia = Diamond(rc, dcx, mid, r);
        string fill = status == AzurIcons.Status.Crit ? "red" : style == "band" ? "tabFill" : "diamondFill";
        rc.DC.FillGeometry(dia, rc.Brush(C(rc, fill)));
        rc.DC.DrawGeometry(dia, rc.Brush(C(rc, ring)), U(1.6));
        AzurIcons.StatusGlyph(rc, status, dcx, mid, U(6), C(rc, status == AzurIcons.Status.Crit ? "critText" : ring));

        string emblem = style == "band" ? "diamondFill" : Card.Down ? "inactiveButton" : Az.KeyToken(Card.Model.Type);
        AzurIcons.Emblem(rc, Card.Model.Type, dcx - U(7.5) - U(9) - U(18), mid - U(9), U(18), C(rc, emblem));

        if (style != "band")
        {
            var rule = rc.Brush(C(rc, Card.Down ? "inactiveButton" : "rule"));
            float ry = top + HeaderH - U(0.5);
            rc.DC.FillRectangle(new Rect(Left, ry, Right(t) - Left, U(1)), rule);
            rc.DC.FillRectangle(new Rect(Right(t) - U(4), ry - U(2), U(5), U(5)), rule);
        }
    }

    /// <summary>The card's rounded top-left corner, so Night Watch's tab follows the card edge.</summary>
    private ID2D1Geometry TopClip(RenderContext rc, Theme t, float top)
    {
        using var factory = rc.DC.Factory;
        var key = (factory.NativePointer, top);
        if (_clip == null || key != _clipKey)
        {
            _clip?.Dispose();
            _clip = factory.CreateRoundedRectangleGeometry(new RoundedRectangle { Rect = new Rect(CardX, top, CardW(t), HeaderH + Radius * 2), RadiusX = Radius, RadiusY = Radius });
            _clipKey = key;
        }
        return _clip;
    }
}

/// <summary>The headline reading: a <c>MAX:</c> (or named) caption over a big light numeral, or
/// Power's dark-glass energy pill. The character or Manjuu sits beside it (<see cref="CastEl"/>).</summary>
internal sealed class HeroEl(AzurCard card, HeroBlock block) : AzEl(card)
{
    private Val _v, _cap;
    private string _label = "";
    private WarnLevel _lvl;

    protected override string Resolve(PanelContext c)
    {
        _v = block.Value(c);
        _label = block.CaptionLabel?.Invoke(c) ?? "";
        _cap = block.CaptionValue?.Invoke(c) ?? Val.Absent;
        _lvl = block.Warn(c);
        return $"{_v}|{_label}|{_cap}|{_lvl}|{Card.Down}";
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = block.Kind == HeroKind.Pill ? U(62) : U(72);

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        float top = (float)Y, x = Left + U(2);
        if (block.Kind == HeroKind.Pill) { DrawPill(rc, top); return; }

        if (_label.Length > 0)
        {
            float w = Text(rc, _label, Caption, C(rc, "text2"), x, top + U(19));
            if (_cap.Text != null)
                Text(rc, _cap.Text, CaptionVal, C(rc, _cap.IsNa ? "inactiveButton" : "text"), x + w + U(3), top + U(19));
        }
        Reading(rc, _v, HeroNum, HeroUnit, C(rc, AzurCard.HeroToken(_lvl, _v.IsNa || Card.Down)), C(rc, "text2"), x, top + U(66), alignLeft: true, gap: 2);
    }

    private void DrawPill(RenderContext rc, float top)
    {
        float x = Left, cy = top + U(33), h = U(40);
        var num = Bar(30, FontWeight.Medium);
        var unit = Bar(13, FontWeight.SemiBold);
        var note = Ox(8.5);
        string[] notes = (block.Note ?? "").Split('\n');
        float noteW = notes.Max(n => W(rc, n, note));
        float w = U(14) + U(16) + U(8) + W(rc, _v.Text, num) + U(3) + W(rc, _v.Unit, unit) + U(6) + noteW + U(22);
        var pill = CutRight(rc, x, cy - h / 2, w, h, U(14));
        rc.DC.FillGeometry(pill, rc.Brush(C(rc, "pillFill")));
        var ink = C(rc, "pillText");
        AzurIcons.Emblem(rc, "power", x + U(13), cy - U(9), U(18), C(rc, Card.Down ? "inactiveButton" : "barWarn"));
        float tx = x + U(14) + U(16) + U(8), base_ = cy + U(10.5);
        tx += Text(rc, _v.Text, num, _v.IsNa ? C(rc, "inactiveButton") : ink, tx, base_) + U(3);
        tx += Text(rc, _v.Unit, unit, Alpha(ink, 0.8f), tx, base_) + U(6);
        for (int i = 0; i < notes.Length; i++)
            Text(rc, notes[i], note, Alpha(ink, 0.7f), tx, cy - U(1.5) + i * U(10.2));
    }
}

/// <summary>AL's Ship-Info row: strip with a cut corner, diamond bullet, label, muted detail,
/// tabular value with its unit, optional MAX column, and optionally a bar under it.</summary>
internal sealed class StatEl(AzurCard card, StatBlock block) : AzEl(card)
{
    /// <summary>Lead with a big propeller instead of the diamond (the Fans card).</summary>
    public bool BigGlyph;
    public float RowH = U(22);

    private string _label = "", _detail = "", _token = "text";
    private Val _v, _max;
    private double _frac = double.NaN;
    private bool _warn;
    private SpinState _spin;

    protected override string Resolve(PanelContext c) => Read(c, block);

    /// <summary>Resolve without the change check — for <see cref="StatPairEl"/>, which owns the check.</summary>
    internal string Sig(PanelContext c) => Read(c, block);

    private string Read(PanelContext c, StatBlock b)
    {
        _label = b.Label(c).ToUpperInvariant();
        _detail = b.Detail?.Invoke(c) ?? "";
        _v = b.Value(c);
        _max = b.Max?.Invoke(c) ?? Val.Absent;
        _frac = b.Fraction?.Invoke(c) ?? double.NaN;
        _warn = b.BarWarn?.Invoke(c) ?? false;
        _token = b.Token?.Invoke(c) ?? "tabFill";
        _spin = b.Spin?.Invoke(c) ?? SpinState.Cruising;
        return $"{_label}|{_detail}|{_v}|{_max}|{Math.Round(_frac * 400)}|{_warn}|{_token}|{_spin}|{Card.Level}|{Card.Down}";
    }

    public override void Measure(RenderContext rc, PanelContext ctx)
        => Height = RowH + U(2) + (block.Fraction != null ? U(2 + 8 + 5) : 0);

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        DrawRow(rc, Left, (float)Y, R(rc) - Left);
        if (block.Fraction != null)
            DrawBar(rc, Left, (float)Y + RowH + U(2) + U(2), R(rc) - Left, _frac, _warn);
    }

    /// <summary>The row itself — shared with <see cref="StatPairEl"/>, which draws two at half width.</summary>
    public void DrawRow(RenderContext rc, float x, float y, float w)
    {
        rc.DC.FillGeometry(Strip(rc, x, y, w, RowH, U(6)), rc.Brush(C(rc, "solidLabel")));
        float mid = y + RowH / 2, lx = x + U(8);
        if (block.Plain)
        {
            lx += Text(rc, _label, Mp(10.5, FontWeight.Medium), C(rc, "text2"), lx, mid + U(3.6));
        }
        else
        {
            if (block.Glyph == RowGlyph.Propeller && BigGlyph)
            {
                Spinner(rc, lx - U(1), mid - U(9), U(18));
                lx += U(18) + U(5);
            }
            else
            {
                var bullet = Diamond(rc, lx + U(2.5), mid, U(3.6));
                rc.DC.FillGeometry(bullet, rc.Brush(C(rc, Card.Data(_token))));
                lx += U(5) + U(7);
            }
            lx += Text(rc, _label, RowLabel, C(rc, "text"), lx, mid + U(3.7));
            if (block.Glyph == RowGlyph.Propeller && !BigGlyph && _detail.Length > 0)
            {
                lx += U(7);
                Spinner(rc, lx, mid - U(6), U(12));
                lx += U(15);
                Text(rc, _detail, RowDetail, C(rc, "text2"), lx, mid + U(3.5));
            }
            else if (_detail.Length > 0)
                Text(rc, _detail, RowDetail, C(rc, "text2"), lx + U(7), mid + U(3.5));
        }

        float right = x + w - U(8);
        if (block.Max != null)
        {
            var maxC = C(rc, "maxLabelGray");
            float mw = W(rc, _max.Text, MaxVal);
            Text(rc, _max.Text, MaxVal, _max.IsNa ? C(rc, "inactiveButton") : maxC, right, mid + U(4.5), TextAlign.Right);
            Text(rc, "MAX", MaxLabel, maxC, right - mw - U(3), mid + U(4.2), TextAlign.Right);
            right -= U(58) + U(7);
        }
        var num = block.Plain ? Bar(14, FontWeight.Medium) : RowVal;
        Reading(rc, _v, num, RowUnit, C(rc, _v.IsNa ? "inactiveButton" : "text"), C(rc, "text2"), right, mid + U(5.6));
    }

    private void Spinner(RenderContext rc, float x, float y, float size)
    {
        string token = Card.Down ? "inactiveButton" : _spin switch
        {
            SpinState.Stopped => "faint",
            SpinState.Full => "barWarn",
            _ => Card.Model.Type == "gpu" ? "bar" : Az.KeyToken(Card.Model.Type),
        };
        AzurIcons.Prop(rc, x, y, size, C(rc, token), _spin == SpinState.Full ? 25 : 0);
    }
}

/// <summary>Two stat rows side by side, 4 px apart.</summary>
internal sealed class StatPairEl : AzEl
{
    private readonly StatEl _l, _r;
    private readonly StatPairBlock _b;

    public StatPairEl(AzurCard card, StatPairBlock b) : base(card)
    {
        _b = b;
        _l = new StatEl(card, b.Left);
        _r = new StatEl(card, b.Right);
    }

    protected override string Resolve(PanelContext c) => _l.Sig(c) + "#" + _r.Sig(c);

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = U(22) + U(2);

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        bool l = _b.Left.Visible?.Invoke(ctx) ?? true, r = _b.Right.Visible?.Invoke(ctx) ?? true;
        float w = R(rc) - Left, half = (w - U(4)) / 2;
        if (l && r)
        {
            _l.DrawRow(rc, Left, (float)Y, half);
            _r.DrawRow(rc, Left + half + U(4), (float)Y, half);
        }
        else (l ? _l : _r).DrawRow(rc, Left, (float)Y, w);
    }
}

/// <summary>Outlined label/value chips (CLOCK · CPU FAN); stacked = label over value.</summary>
internal sealed class ChipsEl(AzurCard card, ChipsBlock block) : AzEl(card)
{
    private readonly List<(string Label, Val Value)> _chips = new();

    protected override string Resolve(PanelContext c)
    {
        _chips.Clear();
        foreach (var chip in block.Chips)
            if (chip.Visible?.Invoke(c) ?? true)
                _chips.Add((chip.Label(c).ToUpperInvariant(), chip.Value(c)));
        return string.Join("|", _chips) + Card.Down;
    }

    private float ChipH => block.Stacked ? U(34) : U(20);

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = ChipH + U(6);

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        if (_chips.Count == 0) return;
        float gap = U(4), w = (R(rc) - Left - gap * (_chips.Count - 1)) / _chips.Count, y = (float)Y + U(3);
        var border = rc.Brush(C(rc, Card.Down ? "inactiveButton" : "rule"));
        for (int i = 0; i < _chips.Count; i++)
        {
            float x = Left + i * (w + gap);
            var (label, v) = _chips[i];
            rc.DC.DrawRoundedRectangle(new RoundedRectangle { Rect = new Rect(x + U(0.5), y + U(0.5), w - U(1), ChipH - U(1)), RadiusX = U(3), RadiusY = U(3) }, border, U(1));
            var vc = C(rc, v.IsNa ? "inactiveButton" : "text");
            if (block.Stacked)
            {
                Text(rc, label, ChipLabel, C(rc, "text2"), x + U(8), y + U(12));
                Reading(rc, v, ChipVal, ChipUnit, vc, C(rc, "text2"), x + U(8), y + U(28), alignLeft: true);
            }
            else
            {
                Text(rc, label, ChipLabel, C(rc, "text2"), x + U(8), y + U(13.3));
                Reading(rc, v, ChipVal, ChipUnit, vc, C(rc, "text2"), x + w - U(8), y + U(14.6));
            }
        }
    }
}

/// <summary>BA list header: pale band, a 2 px tick at the left, tracked caps, a note at the right.</summary>
internal sealed class BandEl(AzurCard card, HeadingBlock block) : AzEl(card)
{
    private string _text = "", _right = "";

    protected override string Resolve(PanelContext c)
    {
        _text = block.Text(c).ToUpperInvariant();
        _right = block.Right?.Invoke(c) ?? "";
        return _text + "|" + _right + Card.Down;
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = U(6 + 15 + 4);

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        float y = (float)Y + U(6), h = U(15), right = R(rc);
        rc.DC.FillRectangle(new Rect(Left, y, right - Left, h), rc.Brush(C(rc, "bandFill")));
        rc.DC.FillRectangle(new Rect(Left, y, U(2), h), rc.Brush(C(rc, Card.Data("bandTick"))));
        Text(rc, _text, Band, C(rc, "text2"), Left + U(9), y + h / 2 + U(3.3));
        Text(rc, _right, BandRight, C(rc, "text2"), right - U(7), y + h / 2 + U(3.9), TextAlign.Right);
    }
}

/// <summary>The compact per-core grid: label, a skewed mini-bar, the percent.</summary>
internal sealed class GridEl(AzurCard card, GridBlock block) : AzEl(card)
{
    private readonly Val[] _v = new Val[block.Cells.Count];
    private readonly double[] _f = new double[block.Cells.Count];
    private int Rows => (block.Cells.Count + block.Columns - 1) / block.Columns;

    protected override string Resolve(PanelContext c)
    {
        var sb = new System.Text.StringBuilder(block.Cells.Count * 6);
        for (int i = 0; i < _v.Length; i++)
        {
            var cell = block.Cells[i];
            if (cell.IsHeading) continue;
            _v[i] = cell.Value(c);
            _f[i] = cell.Fraction(c);
            sb.Append(_v[i].Text).Append(Math.Round(_f[i] * 200)).Append(',');
        }
        return sb.Append(Card.Down).ToString();
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = Rows * U(15) - U(3) + U(2);

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        float x0 = Left + U(2), w = R(rc) - U(2) - x0, gap = U(14);
        float colW = (w - gap * (block.Columns - 1)) / block.Columns;
        var track = rc.Brush(C(rc, "emptyBar"));
        var fill = rc.Brush(C(rc, Card.Data("bar")));
        var skew = Matrix3x2.CreateSkew(MathF.Atan(-0.364f), 0);
        int rows = Rows;
        for (int i = 0; i < block.Cells.Count; i++)
        {
            int col = i / rows, row = i % rows;     // C1..C8 down the left, C9.. down the right
            float x = x0 + col * (colW + gap), y = (float)Y + U(1) + row * U(15), mid = y + U(6);
            var cell = block.Cells[i];
            // text2, not faint: these identify the cores, and faint was 2.88:1 on the body
            Text(rc, cell.Label, Grid, C(rc, "text2"), x, mid + U(4.2));
            if (cell.IsHeading) continue;
            var v = _v[i];
            Text(rc, v.IsNa ? "N/A" : v.Text + v.Unit, Grid, C(rc, v.IsNa ? "inactiveButton" : "text"), x + colW, mid + U(4.2), TextAlign.Right);
            float bx = x + U(20) + U(6), bw = colW - U(20) - U(6) - U(6) - U(28), bh = U(5);
            var saved = rc.DC.Transform;
            rc.DC.Transform = Matrix3x2.CreateTranslation(-bx, -mid) * skew * Matrix3x2.CreateTranslation(bx, mid) * saved;
            rc.DC.FillRectangle(new Rect(bx, mid - bh / 2, bw, bh), track);
            if (!double.IsNaN(_f[i]) && !Card.Down)
                rc.DC.FillRectangle(new Rect(bx, mid - bh / 2, (float)(bw * Math.Clamp(_f[i], 0, 1)), bh), fill);
            rc.DC.Transform = saved;
        }
    }
}

/// <summary>One drive as a dock slot: letter in a diamond, label, used / total, bar, and a caption
/// row of temperature, read, write and percent. <c>DOCK FULL</c> at ≥ 95 %.</summary>
internal sealed class VolumeEl(AzurCard card, VolumeBlock block) : AzEl(card)
{
    private string _label = "";
    private Val _used, _temp, _read, _write;
    private double _frac;
    private bool _warn;
    private WarnLevel _tl;

    protected override string Resolve(PanelContext c)
    {
        _label = block.Label(c);
        _used = block.Used(c);
        _frac = block.Fraction(c);
        _temp = block.Temp(c);
        _tl = block.TempWarn(c);
        _read = block.Read(c);
        _write = block.Write(c);
        _warn = block.BarWarn(c);
        return $"{_label}|{_used}|{Math.Round(_frac * 400)}|{_temp}|{_tl}|{_read}|{_write}|{_warn}|{Card.Level}|{Card.Down}";
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = U(55);

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        float top = (float)Y + U(5), mid = top + U(23), right = R(rc);
        float dcx = Left + U(4) + U(15);
        var dia = Diamond(rc, dcx, mid, U(15) * 1.414f / 1.05f);
        rc.DC.FillGeometry(dia, rc.Brush(C(rc, "diamondFill")));
        rc.DC.DrawGeometry(dia, rc.Brush(C(rc, Card.Down ? "inactiveButton" : Az.KeyToken("drives"))), U(1.6));
        Text(rc, block.Letter.ToString(), Bar(15, FontWeight.Bold), C(rc, "text"), dcx, mid + U(5.4), TextAlign.Center);

        float x = Left + U(4 + 30 + 4 + 9);
        float line1 = top + U(13);
        float lw = Text(rc, _label, RowLabel, C(rc, "text"), x, line1);
        bool full = !double.IsNaN(_frac) && _frac >= 0.95;
        if (full)
        {
            const string tag = "DOCK FULL";
            var tagStyle = Ox(8, 0.5f);
            float tx = x + lw + U(8), tw = W(rc, tag, tagStyle) + U(14);
            rc.DC.FillGeometry(CutRight(rc, tx, line1 - U(9.5), tw, U(12), U(4)), rc.Brush(C(rc, "barWarn")));
            Text(rc, tag, tagStyle, C(rc, "warnText"), tx + U(6), line1 - U(1));
        }
        Reading(rc, _used, Bar(15, FontWeight.Medium), Bar(11, FontWeight.Medium), C(rc, _used.IsNa ? "inactiveButton" : "text"), C(rc, "text2"), right, line1 + U(0.5));
        DrawBar(rc, x, top + U(18), right - x, _frac, _warn);

        float line3 = top + U(41);
        var cap = Ox(8.5, 0.3f);
        var val = Bar(11.5, FontWeight.Medium);
        float cx = x;
        void Pair(string label, Val v, string token = "text")
        {
            if (v.Text == null) return;
            cx += Text(rc, label, cap, C(rc, "text2"), cx, line3) + U(3);
            cx += Text(rc, v.IsNa ? "N/A" : v.Text + (v.Unit.Length > 0 ? (v.Unit.StartsWith('°') ? "" : " ") + v.Unit : ""), val, C(rc, v.IsNa ? "inactiveButton" : token), cx, line3) + U(10);
        }
        Pair("TEMP", _temp, _tl >= WarnLevel.L4 ? AzurCard.HeroToken(_tl, false) : "text");
        Pair("R", _read);
        Pair("W", _write);
        if (!double.IsNaN(_frac))
            Text(rc, ValueFormat.Int0(_frac * 100) + "%", val, C(rc, "text"), right, line3, TextAlign.Right);
    }
}

/// <summary>
/// Network traffic as two MomoTalk bubbles on one row, a character at each end:
/// <c>[face][DOWN] … [UP][face]</c>. DOWN is incoming (white, sharp corner toward its sender on the
/// left), UP outgoing (filled, sharp corner toward the right). The faces are <see cref="CastEl"/>s
/// laid over this row (<see cref="AzurArchiveSkin"/>); this element only keeps their seats clear,
/// and only when someone is actually sitting there.
/// </summary>
internal sealed class TrafficEl(AzurCard card, TrafficBlock block) : AzEl(card)
{
    /// <summary>Face size and the row's own height. The row is shorter than the old staggered
    /// bubbles (70), so the card never grows.</summary>
    public static readonly float Face = U(44), RowH = U(58), FaceBottom = U(52);
    /// <summary>The right seat's default: Kotama, Chihiro's Veritas partner, on the UP side.</summary>
    public const string SecondDefault = "kotama";

    private Val _down, _up;
    private string _dl = "", _ul = "";
    private bool _showDown, _showUp, _leftSeat, _rightSeat;

    protected override string Resolve(PanelContext c)
    {
        // each bubble follows its own metric's show setting, like the two halves of a stat pair
        _showDown = block.Down.Visible?.Invoke(c) ?? true;
        _showUp = block.Up.Visible?.Invoke(c) ?? true;
        _down = block.Down.Value(c);
        _up = block.Up.Value(c);
        _dl = block.Down.Label(c).ToUpperInvariant();
        _ul = block.Up.Label(c).ToUpperInvariant();
        _leftSeat = Seated(c, Card.Character, "character");
        _rightSeat = Seated(c, SecondDefault, "character2");
        return $"{_showDown}|{_showUp}|{_down}|{_up}|{_dl}|{_ul}|{Card.Down}|{Card.Missing}|{_leftSeat}|{_rightSeat}";
    }

    /// <summary>Is a face drawn in this seat? With no data both seats empty and Manjuu's sign takes
    /// the right (<see cref="CastEl.ShowNap"/>), so the boxes close up to the left edge.</summary>
    private bool Seated(PanelContext c, string rosterDefault, string option)
        => Card.Missing == NoData.None && AzurCast.ArtOn(c.Theme)
           && AzurCast.Who(c.Theme, rosterDefault, option) is { } who && AzurCast.Slot(who, "") != null;

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = RowH;

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        float y = (float)Y + FaceBottom - Face / 2 - U(21);
        var num = Bar(22, FontWeight.Medium);
        var unit = Bar(12, FontWeight.Medium);
        float gap = U(6);
        float left = Left + (_leftSeat ? Face + gap : 0);
        // no data: the right end is Manjuu's sign (96 px), as on a card with nothing to list
        float right = Right(rc.Theme) - (_rightSeat ? Face + gap : Card.Missing != NoData.None ? U(104) : 0);
        if (_showDown) Bubble(rc, left, y, _dl, true, _down, num, unit, false);
        if (_showUp) Bubble(rc, right - BubbleW(rc, _ul, _up, num, unit), y, _ul, false, _up, num, unit, true);
    }

    private static float BubbleW(RenderContext rc, string label, Val v, TextStyle num, TextStyle unit)
    {
        float wv = W(rc, v.Text, num) + (v.Unit.Length > 0 ? U(2) + W(rc, v.Unit, unit) : 0);
        float wl = W(rc, label, Caption) + U(11);
        return Math.Max(wv, wl) + U(20);
    }

    private float Bubble(RenderContext rc, float x, float y, string label, bool down, Val v, TextStyle num, TextStyle unit, bool outgoing)
    {
        float w = BubbleW(rc, label, v, num, unit), h = U(42);
        var rect = new Rect(x, y, w, h);
        var shape = Bubble(rc, rect, U(9), U(2), outgoing);
        // the bubble has its own fill so the upload graph line (netUp) keeps its colour
        var fill = outgoing ? C(rc, Card.Down ? "inactiveButton" : "netUpFill") : C(rc, "diamondFill");
        rc.DC.FillGeometry(shape, rc.Brush(fill));
        if (!outgoing) rc.DC.DrawGeometry(shape, rc.Brush(C(rc, "rule")), U(1));
        // outgoing ink is the preset's netUpText, which the contrast gate holds to 4.5:1 on netUpFill;
        // a card that is down greys the bubble, so then it is whichever of black and white reads on grey
        var onFill = !Card.Down ? C(rc, "netUpText")
            : Luma(fill) > 0.55f ? new Color4(0, 0, 0, 1) : new Color4(1, 1, 1, 1);
        var ink = outgoing ? onFill : C(rc, "text");
        // full strength: the caption and unit at 85 % fell under 4.5:1
        var mut = outgoing ? onFill : C(rc, "text2");
        float tx = x + U(10);
        // ▼ / ▲ as vector triangles: the bundled faces have no arrows
        float ay = y + U(10.5), s = U(3.2);
        var tri = down
            ? Poly(rc, "tdn", tx, ay, s, 0, 0, () => [new(tx, ay - s), new(tx + 2 * s, ay - s), new(tx + s, ay + s * 0.7f)])
            : Poly(rc, "tup", tx, ay, s, 0, 0, () => [new(tx + s, ay - s), new(tx + 2 * s, ay + s * 0.7f), new(tx, ay + s * 0.7f)]);
        rc.DC.FillGeometry(tri, rc.Brush(mut));
        Text(rc, label, Caption, mut, tx + U(11), y + U(14));
        Reading(rc, v, num, unit, v.IsNa && !outgoing ? C(rc, "inactiveButton") : ink, mut, tx, y + U(35), alignLeft: true, gap: 2);
        return x + w;
    }

    private static float Luma(Color4 c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;

    /// <summary>A rounded box with one sharp corner: bottom-left for incoming, bottom-right for outgoing.</summary>
    private static ID2D1Geometry Bubble(RenderContext rc, Rect r, float big, float small, bool outgoing)
        => Poly(rc, outgoing ? "bubO" : "bubI", r.Left, r.Top, r.Width, r.Height, big, () =>
        {
            float bl = outgoing ? big : small, br = outgoing ? small : big;
            var pts = new List<Vector2>();
            void Arc(float cx, float cy, float rad, float a0)
            {
                for (int k = 0; k <= 6; k++)
                {
                    float a = a0 + k * MathF.PI / 12;
                    pts.Add(new(cx + rad * MathF.Cos(a), cy + rad * MathF.Sin(a)));
                }
            }
            Arc(r.Left + big, r.Top + big, big, MathF.PI);
            Arc(r.Right - big, r.Top + big, big, -MathF.PI / 2);
            Arc(r.Right - br, r.Bottom - br, br, 0);
            Arc(r.Left + bl, r.Bottom - bl, bl, MathF.PI / 2);
            return pts.ToArray();
        });
}

/// <summary>The optional ticket footer: <c>HALO ✦ CPU</c>. No arrow pill — it looked clickable
/// and did nothing (§4.1).</summary>
internal sealed class FooterEl(AzurCard card, string name) : AzEl(card)
{
    protected override string Resolve(PanelContext c) => name;

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = U(16);

    public override void Draw(RenderContext rc, PanelContext ctx)
        => Text(rc, "HALO ✦ " + name, Foot, C(rc, "faint"), Left + U(2), (float)Y + U(12));
}
