namespace Halo.Shared.Metrics;

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

/// <summary>Describes one metric slot as registered by a provider.</summary>
public readonly record struct MetricDescriptor(string Name, MetricType Type, MetricUnit Unit, string Provider, float MaxRateHz)
{
    public ulong Id => MetricId.Hash(Name);
}
