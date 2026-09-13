namespace Halo.Shared;

/// <summary>
/// The one place that knows where anything lives (packaging plan § Layout contract).
///
/// <b>App root</b> = the folder the exe sits in. Assets, fonts and the bundled PresentMon SDK are
/// read from there, so an installed copy under Program Files and a dev build under
/// <c>src\…\bin\Debug</c> resolve identically — no walking up looking for Halo.sln, no Debug-path
/// fallbacks.
///
/// <b>Data</b> (config + logs) = <c>%LOCALAPPDATA%\Halo</c>, or <c>&lt;app root&gt;\data</c> when a file
/// named <c>portable.marker</c> sits next to the exe. Program Files is not writable, and the
/// elevated collector and the medium-IL widgets run as the same user, so both resolve to the same
/// per-user folder. If that folder cannot be created or written, everything falls back to
/// <c>%TEMP%\Halo</c> and <see cref="DataDirFallbackReason"/> explains why — callers log it loudly.
/// </summary>
public static class Paths
{
    public const string PortableMarkerFile = "portable.marker";

    /// <summary>Folder containing the running exe.</summary>
    public static string AppRoot { get; } = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>True when a portable.marker file sits next to the exe (dev loop + USB-stick mode).</summary>
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppRoot, PortableMarkerFile));

    public static string AssetsDir => Path.Combine(AppRoot, "assets");
    public static string FontsDir => Path.Combine(AssetsDir, "fonts");
    public static string IconFile => Path.Combine(AssetsDir, "halo.ico");

    /// <summary>Bundled Intel PresentMon 2 SDK (PresentMonAPI2.dll + PresentMonService.exe).</summary>
    public static string PresentMonDir => Path.Combine(AppRoot, "presentmon");

    /// <summary>Third-party installers shipped alongside Halo (PawnIO_setup.exe). Populated by
    /// the build script; the Settings app's --install-pawnio verb runs what it finds here.</summary>
    public static string RedistDir => Path.Combine(AppRoot, "redist");

    private static string? _dataDir;
    private static string? _fallbackReason;

    /// <summary>Writable root for user data. Resolved once, on first use.</summary>
    public static string DataDir => _dataDir ??= ResolveDataDir();

    /// <summary>Non-null when <see cref="DataDir"/> is the %TEMP% fallback: the reason the real
    /// data folder could not be used. Log this — a user whose settings silently stopped
    /// persisting has no other way to find out.</summary>
    public static string? DataDirFallbackReason
    {
        get { _ = DataDir; return _fallbackReason; }
    }

    public static string ConfigDir => Path.Combine(DataDir, "config");
    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>Preferred data root, ignoring whether it actually works.</summary>
    public static string PreferredDataDir => IsPortable
        ? Path.Combine(AppRoot, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo");

    private static string ResolveDataDir()
    {
        string preferred = PreferredDataDir;
        if (TryPrepare(preferred, out string? why)) return preferred;

        string fallback = Path.Combine(Path.GetTempPath(), "Halo");
        _fallbackReason = $"{preferred} is not usable ({why}) — falling back to {fallback}";
        try { Directory.CreateDirectory(fallback); } catch { /* nothing left to try */ }
        return fallback;
    }

    private static bool TryPrepare(string dir, out string? error)
    {
        try
        {
            Directory.CreateDirectory(dir);
            // CreateDirectory succeeding doesn't prove we may write (ACLs, read-only media).
            string probe = Path.Combine(dir, ".writetest");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
