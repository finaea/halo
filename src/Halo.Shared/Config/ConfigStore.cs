using System.Security.Cryptography;
using System.Text;
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
    public const string SettingsFile = "settings.json";
    public const string WidgetsFile = "widgets.json";

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
        _lockKey = LockKey(configDir);
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
        Settings = ReloadOne(SettingsFile, ConfigJsonContext.Default.AppSettings, Settings);
        Widgets = ReloadOne(WidgetsFile, ConfigJsonContext.Default.WidgetsConfig, Widgets);
    }

    /// <summary>
    /// The document to publish for <paramref name="file"/>. A <b>missing</b> file is first run —
    /// defaults are the right answer. A file that is <b>present but unreadable</b> is not: a
    /// widgets.json a user left half-edited — an unclosed brace, a string where a number goes —
    /// used to load as an empty document, and <c>ApplyConfigChange</c> then closed every widget
    /// window because none of them were in the (now empty) wanted list. Keep the last-known-good
    /// document instead, and let the log say why. (Trailing commas and // comments are fine: the
    /// serializer is configured to tolerate both so the files stay hand-editable — see
    /// ConfigJsonContext in Models.cs.)
    /// </summary>
    private T ReloadOne<T>(string file, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti, T current)
        where T : class, new()
        => Load(file, ti, out T? loaded) switch
        {
            LoadOutcome.Loaded => loaded!,
            LoadOutcome.Missing => new T(),
            _ => current,
        };

    private enum LoadOutcome
    {
        Loaded,
        /// <summary>No such file — first run, or the user deleted it.</summary>
        Missing,
        /// <summary>The file is there and we could not turn it into a document.</summary>
        Unreadable,
    }

    private LoadOutcome Load<T>(string file, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti, out T? value)
        where T : class
    {
        value = null;
        string path = Path.Combine(ConfigDir, file);
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) { NoteReadable(file); return LoadOutcome.Missing; }
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                value = JsonSerializer.Deserialize(fs, ti);
                if (value == null) return NoteUnreadable(file, "the file holds a bare JSON null");
                NoteReadable(file);
                return LoadOutcome.Loaded;
            }
            catch (Exception ex) when (IsTransient(ex)) { last = ex; Thread.Sleep(TransientRetryMs); }
            catch (JsonException ex) { return NoteUnreadable(file, ex.Message); }
        }
        // Held open by something else for 150 ms: present, just not readable right now. Treating
        // that as "missing" is what turned a locked file into a fresh default document.
        return NoteUnreadable(file, last?.Message ?? "could not be opened");
    }

    /// <summary>
    /// Parse-check config bytes exactly the way <see cref="Load"/> parses a file, and throw
    /// <see cref="InvalidDataException"/> naming the reason when they will not deserialize.
    /// <para>
    /// This is here, next to the reader, rather than at its caller on purpose. The Settings app
    /// validates the file on disk before it writes, so a half-finished hand edit is refused instead
    /// of clobbered — and that gate is only correct while it is exactly as strict as the reader it
    /// guards. It drifted: the gate called <c>JsonSerializer.Deserialize(byte[], …)</c>, which
    /// rejects a UTF-8 BOM with «'0xEF' is an invalid start of a value», while <see cref="Load"/>
    /// reads through a <c>FileStream</c> and that overload skips one. Reported 2026-09-15 against a
    /// settings.json some Windows PowerShell 5.1 <c>Set-Content</c> had rewritten (it writes a BOM
    /// by default): the collector and the widget process read the file all day, and the Settings
    /// app refused every save as invalid JSON. One reader, one verdict, no second opinion.
    /// </para>
    /// Takes the bytes rather than a path because the caller already holds them — it hashes the
    /// same array to tell its own writes apart from a peer's, and that hash has to cover the file
    /// as it actually sits on disk, BOM included.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a valid document of type
    /// <typeparamref name="T"/>.</exception>
    public static void ThrowIfUnparsable<T>(byte[] utf8Json,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti) where T : class
    {
        try
        {
            // MemoryStream, not the byte[] overload. See above — this line is the fix.
            using var stream = new MemoryStream(utf8Json, writable: false);
            if (JsonSerializer.Deserialize(stream, ti) is null)
                throw new JsonException("The file contains no configuration object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"invalid JSON ({ex.Message})", ex);
        }
    }

    private const int TransientAttempts = 3;
    private const int TransientRetryMs = 50;

    /// <summary>
    /// Someone else is holding the file for a moment — retry rather than lose the change.
    /// <para>
    /// <see cref="UnauthorizedAccessException"/> is in here for a measured reason, and it is the
    /// half that is easy to drop by mistake: it derives from <c>SystemException</c>, <b>not</b>
    /// <see cref="IOException"/>, so catching only the latter misses it entirely. A real-time
    /// scanner holding a freshly renamed config file produces exactly this — reproduced at 13.6%
    /// (3 runs in 22) as <c>UnauthorizedAccessException "Access to the path is denied."</c> on the
    /// first write after another process had just replaced widgets.json milliseconds earlier.
    /// </para>
    /// Unhandled, one of those costs the user a widget move and leaves nothing but a log line —
    /// the same "Halo forgot where I put it" symptom the unique temp name was meant to end.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex is IOException or UnauthorizedAccessException;

    // Logged on the way into the bad state and on the way out, not on every poll: the debounce
    // timer re-reads on every watcher event and an unreadable file usually stays unreadable.
    private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);

    private LoadOutcome NoteUnreadable(string file, string reason)
    {
        bool first;
        lock (_unreadable) first = _unreadable.Add(file);
        if (first)
            Log.Warn($"config: {file} is present but unreadable ({reason}) — keeping the last "
                + "known-good copy; Halo will not overwrite it until it parses again");
        return LoadOutcome.Unreadable;
    }

    private void NoteReadable(string file)
    {
        bool wasBad;
        lock (_unreadable) wasBad = _unreadable.Remove(file);
        if (wasBad) Log.Info($"config: {file} parses again — reloaded");
    }

    private string UnreadableMessage(string file)
        => $"{Path.Combine(ConfigDir, file)} is not valid JSON — fix or delete it and Halo will pick it up";

    public void SaveSettings()
        => Transaction(SettingsFile, () => Save(SettingsFile, Settings, ConfigJsonContext.Default.AppSettings));

    public void SaveWidgets()
        => Transaction(WidgetsFile, () => Save(WidgetsFile, Widgets, ConfigJsonContext.Default.WidgetsConfig));

    private readonly Lock _writeLock = new();
    private readonly string _lockKey;
    private readonly Dictionary<string, Mutex?> _fileMutexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long to wait for the other process before writing anyway. Every holder does a
    /// re-read plus one <c>File.Move</c>, so anything near this means the peer is wedged — and a
    /// frozen widget loop is worse than the lost update the lock exists to prevent.</summary>
    private const int CrossProcessWaitMs = 5000;

    /// <summary>
    /// Runs <paramref name="body"/> holding this instance's write lock <b>and</b> a machine-wide
    /// mutex for <paramref name="file"/>.
    /// <para>
    /// <see cref="_writeLock"/> alone only serialises writers inside one process, and there are
    /// two: the widget process patches widgets.json on drag-end, the Settings app rewrites it on a
    /// 200 ms debounce while the user drags a slider. Both do read → mutate → <c>File.Move</c>, and
    /// interleaving those two sequences loses whichever change was read first — the merge API
    /// stopped the whole-document clobber, not the race. So the lock has to span the whole
    /// transaction, including the caller's re-read, in <b>both</b> processes.
    /// </para>
    /// Callers that own their own read (the Settings app reloads into the objects its UI is bound
    /// to) call this directly; <see cref="UpdateWidget"/> and friends use it internally.
    /// </summary>
    public void Transaction(string file, Action body)
    {
        lock (_writeLock)
        {
            Mutex? mutex = FileMutex(file);
            bool held = false;
            try
            {
                try { held = mutex != null && mutex.WaitOne(CrossProcessWaitMs); }
                // The peer died holding it. WaitOne still hands us ownership, so carry on — the
                // file itself is fine either way, File.Move is atomic.
                catch (AbandonedMutexException) { held = true; }
                if (!held && mutex != null)
                    Log.Warn($"config: waited {CrossProcessWaitMs} ms for the {file} write lock and gave up — "
                        + "writing anyway; another Halo process looks wedged mid-save");
                body();
            }
            finally
            {
                if (held) try { mutex!.ReleaseMutex(); } catch (ApplicationException) { /* not ours any more */ }
            }
        }
    }

    /// <summary>The named mutex for one config file, or null when this machine will not give us
    /// one — in which case the in-process lock is all there is, which is exactly where we were
    /// before. A config write must never fail because a kernel object could not be opened.</summary>
    private Mutex? FileMutex(string file)
    {
        lock (_fileMutexes)
        {
            if (_fileMutexes.TryGetValue(file, out Mutex? existing)) return existing;
            try
            {
                // Session-local, like every other Halo name: the elevated collector, the widgets
                // and Settings all run as the interactive user in one session. Keyed by config
                // folder so a portable copy and an installed one never block each other.
                var created = new Mutex(false, $"Local\\Halo.Config.{_lockKey}.{file}");
                _fileMutexes[file] = created;
                return created;
            }
            catch (Exception ex)
            {
                Log.Warn($"config: no cross-process write lock for {file} ({ex.Message}) — "
                    + "concurrent edits from two Halo processes can lose each other");
                _fileMutexes[file] = null;
                return null;
            }
        }
    }

    private static string LockKey(string configDir)
    {
        string canonical;
        try { canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDir)).ToLowerInvariant(); }
        catch { canonical = configDir.ToLowerInvariant(); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    /// <summary>
    /// Change one widget's fields on disk without clobbering anyone else's.
    /// <para>
    /// Both the Settings app and the widget process write widgets.json: Settings every ~200 ms
    /// while a user edits, the widget process on drag-end and on a context-menu toggle. A
    /// whole-file <see cref="SaveWidgets"/> from a stale in-memory copy would silently revert
    /// whatever the other process wrote since the last reload. This re-reads the file, applies
    /// <paramref name="mutate"/> to that fresh copy, and writes it back atomically — all inside
    /// <see cref="Transaction"/>, so the other process cannot slip a write between the two halves.
    /// </para>
    /// The caller's own in-memory <see cref="WidgetInstance"/> is NOT replaced: live widget
    /// windows hold references to it, and swapping the object under them is exactly the orphaned
    /// -config bug the hot-reload path is careful to avoid. Apply the same change there first.
    /// </summary>
    /// <returns>False when the id is not in the file (removed by the other writer).</returns>
    /// <exception cref="InvalidDataException">The file exists but is not valid JSON. Overwriting
    /// it would throw away whatever the user was hand-editing, so the change is refused instead.</exception>
    public bool UpdateWidget(string id, Action<WidgetInstance> mutate)
    {
        bool written = false;
        Transaction(WidgetsFile, () =>
        {
            switch (Load(WidgetsFile, ConfigJsonContext.Default.WidgetsConfig, out WidgetsConfig? fresh))
            {
                case LoadOutcome.Missing:
                    // No file yet (first run, or it was deleted): our in-memory copy is all there is.
                    mutate(Widgets.Widgets.FirstOrDefault(w => w.Id == id) ?? new WidgetInstance());
                    Save(WidgetsFile, Widgets, ConfigJsonContext.Default.WidgetsConfig);
                    written = true;
                    return;
                case LoadOutcome.Unreadable:
                    throw new InvalidDataException(UnreadableMessage(WidgetsFile));
                default:
                    var target = fresh!.Widgets.FirstOrDefault(w => w.Id == id);
                    if (target == null) return;
                    mutate(target);
                    Save(WidgetsFile, fresh, ConfigJsonContext.Default.WidgetsConfig);
                    written = true;
                    return;
            }
        });
        return written;
    }

    /// <summary>
    /// Same merge rule as <see cref="UpdateWidget"/>, for a change that spans the whole document:
    /// the arrange flag, or writing every widget's new position in one pass after the packer has
    /// run. Still a re-read of the file, so a concurrent Settings edit to fields the caller does
    /// not touch survives.
    /// </summary>
    public void UpdateWidgets(Action<WidgetsConfig> mutate)
        => Update(WidgetsFile, ConfigJsonContext.Default.WidgetsConfig, () => Widgets, mutate);

    /// <summary>Same merge rule as <see cref="UpdateWidget"/> for settings.json — the widget
    /// process owns only <c>lockAll</c> there, Settings owns everything else.</summary>
    public void UpdateSettings(Action<AppSettings> mutate)
        => Update(SettingsFile, ConfigJsonContext.Default.AppSettings, () => Settings, mutate);

    private void Update<T>(string file, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti,
        Func<T> inMemory, Action<T> mutate) where T : class
        => Transaction(file, () =>
        {
            switch (Load(file, ti, out T? fresh))
            {
                case LoadOutcome.Missing:
                    T mine = inMemory();
                    mutate(mine);
                    Save(file, mine, ti);
                    return;
                case LoadOutcome.Unreadable:
                    throw new InvalidDataException(UnreadableMessage(file));
                default:
                    mutate(fresh!);
                    Save(file, fresh!, ti);
                    return;
            }
        });

    private static int _tempSequence;

    private void Save<T>(string file, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti)
    {
        Interlocked.Exchange(ref _suppressUntilTicks, DateTime.UtcNow.AddMilliseconds(800).Ticks);
        string path = Path.Combine(ConfigDir, file);
        // Unique per process and per write. Both processes used to write "<file>.tmp" with
        // FileShare.None, so two overlapping saves handed one of them an IOException — and the
        // only thing either caller can do with that is drop the change, which the user reads as
        // "Halo forgot where I put that widget". The .tmp suffix stays: the watchers glob *.json.
        // Retried, because a write that is merely inconvenient must not cost the user their
        // change. Reads have always retried; writes did not, and that asymmetry is what made a
        // single transient denial from a file-system filter drop a widget move (see IsTransient).
        // A fresh temp name per attempt on purpose: whatever was holding the last one is exactly
        // what we are waiting out, so reusing the name would retry into the same object.
        //
        // The loop has no upper bound in its header because the filter carries it: on the last
        // attempt `attempt < TransientAttempts` is false, the transient handler no longer matches,
        // and the bare catch below rethrows with the original stack intact.
        for (int attempt = 1; ; attempt++)
        {
            string tmp = $"{path}.{Environment.ProcessId:x}-{Interlocked.Increment(ref _tempSequence):x}.tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(fs, value, ti);
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < TransientAttempts && IsTransient(ex))
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
                Thread.Sleep(TransientRetryMs);
            }
            catch
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
                throw;
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
        lock (_fileMutexes)
        {
            foreach (Mutex? m in _fileMutexes.Values) m?.Dispose();
            _fileMutexes.Clear();
        }
    }
}
