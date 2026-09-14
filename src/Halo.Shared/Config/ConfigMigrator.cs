using System.Globalization;
using System.Text.Json;

namespace Halo.Shared.Config;

/// <summary>
/// One-way migration of the v1 config files (settings.json + theme.json + widgets.json, no
/// schemaVersion) to the v2 schema.
///
/// v1 kept hardware lists and per-widget knobs in global settings: drive letters, fan nicknames
/// and max RPM, top-process count, the "drive E shows free space" rule that was a C# literal, and
/// the graph-line toggles that were stringly options. All of those become per-widget settings
/// here, so the migrated layout renders exactly like the old one while the global file keeps only
/// what is genuinely global.
/// </summary>
public static class ConfigMigrator
{
    public sealed record Result(bool Migrated, string Detail);

    /// <summary>True when <paramref name="dir"/> holds config files that predate schemaVersion 2.</summary>
    public static bool IsLegacy(string dir)
        => IsLegacyFile(Path.Combine(dir, "settings.json")) || IsLegacyFile(Path.Combine(dir, "widgets.json"));

    private static bool IsLegacyFile(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            return !doc.RootElement.TryGetProperty("schemaVersion", out var v)
                || v.ValueKind != JsonValueKind.Number
                || v.GetInt32() < AppSettings.CurrentSchemaVersion;
        }
        catch { return false; }   // unreadable: leave it alone rather than overwrite it
    }

    /// <summary>
    /// Convert the v1 files in <paramref name="legacyDir"/> into v2 files in
    /// <paramref name="targetDir"/>. When the two are the same folder the originals are kept as
    /// <c>*.v1.bak</c>. Existing v2 files in the target are never overwritten.
    /// </summary>
    public static Result Migrate(string legacyDir, string targetDir, Action<string>? log = null)
    {
        string legacySettings = Path.Combine(legacyDir, "settings.json");
        string legacyTheme = Path.Combine(legacyDir, "theme.json");
        string legacyWidgets = Path.Combine(legacyDir, "widgets.json");

        if (!File.Exists(legacySettings) && !File.Exists(legacyWidgets))
            return new Result(false, $"no v1 config in {legacyDir}");

        bool inPlace = string.Equals(Path.GetFullPath(legacyDir), Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase);
        if (!inPlace && (File.Exists(Path.Combine(targetDir, "settings.json")) || File.Exists(Path.Combine(targetDir, "widgets.json"))))
            return new Result(false, $"{targetDir} already has config — not overwriting");

        var oldSettings = TryParse(legacySettings);
        var oldTheme = TryParse(legacyTheme);
        var oldWidgets = TryParse(legacyWidgets);

        var settings = BuildSettings(oldSettings?.RootElement, oldTheme?.RootElement);
        var widgets = BuildWidgets(oldWidgets?.RootElement, oldSettings?.RootElement);

        Directory.CreateDirectory(targetDir);
        WriteJson(Path.Combine(targetDir, "settings.json"), settings, ConfigJsonContext.Default.AppSettings);
        WriteJson(Path.Combine(targetDir, "widgets.json"), widgets, ConfigJsonContext.Default.WidgetsConfig);

        if (inPlace)
        {
            Backup(legacySettings);
            Backup(legacyWidgets);
            Backup(legacyTheme);
        }

        oldSettings?.Dispose(); oldTheme?.Dispose(); oldWidgets?.Dispose();

        string detail = $"migrated {widgets.Widgets.Count} widgets from {legacyDir} to {targetDir} (schema v2)";
        log?.Invoke(detail);
        return new Result(true, detail);
    }

    private static void Backup(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Move(path, path + ".v1.bak", overwrite: true); }
        catch (Exception ex) { Log.Warn($"config migration: could not back up {path}: {ex.Message}"); }
    }

    private static JsonDocument? TryParse(string path)
    {
        try { return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null; }
        catch (Exception ex) { Log.Warn($"config migration: {path} unreadable ({ex.Message})"); return null; }
    }

    // ---- settings.json + theme.json -> settings.json v2 ----

    private static AppSettings BuildSettings(JsonElement? s, JsonElement? theme)
    {
        var a = new AppSettings
        {
            LockAll = Bool(s, "lockAll") ?? false,
            Snap = true,
        };

        a.Appearance.FontFamily = Str(s, "fontFamily") ?? Str(theme, "fontFamily") ?? a.Appearance.FontFamily;
        a.Appearance.TextSizePt = Num(theme, "textSizePt") ?? a.Appearance.TextSizePt;
        // v1 kept the widget scale in theme.json. Write it back as an explicit number: an existing
        // layout is tuned to its scale, so "auto" (the fresh-install default) would resize it.
        double? scale = Num(theme, "scale");
        a.Appearance.Scale = scale is { } sc ? ScaleValue.Fixed(sc) : ScaleValue.Auto;
        a.Appearance.Colors = MigrateColors(theme);

        a.Collector.FrameLowsWindowS = Num(s, "frameLowsWindowS") ?? a.Collector.FrameLowsWindowS;
        a.Collector.PresentMonEtwFlushMs = (int)(Num(s, "presentMonEtwFlushMs") ?? a.Collector.PresentMonEtwFlushMs);
        // "console" is gone (the capture app was never on disk); anything unknown becomes "auto".
        string transport = (Str(s, "presentMonTransport") ?? "auto").ToLowerInvariant();
        a.Collector.PresentMonTransport = transport == "sdk" ? "sdk" : "auto";
        a.Collector.PresentedTap = (Str(s, "presentedTap") ?? "auto").ToLowerInvariant() == "off" ? "off" : "auto";
        a.Collector.NetworkInterface = Str(s, "networkInterface") ?? a.Collector.NetworkInterface;

        // v1 had no on/off switch and always fetched. Keep the behaviour the user already has
        // (a URL was configured) instead of silently blanking their External IP row; fresh
        // installs default to off (packaging plan P9).
        string? ipUrl = Str(s, "externalIpUrl");
        a.Collector.ExternalIp = new ExternalIpSettings
        {
            Enabled = !string.IsNullOrWhiteSpace(ipUrl),
            Url = string.IsNullOrWhiteSpace(ipUrl) ? a.Collector.ExternalIp.Url : ipUrl!,
            RefreshMinutes = Num(s, "externalIpRefreshMinutes") ?? a.Collector.ExternalIp.RefreshMinutes,
        };

        return a;
    }

    /// <summary>theme.json colours were [r,g,b,a] arrays; v2 uses #RRGGBBAA strings.</summary>
    private static Dictionary<string, string> MigrateColors(JsonElement? theme)
    {
        var result = new Dictionary<string, string>();
        if (theme is not { } t || !t.TryGetProperty("colors", out var colors) || colors.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var p in colors.EnumerateObject())
        {
            // The three tokens nothing ever drew with (stroke, maxValue, horizLine) are dropped.
            if (Panels.ThemeTokens.Default(p.Name) == null) continue;
            var arr = p.Value;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 3) continue;
            byte r = (byte)arr[0].GetInt32(), g = (byte)arr[1].GetInt32(), b = (byte)arr[2].GetInt32();
            byte al = arr.GetArrayLength() >= 4 ? (byte)arr[3].GetInt32() : (byte)255;
            string hex = Panels.ThemeTokens.ToHex(r, g, b, al);
            // Only keep real overrides; a token equal to the default is noise in the file.
            if (!string.Equals(hex, Panels.ThemeTokens.Default(p.Name), StringComparison.OrdinalIgnoreCase))
                result[p.Name] = hex;
        }
        return result;
    }

    // ---- widgets.json -> widgets.json v2 ----

    /// <summary>Legacy option key -> the metric whose graph line it toggled.</summary>
    private static readonly Dictionary<string, string> LegacyGraphOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["graphCpuTemp"] = "temp",
        ["graphCpuUsage"] = "usage",
        ["graphRamUsage"] = "ram",
        ["graphGpuTemp"] = "temp",
        ["graphGpuUsage"] = "usage",
        ["graphGpuMem"] = "vram",
        ["graphGpuFan"] = "fan",
        ["graphDriveWrite"] = "write",
        ["graphDriveRead"] = "read",
    };

    private static WidgetsConfig BuildWidgets(JsonElement? w, JsonElement? settings)
    {
        var cfg = new WidgetsConfig();
        if (w is not { } root || !root.TryGetProperty("widgets", out var list) || list.ValueKind != JsonValueKind.Array)
            return cfg;

        double defaultRate = Num(settings, "defaultRateHz") ?? 5;
        var driveLetters = StrList(settings, "driveLetters");
        var fanNames = StrMap(settings, "fanNames");
        var fanMaxRpm = NumMap(settings, "fanMaxRpm");
        int topN = (int)(Num(settings, "topProcessCount") ?? 5);

        foreach (var e in list.EnumerateArray())
        {
            var inst = new WidgetInstance
            {
                Id = Str(e, "id") ?? "",
                Type = Str(e, "type") ?? "",
                Enabled = Bool(e, "enabled") ?? true,
                Monitor = Str(e, "monitor") ?? "",
                X = (int)(Num(e, "x") ?? 0),
                Y = (int)(Num(e, "y") ?? 0),
                ZMode = Enum.TryParse<ZMode>(Str(e, "zMode"), ignoreCase: true, out var z) ? z : ZMode.Desktop,
                ClickThrough = Bool(e, "clickThrough") ?? false,
                KeepOnScreen = Bool(e, "keepOnScreen") ?? true,
                Locked = Bool(e, "locked") ?? false,
                Opacity = Num(e, "opacity") ?? 1.0,
                RateHz = Math.Clamp(Num(e, "rateHz") ?? defaultRate, 0.5, 10),
            };

            if (e.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object)
            {
                foreach (var o in opts.EnumerateObject())
                {
                    string key = o.Name;
                    string value = o.Value.ValueKind == JsonValueKind.String ? o.Value.GetString() ?? "" : o.Value.ToString();

                    if (key.Equals("title", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.Length > 0) inst.Title = value;      // titles are first-class in v2
                        continue;
                    }
                    if (LegacyGraphOptions.TryGetValue(key, out string? metricKey))
                    {
                        // v1 persisted only hidden lines ("false"); everything else was on.
                        if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
                            Metric(inst, metricKey).Graph = false;
                        continue;
                    }
                    inst.Options[key] = value;
                }
            }

            ApplyTypeDefaults(inst, driveLetters, fanNames, fanMaxRpm, topN);
            cfg.Widgets.Add(inst);
        }
        return cfg;
    }

    /// <summary>Move the global hardware lists onto the widgets that actually used them.</summary>
    private static void ApplyTypeDefaults(WidgetInstance inst, List<string> driveLetters,
        Dictionary<string, string> fanNames, Dictionary<string, double> fanMaxRpm, int topN)
    {
        switch (inst.Type)
        {
            case "drives":
                if (driveLetters.Count > 0)
                    inst.Options["volumes"] = string.Join(",", driveLetters.Select(d => d.ToUpperInvariant()));
                // v1 hardcoded "drive E shows Free:" in C# (DrivesPanel.cs:42). Same pixels, now a setting.
                inst.Options["freeMode"] = "E";
                break;

            case "fans":
                if (fanNames.Count > 0)
                {
                    var channels = fanNames.Keys
                        .Select(k => int.TryParse(k, out int n) ? n : -1)
                        .Where(n => n >= 0).OrderBy(n => n).ToList();
                    inst.Options["channels"] = string.Join(",", channels);
                    foreach (int ch in channels)
                    {
                        var m = Metric(inst, $"rpm.{ch}");
                        if (fanNames.TryGetValue(ch.ToString(CultureInfo.InvariantCulture), out string? nick) && nick.Length > 0)
                            m.Label = nick;
                        // v1 computed fan % collector-side from this table (default 2000 rpm).
                        m.Max = fanMaxRpm.TryGetValue(ch.ToString(CultureInfo.InvariantCulture), out double max) && max > 0 ? max : 2000;
                    }
                }
                break;

            case "cpu-ram":
                // v1's CPU fan row was hardcoded to channel 0 (MetricNames.CpuFanRpm = "fan.0.rpm").
                inst.Options["cpuFanChannel"] = "0";
                break;

            case "gpu":
            case "power":
                if (!inst.Options.ContainsKey("gpuIndex")) inst.Options["gpuIndex"] = "0";
                break;

            case "topcpu":
            case "topram":
                inst.Options["topN"] = Math.Clamp(topN, 1, 10).ToString(CultureInfo.InvariantCulture);
                break;
        }
    }

    private static MetricSetting Metric(WidgetInstance inst, string key)
    {
        if (!inst.Metrics.TryGetValue(key, out var m))
            inst.Metrics[key] = m = new MetricSetting();
        return m;
    }

    // ---- tolerant JSON helpers (a half-written or hand-edited v1 file must not throw) ----

    private static string? Str(JsonElement? e, string name)
        => e is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static double? Num(JsonElement? e, string name)
        => e is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    private static bool? Bool(JsonElement? e, string name)
        => e is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
           && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static List<string> StrList(JsonElement? e, string name)
    {
        var list = new List<string>();
        if (e is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s) list.Add(s);
        return list;
    }

    private static Dictionary<string, string> StrMap(JsonElement? e, string name)
    {
        var map = new Dictionary<string, string>();
        if (e is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            foreach (var p in v.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString() ?? "";
        return map;
    }

    private static Dictionary<string, double> NumMap(JsonElement? e, string name)
    {
        var map = new Dictionary<string, double>();
        if (e is { } el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            foreach (var p in v.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number) map[p.Name] = p.Value.GetDouble();
        return map;
    }

    private static int _tempSequence;

    /// <summary>
    /// Same atomic write as <see cref="ConfigStore"/>, and the temp name has to be unique for the
    /// same reason (audit finding 6b — this was the second place with the defect). Migration runs
    /// from the <see cref="ConfigStore"/> constructor, which <b>all three processes</b> call, and
    /// at logon the collector and widgets tasks start together: a shared <c>&lt;file&gt;.tmp</c>
    /// opened <see cref="FileShare.None"/> handed one of them an IOException, and the constructor
    /// logs that as "config migration failed — loading defaults".
    /// <para>
    /// The <c>mig</c> marker keeps these distinct from ConfigStore's own temps whatever the call
    /// order, rather than relying on migration never overlapping a save. The <c>.tmp</c> suffix
    /// stays because both config watchers glob <c>*.json</c>.
    /// </para>
    /// </summary>
    private static void WriteJson<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti)
    {
        string tmp = $"{path}.mig{Environment.ProcessId:x}-{Interlocked.Increment(ref _tempSequence):x}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(fs, value, ti);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }
}
