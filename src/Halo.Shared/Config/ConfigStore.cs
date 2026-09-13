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
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _suppressUntilTicks)) return;
        _debounce.Change(200, Timeout.Infinite);
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
