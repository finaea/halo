using System.Text.Json;
using Halo.Metrics;

namespace Halo.Collector;

/// <summary>
/// <c>Halo.Collector.exe --dump [--json]</c>. The human table is for support; the JSON is the
/// documented diagnostic shape (docs\metrics-protocol.md) that tools can parse without linking
/// anything. Both go through <see cref="CollectorSession"/>, so the public client surface is
/// exercised by an in-repo consumer.
/// </summary>
internal static class Dump
{
    public static void Table(CollectorSession s)
    {
        var reader = s.Reader;
        Console.WriteLine($"halo metrics {SharedMemoryLayout.VersionMajor}.{SharedMemoryLayout.VersionMinor} · collector {s.CollectorVersion} pid={s.CollectorPid} "
            + $"heartbeatAge={s.HeartbeatAgeSeconds:0.00}s metrics={s.MetricCount} frames={reader.FrameCursor} size={reader.TotalSize / 1024} KiB");

        var providers = s.Providers();
        Console.WriteLine();
        Console.WriteLine($"{"provider",-14} {"state",-11} {"rate",8} {"lastPoll",10} {"error",-12} elevation");
        foreach (var p in providers)
            Console.WriteLine($"{p.Name,-14} {p.State,-11} {p.RateHz,6:0.##}Hz {p.LastPollMs,8:0.00}ms {p.LastError,-12} {(p.NeedsElevation ? "needs admin" : "")}");

        Console.WriteLine();
        foreach (var m in s.Metrics())
        {
            string provider = m.ProviderIndex >= 0 && m.ProviderIndex < providers.Count
                ? providers.FirstOrDefault(p => p.Index == m.ProviderIndex).Name ?? "?"
                : "-";
            if (m.Type == MetricType.String)
            {
                reader.TryReadString(m.Index, out string sv);
                Console.WriteLine($"{m.Name,-38} \"{sv}\" ({m.Unit}, {m.NominalRateHz:0.###}Hz, {provider})");
            }
            else
            {
                bool ok = reader.TryRead(m.Index, out double v, out double age);
                Console.WriteLine($"{m.Name,-38} {(ok ? v.ToString("0.###") : "N/A"),12}  age={(ok ? age.ToString("0.00") : "-"),6}s "
                    + $"({m.Unit}, {m.Semantics}, {m.NominalRateHz:0.###}Hz, {provider})");
            }
        }
    }

    public static void Json(CollectorSession s)
    {
        var reader = s.Reader;
        var providers = s.Providers();
        using var stream = Console.OpenStandardOutput();
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        w.WriteStartObject();

        w.WriteStartObject("header");
        w.WriteNumber("versionMajor", SharedMemoryLayout.VersionMajor);
        w.WriteNumber("versionMinor", SharedMemoryLayout.VersionMinor);
        w.WriteString("section", SharedMemoryLayout.SectionName);
        w.WriteString("collectorVersion", s.CollectorVersion);
        w.WriteNumber("collectorPid", s.CollectorPid);
        w.WriteNumber("heartbeatAgeS", Math.Round(s.HeartbeatAgeSeconds, 3));
        w.WriteNumber("qpcFrequency", s.QpcFrequency);
        w.WriteNumber("metricCount", s.MetricCount);
        w.WriteNumber("totalSize", reader.TotalSize);
        w.WriteEndObject();

        w.WriteStartArray("providers");
        foreach (var p in providers)
        {
            w.WriteStartObject();
            w.WriteNumber("index", p.Index);
            w.WriteString("name", p.Name);
            w.WriteString("state", p.State.ToString().ToLowerInvariant());
            w.WriteBoolean("needsElevation", p.NeedsElevation);
            w.WriteNumber("rateHz", Math.Round(p.RateHz, 3));
            w.WriteNumber("lastPollMs", Math.Round(p.LastPollMs, 3));
            w.WriteString("lastError", p.LastError);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("metrics");
        foreach (var m in s.Metrics())
        {
            w.WriteStartObject();
            w.WriteString("name", m.Name);
            w.WriteString("type", m.Type.ToString().ToLowerInvariant());
            w.WriteString("unit", m.Unit.ToString());
            w.WriteString("semantics", m.Semantics.ToString());
            w.WriteString("flags", m.Flags.ToString());
            w.WriteString("provider", providers.FirstOrDefault(p => p.Index == m.ProviderIndex).Name ?? "");
            w.WriteNumber("nominalHz", Math.Round(m.NominalRateHz, 4));
            w.WriteNumber("effectiveHz", Math.Round(m.EffectiveRateHz, 4));
            if (m.WindowMs > 0) w.WriteNumber("windowMs", m.WindowMs);

            if (m.Type == MetricType.String)
            {
                reader.TryReadString(m.Index, out string sv);
                w.WriteString("text", sv);
                w.WriteBoolean("stale", !reader.TryRead(m.Index, out _, out _));
            }
            else if (reader.TryRead(m.Index, out double v, out double age))
            {
                // JSON has no NaN/Infinity: report those as null so the document stays valid.
                if (double.IsFinite(v)) w.WriteNumber("value", v); else w.WriteNull("value");
                w.WriteNumber("ageS", Math.Round(age, 3));
                w.WriteBoolean("stale", false);
            }
            else
            {
                w.WriteNull("value");
                w.WriteBoolean("stale", true);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartObject("frames");
        w.WriteNumber("cursor", reader.FrameCursor);
        w.WriteEndObject();

        w.WriteEndObject();
        w.Flush();
        stream.Write("\n"u8);
    }
}
