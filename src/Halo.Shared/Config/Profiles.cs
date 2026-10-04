using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Halo.Shared.Config;

/// <summary>
/// One saved profile (decision D20): <c>config\profiles\&lt;id&gt;.json</c>, or a whole
/// <c>.halo-profile</c> export. It holds the profile-scoped half of the config — lock all, snap,
/// all of appearance and all of widgets.json (placements per skin and tweaks per preset come along
/// inside those). <b>Never</b> collector settings or the log level: those describe the machine,
/// not a look, and stay in settings.json whatever profile is active.
/// </summary>
public sealed class Profile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = "";
    public ProfileSettings Settings { get; set; } = new();
    public WidgetsConfig Widgets { get; set; } = new();

    /// <summary>A deep copy of the profile-scoped sections of the live documents.</summary>
    public static Profile FromLive(string name, AppSettings settings, WidgetsConfig widgets) => new()
    {
        Name = name,
        Settings = ProfileSettings.From(settings),
        Widgets = CloneWidgets(widgets),
    };

    /// <summary>A fresh look and a real default layout (<c>DefaultLayout.Generate</c>), left for the
    /// widget process to arrange — the same request System check's "Generate default layout" makes.</summary>
    public static Profile Default(string name, IEnumerable<WidgetInstance> layout) => new()
    {
        Name = name,
        Settings = new ProfileSettings(),
        Widgets = new WidgetsConfig
        {
            Widgets = CloneWidgets(new WidgetsConfig { Widgets = layout.ToList() }).Widgets,
            Arrange = WidgetsConfig.ArrangePending,
        },
    };

    /// <summary>Write this profile's sections into the live settings document. Collector,
    /// diagnostics and the active-profile id are left exactly as they are.</summary>
    public void ApplyTo(AppSettings target)
    {
        ProfileSettings copy = Clone(Settings, ConfigJsonContext.Default.ProfileSettings);
        target.LockAll = copy.LockAll;
        target.Snap = copy.Snap;
        target.Appearance = copy.Appearance ?? new AppearanceSettings();
    }

    /// <summary>Replace the live widgets document with this profile's.</summary>
    public void ApplyTo(WidgetsConfig target)
    {
        WidgetsConfig copy = CloneWidgets(Widgets);
        target.SchemaVersion = copy.SchemaVersion;
        target.Widgets = copy.Widgets;
        target.Arrange = copy.Arrange;
    }

    public Profile Clone() => Clone(this, ConfigJsonContext.Default.Profile);

    /// <summary>
    /// True when no widget would land on a monitor this PC has: every one names a device id that is
    /// not in <paramref name="knownMonitors"/>. "" means the primary monitor, which always exists.
    /// An unknown id falls back to the primary at draw time, so such a layout would pile up there
    /// unless the widget process arranges it.
    /// </summary>
    public bool NoKnownMonitor(IEnumerable<string> knownMonitors)
    {
        var known = new HashSet<string>(knownMonitors, StringComparer.OrdinalIgnoreCase);
        return Widgets.Widgets.Count > 0
            && Widgets.Widgets.All(w => !string.IsNullOrEmpty(w.Monitor) && !known.Contains(w.Monitor));
    }

    internal static WidgetsConfig CloneWidgets(WidgetsConfig widgets) => Clone(widgets, ConfigJsonContext.Default.WidgetsConfig);

    private static T Clone<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti)
        => JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, ti), ti)!;
}

/// <summary>The settings.json fields a profile owns.</summary>
public sealed class ProfileSettings
{
    public bool LockAll { get; set; }
    public bool Snap { get; set; } = true;
    public AppearanceSettings Appearance { get; set; } = new();

    public static ProfileSettings From(AppSettings settings)
    {
        var copy = new ProfileSettings { LockAll = settings.LockAll, Snap = settings.Snap, Appearance = settings.Appearance };
        return JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(copy, ConfigJsonContext.Default.ProfileSettings),
            ConfigJsonContext.Default.ProfileSettings)!;
    }
}

public sealed record ProfileEntry(string Id, string Name);

/// <summary>
/// The profiles folder (<c>config\profiles\</c>). Takes its folder rather than reaching for
/// <see cref="Paths"/>, so tests run against a temp directory. Nothing else watches this folder:
/// both config watchers look only at <c>config\*.json</c>, so a profile write reloads nothing.
/// <para>
/// The <b>active</b> profile is the live settings.json + widgets.json, not its file here (auto-save,
/// D20). Its file is refreshed when another profile is selected; until then it can be behind.
/// </para>
/// </summary>
public sealed class ProfileStore
{
    public const string Extension = ".halo-profile";
    public const string LegacyExtension = ".halo-layout";
    public const string DefaultName = "Default";

    public string Folder { get; }

    public ProfileStore(string folder) => Folder = folder;

    public bool Exists => Directory.Exists(Folder);

    /// <summary>Every readable profile, by name. A file that does not parse is skipped and logged,
    /// and left on disk.</summary>
    public IReadOnlyList<ProfileEntry> List()
    {
        if (!Exists) return [];
        var entries = new List<ProfileEntry>();
        foreach (string path in Directory.EnumerateFiles(Folder, "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(path);
            if (Load(id) is { } profile) entries.Add(new ProfileEntry(id, profile.Name));
        }
        return entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    public bool Contains(string? id) => !string.IsNullOrEmpty(id) && File.Exists(PathFor(id));

    public Profile? Load(string id)
    {
        string path = PathFor(id);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Profile? profile = JsonSerializer.Deserialize(fs, ConfigJsonContext.Default.Profile);
                if (profile == null) { Log.Warn($"profiles: {id}.json holds a bare null — skipped"); return null; }
                Normalise(profile, id);
                return profile;
            }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Warn($"profiles: {id}.json could not be read ({ex.Message}) — skipped");
                return null;
            }
        }
    }

    /// <summary>Write <paramref name="profile"/> as <paramref name="id"/>: same-folder temp file,
    /// then <c>File.Move</c>, retried on the transient denials a scanner causes.</summary>
    public void Save(string id, Profile profile)
    {
        Directory.CreateDirectory(Folder);
        string path = PathFor(id);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(profile, ConfigJsonContext.Default.Profile);
        for (int attempt = 1; ; attempt++)
        {
            string tmp = $"{path}.{Environment.ProcessId:x}-{Interlocked.Increment(ref _tempSequence):x}.tmp";
            try
            {
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
                Thread.Sleep(50);
            }
            catch
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
                throw;
            }
        }
    }

    private static int _tempSequence;

    /// <summary>Store <paramref name="profile"/> as a new profile under a free name (a clash gets
    /// " (2)", " (3)" …) and a new id. Returns the id.</summary>
    public string Add(Profile profile)
    {
        Profile copy = profile.Clone();
        copy.Name = UniqueName(copy.Name);
        string id = NewId(copy.Name);
        Save(id, copy);
        return id;
    }

    /// <summary>Rename only; the file and its id stay. A clash with another profile gets " (2)".</summary>
    public string Rename(string id, string name)
    {
        Profile profile = Load(id) ?? throw new InvalidOperationException("That profile no longer exists.");
        profile.Name = UniqueName(name, exceptId: id);
        Save(id, profile);
        return profile.Name;
    }

    /// <summary>Copy as "&lt;name&gt; copy". <paramref name="contents"/> overrides the stored
    /// sections — the active profile passes the live files, which are newer than its file.</summary>
    public string Duplicate(string id, Profile? contents = null)
    {
        Profile source = contents?.Clone() ?? Load(id) ?? throw new InvalidOperationException("That profile no longer exists.");
        source.Name = (Load(id)?.Name ?? source.Name) + " copy";
        return Add(source);
    }

    /// <summary>Refused for the active profile and for the last one left.</summary>
    public void Delete(string id, string? activeId)
    {
        if (string.Equals(id, activeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The active profile cannot be deleted. Select another profile first.");
        if (List().Count <= 1)
            throw new InvalidOperationException("The last profile cannot be deleted.");
        File.Delete(PathFor(id));
    }

    /// <summary>
    /// Make sure there is an active profile that exists, and return its id. With no profiles folder
    /// at all (first launch after the upgrade) — or an active id whose file is gone — the live files
    /// become a new profile, "Default". The live files themselves are not changed; the caller
    /// records the returned id as <see cref="AppSettings.ActiveProfile"/>.
    /// </summary>
    public string EnsureActive(string? activeId, Func<Profile> live)
    {
        if (Contains(activeId)) return activeId!;
        Profile current = live();
        current.Name = DefaultName;
        return Add(current);
    }

    public string UniqueName(string name, string? exceptId = null)
    {
        string trimmed = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
        var taken = new HashSet<string>(List().Where(e => !string.Equals(e.Id, exceptId, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name), StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(trimmed)) return trimmed;
        for (int n = 2; ; n++)
            if (!taken.Contains($"{trimmed} ({n})")) return $"{trimmed} ({n})";
    }

    // ---- export / import ----

    public static void Export(Profile profile, string path)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(profile, ConfigJsonContext.Default.Profile);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Add the profile in <paramref name="path"/> as a new one; never overwrites (D20). Reads a
    /// <c>.halo-profile</c> (JSON) or a legacy <c>.halo-layout</c> zip (settings.json + widgets.json,
    /// upgraded to schema v3 on the way in). A layout none of whose monitors exist on this PC is
    /// marked for arranging. Returns the new id.
    /// </summary>
    public string Import(string path, IEnumerable<string>? knownMonitors = null)
    {
        Profile profile = ReadImport(path);
        if (knownMonitors != null && profile.NoKnownMonitor(knownMonitors))
            profile.Widgets.Arrange = WidgetsConfig.ArrangePending;
        return Add(profile);
    }

    /// <summary>Parse an export without storing it. Throws <see cref="InvalidDataException"/> for a
    /// file that is neither format.</summary>
    public static Profile ReadImport(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string fallbackName = Path.GetFileNameWithoutExtension(path);
        Profile profile;
        if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K')
            profile = ReadLegacyLayout(bytes, fallbackName);
        else
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                profile = JsonSerializer.Deserialize(stream, ConfigJsonContext.Default.Profile)
                    ?? throw new InvalidDataException("The file is empty.");
            }
            catch (JsonException ex) { throw new InvalidDataException($"Not a Halo profile ({ex.Message}).", ex); }
        }
        Normalise(profile, fallbackName);
        return profile;
    }

    private static Profile ReadLegacyLayout(byte[] bytes, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        AppSettings settings = ReadEntry(archive, ConfigStore.SettingsFile, ConfigJsonContext.Default.AppSettings);
        WidgetsConfig widgets = ReadEntry(archive, ConfigStore.WidgetsFile, ConfigJsonContext.Default.WidgetsConfig);
        // A layout exported before schema v3 carries v2 appearance; bring it up the way ConfigStore
        // does at load, or its colours would be dropped. Its collector block is ignored: not profile scope.
        SchemaV3.Upgrade(settings);
        SchemaV3.Upgrade(widgets);
        return Profile.FromLive(name, settings, widgets);
    }

    private static T ReadEntry<T>(ZipArchive archive, string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> ti) where T : class
    {
        ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidDataException($"The layout does not contain {name}.");
        using Stream stream = entry.Open();
        try { return JsonSerializer.Deserialize(stream, ti) ?? throw new InvalidDataException($"{name} is empty."); }
        catch (JsonException ex) { throw new InvalidDataException($"{name} is not valid JSON ({ex.Message}).", ex); }
    }

    /// <summary>Fill what a hand-edited or older file may leave null, so nothing downstream has to.</summary>
    private static void Normalise(Profile profile, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = fallbackName;
        profile.Name = profile.Name.Trim();
        profile.Settings ??= new ProfileSettings();
        profile.Settings.Appearance ??= new AppearanceSettings();
        profile.Widgets ??= new WidgetsConfig();
        profile.Widgets.Widgets ??= new List<WidgetInstance>();
        SchemaV3.Upgrade(profile.Widgets);
    }

    // ---- ids ----

    private string PathFor(string id) => Path.Combine(Folder, id + ".json");

    /// <summary>A readable file name from the profile's name, made unique in the folder. Renaming
    /// later leaves it as it is.</summary>
    private string NewId(string name)
    {
        var slug = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
            if (slug.Length >= 40) break;
        }
        string stem = slug.ToString().Trim('-');
        if (stem.Length == 0 || ReservedNames.Contains(stem)) stem = stem.Length == 0 ? "profile" : stem + "-profile";
        if (!File.Exists(PathFor(stem))) return stem;
        for (int n = 2; ; n++)
            if (!File.Exists(PathFor($"{stem}-{n}"))) return $"{stem}-{n}";
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };
}
