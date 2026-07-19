using System.Text.Json;
using System.Text.Json.Serialization;

namespace Halo.Shared.Config;

/// <summary>config\settings.json — global app settings (hot-reloaded by both processes).</summary>
public sealed class GeneralSettings
{
    public double DefaultRateHz { get; set; } = 10;
    public bool LockAll { get; set; } = false;
    public double Scale { get; set; } = 1.7;
    public string FontFamily { get; set; } = "Trebuchet MS";
    /// <summary>Rolling window (seconds) for 1%/0.1% lows.</summary>
    public double FrameLowsWindowS { get; set; } = 60;
    /// <summary>Frame-data transport: "auto" (SDK service, console-app fallback), "sdk", "console".</summary>
    public string PresentMonTransport { get; set; } = "auto";
    /// <summary>SDK transport: ETW buffer flush period requested from the service, ms (1–1000; 0 = service default).</summary>
    public int PresentMonEtwFlushMs { get; set; } = 20;
    /// <summary>Graph history depth in seconds (widget-local rings).</summary>
    public double GraphHistoryS { get; set; } = 600;
    public string ExternalIpUrl { get; set; } = "https://api.ipify.org";
    public double ExternalIpRefreshMinutes { get; set; } = 5;
    public List<string> DriveLetters { get; set; } = ["C", "D", "E", "F", "G", "H", "I"];
    /// <summary>Preferred network adapter; "Best" picks the one with the default route.</summary>
    public string NetworkInterface { get; set; } = "Best";
    public int TopProcessCount { get; set; } = 5;
    /// <summary>Per-channel fan nicknames, e.g. {"4": "BACK", "5": "FRONT"} (NCT6687D System Fan channels).</summary>
    public Dictionary<string, string> FanNames { get; set; } = new();
    /// <summary>Max RPM per fan channel for % calculation, e.g. {"4": 2000}.</summary>
    public Dictionary<string, double> FanMaxRpm { get; set; } = new();
}

public enum ZMode
{
    Desktop,      // bottom of z-order, above wallpaper, survives Win+D
    Normal,
    Topmost,
}

/// <summary>One widget window instance (config\widgets.json).</summary>
public sealed class WidgetInstance
{
    public string Id { get; set; } = "";
    /// <summary>Panel layout name: clock, power, drives, fps, gpu, fans, network, cpu-ram, topcpu, topram.</summary>
    public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Stable monitor device id; empty = primary.</summary>
    public string Monitor { get; set; } = "";
    /// <summary>Position relative to the monitor's work area origin (physical px).</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public ZMode ZMode { get; set; } = ZMode.Desktop;
    public bool ClickThrough { get; set; } = false;
    public bool KeepOnScreen { get; set; } = true;
    public bool Locked { get; set; } = false;
    public double Opacity { get; set; } = 1.0;
    /// <summary>Per-widget tick override (Hz). null = settings.DefaultRateHz. Graphs may go to 100.</summary>
    public double? RateHz { get; set; }
    /// <summary>Panel-specific options (e.g. fps stream: "presented"|"displayed").</summary>
    public Dictionary<string, string> Options { get; set; } = new();
}

public sealed class WidgetsConfig
{
    public List<WidgetInstance> Widgets { get; set; } = new();
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(GeneralSettings))]
[JsonSerializable(typeof(WidgetsConfig))]
public sealed partial class ConfigJsonContext : JsonSerializerContext;
