using Halo.Shared;
using Halo.Widgets;

// --start-collector: what the "Halo" shortcut runs. Bring the overlay up, then ask for the
// elevation the collector needs (CollectorLauncher). Plain Halo.Widgets.exe stays what it was —
// the overlay on its own, against whatever collector happens to be running.
bool startCollector = args.Contains("--start-collector", StringComparer.OrdinalIgnoreCase);

using var singleInstance = new Mutex(true, "Local\\Halo.Widgets.SingleInstance.v2", out bool isNew);
if (!isNew)
{
    // The shortcut was clicked while the overlay is already up. The widgets are a single instance,
    // but the collector may have died since — and clicking the shortcut again is exactly how a
    // user says "start Halo properly", so honour that half before bowing out.
    if (startCollector)
    {
        Log.Init("widgets");
        CollectorLauncher.EnsureRunning();
        Log.Flush();
    }
    return 1;
}

Log.Init("widgets");
Log.Info($"halo {AppVersion.Current} · app root: {Paths.AppRoot}");
Log.Info($"data: {Paths.DataDir}{(Paths.IsPortable ? " (portable)" : "")}");

try
{
    using var app = new App(startCollector);
    app.Run();
}
catch (Exception ex)
{
    Log.Error("fatal", ex);
    Log.Flush();
    return 2;
}
Log.Flush();
return 0;
