using System.IO;
using Halo.Shared.Config;
using Xunit;

namespace Halo.Tests;

/// <summary>
/// Audit finding 9 — <c>ConfigStore.Load</c> returned the same <c>null</c> for "no such file" and
/// for "the file is there and will not parse", and <c>Reload</c> turned both into a fresh default
/// document. One malformed widgets.json therefore published an empty layout, and
/// <c>App.ApplyConfigChange</c> closed every window that was not in the (now empty) wanted list.
/// The two cases are opposite: a missing file IS first run, an unreadable one must change nothing.
/// </summary>
public sealed class ConfigStoreLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Halo.Tests", Guid.NewGuid().ToString("N"));

    private string WidgetsPath => Path.Combine(_dir, ConfigStore.WidgetsFile);
    private string SettingsPath => Path.Combine(_dir, ConfigStore.SettingsFile);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private ConfigStore Seeded()
    {
        var store = new ConfigStore(_dir, watch: false);
        store.Widgets.Widgets.Add(new WidgetInstance { Id = "cpu", Type = "cpuram", X = 11 });
        store.Widgets.Widgets.Add(new WidgetInstance { Id = "gpu", Type = "gpu", X = 22 });
        store.SaveWidgets();
        store.Reload();
        return store;
    }

    [Fact]
    public void MissingFile_LoadsDefaults()
    {
        using var store = new ConfigStore(_dir, watch: false);

        Assert.False(File.Exists(WidgetsPath));
        Assert.Empty(store.Widgets.Widgets);
    }

    /// <summary>The audit named a trailing comma as the trigger. It is not one, and this pins that
    /// down: ConfigJsonContext sets AllowTrailingCommas and skips comments precisely so the files
    /// stay hand-editable, so "fixing" that would break a documented affordance.</summary>
    [Theory]
    [InlineData("trailing comma")]
    [InlineData("line comment")]
    public void ToleratedHandEdits_StillParse(string kind)
    {
        using var store = Seeded();
        string good = File.ReadAllText(WidgetsPath);
        File.WriteAllText(WidgetsPath, kind switch
        {
            "trailing comma" => good.Replace("]", ",]"),
            _ => "// a note to self" + Environment.NewLine + good,
        });

        store.Reload();

        Assert.Equal(2, store.Widgets.Widgets.Count);
        Assert.Equal(11, store.Widgets.Widgets.Single(w => w.Id == "cpu").X);
    }

    [Fact]
    public void UnreadableFile_KeepsLastKnownGoodDocument()
    {
        using var store = Seeded();
        Truncate(WidgetsPath);

        store.Reload();

        // The whole point: not an empty document, and not stale-but-wrong values either.
        Assert.Equal(2, store.Widgets.Widgets.Count);
        Assert.Equal(11, store.Widgets.Widgets.Single(w => w.Id == "cpu").X);
        Assert.Equal(22, store.Widgets.Widgets.Single(w => w.Id == "gpu").X);
    }

    [Fact]
    public void UnreadableFile_RefusesWrites_AndLeavesTheUsersFileAlone()
    {
        using var store = Seeded();
        string brokenWidgets = Truncate(WidgetsPath);
        // settings.json has its own second writer (the widget process owns lockAll there), so
        // break it too rather than asserting against a file that is merely absent — absent is
        // first run, which is the case that SHOULD write.
        store.SaveSettings();
        string brokenSettings = Truncate(SettingsPath);

        // Writing our in-memory copy over the top would throw away whatever the user is midway
        // through typing, so the change is refused instead. App.PatchWidget logs it; the Settings
        // app surfaces it as "Could not save widgets.json".
        Assert.Throws<InvalidDataException>(() => store.UpdateWidget("cpu", w => w.X = 99));
        Assert.Throws<InvalidDataException>(() => store.UpdateWidgets(c => c.Arrange = WidgetsConfig.ArrangePending));
        Assert.Throws<InvalidDataException>(() => store.UpdateSettings(s => s.LockAll = true));

        Assert.Equal(brokenWidgets, File.ReadAllText(WidgetsPath));
        Assert.Equal(brokenSettings, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void UnreadableFile_RecoversOnceItParsesAgain()
    {
        using var store = Seeded();
        string good = File.ReadAllText(WidgetsPath);
        Truncate(WidgetsPath);
        store.Reload();

        File.WriteAllText(WidgetsPath, good);
        store.Reload();
        Assert.Equal(2, store.Widgets.Widgets.Count);

        Assert.True(store.UpdateWidget("cpu", w => w.X = 99));
        store.Reload();
        Assert.Equal(99, store.Widgets.Widgets.Single(w => w.Id == "cpu").X);
    }

    [Fact]
    public void DeletedFile_FallsBackToDefaults_NotTheStaleDocument()
    {
        using var store = Seeded();
        Assert.Equal(2, store.Widgets.Widgets.Count);

        File.Delete(WidgetsPath);
        store.Reload();

        // Absent is first run, so defaults are correct here — this is the half that must NOT
        // behave like the unreadable case above.
        Assert.Empty(store.Widgets.Widgets);
    }

    /// <summary>A half-written hand edit: the real trigger, unlike a trailing comma.</summary>
    private static string Truncate(string path)
    {
        string whole = File.ReadAllText(path);
        string broken = whole[..(whole.Length / 2)];
        File.WriteAllText(path, broken);
        return broken;
    }
}
