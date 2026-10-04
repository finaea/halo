using System.Diagnostics;
using System.Numerics;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Shared.Panels;
using Halo.Shared.Skins;
using Halo.Widgets;
using Halo.Widgets.Harness;
using Halo.Widgets.Render;
using Halo.Widgets.Skins;
using Halo.Widgets.Skins.AzurArchive;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Halo.Tests;

/// <summary>MomoTalk's wheel scroll (offset arithmetic, and through the element), and the ambient bake
/// staying inside its own region of the shared atlas.</summary>
[Collection(LogTestCollection.Name)]
public sealed class AzurScrollAndBustTests
{
    private static readonly Lazy<Dx> SharedDx = new(() => new Dx(Paths.FontsDir, warp: true));
    private static readonly long Hz = Stopwatch.Frequency;
    private static long S(double s) => (long)(s * Hz);

    // ---- a) TalkScroll (pixel offsets) ----

    private const float Step = 14.85f / 1.7f;

    private static TalkScroll Laid(float max = 100)
    {
        var s = new TalkScroll();
        s.Layout(max, _ => 0);
        return s;
    }

    [Fact]
    public void Wheel_moves_by_step_per_notch_and_clamps_at_Max_and_at_zero()
    {
        var s = Laid(30);
        Assert.True(s.Wheel(1, Step, 0));
        Assert.Equal(Step, s.Offset, 3);
        Assert.True(s.Wheel(1, Step, 0));
        Assert.Equal(2 * Step, s.Offset, 3);
        Assert.True(s.Wheel(10, Step, 0));
        Assert.Equal(30, s.Offset);                 // Max
        Assert.False(s.Wheel(1, Step, 0));
        Assert.True(s.Wheel(-1, Step, 0));
        Assert.Equal(30 - Step, s.Offset, 3);
        Assert.True(s.Wheel(-10, Step, 0));
        Assert.Equal(0, s.Offset);
        Assert.False(s.Wheel(-1, Step, 0));         // wheel-down at newest stays and reports no move
        Assert.Equal(0, s.Offset);
    }

    [Fact]
    public void A_thread_that_fits_the_zone_cannot_scroll()
    {
        var s = Laid(0);
        Assert.False(s.Wheel(1, Step, 0));
        Assert.Equal(0, s.Offset);
    }

    [Fact]
    public void Messages_that_arrive_while_scrolled_back_raise_Offset_by_their_height_exactly_once()
    {
        var s = Laid(200);
        s.Sync([1, 2, 3, 4], 0);
        s.Wheel(2, Step, 0);
        float before = s.Offset;
        s.Sync([1, 2, 3, 4, 5, 6], S(1));           // two new
        int asked = 0;
        s.Layout(200, k => { asked = k; return k * 40f; });
        Assert.Equal(2, asked);
        Assert.Equal(before + 80, s.Offset, 3);
        bool called = false;
        s.Layout(200, k => { called = true; return 999; });   // nothing fresh now: adds nothing
        Assert.False(called);
        Assert.Equal(before + 80, s.Offset, 3);
    }

    [Fact]
    public void A_new_message_at_the_newest_does_not_move_the_view()
    {
        var s = Laid(200);
        s.Sync([1, 2], 0);
        s.Sync([1, 2, 3], S(1));
        s.Layout(200, k => 40f * k);
        Assert.Equal(0, s.Offset);
    }

    [Fact]
    public void Lines_with_seq_zero_are_not_new_messages()
    {
        var s = Laid(200);
        s.Sync([1, 2, 3], 0);
        s.Wheel(2, Step, 0);
        float before = s.Offset;
        s.Sync([1, 2, 3, 0], S(1));                 // a system note appended
        s.Layout(200, k => k * 40f);
        Assert.Equal(before, s.Offset, 3);
    }

    [Fact]
    public void The_view_returns_to_newest_after_twenty_seconds_without_wheel_and_not_before()
    {
        var s = Laid();
        long t0 = S(100);
        s.Sync([1, 2, 3], t0);
        s.Wheel(3, Step, t0);
        s.Sync([1, 2, 3], t0 + S(TalkScroll.ReturnS) - 1);
        Assert.True(s.Offset > 0);
        s.Sync([1, 2, 3], t0 + S(TalkScroll.ReturnS));
        Assert.Equal(0, s.Offset);
    }

    [Fact]
    public void A_wheel_turn_restarts_the_twenty_second_clock()
    {
        var s = Laid();
        s.Sync([1, 2, 3], 0);
        s.Wheel(2, Step, 0);
        s.Wheel(1, Step, S(15));
        s.Sync([1, 2, 3], S(30));                   // 15 s since the last turn
        Assert.True(s.Offset > 0);
        s.Sync([1, 2, 3], S(35));
        Assert.Equal(0, s.Offset);
    }

    [Fact]
    public void Layout_clamps_Offset_when_Max_shrinks()
    {
        var s = Laid(100);
        s.Wheel(50, Step, 0);
        Assert.Equal(100, s.Offset);
        s.Layout(40, _ => 0);
        Assert.Equal(40, s.Offset);
        Assert.Equal(40, s.Max);
        s.Layout(0, _ => 0);
        Assert.Equal(0, s.Offset);
    }

    [Fact]
    public void ToNewest_returns_to_zero_reports_the_move_and_forgets_pending_arrivals()
    {
        var s = Laid(100);
        Assert.False(s.ToNewest());
        s.Sync([1, 2], 0);
        s.Wheel(2, Step, 0);
        s.Sync([1, 2, 3], S(1));                    // one fresh, not yet laid out
        Assert.True(s.ToNewest());
        Assert.Equal(0, s.Offset);
        s.Wheel(1, Step, S(2));
        float o = s.Offset;
        s.Layout(100, k => 40f * k);                // the dropped arrival must not resurface
        Assert.Equal(o, s.Offset, 3);
    }

    // ---- b) through CompanionEl ----

    private sealed class Rig : IDisposable
    {
        public Panel Panel = null!;
        public CompanionEl El = null!;
        public PanelContext Ctx = null!;
        public RenderContext Rc = null!;
        public ID2D1DeviceContext Dc = null!;
        public ID2D1Bitmap1 Target = null!;
        public Action Redraw = null!;
        public double ZoneTop, ZoneBottom;

        public void Dispose() { Panel.Dispose(); Rc.Dispose(); Target.Dispose(); Dc.Dispose(); }
    }

    private static Rig Build(bool talk)
    {
        var settings = new AppSettings();
        settings.Appearance.Skin = AzurArchiveSkinInfo.Id;
        var widget = new WidgetInstance { Id = "t", Type = "companion" };
        var widgets = new[] { widget };
        var metrics = FixtureMetrics.Load("mood-crosstalk");
        var mood = new SystemMood(() => widgets, AzurArchiveSkin.Birthdays);
        var theme = Theme.Resolve(settings.Appearance, widget, 1.7);
        theme.Dpi = 96;
        if (!talk) theme.SkinOptions["talk"] = "false";
        var ctx = new PanelContext { Metrics = metrics, Mood = mood, Theme = theme, Settings = settings, Widget = widget, Type = PanelCatalog.Find("companion") };
        var panel = PanelFactory.Create("companion", ctx)!;

        long step = (long)(PanelRenderer.TickS * Hz), start = 1000L * Hz;
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
        var dc = dx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        var rc = new RenderContext(dc, dx.DWrite, dx.CustomFonts, theme);
        double h = panel.Layout(rc, ctx);
        int w = (int)Math.Ceiling(theme.BgWidth * theme.EffectiveScale), ph = Math.Max(8, (int)Math.Ceiling(h * theme.EffectiveScale));
        var fmt = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        var target = dc.CreateBitmap(new SizeI(w, ph), IntPtr.Zero, 0,
            new BitmapProperties1(fmt, 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        var rig = new Rig { Panel = panel, Ctx = ctx, Rc = rc, Dc = dc, Target = target, El = panel.Elements.OfType<CompanionEl>().Single() };
        rig.Redraw = () =>
        {
            dc.Target = target;
            dc.SetDpi(96, 96);
            dc.BeginDraw();
            dc.Transform = Matrix3x2.CreateScale((float)theme.BaseScale);
            panel.Draw(rc, ctx);
            dc.EndDraw();
            dc.Target = null;
        };
        rig.Redraw();
        // the zone body: below the 102 host row and 22 header, 104 tall (all /1.7 logical units)
        rig.ZoneTop = rig.El.Y + (102 + 22) / 1.7;
        rig.ZoneBottom = rig.ZoneTop + 104 / 1.7;
        return rig;
    }

    [Fact]
    public void Wheel_over_the_MomoTalk_zone_scrolls_and_outside_it_does_not()
    {
        using var r = Build(talk: true);
        double inZoneY = r.ZoneBottom - 20 / 1.7;
        Assert.True(r.Ctx.Mood.Messages.Count > 1, "the fixture should leave a thread to scroll");
        Assert.True(r.El.Wheel(r.Ctx, 100, inZoneY, 1));
        Assert.False(r.El.Wheel(r.Ctx, 100, r.El.Y + 10 / 1.7, 1));          // the host row
        Assert.False(r.El.Wheel(r.Ctx, 100, r.ZoneBottom + 2, 1));           // below the zone
        Assert.False(r.El.Wheel(r.Ctx, -20, inZoneY, 1));                    // left of the card
        Assert.True(r.El.Wheel(r.Ctx, 100, inZoneY, -1));                    // back down: moves
        Assert.False(r.El.Wheel(r.Ctx, 100, inZoneY, -1));                   // already newest
    }

    [Fact]
    public void Wheel_with_talk_off_does_nothing()
    {
        using var r = Build(talk: false);
        Assert.False(r.El.Wheel(r.Ctx, 100, r.El.Y + r.El.Height / 2, 1));
    }

    [Fact]
    public void A_message_cut_by_the_zones_top_is_drawn_and_its_hit_rect_starts_at_the_zone_top()
    {
        using var r = Build(talk: true);
        Assert.True(r.El.Wheel(r.Ctx, 100, r.ZoneBottom - 5, 1), "the thread must overflow the zone");
        r.El.Wheel(r.Ctx, 100, r.ZoneBottom - 5, -1);
        double y = r.ZoneTop + 0.05;
        bool topHit = false, aboveHit = false;
        for (double x = -20; x < 400; x += 1)
        {
            if (r.El.BubbleAt(x, y) != null) topHit = true;
            if (r.El.BubbleAt(x, r.ZoneTop - 1) != null) aboveHit = true;
        }
        Assert.True(topHit, "a cut-off message should still answer a click at the zone's top");
        Assert.False(aboveHit);
    }

    [Fact]
    public void Poke_on_the_NEWER_pill_returns_to_newest()
    {
        using var r = Build(talk: true);
        double pillY = r.ZoneBottom - 5 / 1.7 - 7 / 1.7;
        Assert.True(r.El.Wheel(r.Ctx, 100, r.ZoneBottom - 5, 100));          // all the way back
        r.Redraw();                                                         // the pill is drawn
        int hits = 0;
        for (double x = 0; x < 300; x += 1)
        {
            r.El.Poke(r.Ctx, x, pillY);
            bool atNewest = !r.El.Wheel(r.Ctx, 100, r.ZoneBottom - 5, -1);   // a move down means it was not
            if (atNewest) { hits++; r.El.Wheel(r.Ctx, 100, r.ZoneBottom - 5, 100); r.Redraw(); }
            else r.El.Wheel(r.Ctx, 100, r.ZoneBottom - 5, 1);
        }
        Assert.True(hits > 0, "a click on the pill should return to newest");
        r.El.Poke(r.Ctx, -20, pillY);                                       // off the card: not the pill
        Assert.True(r.El.Wheel(r.Ctx, 100, r.ZoneBottom - 5, -1));
    }

    // ---- c) AmbientVisual.Paint stays inside its region ----

    private static (PanelImage Img, Matrix3x2 After) PaintOnto(bool old)
    {
        var dx = SharedDx.Value;
        using var dc = dx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        using var rc = new RenderContext(dc, dx.DWrite, dx.CustomFonts, new Theme());
        var fmt = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var target = dc.CreateBitmap(new SizeI(200, 120), IntPtr.Zero, 0,
            new BitmapProperties1(fmt, 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        dc.Target = target;
        dc.SetDpi(96, 96);
        dc.BeginDraw();
        dc.Clear(new Color4(1, 0, 0, 1));
        dc.Transform = Matrix3x2.CreateScale(1.7f);         // Render's leftover world transform

        var loop = new AmbientLoop { Kind = AmbientKind.Spin, Key = "k", Bounds = new Rect(10, 10, 28, 8), Color = new Color4(0, 0, 1, 1), Stroke = 2 };
        float s = 1.7f, pad = MathF.Ceiling(loop.Stroke * 2 * s) + 2;
        uint w = (uint)Math.Ceiling(loop.Bounds.Width * s + 2 * pad), h = w;   // a spin bakes flat-on
        var off = new Int2(37, 23);
        if (old) OldPaint(dc, off, w, h);
        else AmbientVisual.Paint(dc, rc, loop, off, w, h, s, pad, 1f);
        var after = dc.Transform;
        dc.EndDraw();
        dc.Target = null;
        return (PanelRenderer.Read(dc, target, 200, 120), after);
    }

    /// <summary>The pre-fix shape, kept only to prove the check bites: the clip is pushed while the
    /// context still carries the world transform, so it (and the Clear inside it) lands scaled off
    /// the region.</summary>
    private static void OldPaint(ID2D1DeviceContext dc, Int2 off, uint w, uint h)
    {
        dc.PushAxisAlignedClip(new Rect(off.X, off.Y, w, h), AntialiasMode.Aliased);
        dc.Clear(new Color4(0, 0, 0, 0));
        dc.PopAxisAlignedClip();
        dc.Transform = Matrix3x2.Identity;
    }

    private static bool OpaqueRed(PanelImage img, int x, int y)
    {
        int i = (y * img.Width + x) * 4;       // BGRA
        return img.Pixels[i] == 0 && img.Pixels[i + 1] == 0 && img.Pixels[i + 2] == 255 && img.Pixels[i + 3] == 255;
    }

    private static (int OutsideNotRed, int InsideRed) Count(PanelImage img, int x0, int y0, int w, int h)
    {
        int outside = 0, inside = 0;
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                bool inRegion = x >= x0 && x < x0 + w && y >= y0 && y < y0 + h;
                bool red = OpaqueRed(img, x, y);
                if (inRegion && red) inside++;
                if (!inRegion && !red) outside++;
            }
        return (outside, inside);
    }

    [Fact]
    public void Paint_clears_and_draws_only_inside_its_region_whatever_transform_the_context_carries()
    {
        const int X = 37, Y = 23, Size = 66;       // ceil(28 * 1.7 + 2 * 9)
        var (img, after) = PaintOnto(old: false);
        var (outside, inside) = Count(img, X, Y, Size, Size);
        Assert.Equal(0, outside);       // a neighbour's pixels in the shared atlas are untouched
        Assert.Equal(0, inside);        // cleared, then the ring drawn: none of the old content left
        Assert.Equal(Matrix3x2.Identity, after);

        bool drew = false;              // and the ring really is there (blue, premultiplied)
        for (int y = Y; y < Y + Size && !drew; y++)
            for (int x = X; x < X + Size; x++)
                if (img.Pixels[(y * img.Width + x) * 4] > 0) { drew = true; break; }
        Assert.True(drew, "the ring should be visible");
    }

    [Fact]
    public void The_pre_fix_clip_under_the_world_transform_violates_the_same_check()
    {
        var (img, _) = PaintOnto(old: true);
        var (outside, inside) = Count(img, 37, 23, 66, 66);
        Assert.True(outside > 0 || inside > 0, "the pre-fix shape must break the region contract");
    }
}
