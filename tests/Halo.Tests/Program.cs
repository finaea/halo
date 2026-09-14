using System.IO;
using Halo.Shared.Config;

namespace Halo.Tests;

/// <summary>
/// Custom entry point (<c>GenerateProgramFile=false</c> in the csproj).
///
/// <para>
/// It exists for exactly one test: <see cref="ConfigStoreCrossProcessTests"/> has to prove that two
/// <b>processes</b> cannot lose each other's config writes, and that is not something a second
/// thread can stand in for — <c>ConfigStore._writeLock</c> already serialises threads inside one
/// process, so a threaded version of that test passes against the very bug it is named after
/// (audit finding 6a). It needs a real peer process.
/// </para>
///
/// Rather than carry a second project just to get a second exe, the test re-runs this one: a test
/// assembly built by <c>Microsoft.NET.Test.Sdk</c> already produces <c>Halo.Tests.exe</c> next to
/// itself, and this Main turns a magic first argument into "be the peer writer" instead of
/// "run tests". <c>dotnet test</c> never reaches here — VSTest loads the assembly through
/// testhost.exe and discovers tests by reflection.
/// </summary>
public static class Program
{
    public const string PeerArgument = "--config-write-peer";

    public static int Main(string[] args)
        => args.Length > 0 && args[0] == PeerArgument ? RunPeer(args) : 0;

    /// <summary>
    /// <c>--config-write-peer &lt;configDir&gt; &lt;widgetId&gt; &lt;iterations&gt; settings|widget</c>
    /// <para>
    /// Hammers one widget's X through <see cref="ConfigStore"/> and reports how many writes the
    /// store could not complete. Exit code 0 means every single one landed.
    /// </para>
    /// </summary>
    private static int RunPeer(string[] args)
    {
        string configDir = args[1];
        string widgetId = args[2];
        int iterations = int.Parse(args[3]);
        // "settings" replays LiveConfigService.FlushAsync (reload the whole document, apply, write
        // it all back); "widget" replays App.PatchWidget on drag-end. The two shapes are what
        // actually race in the field, so the test runs one of each rather than two of the same.
        bool settingsStyle = args[4] == "settings";

        using var store = new ConfigStore(configDir, watch: false);
        int failures = 0;
        for (int i = 0; i < iterations; i++)
        {
            int value = i;
            try
            {
                if (settingsStyle)
                    store.Transaction(ConfigStore.WidgetsFile, () =>
                    {
                        store.Reload();
                        WidgetInstance? w = store.Widgets.Widgets.FirstOrDefault(x => x.Id == widgetId);
                        if (w == null) { failures++; return; }
                        w.X = value;
                        store.SaveWidgets();
                    });
                else if (!store.UpdateWidget(widgetId, w => w.X = value)) failures++;
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"{widgetId}[{i}]: {ex.GetType().Name} {ex.Message}");
            }
        }
        Console.Out.WriteLine(failures);
        return failures == 0 ? 0 : 1;
    }
}
