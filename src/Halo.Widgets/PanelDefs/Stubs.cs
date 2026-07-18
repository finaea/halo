using Halo.Widgets.Render;

namespace Halo.Widgets.PanelDefs;

// Temporary placeholders — replaced by the real panel implementations, one file per panel.
// Each Build() must fully mirror the extracted spec in tools\extracted\<panel>.json.

public static class CpuRamPanel
{
    public static Panel Build(PanelContext ctx) => CpuRamPanelImpl.Build(ctx);
}

public static class GpuPanel
{
    public static Panel Build(PanelContext ctx) => Placeholder.Build(ctx, "GPU");
}

public static class FpsPanel
{
    public static Panel Build(PanelContext ctx) => Placeholder.Build(ctx,
        ctx.Options.GetValueOrDefault("stream", "displayed") == "presented" ? "FPS COUNTER (PRESENTED)" : "FPS COUNTER (DISPLAYED)");
}

public static class PowerPanel
{
    public static Panel Build(PanelContext ctx) => Placeholder.Build(ctx, "POWER");
}

public static class DrivesPanel
{
    public static Panel Build(PanelContext ctx) => Placeholder.Build(ctx, "DRIVES");
}

public static class NetworkPanel
{
    public static Panel Build(PanelContext ctx) => Placeholder.Build(ctx, "NETWORK");
}

public static class FansPanel
{
    public static Panel Build(PanelContext ctx) => Placeholder.Build(ctx, "FANS");
}

public static class TopProcPanel
{
    public static Panel Build(PanelContext ctx, bool byRam) => Placeholder.Build(ctx, byRam ? "TOP RAM" : "TOP CPU");
}

internal static class Placeholder
{
    public static Panel Build(PanelContext ctx, string title)
    {
        var p = new Panel();
        p.TitleElements.Add(new TextEl { Text = _ => title, Style = TextStyle.Bold9, Align = TextAlign.Center, Color = "title", Upper = true });
        p.Elements.Add(new TextEl { Text = _ => "(under construction)", Style = TextStyle.Text8, Align = TextAlign.Center, Color = "text2", FixedH = 12 });
        return p;
    }
}
