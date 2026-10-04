using System.IO;
using System.Text.Json.Nodes;
using Halo.Shared.Config;
using Xunit;

namespace Halo.Tests;

/// <summary>
/// Audit finding 9 â€” <c>ConfigStore.Load</c> returned the same <c>null</c> for "no such file" and
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
        // break it too rather than asserting against a file that is merely absent â€” absent is
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

        // Absent is first run, so defaults are correct here â€” this is the half that must NOT
        // behave like the unreadable case above.
        Assert.Empty(store.Widgets.Widgets);
    }

    /// <summary>
    /// A UTF-8 BOM is a hand edit, not corruption, and the reader has always treated it as one:
    /// <c>Load</c> deserializes from a <c>FileStream</c>, and that overload skips a BOM. The
    /// <c>byte[]</c> overload does not â€” it reports Â«'0xEF' is an invalid start of a valueÂ» â€” and
    /// the Settings app's pre-write validator used to call it. Reported 2026-09-15 against a
    /// settings.json a Windows PowerShell 5.1 <c>Set-Content</c> had rewritten: the collector and
    /// the widget process read it all day while Settings refused every save as invalid JSON.
    /// <para>
    /// So this pins the contract the two sides have to agree on rather than one call site: a BOM'd
    /// file <b>parses</b>, and â€” the half that actually bit â€” a BOM'd file is still <b>writable</b>.
    /// </para>
    /// </summary>
    [Fact]
    public void ByteOrderMark_ParsesAndStillAcceptsWrites()
    {
        using var store = Seeded();
        store.SaveSettings();

        // Put values on disk that the in-memory document does not have, then add the BOM. Asserting
        // the seeded 11 would pass on the fallback path too â€” LoadOutcome.Unreadable keeps the last
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
        // Â«'0xEF' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0.Â»
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

    // ---- schema v2 â†’ v3 (SchemaV3.Upgrade, applied by ConfigStore.Load) ----

    private ConfigStore LoadV2Settings(string appearanceJson)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, $$"""{ "schemaVersion": 2, "appearance": {{appearanceJson}} }""");
        return new ConfigStore(_dir, watch: false);
    }

    private ConfigStore LoadV2Widget(string appearanceJson)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(WidgetsPath,
            $$"""{ "schemaVersion": 2, "widgets": [ { "id": "cpu", "type": "cpuram", "appearance": {{appearanceJson}} } ] }""");
        return new ConfigStore(_dir, watch: false);
    }

    private static SkinSettings Rainformer(AppearanceSettings a) => a.Skins["rainformer"];

    [Fact]
    public void V2_DefaultColours_BecomeThePresetWithNoTweaks()
    {
        string colors = string.Join(",", Halo.Shared.Panels.ThemeTokens.Defaults.Select(d => $"\"{d.Token}\":\"{d.Color}\""));
        using var store = LoadV2Settings($"{{ \"colors\": {{ {colors} }} }}");

        var a = store.Settings.Appearance;
        Assert.Equal(3, store.Settings.SchemaVersion);
        Assert.Equal("rainformer-light", Rainformer(a).Preset);
        Assert.Empty(Rainformer(a).Colors);
        Assert.Null(a.V2Colors);
    }

    [Fact]
    public void V2_SparseColours_KeepOnlyTheTokenThatDiffers()
    {
        // bgBody differs, text is spelled as the default and so is not a tweak.
        using var store = LoadV2Settings("""{ "colors": { "bgBody": "#112233FF", "text": "#000000CD" } }""");

        var tweaks = Rainformer(store.Settings.Appearance).Colors;
        Assert.Equal(new[] { "bgBody" }, tweaks.Keys);
        Assert.Equal("#112233FF", tweaks["bgBody"]);
    }

    [Fact]
    public void V2_CustomColours_BecomeExactTweaks()
    {
        using var store = LoadV2Settings("""{ "colors": { "bgTop": "#010203FF", "bar": "#A1B2C3D4" } }""");

        var tweaks = Rainformer(store.Settings.Appearance).Colors;
        Assert.Equal(2, tweaks.Count);
        Assert.Equal("#010203FF", tweaks["bgTop"]);
        Assert.Equal("#A1B2C3D4", tweaks["bar"]);
    }

    [Fact]
    public void V2_DefaultColourInAnotherSpelling_IsNotATweak()
    {
        // lowercase 8-digit bgBody, and 6-digit bar (alpha FF implied == default #5D8DACFF)
        using var store = LoadV2Settings("""{ "colors": { "bgBody": "#e6e6e6b4", "bar": "#5d8dac" } }""");

        Assert.Empty(Rainformer(store.Settings.Appearance).Colors);
    }

    [Fact]
    public void V2_WidgetColours_MoveAcrossVerbatim_WithoutPickingAPreset()
    {
        // Includes a value equal to the default: a widget tweak overrides the *global* look, so it stays.
        using var store = LoadV2Widget("""{ "colors": { "bgBody": "#E6E6E6B4", "text": "#112233FF" } }""");

        var app = store.Widgets.Widgets.Single().Appearance;
        Assert.Equal(3, store.Widgets.SchemaVersion);
        var rf = app.Skins!["rainformer"];
        Assert.Null(rf.Preset);
        Assert.Equal("#E6E6E6B4", rf.Colors["bgBody"]);
        Assert.Equal("#112233FF", rf.Colors["text"]);
        Assert.Equal(2, rf.Colors.Count);
        Assert.Null(app.V2Colors);
    }

    [Theory]
    [InlineData("Trebuchet MS", null)]
    [InlineData("trebuchet ms", null)]
    [InlineData("Segoe UI", "Segoe UI")]
    public void V2_FontFamily(string input, string? expected)
    {
        using var store = LoadV2Settings($"{{ \"fontFamily\": \"{input}\" }}");

        Assert.Equal(expected, store.Settings.Appearance.FontFamily);
    }

    [Fact]
    public void V2_NonDefaultOptions_BecomeSkinOptions_AndDefaultsDoNot()
    {
        using (var store = LoadV2Settings("""{ "cornerRadius": 9, "textSizePt": 10 }"""))
        {
            var o = Rainformer(store.Settings.Appearance).Options;
            Assert.Equal("9", o["cornerRadius"]);
            Assert.Equal("10", o["textSizePt"]);
            Assert.Null(store.Settings.Appearance.V2CornerRadius);
            Assert.Null(store.Settings.Appearance.V2TextSizePt);
        }

        using var defaults = LoadV2Settings("""{ "cornerRadius": 4, "textSizePt": 8 }""");
        Assert.Empty(Rainformer(defaults.Settings.Appearance).Options);
    }

    private const string V3Appearance = """
        {
          "skin": "rainformer",
          "scale": "auto",
          "motion": "subtle",
          "motionFps": 30,
          "skins": {
            "rainformer": { "preset": "rainformer-light", "colors": { "bgBody": "#112233FF", "futureToken": "#445566FF" }, "options": { "cornerRadius": "9", "futureOption": "x" } },
            "future-skin": { "preset": "no-such-preset", "colors": { "whatever": "#FFFFFFFF" }, "options": { "k": "v" } }
          }
        }
        """;

    [Fact]
    public void V3Settings_RoundTripUnchanged_KeepingUnknownEntries()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, $$"""{ "schemaVersion": 3, "appearance": {{V3Appearance}} }""");
        using var store = new ConfigStore(_dir, watch: false);

        store.SaveSettings();

        var written = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(V3Appearance), written["appearance"]),
            written["appearance"]!.ToJsonString());
        Assert.Equal(3, (int)written["schemaVersion"]!);
    }

    [Fact]
    public void V3_RetiredTextSizePt_LoadsAndSurvivesAWrite()
    {
        // Rainformer no longer declares textSizePt, but a stored value is the user's data: it loads,
        // and an UpdateSettings write does not prune it.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
            { "schemaVersion": 3, "appearance": { "skins": { "rainformer": { "options": { "cornerRadius": "9", "textSizePt": "10" } } } } }
            """);
        using var store = new ConfigStore(_dir, watch: false);

        Assert.Equal("10", Rainformer(store.Settings.Appearance).Options["textSizePt"]);

        store.UpdateSettings(s => s.Appearance.Motion = "off");

        var o = JsonNode.Parse(File.ReadAllText(SettingsPath))!["appearance"]!["skins"]!["rainformer"]!["options"]!;
        Assert.Equal("10", (string)o["textSizePt"]!);
        Assert.Equal("9", (string)o["cornerRadius"]!);
    }

    [Fact]
    public void V3Widgets_RoundTripUnchanged_KeepingUnknownEntries()
    {
        const string widgetAppearance = """
            {
              "skin": "future-skin",
              "skins": {
                "rainformer": { "preset": "rainformer-light", "colors": { "bgBody": "#112233FF", "futureToken": "#445566FF" }, "options": { "cornerRadius": "2" } },
                "future-skin": { "preset": "no-such-preset", "colors": { "whatever": "#FFFFFFFF" }, "options": { "k": "v" } }
              }
            }
            """;
        Directory.CreateDirectory(_dir);
        File.WriteAllText(WidgetsPath,
            $$"""{ "schemaVersion": 3, "widgets": [ { "id": "cpu", "type": "cpuram", "appearance": {{widgetAppearance}} } ] }""");
        using var store = new ConfigStore(_dir, watch: false);

        store.SaveWidgets();

        var written = JsonNode.Parse(File.ReadAllText(WidgetsPath))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(widgetAppearance), written["widgets"]![0]!["appearance"]),
            written["widgets"]![0]!["appearance"]!.ToJsonString());
    }

    [Fact]
    public void SavingAV2File_WritesV3_WithNoV2AppearanceFields()
    {
        using var store = LoadV2Settings("""{ "colors": { "bgBody": "#112233FF" }, "cornerRadius": 9, "textSizePt": 10 }""");
        store.SaveSettings();

        var written = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal(3, (int)written["schemaVersion"]!);
        var appearance = written["appearance"]!.AsObject();
        Assert.False(appearance.ContainsKey("colors"));
        Assert.False(appearance.ContainsKey("cornerRadius"));
        Assert.False(appearance.ContainsKey("textSizePt"));
        Assert.Equal("#112233FF", (string)appearance["skins"]!["rainformer"]!["colors"]!["bgBody"]!);

        using var widgetStore = LoadV2Widget("""{ "colors": { "text": "#112233FF" } }""");
        widgetStore.SaveWidgets();
        var w = JsonNode.Parse(File.ReadAllText(WidgetsPath))!;
        Assert.Equal(3, (int)w["schemaVersion"]!);
        Assert.False(w["widgets"]![0]!["appearance"]!.AsObject().ContainsKey("colors"));
    }

    /// <summary>Regression: IsLegacy once meant "not current version", so a v2 file would have been
    /// rebuilt from v1 field names â€” wiping it.</summary>
    [Fact]
    public void V2Files_AreNotLegacy()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "schemaVersion": 2, "lockAll": true }""");
        File.WriteAllText(WidgetsPath, """{ "schemaVersion": 2, "widgets": [] }""");
        Assert.False(ConfigMigrator.IsLegacy(_dir));

        File.WriteAllText(SettingsPath, """{ "lockAll": true }""");
        Assert.True(ConfigMigrator.IsLegacy(_dir));
    }

    /// <summary>
    /// An in-place v1 migration used to write the new files and then <c>File.Move</c> the same
    /// paths to <c>*.v1.bak</c>: the migrated settings.json vanished, the migrated widgets ended up
    /// only in the backup, and the v1 bytes were already gone. The backup must be the v1 file and
    /// the active file the migrated one.
    /// </summary>
    [Fact]
    public void InPlaceV1Migration_KeepsTheMigratedFiles_AndBacksUpTheOriginals()
    {
        Directory.CreateDirectory(_dir);
        string themePath = Path.Combine(_dir, "theme.json");
        File.WriteAllText(SettingsPath, """{ "lockAll": true, "fontFamily": "Consolas" }""");
        File.WriteAllText(WidgetsPath, """{ "widgets": [ { "id": "cpu", "type": "cpu-ram", "x": 11 }, { "id": "gpu", "type": "gpu", "x": 22 } ] }""");
        File.WriteAllText(themePath, """{ "textSizePt": 8, "colors": { "bgBody": [1, 2, 3, 255] } }""");
        byte[] v1Settings = File.ReadAllBytes(SettingsPath);
        byte[] v1Widgets = File.ReadAllBytes(WidgetsPath);
        byte[] v1Theme = File.ReadAllBytes(themePath);

        Assert.True(ConfigMigrator.Migrate(_dir, _dir).Migrated);

        Assert.Equal(v1Settings, File.ReadAllBytes(SettingsPath + ".v1.bak"));
        Assert.Equal(v1Widgets, File.ReadAllBytes(WidgetsPath + ".v1.bak"));
        Assert.Equal(v1Theme, File.ReadAllBytes(themePath + ".v1.bak"));
        Assert.False(File.Exists(themePath));   // no v3 counterpart, so it is moved, not kept
        Assert.False(ConfigMigrator.IsLegacy(_dir));

        using var store = new ConfigStore(_dir, watch: false);
        Assert.Equal(AppSettings.CurrentSchemaVersion, store.Settings.SchemaVersion);
        Assert.True(store.Settings.LockAll);
        Assert.Equal("Consolas", store.Settings.Appearance.FontFamily);
        Assert.Equal("#010203FF", store.Settings.Appearance.Skins["rainformer"].Colors["bgBody"]);
        Assert.Equal(AppSettings.CurrentSchemaVersion, store.Widgets.SchemaVersion);
        Assert.Equal(["cpu", "gpu"], store.Widgets.Widgets.Select(w => w.Id));
    }

    /// <summary>A preset id that no longer ships ("nord") is the user's data: loading it neither
    /// rewrites the file nor changes the in-memory value. Resolution falls back at draw time only.</summary>
    [Fact]
    public void RemovedPresetId_InSettings_LoadsVerbatim_AndDoesNotRewriteTheFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "schemaVersion": 3, "appearance": { "skins": { "rainformer": { "preset": "nord" } } } }""");
        byte[] before = File.ReadAllBytes(SettingsPath);

        using var store = new ConfigStore(_dir, watch: false);
        store.Reload();

        Assert.Equal("nord", Rainformer(store.Settings.Appearance).Preset);
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void RemovedPresetId_InWidgets_LoadsVerbatim_AndDoesNotRewriteTheFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(WidgetsPath,
            """{ "schemaVersion": 3, "widgets": [ { "id": "cpu", "type": "cpuram", "appearance": { "skins": { "rainformer": { "preset": "nord" } } } } ] }""");
        byte[] before = File.ReadAllBytes(WidgetsPath);

        using var store = new ConfigStore(_dir, watch: false);
        store.Reload();

        Assert.Equal("nord", store.Widgets.Widgets.Single().Appearance.Skins!["rainformer"].Preset);
        Assert.Equal(before, File.ReadAllBytes(WidgetsPath));
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
