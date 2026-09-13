using Halo.Metrics;
using Halo.Shared.Config;

namespace Halo.Shared.Panels;

/// <summary>
/// The one description of "a sensible set of widgets for this PC".
///
/// Two places offer to build a default layout — the Settings app's "Generate default layout" and
/// the widget process's first run — and they must not disagree about what that means. Both call
/// <see cref="Generate"/>, both set <see cref="WidgetsConfig.Arrange"/> to
/// <see cref="WidgetsConfig.ArrangePending"/>, and the widget process does the placing, because
/// the column packer needs each panel's laid-out pixel size and only the renderer has one
/// (hardware plan H6). Nothing here decides a position.
/// </summary>
public static class DefaultLayout
{
    /// <summary>
    /// One widget per type that has data on this machine, in the order the hardware plan fixes.
    /// <paramref name="metric"/> and <paramref name="text"/> read the live registry (0 / "" when a
    /// metric is absent); with <paramref name="collectorOnline"/> false nothing can be discovered,
    /// so only the types that need no hardware discovery are created.
    /// </summary>
    public static List<WidgetInstance> Generate(Func<string, double> metric, Func<string, string> text, bool collectorOnline)
    {
        var widgets = new List<WidgetInstance>();

        void Add(string type, string id, Dictionary<string, string>? options = null)
            => widgets.Add(new WidgetInstance
            {
                Id = id,
                Type = type,
                RateHz = PanelCatalog.Find(type)?.DefaultRateHz ?? 5,
                Options = options ?? new Dictionary<string, string>(),
                // position and scale are deliberately left alone: the packer places it and
                // "auto" scale keeps adapting if the user later moves to another monitor (H7)
            });

        Add("clock", "clock-1");
        Add("cpu-ram", "cpu-ram-1");

        if (!collectorOnline)
        {
            // Offline: no GPU count, no volumes, no fan channels. Only the types whose panels are
            // complete without discovery — the user regenerates from System check once it is up.
            Add("network", "network-1");
            return widgets;
        }

        int gpus = (int)Math.Clamp(metric(MetricNames.GpuCount), 0, 8);
        bool anyNvidia = false;
        for (int i = 0; i < gpus; i++)
        {
            Add("gpu", $"gpu-{i}", new Dictionary<string, string> { ["gpuIndex"] = i.ToString() });
            if (text(MetricNames.GpuVendor(i)).Equals("nvidia", StringComparison.OrdinalIgnoreCase))
                anyNvidia = true;
        }

        // Only the presented stream: the displayed one says nothing until a game supplies frames.
        Add("fps", "fps-presented", new Dictionary<string, string> { ["stream"] = "presented" });

        // PC latency comes from NVIDIA Reflex (PCL Stats) markers; on an AMD or Intel card the
        // whole panel would read N/A forever, which is not a default worth shipping.
        if (anyNvidia) Add("latency", "latency-1");

        Add("power", "power-1");
        Add("drives", "drives-1");
        Add("network", "network-1");
        if (metric(MetricNames.FanCount) > 0) Add("fans", "fans-1");
        Add("topcpu", "topcpu-1");
        Add("topram", "topram-1");

        return widgets;
    }
}
