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

    /// <summary>
    /// A UTF-8 BOM is a hand edit, not corruption, and the reader has always treated it as one:
    /// <c>Load</c> deserializes from a <c>FileStream</c>, and that overload skips a BOM. The
    /// <c>byte[]</c> overload does not — it reports «'0xEF' is an invalid start of a value» — and
    /// the Settings app's pre-write validator used to call it. Reported 2026-09-15 against a
    /// settings.json a Windows PowerShell 5.1 <c>Set-Content</c> had rewritten: the collector and
    /// the widget process read it all day while Settings refused every save as invalid JSON.
    /// <para>
    /// So this pins the contract the two sides have to agree on rather than one call site: a BOM'd
    /// file <b>parses</b>, and — the half that actually bit — a BOM'd file is still <b>writable</b>.
    /// </para>
    /// </summary>
    [Fact]
    public void ByteOrderMark_ParsesAndStillAcceptsWrites()
    {
        using var store = Seeded();
        store.SaveSettings();

        // Put values on disk that the in-memory document does not have, then add the BOM. Asserting
        // the seeded 11 would pass on the fallback path too — LoadOutcome.Unreadable keeps the last
        // known-good copy, which holds exactly that. Only a real parse of these bytes yields 77.
        using (var onDisk = new ConfigStore(_dir, watch: false))
        {
            onDisk.Reload();
            onDisk.Widgets.Widgets.Single(w => w.Id == "cpu").X = 77;
            onDisk.Settings.LockAll = true;
            onDisk.SaveWidgets();
            onDisk.SaveSettings();
        }
        PrependBom(WidgetsPath);
        PrependBom(SettingsPath);

        store.Reload();

        Assert.Equal(2, store.Widgets.Widgets.Count);
        Assert.Equal(77, store.Widgets.Widgets.Single(w => w.Id == "cpu").X);
        Assert.True(store.Settings.LockAll);

        // The write gate has to reach the same verdict as the reader. This is the assertion that
        // fails if anyone puts a byte[] deserialize back in front of a config write.
        Assert.True(store.UpdateWidget("cpu", w => w.X = 99));
        store.UpdateWidgets(c => c.Arrange = WidgetsConfig.ArrangePending);
        store.UpdateSettings(s => s.LockAll = false);

        store.Reload();
        Assert.Equal(99, store.Widgets.Widgets.Single(w => w.Id == "cpu").X);
        Assert.False(store.Settings.LockAll);
    }

    /// <summary>
    /// The gate the Settings app runs before it writes, on the file that broke it. This is the
    /// assertion that was failing in the field: <c>ThrowIfUnparsable</c> deserialized from
    /// <c>byte[]</c>, which stops dead on the BOM's first byte, so a file every other Halo process
    /// could read was rejected here and the save was refused.
    /// </summary>
    [Theory]
    [InlineData(ConfigStore.SettingsFile)]
    [InlineData(ConfigStore.WidgetsFile)]
    public void ThrowIfUnparsable_AcceptsAByteOrderMark(string file)
    {
        using var store = Seeded();
        store.SaveSettings();
        string path = Path.Combine(_dir, file);
        PrependBom(path);

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        // The whole bug in one line. Pre-fix this threw InvalidDataException wrapping
        // «'0xEF' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0.»
        if (file == ConfigStore.SettingsFile)
            ConfigStore.ThrowIfUnparsable(bytes, ConfigJsonContext.Default.AppSettings);
        else
            ConfigStore.ThrowIfUnparsable(bytes, ConfigJsonContext.Default.WidgetsConfig);
    }

    /// <summary>The other half: tolerating a BOM must not turn the gate into a rubber stamp.</summary>
    [Fact]
    public void ThrowIfUnparsable_StillRejectsAHalfWrittenFile()
    {
        using var store = Seeded();
        Truncate(WidgetsPath);
        PrependBom(WidgetsPath);

        byte[] bytes = File.ReadAllBytes(WidgetsPath);
        Assert.Throws<InvalidDataException>(
            () => ConfigStore.ThrowIfUnparsable(bytes, ConfigJsonContext.Default.WidgetsConfig));
    }

    /// <summary>Rewrites the file with a UTF-8 BOM in front, byte for byte otherwise.</summary>
    private static void PrependBom(string path)
    {
        byte[] body = File.ReadAllBytes(path);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write([0xEF, 0xBB, 0xBF]);
        fs.Write(body);
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
