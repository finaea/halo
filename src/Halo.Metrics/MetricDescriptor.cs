namespace Halo.Metrics;

/// <summary>Stable 64-bit metric identity = FNV-1a hash of the metric name string.</summary>
public static class MetricId
{
    public const ulong FnvOffset = 14695981039346656037UL;
    public const ulong FnvPrime = 1099511628211UL;

    public static ulong Hash(string name)
    {
        ulong h = FnvOffset;
        foreach (char c in name)
        {
            h ^= (byte)c;          // metric names are ASCII by convention
            h *= FnvPrime;
        }
        return h;
    }
}

public enum MetricType : byte
{
    Double = 0,
    String = 1,
}

public enum MetricUnit : byte
{
    None = 0,
    Percent = 1,
    Celsius = 2,
    Volts = 3,
    Watts = 4,
    Rpm = 5,
    Megahertz = 6,
    Bytes = 7,
    BytesPerSecond = 8,
    Gigabytes = 9,
    Megabytes = 10,
    Fps = 11,
    Milliseconds = 12,
    Seconds = 13,
    Count = 14,
    Hertz = 15,
    Text = 16,
}

/// <summary>
/// What a published number means over time. Consumers need this to know whether averaging or
/// re-sampling a metric is meaningful (docs\metrics-protocol.md).
/// </summary>
public enum MetricSemantics : byte
{
    /// <summary>Instantaneous sensor/OS read at poll time, no aggregation.</summary>
    Latest = 0,
    /// <summary>Mean over the gap between two polls (Δcounter ÷ Δt).</summary>
    IntervalAvg = 1,
    /// <summary>Sliding time window over per-sample data; see WindowMs.</summary>
    RollingWindow = 2,
    /// <summary>Accumulates since session start (or since a reset command).</summary>
    Cumulative = 3,
    /// <summary>Session extremum, latched until reset-max.</summary>
    RunningMax = 4,
    /// <summary>Arithmetic over other metrics, no sensor of its own.</summary>
    Calc = 5,
    /// <summary>Written once at discovery and never changes (names, counts, capabilities).</summary>
    Static = 6,
}

[Flags]
public enum MetricFlags : byte
{
    None = 0,
    /// <summary>A "&lt;name&gt;.max" companion metric exists.</summary>
    HasMaxCompanion = 1,
    /// <summary>This metric IS the ".max" companion of another.</summary>
    IsMaxCompanion = 2,
    /// <summary>Reads N/A unless the collector runs elevated.</summary>
    NeedsElevation = 4,
    /// <summary>Computed by the collector from other metrics rather than read from hardware.</summary>
    Derived = 8,
}

public enum ProviderState : byte
{
    Unavailable = 0,
    Ok = 1,
    Degraded = 2,
}

/// <summary>Describes one metric slot as registered by a provider (writer side).</summary>
public readonly record struct MetricDescriptor(
    string Name,
    MetricType Type,
    MetricUnit Unit,
    string Provider,
    double NominalRateHz,
    MetricSemantics Semantics = MetricSemantics.Latest,
    MetricFlags Flags = MetricFlags.None,
    int WindowMs = 0)
{
    public ulong Id => MetricId.Hash(Name);
}

/// <summary>A registry entry as read back from the section (reader side).</summary>
public readonly record struct MetricInfo(
    int Index,
    string Name,
    MetricType Type,
    MetricUnit Unit,
    MetricSemantics Semantics,
    MetricFlags Flags,
    int ProviderIndex,
    float NominalRateHz,
    float EffectiveRateHz,
    int WindowMs);

/// <summary>A provider-table row as read back from the section.</summary>
public readonly record struct ProviderInfo(
    int Index,
    string Name,
    ProviderState State,
    bool NeedsElevation,
    float RateHz,
    long LastPollQpc,
    float LastPollMs,
    string LastError);

/// <summary>Short, stable reason codes written into the provider table's lastError field.</summary>
public static class ProviderError
{
    public const string None = "";
    /// <summary>Needs admin rights the collector does not have.</summary>
    public const string Unelevated = "unelevated";
    /// <summary>Kernel driver (PawnIO) missing.</summary>
    public const string NoDriver = "no-driver";
    /// <summary>No hardware of this class found.</summary>
    public const string NoHardware = "no-hw";
    /// <summary>nvml.dll absent (no NVIDIA driver).</summary>
    public const string NoNvml = "no-nvml";
    /// <summary>Bundled SDK/service unavailable.</summary>
    public const string NoSdk = "no-sdk";
    /// <summary>Initialise or poll threw; see the log.</summary>
    public const string Failed = "failed";
}
