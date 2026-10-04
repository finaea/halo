using System.Text.Json;
using Halo.Metrics;

namespace Halo.Widgets.Harness;

/// <summary>
/// A pure in-memory <see cref="IMetricSource"/> loaded from a JSON snapshot, for the render
/// harness. Never touches shared memory: <c>MetricsWriter</c> hard-codes <c>Local\Halo.Metrics.v2</c>
/// and clears it on construction, so building a fake collector out of the real one would wipe
/// whatever collector is running on the machine.
///
/// <para>Fixture shape (all keys optional):</para>
/// <code>
/// {
///   "extends": "idle",                       // start from another fixture, then override
///   "stale": false,                          // true = collector down
///   "values": { "cpu.total.pct": 4.2 },      // registered, with a reading
///   "text":   { "cpu.name": "..." },         // registered strings
///   "na":     [ "cpu.package.temp.c" ],      // registered, reading absent (Has true, TryValue false)
///   "series": { "cpu.total.pct": [3, 9, 4] },// a reading that moves across the run, for graphs
///   "frametimesMs": [6.9, 7.1, 7.0],         // the frame ring, cycled
///   "durationS": 90,                         // run longer than the default 44 s (mood events)
///   "now": "2026-03-14T02:11:00",            // wall time other than PanelRenderer.PinnedNow
///   "poke": "arona",                         // a click on this character at the last tick
///   "pokeCount": 6                           // ...and that many clicks in a row, 2.2 s apart
/// }
/// </code>
/// A name that appears nowhere is unregistered — the "this machine has no such sensor" answer.
/// Every reading is fresh: the harness draws one instant, so there is no age to model.
/// </summary>
public sealed class FixtureMetrics : IMetricSource
{
    /// <summary>The fixtures that ship inside this assembly (and that the goldens cover).</summary>
    public static IReadOnlyList<string> BuiltIn { get; } = ["idle", "gaming", "hot", "na", "partial"];

    private readonly Dictionary<string, double> _values;
    private readonly Dictionary<string, string> _text;
    private readonly Dictionary<string, double[]> _series;
    private readonly HashSet<string> _na;
    private readonly List<MetricInfo> _registry = new();
    private readonly double[] _frametimesMs;

    private readonly List<FrameEntry> _newFrames = new();
    private int _frameIndex;
    private long _nextFrameQpc = long.MinValue;

    public bool Stale { get; }
    public double? DurationS { get; }
    public DateTime? Now { get; }
    public string? Poke { get; }
    public int PokeCount { get; }
    /// <summary>Mouse-wheel notches turned over the panel's scrollable zone before the draw (positive =
    /// back in time), so a render can show a scrolled thread.</summary>
    public int Wheel { get; }

    private FixtureMetrics(Raw raw)
    {
        Stale = raw.Stale;
        DurationS = raw.DurationS;
        Now = raw.Now;
        Poke = raw.Poke;
        PokeCount = raw.PokeCount;
        Wheel = raw.Wheel;
        _values = raw.Values;
        _text = raw.Text;
        _series = raw.Series;
        _na = raw.Na;
        _frametimesMs = raw.FrametimesMs;
        foreach (string n in _na) _values.Remove(n);

        var names = new SortedSet<string>(StringComparer.Ordinal);
        names.UnionWith(_values.Keys);
        names.UnionWith(_text.Keys);
        names.UnionWith(_series.Keys);
        names.UnionWith(_na);
        foreach (string n in names)
            _registry.Add(new MetricInfo(_registry.Count, n, default, default, default, default, 0, 0, 0, 0));
    }

    /// <summary>A built-in fixture by name, or a JSON file when the argument ends in .json.</summary>
    public static FixtureMetrics Load(string nameOrPath) => new(LoadRaw(nameOrPath));

    // ---- driving the run ----

    /// <summary>
    /// Move to <paramref name="progress"/> (0 = first tick, 1 = last) at <paramref name="nowQpc"/>:
    /// series readings take their value for that point, and frames are emitted up to that QPC —
    /// what one collector poll followed by one <c>MetricCache.Tick</c> would have delivered.
    /// </summary>
    public void Advance(double progress, long nowQpc)
    {
        foreach (var (name, points) in _series)
            if (!_na.Contains(name)) _values[name] = Sample(points, progress);

        _newFrames.Clear();
        if (_frametimesMs.Length == 0) return;
        if (_nextFrameQpc == long.MinValue) _nextFrameQpc = nowQpc;
        uint pid = (uint)Value(MetricNames.FpsAppPid);
        while (_nextFrameQpc <= nowQpc)
        {
            float ft = (float)_frametimesMs[_frameIndex++ % _frametimesMs.Length];
            _newFrames.Add(new FrameEntry
            {
                Qpc = _nextFrameQpc,
                FrametimeMs = ft,
                DisplayedFtMs = ft,
                Flags = (uint)(FrameFlags.Displayed | FrameFlags.AppFrame),
                Pid = pid,
            });
            _nextFrameQpc += (long)(ft / 1000.0 * System.Diagnostics.Stopwatch.Frequency);
        }
    }

    private static double Sample(double[] points, double progress)
    {
        if (points.Length == 1) return points[0];
        double pos = Math.Clamp(progress, 0, 1) * (points.Length - 1);
        int i = Math.Min((int)pos, points.Length - 2);
        return points[i] + (points[i + 1] - points[i]) * (pos - i);
    }

    // ---- IMetricSource ----

    public double Value(string name, double fallback = 0) => _values.TryGetValue(name, out double v) ? v : fallback;

    public bool TryValue(string name, out double value, double maxAgeS = double.MaxValue)
        => _values.TryGetValue(name, out value);

    public bool Has(string name) => _values.ContainsKey(name) || _text.ContainsKey(name) || _na.Contains(name);

    public string Text(string name, string fallback = "") => _text.TryGetValue(name, out string? s) ? s : fallback;

    public ReadOnlySpan<FrameEntry> NewFrames => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_newFrames);

    public IReadOnlyList<MetricInfo> Describe() => _registry;

    // ---- loading ----

    private sealed class Raw
    {
        public bool Stale;
        public Dictionary<string, double> Values = new(StringComparer.Ordinal);
        public Dictionary<string, string> Text = new(StringComparer.Ordinal);
        public Dictionary<string, double[]> Series = new(StringComparer.Ordinal);
        public HashSet<string> Na = new(StringComparer.Ordinal);
        public double[] FrametimesMs = [];
        public double? DurationS;
        public DateTime? Now;
        public string? Poke;
        public int PokeCount = 1;
        public int Wheel;
    }

    private static Raw LoadRaw(string nameOrPath)
    {
        string json = nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? File.ReadAllText(nameOrPath)
            : ReadBuiltIn(nameOrPath);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;

        // An override fixture starts as a copy of its base, so "hot" is idle plus what changes.
        var raw = root.TryGetProperty("extends", out var ext) ? LoadRaw(ext.GetString()!) : new Raw();

        if (root.TryGetProperty("stale", out var stale)) raw.Stale = stale.GetBoolean();
        if (root.TryGetProperty("values", out var values))
            foreach (var p in values.EnumerateObject()) { raw.Values[p.Name] = p.Value.GetDouble(); raw.Na.Remove(p.Name); }
        if (root.TryGetProperty("text", out var text))
            foreach (var p in text.EnumerateObject()) raw.Text[p.Name] = p.Value.GetString() ?? "";
        if (root.TryGetProperty("series", out var series))
            foreach (var p in series.EnumerateObject())
            {
                raw.Series[p.Name] = p.Value.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                raw.Na.Remove(p.Name);
            }
        if (root.TryGetProperty("na", out var na))
            foreach (var e in na.EnumerateArray()) raw.Na.Add(e.GetString()!);
        if (root.TryGetProperty("durationS", out var dur)) raw.DurationS = dur.GetDouble();
        if (root.TryGetProperty("now", out var now))
            raw.Now = DateTime.Parse(now.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal);
        if (root.TryGetProperty("poke", out var poke)) raw.Poke = poke.GetString();
        if (root.TryGetProperty("pokeCount", out var pokes)) raw.PokeCount = pokes.GetInt32();
        if (root.TryGetProperty("wheel", out var wheel)) raw.Wheel = wheel.GetInt32();
        if (root.TryGetProperty("frametimesMs", out var frames))
            raw.FrametimesMs = frames.EnumerateArray().Select(e => e.GetDouble()).ToArray();
        return raw;
    }

    private static string ReadBuiltIn(string name)
    {
        string resource = $"Halo.Widgets.Harness.Fixtures.{name}.json";
        using var stream = typeof(FixtureMetrics).Assembly.GetManifestResourceStream(resource)
            ?? throw new ArgumentException($"no fixture '{name}' (built-in: {string.Join(", ", BuiltIn)}; or pass a .json path)");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
