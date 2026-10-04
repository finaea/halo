using Halo.Shared.Skins;
using Halo.Widgets;
using Halo.Widgets.Harness;
using Halo.Widgets.Render;
using Halo.Widgets.Skins.AzurArchive;
using Vortice.Mathematics;

namespace Halo.Tests;

/// <summary>HaloShape.Load's fallback: anything missing or unusable is null (the generic ring), never
/// a throw. Temp files only; no game art needed.</summary>
[Collection(LogTestCollection.Name)]
public sealed class AzurHaloShapeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("halo-shape").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private HaloShape? Load(string json)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return HaloShape.Load(path, "t");
    }

    [Fact]
    public void A_missing_file_is_null()
        => Assert.Null(HaloShape.Load(Path.Combine(_dir, "nope.json"), "nope"));

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{}")]                                                       // no parts key
    [InlineData("{ \"parts\": [] }")]                                        // no parts
    [InlineData("{ \"parts\": [ { \"squiggle\": [1] } ] }")]                 // unknown part
    [InlineData("{ \"parts\": [ { \"ring\": [] } ] }")]                      // too few numbers
    [InlineData("{ \"parts\": [ { \"poly\": [0,0,1,1,2] } ] }")]             // odd poly
    [InlineData("{ \"tilt\": 0, \"parts\": [ { \"ring\": [1] } ] }")]        // tilt too small
    [InlineData("{ \"tilt\": 9, \"parts\": [ { \"ring\": [1] } ] }")]        // tilt too big
    [InlineData("{ \"glint\": [1], \"parts\": [ { \"ring\": [1] } ] }")]     // glint short
    public void An_unusable_file_is_null_without_throwing(string json)
        => Assert.Null(Load(json));

    [Fact]
    public void A_minimal_file_parses_with_its_tilt_spin_glint_and_parts()
    {
        var s = Load("{ \"tilt\": 0.5, \"spin\": false, \"glint\": [0.25, -0.5], \"parts\": [ { \"ring\": [0.9] }, { \"dot\": [0, -0.9, 0.1] } ] }");
        Assert.NotNull(s);
        Assert.Equal("t", s!.Id);
        Assert.Equal(0.5f, s.Tilt);
        Assert.False(s.Spin);
        Assert.Equal(new System.Numerics.Vector2(0.25f, -0.5f), s.Glint);
        Assert.Equal(["ring", "dot"], s.Parts.Select(p => p.Kind));
    }

    [Fact]
    public void Optional_fields_default()
    {
        var s = Load("{ \"parts\": [ { \"ring\": [1] } ] }");
        Assert.NotNull(s);
        Assert.Equal(HaloShape.DefaultTilt, s!.Tilt);
        Assert.True(s.Spin);
        Assert.Equal(new System.Numerics.Vector2(0, -0.9f), s.Glint);
    }

    [Fact]
    public void An_unknown_id_has_no_shape_and_the_cache_can_be_reset()
    {
        HaloShape.ResetForTests();
        Assert.Null(HaloShape.For("no-such-student-" + Guid.NewGuid().ToString("N")));
        HaloShape.ResetForTests();
    }

    // ---- the bundled shapes (D17) ----

    private static bool ArtPresent => AzurCast.Slot("yuuka", "") != null && Directory.Exists(Path.Combine(Halo.Shared.Paths.AssetsDir, "skins", AzurArchiveSkinInfo.Id, HaloShape.Folder));

    [Fact]
    public void Every_cast_id_has_a_bundled_shape_when_the_art_folder_is_there_and_none_when_it_is_not()
    {
        HaloShape.ResetForTests();
        try
        {
            foreach (string id in AzurArchiveSkinInfo.Cast)
            {
                if (ArtPresent) Assert.True(HaloShape.For(id) != null, id);
                else Assert.Null(HaloShape.For(id));
            }
        }
        finally { HaloShape.ResetForTests(); }
    }

    [Fact]
    public void Only_aris_holds_still()
    {
        if (!ArtPresent) return;
        HaloShape.ResetForTests();
        foreach (string id in AzurArchiveSkinInfo.Cast)
            Assert.Equal(id != "aris", HaloShape.For(id)!.Spin);
    }

    [Fact]
    public void Every_bundled_shape_has_a_pitch_and_scale_in_range()
    {
        if (!ArtPresent) return;
        HaloShape.ResetForTests();
        try
        {
            foreach (string id in AzurArchiveSkinInfo.Cast)
            {
                var s = HaloShape.For(id)!;
                Assert.InRange(s.Pitch, 0f, 85f);
                Assert.InRange(s.Scale, 0.1001f, 2f);
                Assert.InRange(s.Depth, 0f, 50f);
            }
        }
        finally { HaloShape.ResetForTests(); }
    }

    // ---- 3D pose (AmbientLoop.Pose / HaloShape.PosedBounds) ----

    private static System.Numerics.Vector2 Project(System.Numerics.Matrix4x4 m, float x, float y)
    {
        var v = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(x, y, 0, 1), m);
        return new System.Numerics.Vector2(v.X / v.W, v.Y / v.W);
    }

    [Fact]
    public void With_perspective_the_far_top_of_a_pitched_ring_lands_nearer_the_centre_than_the_bottom()
    {
        var m = AmbientLoop.Pose(60 * MathF.PI / 180, 0, 4, 10);
        var top = Project(m, 0, -10);
        var bottom = Project(m, 0, 10);
        Assert.True(top.Y < 0 && bottom.Y > 0);
        Assert.True(MathF.Abs(top.Y) < MathF.Abs(bottom.Y));
    }

    [Fact]
    public void Without_perspective_the_pose_is_symmetric_and_2r_cos_pitch_tall()
    {
        const float r = 10, pitch = 60 * MathF.PI / 180;
        var m = AmbientLoop.Pose(pitch, 0, 0, r);
        var top = Project(m, 0, -r);
        var bottom = Project(m, 0, r);
        Assert.Equal(-top.Y, bottom.Y, 3);
        Assert.Equal(2 * r * MathF.Cos(pitch), bottom.Y - top.Y, 3);
        Assert.Equal(r, Project(m, r, 0).X, 3);                       // width is untouched
    }

    [Fact]
    public void Roll_turns_the_projected_box()
    {
        var flat = Load("{ \"pitch\": 60, \"depth\": 0, \"parts\": [ { \"ring\": [1] } ] }")!;
        var rolled = Load("{ \"pitch\": 60, \"roll\": 90, \"depth\": 0, \"parts\": [ { \"ring\": [1] } ] }")!;
        var a = flat.PosedBounds(10);
        var b = rolled.PosedBounds(10);
        Assert.True(a.Width > a.Height);
        Assert.Equal(a.Width, b.Height, 2);
        Assert.Equal(a.Height, b.Width, 2);
    }

    [Fact]
    public void Posed_bounds_scale_linearly_with_the_radius()
    {
        var s = Load("{ \"pitch\": 55, \"roll\": 12, \"depth\": 4, \"parts\": [ { \"ring\": [1] } ] }")!;
        var a = s.PosedBounds(10);
        var b = s.PosedBounds(30);
        Assert.Equal(a.Width * 3, b.Width, 2);
        Assert.Equal(a.Height * 3, b.Height, 2);
        Assert.Equal(a.Left * 3, b.Left, 2);
        Assert.Equal(a.Top * 3, b.Top, 2);
    }

    // ---- AmbientLoop.SpinPose ----

    [Fact]
    public void SpinPose_without_a_radius_derives_the_flat_ring_from_its_bounds()
    {
        var loop = new AmbientLoop { Kind = AmbientKind.Spin, Key = "k", Bounds = new Rect(10, 10, 28, 8) };
        var (c, r, pitch, roll, depth) = loop.SpinPose;
        Assert.Equal(new System.Numerics.Vector2(24, 14), c);
        Assert.Equal(14f, r, 3);
        Assert.Equal(MathF.Acos(8f / 28f), pitch, 4);
        Assert.Equal(0f, roll);
        Assert.Equal(0f, depth);
    }

    [Fact]
    public void SpinPose_with_a_radius_returns_the_explicit_values()
    {
        var loop = new AmbientLoop
        {
            Kind = AmbientKind.Spin, Key = "k", Bounds = new Rect(10, 10, 28, 8),
            Centre = new System.Numerics.Vector2(5, 6), Radius = 7, Pitch = 0.9f, Roll = 0.2f, Depth = 3,
        };
        var (c, r, pitch, roll, depth) = loop.SpinPose;
        Assert.Equal(new System.Numerics.Vector2(5, 6), c);
        Assert.Equal((7f, 0.9f, 0.2f, 3f), (r, pitch, roll, depth));
    }

    // ---- the pose fields in the file ----

    [Fact]
    public void Pitch_roll_scale_offset_and_depth_parse()
    {
        var s = Load("{ \"pitch\": 70, \"roll\": -12, \"scale\": 0.6, \"dx\": 3, \"dy\": -9, \"depth\": 2.5, \"parts\": [ { \"ring\": [1] } ] }");
        Assert.NotNull(s);
        Assert.Equal(70f, s!.Pitch);
        Assert.Equal(-12f, s.Roll);
        Assert.Equal(0.6f, s.Scale);
        Assert.Equal(3f, s.Dx);
        Assert.Equal(-9f, s.Dy);
        Assert.Equal(2.5f, s.Depth);
    }

    [Fact]
    public void A_tilt_only_file_gets_the_pitch_that_tilt_implies()
    {
        var s = Load("{ \"tilt\": 0.5, \"parts\": [ { \"ring\": [1] } ] }")!;
        Assert.Equal(MathF.Acos(0.5f) * 180 / MathF.PI, s.Pitch, 3);
    }

    [Fact]
    public void The_pose_fields_default_when_absent()
    {
        var s = Load("{ \"parts\": [ { \"ring\": [1] } ] }")!;
        Assert.Equal(MathF.Acos(HaloShape.DefaultTilt) * 180 / MathF.PI, s.Pitch, 3);
        Assert.Equal(0f, s.Roll);
        Assert.Equal(HaloShape.DefaultScale, s.Scale);
        Assert.Equal(0f, s.Dx);
        Assert.Equal(HaloShape.DefaultDy, s.Dy);
        Assert.Equal(HaloShape.DefaultDepth, s.Depth);
    }

    [Theory]
    [InlineData("\"pitch\": -1")]
    [InlineData("\"pitch\": 86")]
    [InlineData("\"scale\": 0.1")]
    [InlineData("\"scale\": 0")]
    [InlineData("\"scale\": 2.5")]
    [InlineData("\"depth\": -1")]
    [InlineData("\"depth\": 51")]
    public void An_out_of_range_pose_field_is_null_without_throwing(string field)
        => Assert.Null(Load("{ " + field + ", \"parts\": [ { \"ring\": [1] } ] }"));

    private static readonly Lazy<Dx> SharedDx = new(() => new Dx(Halo.Shared.Paths.FontsDir, warp: true));

    [Fact]
    public void Every_bundled_shape_draws_some_pixels()
    {
        if (!ArtPresent) return;
        HaloShape.ResetForTests();
        var dx = SharedDx.Value;
        foreach (string id in AzurArchiveSkinInfo.Cast)
        {
            var shape = HaloShape.For(id)!;
            using var dc = dx.D2DDevice.CreateDeviceContext(Vortice.Direct2D1.DeviceContextOptions.None);
            using var rc = new RenderContext(dc, dx.DWrite, dx.CustomFonts, new Theme());
            var fmt = new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            using var target = dc.CreateBitmap(new SizeI(120, 120), IntPtr.Zero, 0,
                new Vortice.Direct2D1.BitmapProperties1(fmt, 96, 96, Vortice.Direct2D1.BitmapOptions.Target | Vortice.Direct2D1.BitmapOptions.CannotDraw));
            dc.Target = target;
            dc.SetDpi(96, 96);
            dc.BeginDraw();
            dc.Clear(new Color4(0, 0, 0, 0));
            shape.Draw(rc, AzurCast.HaloOf(id), new System.Numerics.Vector2(60, 60), 40, 40 * shape.Tilt);
            dc.EndDraw();
            dc.Target = null;
            var img = PanelRenderer.Read(dc, target, 120, 120);
            bool any = false;
            for (int i = 3; i < img.Pixels.Length && !any; i += 4) any = img.Pixels[i] != 0;
            Assert.True(any, id);
        }
        HaloShape.ResetForTests();
    }
}
