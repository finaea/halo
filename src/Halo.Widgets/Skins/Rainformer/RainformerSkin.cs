using Halo.Shared.Skins;
using Halo.Widgets.Render;

namespace Halo.Widgets.Skins.Rainformer;

/// <summary>
/// The original look: 11 hand-built element trees, a pixel transcription of the Rainformer
/// Rainmeter skin (<c>tools\extracted\*.json</c> is their source of truth), guarded by the golden
/// test. They stay hand-built on purpose — pushing them through a generic layout is risk with no
/// visible upside.
/// </summary>
public sealed class RainformerSkin : ISkin
{
    public static RainformerSkin Instance { get; } = new();

    /// <summary>Types this skin can draw. Kept in step with <c>PanelCatalog</c>: anything the
    /// catalog offers in Settings must be buildable here.</summary>
    public static IReadOnlyList<string> KnownTypes { get; } =
        ["clock", "cpu-ram", "gpu", "fps", "power", "drives", "network", "fans", "latency", "topcpu", "topram", "companion"];

    private RainformerSkin() { }

    public SkinInfo Info => SkinCatalog.Rainformer;

    public SkinGeometry Geometry(Theme t) => new(t.BgOffset, t.TitleZoneH, t.ContentWidth, 11);

    public Panel? Build(string type, PanelContext ctx)
    {
        var panel = type switch
        {
            "clock" => ClockPanel.Build(ctx),
            "cpu-ram" => CpuRamPanelImpl.Build(ctx),
            "gpu" => GpuPanel.Build(ctx),
            "fps" => FpsPanel.Build(ctx),
            "power" => PowerPanel.Build(ctx),
            "drives" => DrivesPanel.Build(ctx),
            "network" => NetworkPanel.Build(ctx),
            "fans" => FansPanel.Build(ctx),
            "latency" => LatencyPanel.Build(ctx),
            "topcpu" => TopProcPanel.Build(ctx, byRam: false),
            "topram" => TopProcPanel.Build(ctx, byRam: true),
            "companion" => CompanionPanel.Build(ctx),
            _ => null,
        };
        // One chrome per panel: it caches geometry sized to this panel.
        if (panel != null) panel.Chrome = new RainformerChrome();
        return panel;
    }
}
