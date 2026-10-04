using System.IO;
using Halo.Metrics;
using Halo.Shared;
using Halo.Shared.Config;
using Halo.Shared.Panels;

namespace Halo.Settings.Services;

/// <summary>A default layout ready to apply, and what to tell the user about it.</summary>
public sealed record DefaultLayoutPlan(List<WidgetInstance> Widgets, bool Offline, string Message);

/// <summary>
/// Profiles on the live config (decision D20). The active profile <i>is</i> settings.json +
/// widgets.json — every change, a desktop drag included, belongs to it — so nothing here ever
/// "saves" the active profile except when leaving it. A switch replaces the profile-scoped
/// sections of both files, widgets.json first, and captures what it replaced inside the same
/// transaction, so the outgoing snapshot is exactly what was on disk. Collector and diagnostics
/// settings are never read from or written to a profile.
/// </summary>
public sealed class ProfileService
{
    private readonly LiveConfigService _config;
    private int _sequence;

    public ProfileStore Store { get; }

    public string? ActiveId => _config.Settings.ActiveProfile;

    public ProfileService(LiveConfigService config)
    {
        _config = config;
        Store = new ProfileStore(Path.Combine(config.ConfigDir, "profiles"));
    }

    /// <summary>First launch with profiles: the live files become "Default". Only the active id is
    /// written; nothing else in the live files changes.</summary>
    public void EnsureActive()
    {
        try
        {
            string? before = ActiveId;
            string id = Store.EnsureActive(before, Live);
            if (id == before) return;
            _config.Settings.ActiveProfile = id;
            _config.QueueSettings("activeProfile", s => s.ActiveProfile = id, flushImmediately: true);
            Log.Info($"profiles: the live files became \"{Store.Load(id)?.Name}\" ({id}), now active");
        }
        catch (Exception ex) { Log.Error("profiles: could not set up the active profile", ex); }
    }

    /// <summary>The live files as a profile — what the active profile currently is.</summary>
    public Profile Live() => Profile.FromLive(ActiveName(), _config.Settings, _config.Widgets);

    public async Task<Profile> LiveFlushedAsync()
    {
        await _config.FlushAllAsync();
        return Live();
    }

    public string ActiveName()
        => ActiveId is { } id ? Store.Load(id)?.Name ?? ProfileStore.DefaultName : ProfileStore.DefaultName;

    /// <summary>Snapshot the live files into the active profile, then load <paramref name="id"/>.</summary>
    public async Task SelectAsync(string id)
    {
        if (id == ActiveId) return;
        Profile target = Store.Load(id) ?? throw new InvalidOperationException("That profile could not be read.");
        await ReplaceLiveAsync(target, id, snapshotOutgoing: true);
    }

    /// <summary>The live state becomes a new profile, which becomes active. Under auto-save the
    /// outgoing profile already holds the same state, so it is snapshotted too.</summary>
    public async Task<string> SaveAsNewAsync(string name)
    {
        Profile current = await LiveFlushedAsync();
        string? outgoing = ActiveId;
        if (outgoing != null && Store.Contains(outgoing)) Store.Save(outgoing, current);
        current.Name = name;
        string id = Store.Add(current);
        _config.Settings.ActiveProfile = id;
        _config.QueueSettings("activeProfile", s => s.ActiveProfile = id);
        await _config.FlushAllAsync();
        return id;
    }

    public async Task<string> NewDefaultAsync(string name, DefaultLayoutPlan plan)
    {
        string id = Store.Add(Profile.Default(name, plan.Widgets));
        try { await ReplaceLiveAsync(Store.Load(id)!, id, snapshotOutgoing: true); }
        catch
        {
            try { File.Delete(Path.Combine(Store.Folder, id + ".json")); } catch { /* left for the user to delete */ }
            throw;
        }
        return id;
    }

    /// <summary>The active profile, in place, back to a fresh look and a default layout. Collector
    /// and diagnostics settings are left alone.</summary>
    public async Task ResetActiveAsync(DefaultLayoutPlan plan)
    {
        string id = ActiveId ?? throw new InvalidOperationException("No profile is active.");
        Profile fresh = Profile.Default(Store.Load(id)?.Name ?? ProfileStore.DefaultName, plan.Widgets);
        await ReplaceLiveAsync(fresh, id, snapshotOutgoing: false);
        Store.Save(id, fresh);
    }

    public async Task<string> DuplicateAsync(string id)
        => Store.Duplicate(id, id == ActiveId ? await LiveFlushedAsync() : null);

    /// <summary>The active profile exports the live files; any other one, its file.</summary>
    public async Task ExportAsync(string id, string path)
    {
        Profile profile = id == ActiveId ? await LiveFlushedAsync()
            : Store.Load(id) ?? throw new InvalidOperationException("That profile could not be read.");
        ProfileStore.Export(profile, path);
    }

    /// <summary>System check's default layout, from the collector's discovered hardware when it is
    /// running (read-only, through shared memory), or the offline set otherwise.</summary>
    public static DefaultLayoutPlan CreateDefaultLayout()
    {
        using var session = new CollectorSession();
        bool connected = false;
        try
        {
            session.Poll();
            connected = session.Attached && !session.Stale;
        }
        catch (Exception ex) { Log.Warn($"profiles: collector session unavailable ({ex.Message}) — offline layout"); }
        List<WidgetInstance> widgets = DefaultLayout.Generate(name => session.Get(name), name => session.GetText(name), connected);
        return connected
            ? new(widgets, false, $"It starts with {widgets.Count} widgets for this PC's hardware, arranged on the primary monitor.")
            : new(widgets, true, "The collector is not running, so it starts with Clock, CPU/RAM and Network only. GPU, drive and fan widgets can be added later from System check or the Widgets page.");
    }

    /// <summary>
    /// Load <paramref name="incoming"/> into the live files and make <paramref name="activeId"/> the
    /// active profile. Each file's swap is one queued mutation, so its read, capture and write sit in
    /// one <c>ConfigStore.Transaction</c> — a drag the widget process saves just before is captured,
    /// not lost. The two halves are written one at a time and each is checked
    /// (<see cref="LiveConfigService.FlushFileAsync"/>): a captured value only means its mutation
    /// ran, not that the file it ran on was saved. widgets.json failing stops before settings.json;
    /// settings.json failing puts widgets.json back. Either way the failed mutation is cancelled so
    /// it cannot land on its own later.
    /// </summary>
    private async Task ReplaceLiveAsync(Profile incoming, string activeId, bool snapshotOutgoing)
    {
        const string NotChanged = "so the profile was not changed. The status line at the bottom says why.";
        string path = $"profile.{Interlocked.Increment(ref _sequence)}";
        string? outgoingId = ActiveId;
        string outgoingName = ActiveName();
        WidgetsConfig? oldWidgets = null;
        AppSettings? oldSettings = null;
        Profile target = incoming.Clone();

        // Plain assignment, not "??=": a batch that failed is run again on a fresh read by the
        // next flush, and the capture has to describe the document that write actually replaced.
        long widgetsGeneration = _config.QueueWidgets(path, w => { oldWidgets = Profile.FromLive("", new AppSettings(), w).Widgets; target.ApplyTo(w); });
        if (!await _config.FlushFileAsync(ConfigFileKind.Widgets))
        {
            _config.CancelPending(ConfigFileKind.Widgets, path, widgetsGeneration);
            _config.AnnounceReplaced();
            throw new IOException($"widgets.json could not be written, {NotChanged}");
        }

        long settingsGeneration = _config.QueueSettings(path, s =>
        {
            oldSettings = new AppSettings { LockAll = s.LockAll, Snap = s.Snap, Appearance = ProfileSettings.From(s).Appearance };
            target.ApplyTo(s);
            s.ActiveProfile = activeId;
        });
        if (!await _config.FlushFileAsync(ConfigFileKind.Settings))
        {
            _config.CancelPending(ConfigFileKind.Settings, path, settingsGeneration);
            WidgetsConfig restore = oldWidgets!;
            _config.QueueWidgets(path + ".undo", w => new Profile { Widgets = restore }.ApplyTo(w));
            // A restore that cannot be written either stays queued: it is the repair, and it lands
            // with the next widgets.json write that succeeds.
            bool restored = await _config.FlushFileAsync(ConfigFileKind.Widgets);
            _config.AnnounceReplaced();
            throw new IOException(restored
                ? $"settings.json could not be written, {NotChanged}"
                : "settings.json could not be written, and widgets.json could not be put back yet; it will be on the next successful save. The status line at the bottom says why.");
        }

        _config.AnnounceReplaced();
        if (snapshotOutgoing && outgoingId != null && outgoingId != activeId)
        {
            try { Store.Save(outgoingId, Profile.FromLive(outgoingName, oldSettings!, oldWidgets!)); }
            catch (Exception ex)
            {
                Log.Error($"profiles: could not save \"{outgoingName}\" ({outgoingId}) on the way out", ex);
                throw new IOException($"The profile changed, but the latest changes to \"{outgoingName}\" could not be saved ({ex.Message}).", ex);
            }
        }
        Log.Info($"profiles: {(outgoingId == activeId ? "reset" : "switched to")} {activeId}");
    }
}
