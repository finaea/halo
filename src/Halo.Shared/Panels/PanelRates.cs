namespace Halo.Shared.Panels;

/// <summary>
/// The refresh bound of one widget, computed the same way by the renderer and by the Settings
/// slider (rates plan R2): a widget is never told to repaint faster than its fastest data source,
/// so the UI can't promise freshness the collector does not produce.
///
/// The numbers come from the live registry (<c>nominalRateHz</c> per metric), not from a table
/// here — a provider that gets faster raises the slider with no code change.
/// </summary>
public static class PanelRates
{
    /// <summary>Slowest repaint a user can ask for (one frame every 2 s).</summary>
    public const double MinHz = 0.5;

    /// <summary>Hard ceiling for v1 even when a provider publishes faster (cpu-kernel's cap is
    /// 64 Hz; nobody needs a 64 Hz text repaint).</summary>
    public const double CeilingHz = 10;

    /// <summary>Lowest bound we will hand a user, so a panel made only of 1/300 Hz metrics
    /// (external IP) still offers a usable slider.</summary>
    public const double FloorHz = 1;

    /// <summary>Tick rate of an event-driven panel (the FPS panels repaint on the frames-ready
    /// event; this is only the fallback so a frozen lane still refreshes its text).</summary>
    public const double EventDrivenFallbackHz = 5;

    /// <summary>
    /// Every metric name a widget of this type actually reads, with the templates resolved
    /// against its options. Repeated rows (per core / volume / fan / rank) contribute one
    /// representative name: every repetition of a row comes from the same provider at the same
    /// cadence, so a second one cannot change the maximum.
    /// </summary>
    public static IReadOnlyList<string> MetricNamesFor(PanelType? type, IReadOnlyDictionary<string, string>? options)
    {
        var names = new List<string>();
        if (type == null) return names;
        options ??= new Dictionary<string, string>();

        string gpu = Value(type, options, "gpuIndex", "0");
        string stream = Value(type, options, "stream", "displayed");
        bool agg = Value(type, options, "aggregate", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
        string volume = FirstListItem(Value(type, options, "volumes", "")) is { Length: > 0 } v ? v[..1].ToLowerInvariant() : "c";
        string channel = FirstListItem(Value(type, options, "channels", "")) is { Length: > 0 } ch ? ch : "0";

        foreach (var m in type.Metrics)
        {
            string repeat = m.Repeat == Repeat.PerFan ? channel : "0";
            names.Add(PanelCatalog.ResolveMetricName(m.MetricName, gpu, repeat, volume, stream, agg));
        }
        return names;
    }

    /// <summary>
    /// Fastest useful repaint for this widget: the highest nominal rate among its metrics,
    /// floored at <see cref="FloorHz"/> and capped at <see cref="CeilingHz"/>.
    /// <paramref name="nominalOf"/> returns 0 for a metric that is not registered (or is Static,
    /// which publishes once and never again) — those cannot raise the bound.
    /// </summary>
    public static double MaxHz(PanelType? type, IEnumerable<string> metricNames, Func<string, double> nominalOf)
    {
        // A clock's headline is produced in the widget process, so no collector metric bounds it.
        if (type?.LocalContent == true) return CeilingHz;

        double max = 0;
        foreach (string name in metricNames)
        {
            double hz = nominalOf(name);
            if (hz > max) max = hz;
        }
        // Nothing registered yet (collector down, or a panel whose hardware is absent): stay out
        // of the user's way rather than silently clamping their setting down to 1 Hz.
        if (max <= 0) return CeilingHz;
        return Math.Clamp(max, FloorHz, CeilingHz);
    }

    /// <summary>Clamp a configured rate into [MinHz, maxHz].</summary>
    public static double Clamp(double rateHz, double maxHz)
        => Math.Clamp(rateHz, MinHz, Math.Max(MinHz, maxHz));

    private static string Value(PanelType type, IReadOnlyDictionary<string, string> options, string key, string fallback)
    {
        string v = type.OptionValue(options, key);
        return v.Length > 0 ? v : fallback;
    }

    private static string FirstListItem(string csv)
    {
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            return part;
        return "";
    }
}
