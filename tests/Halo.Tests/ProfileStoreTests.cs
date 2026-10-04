using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Halo.Shared.Config;
using Halo.Shared.Panels;
using Halo.Shared.Skins;
using Xunit;

namespace Halo.Tests;

/// <summary>Ticket 12d: profiles. <see cref="ProfileStore"/> runs against a temp folder; the WPF
/// switch logic is exercised through the Shared API it is built on (snapshot = <c>Profile.FromLive</c>,
/// load = <c>ApplyTo</c>).</summary>
public sealed class ProfileStoreTests : IDisposable
{
    private const string R = SkinCatalog.RainformerId;
    private const string A = AzurArchiveSkinInfo.Id;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Halo.Tests", Guid.NewGuid().ToString("N"));
    private string ProfilesDir => Path.Combine(_dir, "profiles");
    private ProfileStore Store => new(ProfilesDir);

    public ProfileStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private static Profile P(string name, string monitor = "", int x = 1)
        => new()
        {
            Name = name,
            Widgets = new WidgetsConfig
            {
                Widgets = { new WidgetInstance { Id = "w-" + name, Type = "cpu-ram", Monitor = monitor, X = x } },
            },
        };

    private static string Json(Profile p) => JsonSerializer.Serialize(p.Settings, ConfigJsonContext.Default.ProfileSettings)
        + JsonSerializer.Serialize(p.Widgets, ConfigJsonContext.Default.WidgetsConfig);

    private string Zip(string name, string? settings, string? widgets)
    {
        string path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Put(string entry, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        if (settings != null) Put("settings.json", settings);
        if (widgets != null) Put("widgets.json", widgets);
        return path;
    }

    private const string LegacySettings = """
        {"schemaVersion":2,"lockAll":true,"snap":false,
         "appearance":{"colors":{"bar":"#112233FF","text":"#000000CD"}},
         "collector":{"frameLowsWindowS":99,"networkInterface":"Wi-Fi"},
         "diagnostics":{"logLevel":"debug"}}
        """;
    private const string LegacyWidgets = """
        {"schemaVersion":2,"widgets":[{"id":"w1","type":"clock","monitor":"\\\\.\\DISPLAY9","x":5,"y":6}]}
        """;

    // ---- 1. CRUD ----

    [Fact]
    public void Crud_AddListLoadRenameDuplicateDelete()
    {
        var s = Store;
        string a = s.Add(P("Work"));
        string b = s.Add(P("Play"));

        Assert.Equal(["Play", "Work"], s.List().Select(e => e.Name));
        Assert.Equal("Work", s.Load(a)!.Name);
        Assert.True(s.Contains(a));
        Assert.False(s.Contains("nope"));

        Assert.Equal("Office", s.Rename(a, "Office"));
        Assert.Equal("Office", s.Load(a)!.Name);          // id unchanged, name changed
        Assert.True(s.Contains(a));

        string d = s.Duplicate(a);
        Assert.Equal("Office copy", s.Load(d)!.Name);
        Assert.NotEqual(a, d);

        s.Delete(d, activeId: b);
        Assert.False(s.Contains(d));
    }

    [Fact]
    public void Delete_RefusesActiveAndLast()
    {
        var s = Store;
        string a = s.Add(P("One"));
        string b = s.Add(P("Two"));

        Assert.Throws<InvalidOperationException>(() => s.Delete(a, activeId: a));
        s.Delete(b, activeId: a);
        Assert.Throws<InvalidOperationException>(() => s.Delete(a, activeId: null));   // last one left
        Assert.True(s.Contains(a));
    }

    [Fact]
    public void Duplicate_WithLiveContents_UsesThemButStoredName()
    {
        var s = Store;
        string a = s.Add(P("Work", x: 1));
        string d = s.Duplicate(a, P("ignored", x: 99));
        Assert.Equal("Work copy", s.Load(d)!.Name);
        Assert.Equal(99, s.Load(d)!.Widgets.Widgets[0].X);
    }

    // ---- 2. name clashes ----

    [Fact]
    public void Add_ClashingName_GetsNumberedCaseInsensitively()
    {
        var s = Store;
        s.Add(P("Work"));
        string b = s.Add(P("work"));
        string c = s.Add(P("WORK"));
        Assert.Equal("work (2)", s.Load(b)!.Name);
        Assert.Equal("WORK (3)", s.Load(c)!.Name);
    }

    [Fact]
    public void Import_ClashingName_GetsNumbered()
    {
        var s = Store;
        s.Add(P("Shared"));
        string path = Path.Combine(_dir, "Shared.halo-profile");
        ProfileStore.Export(P("Shared"), path);
        string id = s.Import(path);
        Assert.Equal("Shared (2)", s.Load(id)!.Name);
    }

    [Fact]
    public void Rename_ToAnotherProfilesName_GetsNumbered_ButToOwnNameIsKept()
    {
        var s = Store;
        string a = s.Add(P("A"));
        s.Add(P("B"));
        Assert.Equal("b (2)", s.Rename(a, "b"));
        Assert.Equal("b (2)", s.Rename(a, "b (2)"));   // own name is not a clash
    }

    // ---- 3. snapshot on switch ----

    [Fact]
    public void SwitchSnapshotsLiveIntoOldProfile_AndLoadsNewIntoLive()
    {
        var s = Store;
        var settings = new AppSettings { LockAll = true, Snap = false };
        var skin = settings.Appearance.SkinFor(R);
        skin.Preset = "p1";
        skin.Colors["bar"] = "#112233FF";
        skin.PresetColors = new() { ["p2"] = new() { ["text"] = "#445566FF" } };
        var widgets = new WidgetsConfig { Widgets = { new WidgetInstance { Id = "w", Type = "clock", X = 7 } } };
        widgets.Widgets[0].SwitchSkin(R, A);
        widgets.Widgets[0].X = 70;

        string idA = s.Add(P("A"));
        string idB = s.Add(P("B", x: 42));
        string liveBefore = Json(Profile.FromLive("A", settings, widgets));

        // switch A -> B
        s.Save(idA, Profile.FromLive("A", settings, widgets));
        Profile b = s.Load(idB)!;
        b.ApplyTo(settings);
        b.ApplyTo(widgets);

        Assert.Equal(Json(b), Json(Profile.FromLive("B", settings, widgets)));
        Profile a = s.Load(idA)!;
        Assert.Equal(liveBefore, Json(a));
        Assert.Equal(70, a.Widgets.Widgets[0].X);
        Assert.Equal(7, a.Widgets.Widgets[0].Placements![R].X);
        Assert.Equal("#445566FF", a.Settings.Appearance.Skins[R].PresetColors!["p2"]["text"]);
        Assert.True(a.Settings.LockAll);
        Assert.False(a.Settings.Snap);
    }

    [Fact]
    public void FromLive_IsADeepCopy()
    {
        var settings = new AppSettings();
        settings.Appearance.SkinFor(R).Colors["bar"] = "#000000FF";
        var p = Profile.FromLive("x", settings, new WidgetsConfig());
        settings.Appearance.SkinFor(R).Colors["bar"] = "#FFFFFFFF";
        Assert.Equal("#000000FF", p.Settings.Appearance.Skins[R].Colors["bar"]);
    }

    [Fact]
    public void ConfigStoreRoundTrip_SwitchSurvivesDisk()
    {
        string cfg = Path.Combine(_dir, "config");
        var s = new ProfileStore(Path.Combine(cfg, "profiles"));
        using (var live = new ConfigStore(cfg, watch: false))
        {
            live.UpdateSettings(x => { x.LockAll = true; x.Appearance.SkinFor(R).Colors["bar"] = "#112233FF"; });
            live.UpdateWidgets(x => x.Widgets.Add(new WidgetInstance { Id = "w", Type = "clock", X = 3 }));
            string idA = s.EnsureActive(null, () => Profile.FromLive("", live.Settings, live.Widgets));
            string idB = s.Add(P("B", x: 55));

            s.Save(idA, Profile.FromLive("Default", live.Settings, live.Widgets));
            Profile b = s.Load(idB)!;
            live.UpdateSettings(x => { b.ApplyTo(x); x.ActiveProfile = idB; });
            live.UpdateWidgets(b.ApplyTo);
        }
        using var check = new ConfigStore(cfg, watch: false);
        Assert.False(check.Settings.LockAll);
        Assert.Equal(55, check.Widgets.Widgets[0].X);
        Assert.Equal("w-B", check.Widgets.Widgets[0].Id);
        Assert.Equal("b", check.Settings.ActiveProfile);
        Assert.Equal(3, s.Load("default")!.Widgets.Widgets[0].X);
        Assert.Equal("#112233FF", s.Load("default")!.Settings.Appearance.Skins[R].Colors["bar"]);
    }

    // ---- 4. scope ----

    private static AppSettings Machine() => new()
    {
        ActiveProfile = "keep-me",
        Collector = { FrameLowsWindowS = 77, NetworkInterface = "Ethernet 9" },
        Diagnostics = { LogLevel = "debug" },
    };

    private static void AssertMachineUntouched(AppSettings t)
    {
        Assert.Equal(77, t.Collector.FrameLowsWindowS);
        Assert.Equal("Ethernet 9", t.Collector.NetworkInterface);
        Assert.Equal("debug", t.Diagnostics.LogLevel);
        Assert.Equal("keep-me", t.ActiveProfile);
    }

    [Fact]
    public void ApplyTo_NeverTouchesCollectorDiagnosticsOrActiveProfile()
    {
        var src = new AppSettings { LockAll = true, Snap = false, Collector = { FrameLowsWindowS = 1 }, Diagnostics = { LogLevel = "error" }, ActiveProfile = "other" };
        src.Appearance.Skin = A;
        var target = Machine();
        Profile.FromLive("p", src, new WidgetsConfig()).ApplyTo(target);

        AssertMachineUntouched(target);
        Assert.True(target.LockAll);
        Assert.False(target.Snap);
        Assert.Equal(A, target.Appearance.Skin);
    }

    [Fact]
    public void Default_AndImport_NeverTouchCollectorDiagnosticsOrActiveProfile()
    {
        var t1 = Machine();
        Profile.Default("d", []).ApplyTo(t1);
        AssertMachineUntouched(t1);

        var layout = Zip("x.halo-layout", LegacySettings, LegacyWidgets);
        var t2 = Machine();
        ProfileStore.ReadImport(layout).ApplyTo(t2);
        AssertMachineUntouched(t2);               // legacy collector block (99 / Wi-Fi / debug) ignored
        Assert.True(t2.LockAll);

        string export = Path.Combine(_dir, "e.halo-profile");
        ProfileStore.Export(Profile.FromLive("e", Machine(), new WidgetsConfig()), export);
        var t3 = new AppSettings();
        ProfileStore.ReadImport(export).ApplyTo(t3);
        Assert.Equal(new CollectorSettings().FrameLowsWindowS, t3.Collector.FrameLowsWindowS);
        Assert.Null(t3.ActiveProfile);
    }

    [Fact]
    public void ProfileJson_HasNoCollectorOrDiagnostics()
    {
        string path = Path.Combine(_dir, "p.halo-profile");
        ProfileStore.Export(Profile.FromLive("p", Machine(), new WidgetsConfig()), path);
        string json = File.ReadAllText(path);
        Assert.DoesNotContain("collector", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnostics", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("activeProfile", json, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 5. legacy .halo-layout ----

    [Fact]
    public void LegacyLayout_UpgradesToV3_KeepsWidgets_NamedAfterFile()
    {
        var s = Store;
        string id = s.Import(Zip("My Desk.halo-layout", LegacySettings, LegacyWidgets));
        Profile p = s.Load(id)!;

        Assert.Equal("My Desk", p.Name);
        Assert.True(p.Settings.LockAll);
        Assert.False(p.Settings.Snap);
        var rf = p.Settings.Appearance.Skins[R];
        Assert.Equal("#112233FF", rf.Colors["bar"]);
        Assert.False(rf.Colors.ContainsKey("text"));          // equals the preset, so not a tweak
        Assert.Null(p.Settings.Appearance.V2Colors);
        var w = Assert.Single(p.Widgets.Widgets);
        Assert.Equal(("w1", "clock", 5, 6), (w.Id, w.Type, w.X, w.Y));
        Assert.Equal(AppSettings.CurrentSchemaVersion, p.Widgets.SchemaVersion);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void LegacyLayout_MissingEntry_Throws(bool hasSettings, bool hasWidgets)
    {
        string path = Zip("m.halo-layout", hasSettings ? LegacySettings : null, hasWidgets ? LegacyWidgets : null);
        Assert.Throws<InvalidDataException>(() => Store.Import(path));
        Assert.False(Store.Exists && Store.List().Count > 0);
    }

    [Fact]
    public void GarbageFile_Throws()
    {
        string path = Path.Combine(_dir, "junk.halo-profile");
        File.WriteAllText(path, "this is not json {{{");
        Assert.Throws<InvalidDataException>(() => Store.Import(path));
        File.WriteAllBytes(path, [0x00, 0x01, 0x02]);
        Assert.Throws<InvalidDataException>(() => Store.Import(path));
    }

    // ---- 6. monitor check ----

    [Fact]
    public void Import_NoKnownMonitor_MarksArrangePending()
    {
        string path = Path.Combine(_dir, "m.halo-profile");
        ProfileStore.Export(P("M", monitor: "GONE"), path);
        var s = Store;

        Assert.Equal(WidgetsConfig.ArrangePending, s.Load(s.Import(path, ["OTHER"]))!.Widgets.Arrange);
        Assert.Null(s.Load(s.Import(path, ["gone"]))!.Widgets.Arrange);            // known, case-insensitive
        Assert.Null(s.Load(s.Import(path))!.Widgets.Arrange);                      // no monitor list: no check
    }

    [Fact]
    public void Import_PrimaryMonitor_IsNotPending()
    {
        string path = Path.Combine(_dir, "p.halo-profile");
        ProfileStore.Export(P("P", monitor: ""), path);
        var s = Store;
        Assert.Null(s.Load(s.Import(path, ["OTHER"]))!.Widgets.Arrange);

        var mixed = P("Mixed", monitor: "GONE");
        mixed.Widgets.Widgets.Add(new WidgetInstance { Id = "b", Type = "clock", Monitor = "" });
        Assert.False(mixed.NoKnownMonitor(["OTHER"]));
        Assert.False(new Profile().NoKnownMonitor(["OTHER"]));                     // no widgets, nothing to arrange
    }

    // ---- 7. migration ----

    [Fact]
    public void EnsureActive_NoFolder_CreatesDefaultFromLive()
    {
        var s = Store;
        Assert.False(s.Exists);
        int calls = 0;
        string id = s.EnsureActive(null, () => { calls++; return P("whatever", x: 17); });

        Assert.True(s.Exists);
        Assert.Equal(1, calls);
        var entry = Assert.Single(s.List());
        Assert.Equal(("Default", id), (entry.Name, entry.Id));
        Assert.Equal(17, s.Load(id)!.Widgets.Widgets[0].X);

        // second call: unchanged, creates nothing, never asks for the live files
        Assert.Equal(id, s.EnsureActive(id, () => throw new InvalidOperationException("must not be called")));
        Assert.Single(s.List());
    }

    [Fact]
    public void EnsureActive_MissingFile_CreatesNewProfile()
    {
        var s = Store;
        string other = s.Add(P("Other"));
        string id = s.EnsureActive("ghost", () => P("live", x: 8));
        Assert.NotEqual("ghost", id);
        Assert.NotEqual(other, id);
        Assert.Equal(2, s.List().Count);
        Assert.Equal("Default", s.Load(id)!.Name);
    }

    // ---- 8. export / import round trip ----

    [Fact]
    public void ExportImport_RoundTrip_AddsNewProfile_DeepEqual()
    {
        var s = Store;
        var live = P("Round");
        live.Settings.Appearance.SkinFor(A).Colors["bar"] = "#ABCDEF12";
        live.Settings.Appearance.SkinFor(A).PresetColors = new() { ["port-day"] = new() { ["text"] = "#010203FF" } };
        live.Widgets.Widgets[0].SwitchSkin(R, A);
        string src = s.Add(live);
        string before = File.ReadAllText(Path.Combine(ProfilesDir, src + ".json"));

        string file = Path.Combine(_dir, "round" + ProfileStore.Extension);
        ProfileStore.Export(s.Load(src)!, file);
        string imported = s.Import(file);

        Assert.NotEqual(src, imported);
        Assert.Equal(2, s.List().Count);
        Assert.Equal(before, File.ReadAllText(Path.Combine(ProfilesDir, src + ".json")));   // source never overwritten
        Assert.Equal("Round (2)", s.Load(imported)!.Name);
        Assert.Equal(Json(s.Load(src)!), Json(s.Load(imported)!));
    }

    // ---- 9. default profile ----

    [Fact]
    public void Default_Offline_IsClockCpuRamNetwork_Pending()
    {
        var layout = DefaultLayout.Generate(_ => 0, _ => "", collectorOnline: false);
        var p = Profile.Default("Fresh", layout);

        Assert.Equal(["clock", "cpu-ram", "network"], p.Widgets.Widgets.Select(w => w.Type));
        AssertFreshDefault(p);
    }

    [Fact]
    public void Default_Online_HasGpuAndFps_Pending()
    {
        var layout = DefaultLayout.Generate(k => k == "gpu.count" ? 1 : 0, k => k == "gpu.0.vendor" ? "nvidia" : "", collectorOnline: true);
        var p = Profile.Default("Fresh", layout);

        Assert.Contains(p.Widgets.Widgets, w => w.Type == "gpu");
        Assert.Contains(p.Widgets.Widgets, w => w.Type == "latency");
        Assert.True(p.Widgets.Widgets.Count > 3);
        AssertFreshDefault(p);
    }

    private static void AssertFreshDefault(Profile p)
    {
        Assert.Equal(WidgetsConfig.ArrangePending, p.Widgets.Arrange);
        Assert.False(p.Settings.LockAll);
        Assert.True(p.Settings.Snap);
        Assert.Equal(R, p.Settings.Appearance.Skin);
        Assert.Empty(p.Settings.Appearance.Skins);
    }

    // ---- 10. unreadable file ----

    [Fact]
    public void List_SkipsUnreadableFile_AndLeavesItOnDisk()
    {
        var s = Store;
        string good = s.Add(P("Good"));
        string bad = Path.Combine(ProfilesDir, "broken.json");
        File.WriteAllText(bad, "{ not json");
        File.WriteAllText(Path.Combine(ProfilesDir, "null.json"), "null");

        var list = s.List();
        Assert.Equal(good, Assert.Single(list).Id);
        Assert.True(File.Exists(bad));
        Assert.Null(s.Load("broken"));
        Assert.False(s.Contains("missing"));
    }
}
