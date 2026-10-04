using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Halo.Settings.Services;
using Halo.Settings.ViewModels;
using Halo.Shared.Config;
using Halo.Shared.Skins;

namespace Halo.Tests;

/// <summary>
/// Settings' write queue when a file refuses the write (review of round 12, fixup 12g). Every
/// scenario lives in a temp folder, never the real data folder. WPF-touching bodies run on one
/// dedicated STA thread (<see cref="Sta"/>): xUnit's threads are MTA, and only one
/// <see cref="Application"/> can exist per AppDomain.
/// </summary>
[Collection(LogTestCollection.Name)]
public sealed class SettingsWriteFailureTests
{
    private const string R = SkinCatalog.RainformerId;
    private const string A = AzurArchiveSkinInfo.Id;

    /// <summary>One long-lived STA thread that owns the one <see cref="Application"/>. It never runs
    /// a dispatcher loop: the bodies block on their tasks like the review's probe did, and a
    /// pumping dispatcher would post the view-models' awaits back onto the blocked thread.</summary>
    private static class Sta
    {
        private static readonly System.Collections.Concurrent.BlockingCollection<Action> Queue = new();
        private static readonly Lazy<Thread> Thread = new(() =>
        {
            var thread = new Thread(() =>
            {
                _ = new Application();
                foreach (Action work in Queue.GetConsumingEnumerable()) work();
            }) { IsBackground = true, Name = "settings-tests-sta" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread;
        });

        public static void Run(Action body)
        {
            _ = Thread.Value;
            Exception? failure = null;
            using var done = new ManualResetEventSlim();
            Queue.Add(() =>
            {
                try { body(); } catch (Exception ex) { failure = ex; }
                done.Set();
            });
            done.Wait();
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class Sandbox : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "halo-settings-tests-" + Guid.NewGuid().ToString("N"));
        public string SettingsPath => Path.Combine(Dir, "settings.json");
        public string WidgetsPath => Path.Combine(Dir, "widgets.json");
        public LiveConfigService? Config;

        public Sandbox() => Seed();

        /// <summary>Rainformer live at X=10; Azur's placement parked at X=900, disabled. Profiles
        /// "outgoing" (the live state) and "incoming" (Azur, lock all, a clock at X=500).</summary>
        private void Seed()
        {
            using var seed = new ConfigStore(Dir, watch: false);
            seed.Settings.ActiveProfile = "outgoing";
            seed.Settings.Appearance.Skin = R;
            seed.Widgets.Widgets.Add(new WidgetInstance
            {
                Id = "clock", Type = "clock", X = 10,
                Placements = new() { [A] = new SkinPlacement { X = 900, Enabled = false } },
            });
            seed.SaveSettings(); seed.SaveWidgets();
            var store = new ProfileStore(Path.Combine(Dir, "profiles"));
            store.Save("outgoing", Profile.FromLive("Outgoing", seed.Settings, seed.Widgets));
            var incoming = Profile.FromLive("Incoming", new AppSettings(), new WidgetsConfig
            {
                Widgets = [new WidgetInstance { Id = "clock", Type = "clock", X = 500 }],
            });
            incoming.Settings.Appearance.Skin = A;
            incoming.Settings.LockAll = true;
            store.Save("incoming", incoming);
        }

        public LiveConfigService Open() => Config = new LiveConfigService(Dir);

        public void Refuse(string path, string how)
        {
            if (how == "readonly") File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            else File.WriteAllText(path, "{unfinished");
        }

        public void Repair(string path, byte[] original)
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllBytes(path, original);
        }

        public AppSettings DiskSettings() => JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), ConfigJsonContext.Default.AppSettings)!;
        public WidgetsConfig DiskWidgets() => JsonSerializer.Deserialize(File.ReadAllText(WidgetsPath), ConfigJsonContext.Default.WidgetsConfig)!;

        public void Dispose()
        {
            foreach (string path in new[] { SettingsPath, WidgetsPath })
                try { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); } catch { }
            try { Config?.Dispose(); } catch { }
            try { Directory.Delete(Dir, recursive: true); } catch { /* temp folder; the OS gets it */ }
        }
    }

    private static T Block<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private static void Block(Task task) => task.GetAwaiter().GetResult();

    // ---- 1 / 2: profile switch ----

    [Fact]
    public void A_profile_switch_whose_settings_write_is_refused_puts_widgets_json_back()
    {
        using var box = new Sandbox();
        var config = box.Open();
        var profiles = new ProfileService(config);
        byte[] settingsBefore = File.ReadAllBytes(box.SettingsPath);
        box.Refuse(box.SettingsPath, "readonly");

        Assert.ThrowsAny<IOException>(() => Block(profiles.SelectAsync("incoming")));

        var widget = box.DiskWidgets().Widgets.Single();
        Assert.Equal(10, widget.X);                           // the outgoing layout, not the incoming X=500
        Assert.Equal(900, widget.Placements![A].X);
        Assert.Equal(settingsBefore, File.ReadAllBytes(box.SettingsPath));
        Assert.Equal("outgoing", config.Settings.ActiveProfile);
        Assert.DoesNotContain(config.DirtyPaths(ConfigFileKind.Settings), p => p.StartsWith("profile.", StringComparison.Ordinal));
        Assert.DoesNotContain(config.DirtyPaths(ConfigFileKind.Widgets), p => p.StartsWith("profile.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_profile_switch_whose_widgets_file_does_not_parse_leaves_settings_json_alone()
    {
        using var box = new Sandbox();
        var config = box.Open();
        var profiles = new ProfileService(config);
        byte[] settingsBefore = File.ReadAllBytes(box.SettingsPath);
        box.Refuse(box.WidgetsPath, "malformed");

        Assert.ThrowsAny<IOException>(() => Block(profiles.SelectAsync("incoming")));

        Assert.Equal(settingsBefore, File.ReadAllBytes(box.SettingsPath));
        Assert.Equal("outgoing", config.Settings.ActiveProfile);
        Assert.Empty(config.DirtyPaths(ConfigFileKind.Settings));
        Assert.Empty(config.DirtyPaths(ConfigFileKind.Widgets));
    }

    // ---- 3: global skin switch ----

    [Theory]
    [InlineData("malformed")]
    [InlineData("readonly")]
    public void A_global_skin_switch_whose_settings_write_is_refused_keeps_the_old_skins_placement(string how)
    {
        Sta.Run(() =>
        {
            using var box = new Sandbox();
            var config = box.Open();
            using var vm = new AppearanceViewModel(config);
            byte[] settingsBefore = File.ReadAllBytes(box.SettingsPath);
            box.Refuse(box.SettingsPath, how);

            Block(vm.SelectSkinAsync(A));

            var widget = box.DiskWidgets().Widgets.Single();
            Assert.Equal(10, widget.X);                       // placement swap undone
            Assert.True(widget.Enabled);
            Assert.Equal(900, widget.Placements![A].X);
            Assert.False(widget.Placements[A].Enabled);
            Assert.Equal(R, config.Settings.Appearance.Skin);
            Assert.Empty(config.DirtyPaths(ConfigFileKind.Settings));

            // The file is repaired and the page reopened: selecting Azur again must park Rainformer's
            // original placement, not Azur's (the retry used to overwrite it).
            box.Repair(box.SettingsPath, settingsBefore);
            vm.Refresh();
            Block(vm.SelectSkinAsync(A));

            widget = box.DiskWidgets().Widgets.Single();
            Assert.Equal(A, box.DiskSettings().Appearance.Skin);
            Assert.Equal(900, widget.X);
            Assert.False(widget.Enabled);
            Assert.Equal(10, widget.Placements![R].X);
            Assert.True(widget.Placements[R].Enabled);
        });
    }

    // ---- 4: per-widget preset switches ----

    [Fact]
    public void Two_widget_preset_switches_with_a_colour_edit_between_them_keep_each_colour_on_its_preset()
    {
        Sta.Run(() =>
        {
            using var box = new Sandbox();
            string a = SkinCatalog.Rainformer.Presets[0].Id, b = SkinCatalog.Rainformer.Presets[1].Id;
            using (var seed = new ConfigStore(box.Dir, watch: false))
            {
                var skin = seed.Widgets.Widgets[0].Appearance.SkinFor(R);
                skin.Preset = a;
                skin.Colors["bar"] = "#111111FF";
                seed.SaveWidgets();
            }
            var config = box.Open();
            using var vm = new WidgetsPageViewModel(config);
            var row = vm.Widgets[0];
            byte[] before = File.ReadAllBytes(box.WidgetsPath);
            box.Refuse(box.WidgetsPath, "malformed");

            row.PresetChoice = row.PresetChoices.Single(p => p.Value == b);
            Block(config.FlushAllAsync());
            row.ChangeAppearanceColor("bar", "#222222FF");
            Block(config.FlushAllAsync());
            row.PresetChoice = row.PresetChoices.Single(p => p.Value == a);
            Block(config.FlushAllAsync());
            box.Repair(box.WidgetsPath, before);
            Block(config.FlushAllAsync());

            var actual = box.DiskWidgets().Widgets[0].Appearance.Skins![R];
            Assert.Equal(a, actual.Preset);
            Assert.Equal("#111111FF", actual.Colors["bar"]);
            Assert.Equal("#222222FF", actual.PresetColors![b]["bar"]);
        });
    }

    // ---- the queue itself ----

    [Fact]
    public void FlushFileAsync_reports_the_write_it_made()
    {
        using var box = new Sandbox();
        var config = box.Open();
        byte[] before = File.ReadAllBytes(box.WidgetsPath);
        box.Refuse(box.WidgetsPath, "malformed");
        config.QueueWidgets("widgets.clock.x", w => w.Widgets[0].X = 33);

        Assert.False(Block(config.FlushFileAsync(ConfigFileKind.Widgets)));
        Assert.Contains("widgets.clock.x", config.DirtyPaths(ConfigFileKind.Widgets));   // still pending

        box.Repair(box.WidgetsPath, before);
        Assert.True(Block(config.FlushFileAsync(ConfigFileKind.Widgets)));
        Assert.Equal(33, box.DiskWidgets().Widgets[0].X);
        Assert.Empty(config.DirtyPaths(ConfigFileKind.Widgets));
    }

    [Fact]
    public async Task A_flush_queued_behind_one_in_flight_does_not_replay_its_mutations()
    {
        using var box = new Sandbox();
        var config = box.Open();
        var io = (SemaphoreSlim)typeof(LiveConfigService).GetField("_ioGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(config)!;
        io.Wait();
        config.QueueWidgets("widgets.skinSwitch.1", w => w.SwitchGlobalSkin(R, A));
        Task first = config.FlushAllAsync();
        Task second = config.FlushAllAsync();
        io.Release();
        await Task.WhenAll(first, second);

        // Run twice, the switch would park Azur's placement under Rainformer and lose X=10.
        var widget = box.DiskWidgets().Widgets.Single();
        Assert.Equal(900, widget.X);
        Assert.Equal(10, widget.Placements![R].X);
        Assert.True(widget.Placements[R].Enabled);
    }
}
