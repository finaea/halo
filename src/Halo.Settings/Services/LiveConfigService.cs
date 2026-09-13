using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using System.Windows;
using Halo.Shared.Config;

namespace Halo.Settings.Services;

public enum ConfigFileKind
{
    Settings,
    Widgets,
}

public sealed record ConfigChangedEventArgs(ConfigFileKind File, IReadOnlySet<string> DirtyPaths);

public sealed record ConfigWriteStatus(string Message, bool IsError = false, bool IsSaving = false);

/// <summary>
/// Settings-side live editing around the shared ConfigStore. Mutations are keyed by JSON path,
/// coalesced for 200 ms, and applied to a fresh disk snapshot before ConfigStore performs its
/// same-directory temp + move write. A content hash distinguishes our watcher echo from a real
/// edit made during ConfigStore's time-based suppression window.
/// </summary>
public sealed class LiveConfigService : IDisposable
{
    private sealed record Mutation<T>(long Generation, Action<T> Apply);

    private readonly ConfigStore _store;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly Dictionary<string, Mutation<AppSettings>> _settingsPending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Mutation<WidgetsConfig>> _widgetsPending = new(StringComparer.Ordinal);
    private readonly Dictionary<ConfigFileKind, string> _ownHashes = new();
    private readonly Dictionary<ConfigFileKind, string> _seenHashes = new();
    private readonly System.Threading.Timer _settingsWriteTimer;
    private readonly System.Threading.Timer _widgetsWriteTimer;
    private readonly System.Threading.Timer _settingsWatchTimer;
    private readonly System.Threading.Timer _widgetsWatchTimer;
    private readonly FileSystemWatcher _watcher;
    private long _generation;
    private bool _disposed;

    public event EventHandler<ConfigChangedEventArgs>? ExternalChanged;
    public event EventHandler<ConfigWriteStatus>? StatusChanged;

    public AppSettings Settings => _store.Settings;
    public WidgetsConfig Widgets => _store.Widgets;
    public string ConfigDir => _store.ConfigDir;

    public LiveConfigService(string configDir)
    {
        _store = new ConfigStore(configDir, watch: false);
        _settingsWriteTimer = new System.Threading.Timer(_ => _ = FlushAsync(ConfigFileKind.Settings));
        _widgetsWriteTimer = new System.Threading.Timer(_ => _ = FlushAsync(ConfigFileKind.Widgets));
        _settingsWatchTimer = new System.Threading.Timer(_ => _ = ProcessExternalChangeAsync(ConfigFileKind.Settings));
        _widgetsWatchTimer = new System.Threading.Timer(_ => _ = ProcessExternalChangeAsync(ConfigFileKind.Widgets));

        RememberInitialHash(ConfigFileKind.Settings);
        RememberInitialHash(ConfigFileKind.Widgets);

        _watcher = new FileSystemWatcher(configDir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
    }

    public void QueueSettings(string path, Action<AppSettings> apply, bool flushImmediately = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(apply);
        lock (_gate)
        {
            AddMutation(_settingsPending, path, new Mutation<AppSettings>(++_generation, apply));
        }
        PublishStatus(new("Saving…", IsSaving: true));
        _settingsWriteTimer.Change(flushImmediately ? 1 : 200, Timeout.Infinite);
    }

    public void QueueWidgets(string path, Action<WidgetsConfig> apply, bool flushImmediately = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(apply);
        lock (_gate)
        {
            AddMutation(_widgetsPending, path, new Mutation<WidgetsConfig>(++_generation, apply));
        }
        PublishStatus(new("Saving…", IsSaving: true));
        _widgetsWriteTimer.Change(flushImmediately ? 1 : 200, Timeout.Infinite);
    }

    public async Task FlushAllAsync()
    {
        _settingsWriteTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _widgetsWriteTimer.Change(Timeout.Infinite, Timeout.Infinite);
        await FlushAsync(ConfigFileKind.Settings).ConfigureAwait(false);
        await FlushAsync(ConfigFileKind.Widgets).ConfigureAwait(false);
    }

    public IReadOnlySet<string> DirtyPaths(ConfigFileKind kind)
    {
        lock (_gate)
        {
            return kind == ConfigFileKind.Settings
                ? _settingsPending.Keys.ToHashSet(StringComparer.Ordinal)
                : _widgetsPending.Keys.ToHashSet(StringComparer.Ordinal);
        }
    }

    private static void AddMutation<T>(Dictionary<string, Mutation<T>> pending, string path, Mutation<T> mutation)
    {
        // A section/root mutation supersedes queued descendants. A later descendant remains after
        // its parent and therefore intentionally wins for that one field.
        foreach (string key in pending.Keys.Where(key => IsDescendant(key, path)).ToArray())
            pending.Remove(key);
        pending[path] = mutation;
    }

    private static bool IsDescendant(string candidate, string parent)
        => parent == "$" || candidate.StartsWith(parent + ".", StringComparison.Ordinal);

    private async Task FlushAsync(ConfigFileKind kind)
    {
        Dictionary<string, Mutation<AppSettings>>? settingsBatch = null;
        Dictionary<string, Mutation<WidgetsConfig>>? widgetsBatch = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (kind == ConfigFileKind.Settings)
            {
                if (_settingsPending.Count == 0) return;
                settingsBatch = new(_settingsPending, StringComparer.Ordinal);
            }
            else
            {
                if (_widgetsPending.Count == 0) return;
                widgetsBatch = new(_widgetsPending, StringComparer.Ordinal);
            }
        }

        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ValidateCurrentFileAsync(kind).ConfigureAwait(false);
            _store.Reload();

            if (settingsBatch is not null)
            {
                foreach (Mutation<AppSettings> mutation in settingsBatch.Values.OrderBy(x => x.Generation))
                    mutation.Apply(_store.Settings);
                _store.SaveSettings();
            }
            else if (widgetsBatch is not null)
            {
                foreach (Mutation<WidgetsConfig> mutation in widgetsBatch.Values.OrderBy(x => x.Generation))
                    mutation.Apply(_store.Widgets);
                _store.SaveWidgets();
            }

            string hash = await ReadHashWithRetryAsync(PathFor(kind)).ConfigureAwait(false);
            lock (_gate)
            {
                _ownHashes[kind] = hash;
                _seenHashes[kind] = hash;
                if (settingsBatch is not null)
                    RemoveCommitted(_settingsPending, settingsBatch);
                else if (widgetsBatch is not null)
                    RemoveCommitted(_widgetsPending, widgetsBatch);
            }

            PublishStatus(new($"Saved {DateTime.Now:HH:mm:ss}"));
        }
        catch (Exception ex)
        {
            PublishStatus(new($"Could not save {FileName(kind)}: {ex.Message}", IsError: true));
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private static void RemoveCommitted<T>(Dictionary<string, Mutation<T>> pending, Dictionary<string, Mutation<T>> batch)
    {
        foreach ((string path, Mutation<T> written) in batch)
            if (pending.TryGetValue(path, out Mutation<T>? current) && current.Generation == written.Generation)
                pending.Remove(path);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        ConfigFileKind? kind = Path.GetFileName(e.FullPath).ToLowerInvariant() switch
        {
            "settings.json" => ConfigFileKind.Settings,
            "widgets.json" => ConfigFileKind.Widgets,
            _ => null,
        };
        if (kind == ConfigFileKind.Settings)
            _settingsWatchTimer.Change(200, Timeout.Infinite);
        else if (kind == ConfigFileKind.Widgets)
            _widgetsWatchTimer.Change(200, Timeout.Infinite);
    }

    private async Task ProcessExternalChangeAsync(ConfigFileKind kind)
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            string path = PathFor(kind);
            string hash = File.Exists(path) ? await ReadHashWithRetryAsync(path).ConfigureAwait(false) : "<missing>";
            lock (_gate)
            {
                if ((_ownHashes.TryGetValue(kind, out string? own) && own == hash) ||
                    (_seenHashes.TryGetValue(kind, out string? seen) && seen == hash))
                    return;
            }

            await ValidateCurrentFileAsync(kind).ConfigureAwait(false);
            _store.Reload();
            IReadOnlySet<string> dirty = DirtyPaths(kind);
            lock (_gate) _seenHashes[kind] = hash;
            PublishExternalChange(new(kind, dirty));
            PublishStatus(dirty.Count == 0
                ? new($"Loaded external changes from {FileName(kind)}")
                : new("File changed while you were editing; your latest value was kept"));
        }
        catch (Exception ex)
        {
            PublishStatus(new($"Could not load {FileName(kind)}: {ex.Message}", IsError: true));
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task ValidateCurrentFileAsync(ConfigFileKind kind)
    {
        string path = PathFor(kind);
        if (!File.Exists(path)) return;
        byte[] bytes = await ReadBytesWithRetryAsync(path).ConfigureAwait(false);
        try
        {
            object? value = kind == ConfigFileKind.Settings
                ? JsonSerializer.Deserialize(bytes, ConfigJsonContext.Default.AppSettings)
                : JsonSerializer.Deserialize(bytes, ConfigJsonContext.Default.WidgetsConfig);
            if (value is null) throw new JsonException("The file contains no configuration object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"invalid JSON ({ex.Message})", ex);
        }
    }

    private void RememberInitialHash(ConfigFileKind kind)
    {
        string path = PathFor(kind);
        if (!File.Exists(path))
        {
            _seenHashes[kind] = "<missing>";
            return;
        }
        try { _seenHashes[kind] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))); }
        catch { /* A later watcher event will retry and report a readable error. */ }
    }

    private static async Task<byte[]> ReadBytesWithRetryAsync(string path)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory).ConfigureAwait(false);
                return memory.ToArray();
            }
            catch (IOException ex)
            {
                last = ex;
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
        throw last ?? new IOException($"Could not read {path}.");
    }

    private static async Task<string> ReadHashWithRetryAsync(string path)
        => Convert.ToHexString(SHA256.HashData(await ReadBytesWithRetryAsync(path).ConfigureAwait(false)));

    private string PathFor(ConfigFileKind kind) => Path.Combine(ConfigDir, FileName(kind));
    private static string FileName(ConfigFileKind kind) => kind == ConfigFileKind.Settings ? "settings.json" : "widgets.json";

    private static void OnUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(action);
        else
            action();
    }

    private void PublishExternalChange(ConfigChangedEventArgs args)
        => OnUi(() => ExternalChanged?.Invoke(this, args));

    private void PublishStatus(ConfigWriteStatus status)
        => OnUi(() => StatusChanged?.Invoke(this, status));

    public void Dispose()
    {
        _settingsWriteTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _widgetsWriteTimer.Change(Timeout.Infinite, Timeout.Infinite);
        try { FlushAllAsync().GetAwaiter().GetResult(); } catch { }
        lock (_gate) _disposed = true;
        _watcher.Dispose();
        _settingsWriteTimer.Dispose();
        _widgetsWriteTimer.Dispose();
        _settingsWatchTimer.Dispose();
        _widgetsWatchTimer.Dispose();
        _store.Dispose();
    }
}
