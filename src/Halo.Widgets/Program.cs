using Halo.Shared;
using Halo.Widgets;

using var singleInstance = new Mutex(true, "Local\\Halo.Widgets.SingleInstance.v2", out bool isNew);
if (!isNew) return 1;

Log.Init("widgets");
Log.Info($"halo {AppVersion.Current} · app root: {Paths.AppRoot}");
Log.Info($"data: {Paths.DataDir}{(Paths.IsPortable ? " (portable)" : "")}");

try
{
    using var app = new App();
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
