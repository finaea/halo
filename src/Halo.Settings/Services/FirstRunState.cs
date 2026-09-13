using System.IO;
using Halo.Shared;

namespace Halo.Settings.Services;

public static class FirstRunState
{
    public static string MarkerPath => Path.Combine(Paths.DataDir, "first-run.done");
    public static bool IsPending => !File.Exists(MarkerPath);

    public static bool Complete(out string? error)
    {
        error = null;
        if (!IsPending) return true;
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            using var stream = new FileStream(MarkerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine($"completedUtc={DateTimeOffset.UtcNow:O}");
            writer.WriteLine($"version={AppVersion.Current}");
            return true;
        }
        catch (IOException) when (File.Exists(MarkerPath))
        {
            // A second Settings process completed first run at the same time.
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
