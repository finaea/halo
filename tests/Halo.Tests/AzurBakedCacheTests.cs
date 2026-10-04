using Halo.Shared;
using Halo.Widgets;
using Halo.Widgets.Harness;
using Halo.Widgets.Skins.AzurArchive;

namespace Halo.Tests;

/// <summary>
/// Azur Archive's baked bitmaps (face crops, corner texture) are device resources in a process-wide
/// cache. A device recreate or dispose must empty it — otherwise a skin no widget shows any more
/// pins the old device until something happens to draw it again.
///
/// <para>In the log collection: <see cref="Dx"/> writes breadcrumbs, and the cache is static, so
/// another render test running alongside would refill it mid-assert.</para>
/// </summary>
[Collection(LogTestCollection.Name)]
public sealed class AzurBakedCacheTests
{
    [Fact]
    public void Recreate_and_dispose_empty_the_baked_cache()
    {
        using var dx = new Dx(Paths.FontsDir, warp: true);
        PanelRenderer.Render(dx, new RenderRequest("cpu-ram", "idle", Skin: "azur-archive"));
        Assert.True(Az.BakedCount > 0, "rendering an Azur card should bake its texture (and face)");

        dx.Recreate(Paths.FontsDir);
        Assert.Equal(0, Az.BakedCount);

        PanelRenderer.Render(dx, new RenderRequest("cpu-ram", "idle", Skin: "azur-archive"));
        Assert.True(Az.BakedCount > 0);
        dx.Dispose();
        Assert.Equal(0, Az.BakedCount);
    }
}
