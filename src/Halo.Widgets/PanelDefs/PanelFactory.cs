using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

public static class PanelFactory
{
    /// <summary>Instantiable widget types (plan D6): any type × N instances.</summary>
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
}
