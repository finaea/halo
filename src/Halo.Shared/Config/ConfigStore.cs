using System.Text.Json;

namespace Halo.Shared.Config;

/// <summary>
/// Loads settings.json / widgets.json from the user's data folder and hot-reloads on change
/// (debounced FileSystemWatcher). Writers (Settings app, widget drag) save through this class
/// too; a save suppresses the immediate self-notification.
///
/// Consumers must read <see cref="Settings"/> through this object every time they need a value:
/// <see cref="Reload"/> replaces the instance, so a cached snapshot stops seeing edits (that was
/// a real bug in v1 — the network and PresentMon providers held the first object forever).
/// </summary>
public sealed class ConfigStore : IDisposable
{
    public string ConfigDir { get; }
    public AppSettings Settings { get; private set; } = new();
    public WidgetsConfig Widgets { get; private set; } = new();

    /// <summary>Fired on any config change (debounced, on a threadpool thread).</summary>
    public event Action? Changed;

    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer _debounce;
    private long _suppressUntilTicks;

    public ConfigStore(string configDir, bool watch = true)
    {
        ConfigDir = configDir;
        try
        {
            Directory.CreateDirectory(configDir);
        }
        catch (Exception ex)
        {
            // Paths already falls back to %TEMP% when the data root is unusable, so getting here
            // means something is badly wrong — say so instead of failing silently later.
            Log.Error($"config directory {configDir} could not be created", ex);
        }

        // A v1 install upgrading in place: convert before the first load so nothing sees v1 shapes.
        if (ConfigMigrator.IsLegacy(configDir))
        {
            try
            {
                var r = ConfigMigrator.Migrate(configDir, configDir, Log.Info);
                if (r.Migrated) Log.Info($"config: {r.Detail} (originals kept as *.v1.bak)");
            }
            catch (Exception ex) { Log.Error("config migration failed — loading defaults", ex); }
        }

        _debounce = new System.Threading.Timer(_ => { Reload(); Changed?.Invoke(); });
        Reload();
        if (watch)
        {
            _watcher = new FileSystemWatcher(configDir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFsEvent;
            _watcher.Created += OnFsEvent;
            _watcher.Renamed += OnFsEvent;
        }
    }

    private void OnFsEvent(object? s, FileSystemEventArgs e)
    {
        // Our own save must not trigger a reload storm, but it must not swallow someone else's
        // write either: two processes edit these files now (Settings while a user drags a slider,
        // the widget process on drag-end), and a dropped event leaves us on a stale copy until
        // the next unrelated change. So a suppressed event is deferred past the window, never lost.
        long suppressUntil = Interlocked.Read(ref _suppressUntilTicks);
        long now = DateTime.UtcNow.Ticks;
        int delayMs = now < suppressUntil
            ? (int)Math.Min(2000, (suppressUntil - now) / TimeSpan.TicksPerMillisecond) + 50
            : 200;
        _debounce.Change(delayMs, Timeout.Infinite);
    }

    public void Reload()
    {
        Settings = Load("settings.json", ConfigJsonContext.Default.AppSettings) ?? new AppSettings();
        Widgets = Load("widgets.json", ConfigJsonContext.Default.WidgetsConfig) ?? new WidgetsConfig();
    }

    private T? Load<T>(string file, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti) where T : class
    {
        string path = Path.Combine(ConfigDir, file);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return JsonSerializer.Deserialize(fs, ti);
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (JsonException) { return null; } // mid-write torn read; keep old config
        }
        return null;
    }

    public void SaveSettings() => Save("settings.json", Settings, ConfigJsonContext.Default.AppSettings);
    public void SaveWidgets() => Save("widgets.json", Widgets, ConfigJsonContext.Default.WidgetsConfig);

    private readonly Lock _writeLock = new();

    /// <summary>
    /// Change one widget's fields on disk without clobbering anyone else's.
    /// <para>
    /// Both the Settings app and the widget process write widgets.json: Settings every ~200 ms
    /// while a user edits, the widget process on drag-end and on a context-menu toggle. A
    /// whole-file <see cref="SaveWidgets"/> from a stale in-memory copy would silently revert
    /// whatever the other process wrote since the last reload. This re-reads the file, applies
    /// <paramref name="mutate"/> to that fresh copy, and writes it back atomically — so only the
    /// fields the caller touches move.
    /// </para>
    /// The caller's own in-memory <see cref="WidgetInstance"/> is NOT replaced: live widget
    /// windows hold references to it, and swapping the object under them is exactly the orphaned
    /// -config bug the hot-reload path is careful to avoid. Apply the same change there first.
    /// </summary>
    /// <returns>False when the id is not in the file (removed by the other writer).</returns>
    public bool UpdateWidget(string id, Action<WidgetInstance> mutate)
    {
        lock (_writeLock)
        {
            var fresh = Load("widgets.json", ConfigJsonContext.Default.WidgetsConfig);
            if (fresh == null)
            {
                // No file yet (first run, or it was deleted): our in-memory copy is all there is.
                mutate(Widgets.Widgets.FirstOrDefault(w => w.Id == id) ?? new WidgetInstance());
                SaveWidgets();
                return true;
            }
            var target = fresh.Widgets.FirstOrDefault(w => w.Id == id);
            if (target == null) return false;
            mutate(target);
            Save("widgets.json", fresh, ConfigJsonContext.Default.WidgetsConfig);
            return true;
        }
    }

    /// <summary>
    /// Same merge rule as <see cref="UpdateWidget"/>, for a change that spans the whole document:
    /// the arrange flag, or writing every widget's new position in one pass after the packer has
    /// run. Still a re-read of the file, so a concurrent Settings edit to fields the caller does
    /// not touch survives.
    /// </summary>
    public void UpdateWidgets(Action<WidgetsConfig> mutate)
    {
        lock (_writeLock)
        {
            var fresh = Load("widgets.json", ConfigJsonContext.Default.WidgetsConfig);
            if (fresh == null) { mutate(Widgets); SaveWidgets(); return; }
            mutate(fresh);
            Save("widgets.json", fresh, ConfigJsonContext.Default.WidgetsConfig);
        }
    }

    /// <summary>Same merge rule as <see cref="UpdateWidget"/> for settings.json — the widget
    /// process owns only <c>lockAll</c> there, Settings owns everything else.</summary>
    public void UpdateSettings(Action<AppSettings> mutate)
    {
        lock (_writeLock)
        {
            var fresh = Load("settings.json", ConfigJsonContext.Default.AppSettings);
            if (fresh == null) { mutate(Settings); SaveSettings(); return; }
            mutate(fresh);
            Save("settings.json", fresh, ConfigJsonContext.Default.AppSettings);
        }
    }

    private void Save<T>(string file, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti)
    {
        Interlocked.Exchange(ref _suppressUntilTicks, DateTime.UtcNow.AddMilliseconds(800).Ticks);
        string path = Path.Combine(ConfigDir, file);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(fs, value, ti);
        File.Move(tmp, path, overwrite: true);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
