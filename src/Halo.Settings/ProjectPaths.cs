using System.IO;

namespace Halo.Settings;

/// <summary>Project-root discovery: walk up from the exe dir until a folder containing Halo.sln
/// is found (same pattern as Halo.Collector/Program.cs FindProjectRoot).</summary>
public static class ProjectPaths
{
    public static string ProjectRoot { get; } = Find(AppContext.BaseDirectory);

    public static string ConfigDir => Path.Combine(ProjectRoot, "config");

    private static string Find(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Halo.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return new DirectoryInfo(start).FullName;
    }
}
