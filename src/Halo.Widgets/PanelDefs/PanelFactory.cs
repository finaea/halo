using Halo.Shared.Panels;
using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

/// <summary>
/// Builds the element tree for a widget type. The set of types, their options and their metric
/// rows are described once in <see cref="PanelCatalog"/>; this maps an id to the builder that
/// draws it (plan D6: any type × N instances).
/// </summary>
public static class PanelFactory
{
    public static Panel? Create(string type, PanelContext ctx) => type switch
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
        _ => null,
    };

    /// <summary>Types this factory can draw. Kept in step with <see cref="PanelCatalog"/>:
    /// anything the catalog offers in Settings must be buildable here.</summary>
    public static IReadOnlyList<string> KnownTypes { get; } =
        ["clock", "cpu-ram", "gpu", "fps", "power", "drives", "network", "fans", "latency", "topcpu", "topram"];
}
