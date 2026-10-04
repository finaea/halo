using System.Numerics;
using Halo.Widgets.PanelModels;
using Halo.Widgets.Render;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using static Halo.Widgets.Skins.AzurArchive.Az;

namespace Halo.Widgets.Skins.AzurArchive;

/// <summary>
/// The cast on the cards (design §9). A card's character is the panel's roster default or the
/// widget's <c>character</c> option, and her face follows the card's own warn step:
/// <c>&lt;character&gt;-&lt;mood&gt;.png</c> → <c>&lt;character&gt;.png</c> plus our vector overlay → nothing.
/// When the card has no data she steps out and Manjuu naps behind a sign saying why.
///
/// <para>Every image goes through <see cref="SkinAssets"/>, so a user's own file wins, a missing one
/// falls through, and with <c>gameArt</c> off — or the <c>game-art</c> folder deleted — the card is
/// simply drawn without a picture. The sign is ours and vector, so it stays either way.</para>
/// </summary>
internal static class AzurCast
{
    /// <summary>Each character's halo colour: her in-game halo's, matched by eye from her portraits
    /// (D17), deepened where a pale one (Akane's cream, Hare's and Hibiki's yellow) would vanish on
    /// a light card. Neru's halo is near-black with a yellow mark: hers is the yellow, and her shape
    /// tones its rings dark.</summary>
    private static readonly Dictionary<string, Color4> Halos = new()
    {
        ["yuuka"] = Hex(0x4AA8F5), ["momoi"] = Hex(0xF59AD6), ["midori"] = Hex(0x7EE29A), ["utaha"] = Hex(0xF06AA6),
        ["chihiro"] = Hex(0x4FC6F2), ["noa"] = Hex(0x6FA6EE), ["arona"] = Hex(0x3DD2F9), ["plana"] = Hex(0xF2496F),
        ["aris"] = Hex(0x6EE3E6), ["asuna"] = Hex(0x2ED3F2), ["akane"] = Hex(0xD2B07E), ["kotama"] = Hex(0x9E98F2),
        ["toki"] = Hex(0x4A8EE0), ["koyuki"] = Hex(0xF286C4), ["yuzu"] = Hex(0xF2A93E), ["kotori"] = Hex(0xEDB24A),
        ["maki"] = Hex(0xE8413F), ["neru"] = Hex(0xE8BE38), ["karin"] = Hex(0xB287EE), ["hare"] = Hex(0xE0C21C),
        ["hibiki"] = Hex(0xE3C651),
    };

    private static Color4 Hex(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1);

    public static Color4 HaloOf(string character) => Halos.GetValueOrDefault(character, Hex(0x3DD2F9));

    public static SkinAssets Assets => SkinAssets.For(Halo.Shared.Skins.AzurArchiveSkinInfo.Id);

    public static bool ArtOn(Theme t) => AzurChrome.Option(t, "gameArt") != "false";

    /// <summary>The <c>halo</c> option: off draws faces with no ring and offers no spin loop.</summary>
    public static bool HaloOn(Theme t) => AzurChrome.Option(t, "halo") != "false";

    /// <summary>The options a face's look depends on, for an element's resolve key.</summary>
    public static string FaceKey(Theme t) => $"{ArtOn(t)}|{HaloOn(t)}";

    /// <summary>Who sits in a seat on this card: the seat's option (<c>character</c>, or
    /// <c>character2</c> for the network card's second seat), else the roster default; null = nobody.</summary>
    public static string? Who(Theme t, string rosterDefault, string option = "character")
    {
        string pick = AzurChrome.Option(t, option);
        return pick is "" or "auto" ? rosterDefault : pick == "none" ? null : pick;
    }

    /// <summary>The slot to draw for a character in a mood, or null when not even her neutral file
    /// exists. Resolution is remembered by <see cref="SkinAssets"/>, so this does not stat the disk
    /// on a repaint.</summary>
    public static string? Slot(string character, string mood)
    {
        if (mood.Length > 0 && Assets.Resolve(character + "-" + mood, Bundled(character + "-" + mood)).Count > 0)
            return character + "-" + mood;
        return Assets.Resolve(character, Bundled(character)).Count > 0 ? character : null;
    }

    public static string Bundled(string slot) => $"game-art/blue-archive/{slot}.png";

    /// <summary>
    /// The card face and overlay: the card's own warn step, or the machine's mood when that is
    /// worse (design §9.5 — a GPU running hot makes everyone sweat), then a poke, then the moment
    /// reactions (Yuuka over budget and relieved, Chihiro at peace on a quiet line), then the
    /// mood's resting face — relaxed when idle, working when busy — and only where that art exists,
    /// so a student without a working face keeps her card's resting one instead of falling back to
    /// neutral.
    /// </summary>
    public static (string Mood, AzurIcons.Overlay Fx) Mood(AzurCard card, string? who, PanelContext c)
    {
        var m = c.Mood;
        var s = m.Known ? m.State : MoodState.Idle;
        if (card.Crit || s == MoodState.Critical) return ("cross", AzurIcons.Overlay.Spiral);
        if (card.Warn || s == MoodState.Hot) return ("hot", AzurIcons.Overlay.Sweat);
        if (who == null || !m.Known) return Rest(card.NormalMood);
        if (m.Poked(who, c.NowQpc)) return ("surprised", AzurIcons.Overlay.None);
        if (who == "yuuka" && m.RamHigh) return ("cross", AzurIcons.Overlay.None);
        if (who == "yuuka" && m.RamRelief(c.NowQpc)) return ("relieved", AzurIcons.Overlay.None);
        if (who == "chihiro" && m.NetQuiet) return ("relaxed", AzurIcons.Overlay.None);
        string mood = s switch
        {
            MoodState.Idle => "relaxed",
            MoodState.Busy => "working",
            _ => "",
        };
        return Rest(mood.Length > 0 && HasArt(who, mood) ? mood : card.NormalMood);
    }

    private static (string, AzurIcons.Overlay) Rest(string mood)
        => (mood, mood == "excited" ? AzurIcons.Overlay.Star : AzurIcons.Overlay.None);

    private static bool HasArt(string character, string mood) => Slot(character, mood) == character + "-" + mood;

    /// <summary>A face crop drawn straight onto the card — no plate, no frame — with only its edge
    /// softened, the character's vector halo over it (option <c>halo</c>) and the mood overlay on its corner (§9.6).
    /// False (and nothing drawn) when there is no art. Given a <paramref name="loop"/> context, the
    /// halo is offered as the widget's ambient loop (motion full) and, when taken, left to its own
    /// visual instead of drawn here. <paramref name="ceiling"/> is the highest the halo may reach
    /// (logical y; default just under the card's header rule).</summary>
    public static bool Face(RenderContext rc, string character, string mood, AzurIcons.Overlay fx, Rect slot,
        float opacity = 1, PanelContext? loop = null, float? ceiling = null)
    {
        var t = rc.Theme;
        if (!ArtOn(t) || Slot(character, mood) is not { } s) return false;

        float px = slot.Width / U(48);

        // The art under a soft radial alpha mask, baked once: a layer and a cubic downscale per
        // repaint were the costliest thing on the card. "feather" is in the key so a bake from the
        // framed version (clipped to a rounded rectangle) is never reused.
        DrawBaked(rc, "face-feather:" + s, slot, b => Feathered(b, slot, () => Assets.Draw(b, s, Bundled(s), slot)), opacity);
        // the halo over the hair, as the spinning one is (its visual sits above the card)
        if (HaloOn(t)) DrawHalo(rc, character, slot, opacity, loop, ceiling ?? ContentTop + U(1));
        AzurIcons.Draw(rc, fx, slot.Right - U(14) * px, slot.Top - U(6) * px, U(20) * px, t);
        return true;
    }

    /// <summary>Solid share of the feather's radius; the rest eases to nothing.</summary>
    internal const float FeatherCore = 0.84f;

    /// <summary>Draw <paramref name="draw"/> through a radial alpha mask: solid out to
    /// <see cref="FeatherCore"/> of the radius, easing to transparent at the slot's edge, so a crop
    /// has no hard border on any wallpaper but is not vignetted either (a core of 0.62 read as a
    /// heavy fade on the drives card — Jack, 2026-10-04). The radius is the half-width: the layer
    /// clips there, so a longer one left about 44 % alpha at each side's midpoint and a straight
    /// edge where the clip cut it.</summary>
    private static void Feathered(RenderContext b, Rect slot, Action draw)
    {
        var centre = new Vector2(slot.Left + slot.Width / 2, slot.Top + slot.Height / 2);
        float r = slot.Width * 0.5f;
        using var stops = b.DC.CreateGradientStopCollection(
        [
            new GradientStop { Position = 0, Color = new Color4(0, 0, 0, 1) },
            new GradientStop { Position = FeatherCore, Color = new Color4(0, 0, 0, 1) },
            new GradientStop { Position = 1, Color = new Color4(0, 0, 0, 0) },
        ]);
        using var mask = b.DC.CreateRadialGradientBrush(new RadialGradientBrushProperties(centre, Vector2.Zero, r, r), stops);
        b.DC.PushLayer(new LayerParameters1 { ContentBounds = slot, OpacityBrush = mask, Opacity = 1, MaskTransform = Matrix3x2.Identity }, null!);
        draw();
        b.DC.PopLayer();
    }

    /// <summary>How far down the face a halo may reach, in px at a 48 px face: the top of the hair.
    /// A halo that would need to come lower than this to clear its ceiling leans flatter instead
    /// (up to <see cref="HaloFlattest"/>), and past that shrinks.</summary>
    internal const float HaloDip = 9, HaloFlattest = 76;

    /// <summary>
    /// A face's halo, posed in 3D over the top of <paramref name="slot"/> (D17): her own shape
    /// (<see cref="HaloShape"/>), or the generic ring, sized, placed and tilted by the shape's own
    /// numbers. It never reaches above <paramref name="ceiling"/> (the header rule): it moves down
    /// first, as far as <see cref="HaloDip"/> into the face, then leans flatter, and past that
    /// shrinks — a seat close under the header gets a flatter halo rather than one across the eyes. Drawn still, or
    /// handed to one of the widget's loops to turn (motion full, design rule 9) unless her shape says it
    /// stays still.
    /// </summary>
    public static void DrawHalo(RenderContext rc, string character, Rect slot, float opacity, PanelContext? loop, float ceiling)
    {
        float px = slot.Width / U(48);
        var shape = HaloShape.For(character) ?? HaloShape.Generic;
        float r = U(24) * shape.Scale * px;
        var c = new Vector2(slot.Left + slot.Width / 2 + U(shape.Dx) * px, slot.Top + U(shape.Dy) * px);
        float pitch = shape.Pitch;
        var box = shape.PosedBounds(r, pitch);
        float room = Math.Max(U(4) * px, slot.Top + U(HaloDip) * px - ceiling);
        while (box.Height > room && pitch < HaloFlattest)
        {
            pitch = Math.Min(HaloFlattest, pitch + 2);
            box = shape.PosedBounds(r, pitch);
        }
        if (box.Height > room)
        {
            // the posed size is proportional to the radius (the perspective is in radii)
            float k = room / box.Height;
            r *= k;
            box = shape.PosedBounds(r, pitch);
        }
        if (c.Y + box.Top < ceiling) c.Y = ceiling - box.Top;
        Ring(rc, HaloOf(character), shape, c, r, pitch, U(2) * px, opacity, shape.Spin ? loop : null);
    }

    /// <summary>A posed halo centred on <paramref name="c"/> with flat-on radius <paramref name="r"/>.</summary>
    public static void Ring(RenderContext rc, Color4 halo, HaloShape shape, Vector2 c, float r, float pitch, float stroke, float opacity, PanelContext? loop)
    {
        var colour = Alpha(halo, 0.9f);
        var box = shape.PosedBounds(r, pitch);
        if (loop != null && AmbientLoop.Offer(loop, new AmbientLoop
        {
            Kind = AmbientKind.Spin,
            Key = $"ring:{shape.Id}:{Alpha(colour, opacity)}:{pitch}:{shape.Roll}:{shape.Depth}",
            Bounds = new Rect(c.X + box.Left, c.Y + box.Top, box.Width, box.Height),
            Color = Alpha(colour, opacity),
            Stroke = stroke,
            Flat = (rcx, centre, radius) => shape.Draw(rcx, Alpha(colour, opacity), centre, radius, radius),
            GlintAt = shape.Glint,
            Centre = c,
            Radius = r,
            Pitch = pitch * MathF.PI / 180,
            Roll = shape.RollRad,
            Depth = shape.Depth,
        })) return;
        shape.DrawPosed(rc, colour, c, r, pitch, opacity);
    }

    /// <summary>
    /// A MomoTalk sender's round icon: Arona's and Plana's own icon crops, everyone else's neutral
    /// face, clipped to a circle and baked. With no art it is a disc in her halo colour with her
    /// initial, so a message never loses its sender.
    /// </summary>
    public static void Icon(RenderContext rc, string who, Rect r)
    {
        var t = rc.Theme;
        string? slot = !ArtOn(t) ? null
            : who is "arona" or "plana" && Assets.Resolve(who + "-icon", Bundled(who + "-icon")).Count > 0 ? who + "-icon"
            : Slot(who, "");
        var circle = new Ellipse(new Vector2(r.Left + r.Width / 2, r.Top + r.Height / 2), r.Width / 2, r.Height / 2);
        if (slot == null)
        {
            var disc = HaloOf(who);
            rc.DC.FillEllipse(circle, rc.Brush(disc));
            Text(rc, who.Length > 0 ? char.ToUpperInvariant(who[0]).ToString() : "?", Bar(13, Vortice.DirectWrite.FontWeight.Bold, tab: false),
                InitialInk(disc), circle.Point.X, r.Top + r.Height / 2 + U(4.5), TextAlign.Center);
            return;
        }
        DrawBaked(rc, $"icon:{slot}|{t.Color("bandFill")}", r, b =>
        {
            b.DC.FillEllipse(circle, b.Brush(t.Color("bandFill")));
            b.DC.PushLayer(new LayerParameters1 { ContentBounds = r, GeometricMask = RoundRect(b, r, r.Width / 2), Opacity = 1, MaskTransform = Matrix3x2.Identity }, null!);
            Assets.Draw(b, slot, Bundled(slot), r);
            b.DC.PopLayer();
        });
        rc.DC.DrawEllipse(circle, rc.Brush(Alpha(t.Color("talkText"), 0.12f)), U(1));
    }

    private static readonly Color4 DarkInk = Hex(0x1E2430);

    /// <summary>The initial's colour on a halo-coloured disc: white or near-black, whichever has
    /// the higher WCAG contrast — white on Hare's yellow disc was unreadable.</summary>
    internal static Color4 InitialInk(Color4 disc)
    {
        double l = Luminance(disc);
        return (1.05) / (l + 0.05) >= (l + 0.05) / (Luminance(DarkInk) + 0.05) ? new Color4(1, 1, 1, 1) : DarkInk;
    }

    private static double Luminance(Color4 c)
    {
        static double Lin(float v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>A click on a face drawn at <paramref name="face"/> (empty = nobody drawn there): she
    /// reacts through the shared mood, so every card she sits on looks surprised together.</summary>
    public static bool Poke(PanelContext c, string? who, Rect face, double x, double y)
    {
        if (who == null || face.Width <= 0 || x < face.Left || x > face.Right || y < face.Top || y > face.Bottom) return false;
        c.Mood.Poke(who, c.NowQpc);
        return true;
    }

    /// <summary>
    /// Manjuu asleep behind a little sign, in a 96 × 52 px box whose bottom-right corner is
    /// (<paramref name="right"/>, <paramref name="bottom"/>). The sign says why there is no data;
    /// the reading itself keeps its own N/A, so one dead sensor never claims the machine is gone.
    /// </summary>
    public static void Nap(RenderContext rc, NoData why, float right, float bottom)
    {
        var t = rc.Theme;
        string[] lines = why switch
        {
            NoData.Collector => ["WAITING FOR", "COLLECTOR"],
            NoData.NoApp => ["NO 3D APP"],
            _ => ["SENSOR", "UNAVAILABLE"],
        };
        float x0 = right - U(96), y0 = bottom - U(52);

        if (ArtOn(t))
        {
            var img = new Rect(right - U(2) - U(46), bottom + U(2) - U(46), U(46), U(46));
            var saved = rc.DC.Transform;
            rc.DC.Transform = Matrix3x2.CreateRotation(-8 * MathF.PI / 180, new Vector2(img.Left + img.Width / 2, img.Top + img.Height / 2)) * saved;
            Assets.Draw(rc, "manjuu", "game-art/azur-lane/manjuu.png", img);
            rc.DC.Transform = saved;
        }

        // zZz, small then large
        var z = Bar(12, Vortice.DirectWrite.FontWeight.Bold, 1, tab: false);
        var zs = Bar(9, Vortice.DirectWrite.FontWeight.Bold, 1, tab: false);
        var zc = t.Color("mascotFx");
        float zx = right - U(42) - U(22), zb = y0 + U(11);
        zx += Text(rc, "z", z, zc, zx, zb);
        zx += Text(rc, "z", zs, zc, zx, zb);
        Text(rc, "Z", z, zc, zx, zb);

        // the sign: two strings, a framed card, tilted -4°
        var font = Ox(7.5, 0.3f);
        float sw = U(58), lh = U(8.6), sh = lines.Length * lh + U(4) + U(3);
        var sign = new Rect(x0, bottom - U(6) - sh, sw, sh);
        var saved2 = rc.DC.Transform;
        rc.DC.Transform = Matrix3x2.CreateRotation(-4 * MathF.PI / 180, new Vector2(sign.Left + sw / 2, sign.Top + sh / 2)) * saved2;
        var border = rc.Brush(t.Color("signBorder"));
        rc.DC.FillRectangle(new Rect(sign.Left + U(12), sign.Top - U(6), U(1), U(6)), border);
        rc.DC.FillRectangle(new Rect(sign.Right - U(13), sign.Top - U(6), U(1), U(6)), border);
        var round = new RoundedRectangle { Rect = sign, RadiusX = U(2), RadiusY = U(2) };
        rc.DC.FillRoundedRectangle(round, rc.Brush(t.Color("signFill")));
        rc.DC.DrawRoundedRectangle(round, border, U(1.5));
        for (int i = 0; i < lines.Length; i++)
            Text(rc, lines[i], font, t.Color("signInk"), sign.Left + sw / 2, sign.Top + U(3) + lh * i + U(6.6), TextAlign.Center);
        rc.DC.Transform = saved2;
    }
}

/// <summary>
/// The character slot beside a block (the hero, or the first row of a hero-less card). It shares
/// its anchor's row and takes no height of its own, so it never pushes the layout; the anchor
/// keeps <see cref="AzEl.RightInset"/> clear for it.
/// </summary>
internal sealed class CastEl : AzEl, IPokeTarget
{
    /// <summary>Slot bottom, measured from the anchor's top.</summary>
    public float Bottom = U(67);
    /// <summary>Slot size; 48 beside a hero.</summary>
    public float Size = U(48);
    /// <summary>Set: the slot's left edge (the network card's left seat). Unset: flush right.</summary>
    public float? LeftX;
    /// <summary>The option that picks who sits here, and who does by default.</summary>
    public string Option = "character";
    public string? Default;
    /// <summary>Whether this seat shows Manjuu's no-data sign. One per card: a second seat just empties.</summary>
    public bool ShowNap = true;
    private string? _who;
    private readonly Fade<Look> _look = new();

    /// <summary>Everything the slot shows: who, her face, its overlay, or why she stepped out.</summary>
    internal readonly record struct Look(string? Who, string Mood, AzurIcons.Overlay Fx, NoData Missing);

    public CastEl(AzurCard card) : base(card)
    {
        SameRow = true;
    }

    protected override string Resolve(PanelContext c)
    {
        _who = AzurCast.Who(c.Theme, Default ?? Card.Character, Option);
        var (mood, fx) = AzurCast.Mood(Card, _who, c);
        _look.Step(c, new Look(_who, mood, fx, Card.Missing));
        return $"{_look.Current}|{AzurCast.FaceKey(c.Theme)}";
    }

    public override void Measure(RenderContext rc, PanelContext ctx) => Height = 0;

    private Rect _face;

    public override void Draw(RenderContext rc, PanelContext ctx)
    {
        float right = Right(rc.Theme), bottom = (float)Y + Bottom;
        _face = default;
        bool settled = !_look.Moving(ctx);
        _look.Draw(rc, ctx, (look, current) =>
        {
            if (look.Missing != NoData.None)
            {
                if (ShowNap) AzurCast.Nap(rc, look.Missing, right, bottom + U(2));
                return;
            }
            if (look.Who == null) return;
            var slot = new Rect(LeftX ?? right - Size, bottom - Size, Size, Size);
            if (AzurCast.Face(rc, look.Who, look.Mood, look.Fx, slot, loop: current && settled ? ctx : null) && current) _face = slot;
        });
    }

    public bool Poke(PanelContext ctx, double x, double y) => AzurCast.Poke(ctx, _who, _face, x, y);
}
