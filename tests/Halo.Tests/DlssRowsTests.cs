using Halo.Shared.Config;
using Halo.Shared.Panels;
using Halo.Widgets;
using Halo.Widgets.Harness;
using Halo.Widgets.PanelModels;
using Halo.Widgets.Render;

namespace Halo.Tests;

/// <summary>
/// The words on the Latency panel's SR / RR / FG / FG MULT rows, decided once for every skin
/// (<see cref="DlssRows"/>). Fixtures stand in for the collector, the live Cyberpunk 2077 reading
/// of 2026-10-04 among them.
/// </summary>
public sealed class DlssRowsTests : IDisposable
{
    private readonly List<string> _temp = [];

    private PanelContext Ctx(string fixtureOrJson)
    {
        string source = fixtureOrJson;
        if (fixtureOrJson.TrimStart().StartsWith('{'))
        {
            source = Path.Combine(Path.GetTempPath(), $"halo-dlss-{Guid.NewGuid():N}.json");
            File.WriteAllText(source, fixtureOrJson);
            _temp.Add(source);
        }
        var metrics = FixtureMetrics.Load(source);
        var settings = new AppSettings();
        var widget = new WidgetInstance { Id = "t-latency", Type = "latency" };
        var ctx = new PanelContext
        {
            Metrics = metrics, Theme = Theme.Resolve(settings.Appearance, widget, 1.7),
            Settings = settings, Widget = widget, Type = PanelCatalog.Find("latency"),
        };
        metrics.Advance(1.0, 1000);
        ctx.Stale = metrics.Stale;
        return ctx;
    }

    public void Dispose()
    {
        foreach (string f in _temp) try { File.Delete(f); } catch (IOException) { }
    }

    /// <summary>A fixture on top of "gaming" (a 3D app presenting, so the panel is not idle).</summary>
    private static string Gaming(string values, string na = "", string version = "3.7.10")
        => $$"""{ "extends": "gaming", "values": { {{values}} }, "text": { "dlss.version": "{{version}}" }, "na": [ {{na}} ] }""";

    [Fact]
    public void Gaming_ActiveRowsShowPresetAndMode_AbsentFeatureIsOff()
    {
        var c = Ctx("gaming");
        Assert.Equal(new DlssRow(DlssRowKind.Active, "Preset K · Ultra Perf."), DlssRows.Feature(c, "sr"));
        Assert.Equal(new DlssRow(DlssRowKind.Off, "off"), DlssRows.Feature(c, "rr"));
        Assert.Equal(new DlssRow(DlssRowKind.Active, "Preset A · Dynamic"), DlssRows.Feature(c, "fg"));
        Assert.Equal(new DlssRow(DlssRowKind.Active, "2.0×"), DlssRows.Multiplier(c));
    }

    [Fact]
    public void LiveCyberpunk_MatchesNvidiasOwnDlssView()
    {
        // NVIDIA's overlay: SR inactive, RR Preset D Ultra Performance, FG Preset A Dynamic.
        // The override copy is not named nvngx_dlss, so there is no version.
        var c = Ctx(Gaming("""
            "dlss.sr.present": 1, "dlss.sr.active": 0,
            "dlss.rr.present": 1, "dlss.rr.active": 1, "dlss.rr.preset": 4, "dlss.rr.mode": 3,
            "dlss.fg.present": 1, "dlss.fg.active": 1, "dlss.fg.preset": 1, "dlss.fg.mode": 3,
            "fps.fg.multiplier": 1.84
            """, na: "\"dlss.sr.preset\", \"dlss.sr.mode\"", version: ""));
        Assert.Equal(new DlssRow(DlssRowKind.Loaded, "loaded"), DlssRows.Feature(c, "sr"));
        Assert.Equal(new DlssRow(DlssRowKind.Active, "Preset D · Ultra Perf."), DlssRows.Feature(c, "rr"));
        Assert.Equal(new DlssRow(DlssRowKind.Active, "Preset A · Dynamic"), DlssRows.Feature(c, "fg"));
        Assert.Equal(new DlssRow(DlssRowKind.Active, "1.8×"), DlssRows.Multiplier(c));
    }

    [Fact]
    public void NoOverride_LoadedWithTheDllVersion()
    {
        // NVAPI has no record, so active/preset/mode are N/A and only the DLL scan speaks
        var c = Ctx(Gaming("\"dlss.sr.present\": 1", na: "\"dlss.sr.active\", \"dlss.sr.preset\", \"dlss.sr.mode\"", version: "310.3.0"));
        Assert.Equal(new DlssRow(DlssRowKind.Loaded, "loaded · 310.3.0"), DlssRows.Feature(c, "sr"));
    }

    [Fact]
    public void ActiveWithoutPresetOrMode_SaysActive()
    {
        var c = Ctx(Gaming("\"dlss.sr.active\": 1", na: "\"dlss.sr.preset\", \"dlss.sr.mode\""));
        Assert.Equal(new DlssRow(DlssRowKind.Active, "active"), DlssRows.Feature(c, "sr"));
    }

    [Fact]
    public void ActiveWithPresetOnly_SaysThePreset()
    {
        var c = Ctx(Gaming("\"dlss.sr.preset\": 13", na: "\"dlss.sr.mode\""));
        Assert.Equal(new DlssRow(DlssRowKind.Active, "Preset M"), DlssRows.Feature(c, "sr"));
    }

    [Fact]
    public void PresentUnknown_IsNa_NotOff()
    {
        // a scan that could not open the game: "could not look" must not read as "not loaded"
        var c = Ctx(Gaming("", na: "\"dlss.sr.present\""));
        Assert.Equal(DlssRowKind.Na, DlssRows.Feature(c, "sr").Kind);
    }

    [Fact]
    public void NoGameFrameGenerationAtOne_ReadsOneButIsNotActive()
    {
        var c = Ctx(Gaming("\"fps.fg.multiplier\": 1.02"));
        Assert.Equal(new DlssRow(DlssRowKind.Loaded, "1.0×"), DlssRows.Multiplier(c));
    }

    [Fact]
    public void Idle_EveryRowIsTheIdleDash()
    {
        var c = Ctx("idle");
        foreach (string key in new[] { "sr", "rr", "fg" })
            Assert.Equal(new DlssRow(DlssRowKind.Idle, "—"), DlssRows.Feature(c, key));
        Assert.Equal(DlssRowKind.Idle, DlssRows.Multiplier(c).Kind);
    }

    [Theory]
    [InlineData(1, "Preset A")]
    [InlineData(11, "Preset K")]
    [InlineData(15, "Preset O")]
    [InlineData(0x00FFFFFF, "Preset Rec.")]
    [InlineData(42, "Preset #42")]
    public void PresetLetters(int v, string expected) => Assert.Equal(expected, DlssRows.PresetLabel(v));

    [Fact]
    public void PresetZero_HasNoLabel() => Assert.Null(DlssRows.PresetLabel(0));

    [Theory]
    [InlineData(0, "Perf")]
    [InlineData(1, "Balanced")]
    [InlineData(2, "Quality")]
    [InlineData(3, "Ultra Perf.")] // the runtime enum: 3 is Ultra Performance (the override SETTING's 3 is "in-game")
    [InlineData(4, "#4")]          // no NVIDIA label for 4
    [InlineData(5, "DLAA")]
    [InlineData(6, "Custom")]
    public void ModeLabels(int v, string expected) => Assert.Equal(expected, DlssRows.ModeLabel(v));

    [Theory]
    [InlineData(1, "Fixed")]
    [InlineData(3, "Dynamic")]
    public void FgModeLabels(int v, string expected) => Assert.Equal(expected, DlssRows.FgModeLabel(v));
}
