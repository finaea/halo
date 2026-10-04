using Halo.Shared;
using Halo.Widgets;
using Halo.Widgets.Harness;
using Halo.Widgets.Render;
using Halo.Widgets.Skins;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Halo.Tests;

/// <summary>
/// The renderer features a non-Rainformer skin leans on: the asset slot chain, bundled fonts,
/// tabular figures, shape geometry and surviving a device recreate. WARP, so no GPU is needed.
/// In the log collection because <see cref="Dx"/> logs.
/// </summary>
[Collection(LogTestCollection.Name)]
public sealed class RendererCapabilityTests
{
    private static readonly Lazy<Dx> SharedDx = new(() => new Dx(Paths.FontsDir, warp: true));
    private static Dx Dx => SharedDx.Value;

    private static string Manjuu => Path.Combine(Paths.AssetsDir, "skins", "azur-archive", "game-art", "azur-lane", "manjuu.png");

    private sealed class TempDirs : IDisposable
    {
        public string Bundled = Directory.CreateTempSubdirectory("halo-bundled").FullName;
        public string User = Directory.CreateTempSubdirectory("halo-user").FullName;
        public void Dispose()
        {
            try { Directory.Delete(Bundled, true); } catch { }
            try { Directory.Delete(User, true); } catch { }
        }
    }

    private static PanelImage Draw(Action<RenderContext> body)
    {
        using var dc = Dx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        using var rc = new RenderContext(dc, Dx.DWrite, Dx.CustomFonts, new Theme());
        var format = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var target = dc.CreateBitmap(new SizeI(64, 64), IntPtr.Zero, 0,
            new BitmapProperties1(format, 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        dc.Target = target;
        dc.SetDpi(96, 96);
        dc.BeginDraw();
        dc.Clear(new Color4(0, 0, 0, 0));
        body(rc);
        dc.EndDraw();
        dc.Target = null;
        return PanelRenderer.Read(dc, target, 64, 64);
    }

    private static RenderContext Rc(out ID2D1DeviceContext dc)
    {
        dc = Dx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        return new RenderContext(dc, Dx.DWrite, Dx.CustomFonts, new Theme());
    }

    // ---- 1. SkinAssets ----

    [Fact]
    public void Resolve_prefers_user_then_bundled()
    {
        using var d = new TempDirs();
        File.WriteAllText(Path.Combine(d.User, "slot.png"), "x");
        Directory.CreateDirectory(Path.Combine(d.Bundled, "art"));
        File.WriteAllText(Path.Combine(d.Bundled, "art", "slot.png"), "x");

        var both = new SkinAssets(d.Bundled, d.User).Resolve("slot", "art/slot.png");
        Assert.Equal(new[] { Path.Combine(d.User, "slot.png"), Path.Combine(d.Bundled, "art/slot.png") }, both);
    }

    [Fact]
    public void Resolve_with_only_bundled_returns_it()
    {
        using var d = new TempDirs();
        File.WriteAllText(Path.Combine(d.Bundled, "b.png"), "x");
        var r = new SkinAssets(d.Bundled, d.User).Resolve("slot", "b.png");
        Assert.Equal(new[] { Path.Combine(d.Bundled, "b.png") }, r);
    }

    [Fact]
    public void Resolve_with_nothing_is_empty()
    {
        using var d = new TempDirs();
        Assert.Empty(new SkinAssets(d.Bundled, d.User).Resolve("slot", "nope.png"));
    }

    [Fact]
    public void Draw_without_a_file_returns_false_and_paints_nothing()
    {
        using var d = new TempDirs();
        var assets = new SkinAssets(d.Bundled, d.User);
        bool drew = true;
        var img = Draw(rc => drew = assets.Draw(rc, "slot", "nope.png", new Rect(0, 0, 32, 32)));
        Assert.False(drew);
        Assert.All(img.Pixels, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Draw_falls_through_a_corrupt_user_file_to_the_bundled_one()
    {
        using var d = new TempDirs();
        File.WriteAllBytes(Path.Combine(d.User, "slot.png"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        // a 1x1 stand-in written into the test's own folder: the suite must not need game-art folder
        File.WriteAllBytes(Path.Combine(d.Bundled, "m.png"), Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="));
        var assets = new SkinAssets(d.Bundled, d.User);
        bool drew = false;
        var img = Draw(rc => drew = assets.Draw(rc, "slot", "m.png", new Rect(0, 0, 32, 32)));
        Assert.True(drew);
        Assert.Contains(img.Pixels, b => b != 0);
    }

    // ---- 2. fonts ----

    [Theory]
    [InlineData("Barlow")]
    [InlineData("Rounded Mplus 1c")]
    [InlineData("Oxanium SemiBold")]
    public void Bundled_skin_families_are_in_the_custom_collection(string family)
    {
        Assert.NotNull(Dx.CustomFonts);
        Assert.True(Dx.CustomFonts!.FindFamilyName(family, out _), $"'{family}' not found");
    }

    [Fact]
    public void Format_uses_custom_collection_only_for_bundled_families()
    {
        using var rc = Rc(out var dc);
        using var _ = dc;
        using var custom = rc.Format(new TextStyle(9, false, "Barlow")).FontCollection;
        Assert.Equal(Dx.CustomFonts!.NativePointer, custom.NativePointer);

        using var system = rc.Format(new TextStyle(9, false)).FontCollection;
        Assert.NotEqual(Dx.CustomFonts.NativePointer, system.NativePointer);
    }

    // ---- 3. tabular figures ----

    [Fact]
    public void Tabular_forces_an_advance_only_for_faces_without_tnum()
    {
        using var rc = Rc(out var dc);
        using var _ = dc;
        Assert.True(rc.DigitAdvance(new TextStyle(9, false, "Georgia") { Tabular = true }) > 0);
        Assert.Equal(0f, rc.DigitAdvance(new TextStyle(9, false, "Barlow") { Tabular = true }));
    }

    [Theory]
    [InlineData("Georgia")]
    [InlineData("Barlow")]
    public void Tabular_digits_measure_the_same(string family)
    {
        using var rc = Rc(out var dc);
        using var _ = dc;
        var s = new TextStyle(12, false, family) { Tabular = true };
        Assert.Equal(rc.TextWidth("1111", s), rc.TextWidth("8808", s), 0.01);
    }

    [Fact]
    public void Georgia_without_tabular_has_uneven_digits()
    {
        using var rc = Rc(out var dc);
        using var _ = dc;
        var s = new TextStyle(12, false, "Georgia");
        Assert.True(Math.Abs(rc.TextWidth("1111", s) - rc.TextWidth("8808", s)) > 0.01);
    }

    // ---- 4. shapes ----

    public static IEnumerable<object[]> Specs() => new[]
    {
        new object[] { ShapeSpec.Chamfer(10, 20, 44, 26, 6) },
        new object[] { ShapeSpec.Chamfer(10, 20, 44, 26, 6, Corners.TopLeft | Corners.BottomRight) },
        new object[] { ShapeSpec.Parallelogram(10, 20, 44, 26, 8) },
        new object[] { ShapeSpec.Parallelogram(10, 20, 44, 26, -8) },
        new object[] { ShapeSpec.Notch(10, 20, 44, 26, 16, 6) },
        new object[] { ShapeSpec.Diamond(10, 20, 44, 26) },
        new object[] { ShapeSpec.Ellipse(10, 20, 44, 26) },
        new object[] { ShapeSpec.TriangleMosaic(10, 20, 70, 26, 6, 0.35f) },
    };

    [Theory]
    [MemberData(nameof(Specs))]
    public void Shape_bounds_stay_inside_the_box(ShapeSpec s)
    {
        using var rc = Rc(out var dc);
        using var _ = dc;
        var b = rc.Shape(s).GetBounds();
        const float tol = 0.01f;
        Assert.True(b.Left >= s.X - tol, $"left {b.Left} < {s.X}");
        Assert.True(b.Top >= s.Y - tol, $"top {b.Top} < {s.Y}");
        Assert.True(b.Right <= s.X + s.W + tol, $"right {b.Right} > {s.X + s.W}");
        Assert.True(b.Bottom <= s.Y + s.H + tol, $"bottom {b.Bottom} > {s.Y + s.H}");
    }

    // ---- 5. device recreate ----

    [Fact]
    public void Capability_sheet_renders_identically_after_a_device_recreate()
    {
        var (image, survived) = CapabilitySheet.Render(Dx);
        Assert.True(image.Width > 0);
        Assert.True(survived);
    }

    [Fact]
    public void Recreate_releases_the_bitmaps_of_a_skin_no_longer_drawn()
    {
        if (!File.Exists(Manjuu)) return;                  // art-free checkout: the skin has no bundled bitmap to cache
        // own device: the shared one stays usable for the other tests
        using var dx = new Dx(Paths.FontsDir, warp: true);
        var azur = SkinAssets.For("azur-archive");
        using (var dc = dx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None))
        using (var rc = new RenderContext(dc, dx.DWrite, dx.CustomFonts, new Theme()))
        {
            var format = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            using var target = dc.CreateBitmap(new SizeI(64, 64), IntPtr.Zero, 0,
                new BitmapProperties1(format, 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
            dc.Target = target;
            dc.BeginDraw();
            Assert.True(azur.Draw(rc, "manjuu", "game-art/azur-lane/manjuu.png", new Rect(0, 0, 32, 32)));
            dc.EndDraw();
            dc.Target = null;
        }
        Assert.True(azur.CachedBitmaps > 0);

        // the widgets switched away from Azur Archive, so nothing will draw it again to notice
        dx.Recreate(Paths.FontsDir);
        Assert.Equal(0, azur.CachedBitmaps);
    }

    [Fact]
    public void Gradient_caches_stay_bounded_and_dispose_what_they_drop()
    {
        using var rc = Rc(out var dc);
        using var _ = dc;
        static Color4 C(int i) => new(i / 255f, 0, 0, 1);
        var white = new Color4(1, 1, 1, 1);

        var firstLinear = rc.LinearGradient(C(0), white, default, new(1, 0));
        var firstRadial = rc.RadialGradient(C(0), white, default, 1, 1);
        for (int i = 1; i < RenderContext.GradientCap * 3; i++)
        {
            rc.LinearGradient(C(i), white, default, new(1, 0));
            rc.RadialGradient(C(i), white, default, 1, 1);
            Assert.True(rc.LinearGradientCount <= RenderContext.GradientCap);
            Assert.True(rc.RadialGradientCount <= RenderContext.GradientCap);
        }
        Assert.Equal(IntPtr.Zero, firstLinear.NativePointer);
        Assert.Equal(IntPtr.Zero, firstRadial.NativePointer);
    }
}
