using System.Text.Json;
using System.Text.Json.Serialization;

namespace Halo.Shared.Config;

/// <summary>
/// config\settings.json — everything that is not per widget. Schema v2 (settings plan §Config
/// schema v2): hardware lists are gone (the collector discovers hardware and widgets pick from
/// it), appearance moved in from the deleted theme.json, and the collector's own knobs live
/// under "collector" so it is obvious who reads what.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Lock every widget's position (also toggled from the tray/context menu).</summary>
    public bool LockAll { get; set; }

    /// <summary>Snap widgets to screen and widget edges while dragging.</summary>
    public bool Snap { get; set; } = true;

    public AppearanceSettings Appearance { get; set; } = new();

    public CollectorSettings Collector { get; set; } = new();

    public DiagnosticsSettings Diagnostics { get; set; } = new();
}

/// <summary>
/// Logging knobs. Top level, not under <see cref="CollectorSettings"/>, because all three
/// processes read them.
/// <para>Adding this block is additive: an existing settings.json without it deserializes to these
/// defaults and gains the block on its next write, so no schema bump is needed.</para>
/// </summary>
public sealed class DiagnosticsSettings
{
    /// <summary>"debug" | "info" | "warn" | "error". The <c>HALO_LOG_LEVEL</c> environment
    /// variable overrides this — deliberately, so a settings.json that will not parse cannot lock
    /// you out of the verbose logging you need to find out why.</summary>
    public string LogLevel { get; set; } = "info";
}

/// <summary>Global look: the defaults every widget inherits unless it overrides them.</summary>
public sealed class AppearanceSettings
{
    /// <summary>"auto" (per-monitor rule, hardware plan H7) or a fixed number.</summary>
    public ScaleValue Scale { get; set; } = ScaleValue.Auto;
    public string FontFamily { get; set; } = "Trebuchet MS";
    public double TextSizePt { get; set; } = 8;
    public double CornerRadius { get; set; } = 4;

    /// <summary>Theme tokens as #RRGGBBAA. Missing tokens fall back to the built-in palette.</summary>
    public Dictionary<string, string> Colors { get; set; } = new();
}

/// <summary>Knobs only the collector reads. Rates are engineering constants and are NOT here.</summary>
public sealed class CollectorSettings
{
    /// <summary>Rolling window (seconds) for 1% / 0.1% lows.</summary>
    public double FrameLowsWindowS { get; set; } = 60;

    /// <summary>Frame-data transport: "auto" or "sdk" (the console capture app is gone).</summary>
    public string PresentMonTransport { get; set; } = "auto";

    /// <summary>ETW buffer flush period while a game is tracked, ms (1–1000; 0 = service default).
    /// Applies to the PresentMon service and the present tap; both relax automatically when no
    /// 3D app is in the foreground (idle mode).</summary>
    public int PresentMonEtwFlushMs { get; set; } = 10;

    /// <summary>Door-1 present tap for the presented FPS panel: "auto" (DXGI/D3D9 titles get a
    /// live presented stream, others fall back to the resolved lane) or "off".</summary>
    public string PresentedTap { get; set; } = "auto";

    /// <summary>Preferred network adapter; "Best" picks the one with the default route.</summary>
    public string NetworkInterface { get; set; } = "Best";

    public ExternalIpSettings ExternalIp { get; set; } = new();
}

/// <summary>The only outbound network call Halo makes. Off unless the user turns it on.</summary>
public sealed class ExternalIpSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "https://api.ipify.org";
    public double RefreshMinutes { get; set; } = 5;
}

public enum ZMode
{
    Desktop,      // bottom of z-order, above wallpaper, survives Win+D
    Normal,
    Topmost,
}

/// <summary>One widget window instance (config\widgets.json, schema v2).</summary>
public sealed class WidgetInstance
{
    public string Id { get; set; } = "";

    /// <summary>Panel type id from <see cref="Panels.PanelCatalog"/>.</summary>
    public string Type { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Title-bar override; null/empty = the panel's own default (often a live metric).</summary>
    public string? Title { get; set; }

    /// <summary>Stable monitor device id; empty = primary.</summary>
    public string Monitor { get; set; } = "";

    /// <summary>Position relative to the monitor's work area origin (physical px).</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public ZMode ZMode { get; set; } = ZMode.Desktop;
    public bool ClickThrough { get; set; }
    public bool KeepOnScreen { get; set; } = true;
    public bool Locked { get; set; }
    public double Opacity { get; set; } = 1.0;

    /// <summary>Hide while a fullscreen app has focus.</summary>
    public bool HideOnFullscreen { get; set; }

    /// <summary>Repaint rate. Clamped to [0.5, the fastest data source in this panel].</summary>
    public double RateHz { get; set; } = 5;

    public GraphSettings Graph { get; set; } = new();

    /// <summary>Panel-specific options, typed by the catalog's OptionSpec list.</summary>
    public Dictionary<string, string> Options { get; set; } = new();

    /// <summary>Per-metric overrides, keyed by the catalog's MetricSpec key. Repeated metrics use
    /// "&lt;key&gt;.&lt;n&gt;" (e.g. "rpm.2" for fan channel 2, "volume.C" for drive C).</summary>
    public Dictionary<string, MetricSetting> Metrics { get; set; } = new();

    public WidgetAppearance Appearance { get; set; } = new();
}

public sealed class GraphSettings
{
    /// <summary>Visible history in seconds (10–600). Columns are time buckets, so this is the
    /// span the graph means regardless of the widget's refresh rate.</summary>
    public double HistoryS { get; set; } = 40;
    public double Height { get; set; } = 25;
    /// <summary>"line" or "filled".</summary>
    public string Style { get; set; } = "line";
}

/// <summary>Per-metric user overrides. Every field is nullable = "use the catalog default".</summary>
public sealed class MetricSetting
{
    public bool? Show { get; set; }
    public string? Label { get; set; }
    public bool? Graph { get; set; }
    /// <summary>#RRGGBBAA.</summary>
    public string? Color { get; set; }
    /// <summary>Staged warn thresholds (1 value = single warn point, 4 = the five-stage ramp).</summary>
    public double[]? Warn { get; set; }
    /// <summary>Scale ceiling for metrics that need one (fan max RPM, graph fixed max).</summary>
    public double? Max { get; set; }
}

/// <summary>Per-widget appearance overrides; null = inherit the global value.</summary>
public sealed class WidgetAppearance
{
    public Dictionary<string, string>? Colors { get; set; }
    public string? FontFamily { get; set; }
    public ScaleValue? Scale { get; set; }
    public bool? ShowTitle { get; set; }
    public double? Width { get; set; }
    /// <summary>"C" or "F".</summary>
    public string? TempUnit { get; set; }
}

public sealed class WidgetsConfig
{
    public int SchemaVersion { get; set; } = AppSettings.CurrentSchemaVersion;
    public List<WidgetInstance> Widgets { get; set; } = new();

    /// <summary>
    /// <c>"pending"</c> asks the widget process to place every widget on the primary monitor with
    /// the column packer and write the result back, then clear this. Anyone can request a layout
    /// — the Settings app's "Generate default layout", or the widget process's own first run —
    /// but only the renderer can actually do it, because packing needs each panel's laid-out
    /// pixel size and only the renderer has one (hardware plan H6).
    /// </summary>
    public string? Arrange { get; set; }

    public const string ArrangePending = "pending";

    public bool ArrangeRequested
        => string.Equals(Arrange, ArrangePending, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A scale that is either "auto" (resolved per monitor at runtime) or a fixed number.
/// Serialises back as whichever it is, so the file stays human-editable.
/// </summary>
[JsonConverter(typeof(ScaleValueConverter))]
public readonly record struct ScaleValue(bool IsAuto, double Value)
{
    public static readonly ScaleValue Auto = new(true, 0);
    public static ScaleValue Fixed(double v) => new(false, v);

    /// <summary>The number to use when "auto" cannot be resolved.</summary>
    public double Or(double autoValue) => IsAuto ? autoValue : Value;

    public override string ToString() => IsAuto ? "auto" : Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class ScaleValueConverter : JsonConverter<ScaleValue>
{
    public override ScaleValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return ScaleValue.Fixed(reader.GetDouble());
            case JsonTokenType.String:
                string s = reader.GetString() ?? "auto";
                return double.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double d)
                    ? ScaleValue.Fixed(d)
                    : ScaleValue.Auto;
            case JsonTokenType.Null:
                return ScaleValue.Auto;
            default:
                reader.Skip();
                return ScaleValue.Auto;
        }
    }

    public override void Write(Utf8JsonWriter writer, ScaleValue value, JsonSerializerOptions options)
    {
        if (value.IsAuto) writer.WriteStringValue("auto");
        else writer.WriteNumberValue(value.Value);
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(WidgetsConfig))]
public sealed partial class ConfigJsonContext : JsonSerializerContext;
