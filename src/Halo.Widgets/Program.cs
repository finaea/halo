using Halo.Shared;
using Halo.Widgets;

using var singleInstance = new Mutex(true, "Local\\Halo.Widgets.SingleInstance", out bool isNew);
if (!isNew) return 1;

string projectRoot = FindProjectRoot(AppContext.BaseDirectory);
Log.Init(Path.Combine(projectRoot, "logs"), "widgets");
Log.Info($"project root: {projectRoot}");

try
{
    using var app = new App(projectRoot);
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

static string FindProjectRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Halo.sln"))) return dir.FullName;
        dir = dir.Parent;
    }
    return new DirectoryInfo(start).FullName;
}
