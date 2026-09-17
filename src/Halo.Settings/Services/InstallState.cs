using System.IO;
using Halo.Shared;

namespace Halo.Settings.Services;

/// <summary>
/// What the installer decided, as recorded by <c>installer\halo.iss</c> in
/// <c>&lt;data dir&gt;\install-state.json</c>.
///
/// <para><b>Why it exists.</b> <see cref="AutostartStatus"/>'s own comment says it: "nothing
/// distinguishes [a failed --register-autostart] from a deliberate decline without recording
/// installer intent". Both leave no tasks at all, System check renders both as
/// <see cref="AutostartStatus.Off"/>, and the difference is the difference between "your choice"
/// and "setup broke". This file is the record that tells them apart.</para>
///
/// <para>Read-only here, and deliberately logged rather than parsed: the one question it answers
/// today — what did setup intend, and what did it manage — is answered by having the text in the
/// same log as everything else. Anything that wants to branch on a field can parse it later.
/// It also lives in the data folder's root and <b>not</b> in <c>config\</c>, because
/// <c>LiveConfigService</c> watches that folder for <c>*.json</c> and this is not user config.</para>
/// </summary>
internal static class InstallState
{
    public const string FileName = "install-state.json";

    public static string Path => System.IO.Path.Combine(Paths.DataDir, FileName);

    /// <summary>Put the installer's record in this process's log, or say that there isn't one.</summary>
    public static void LogIfPresent()
    {
        ComponentLog log = Log.For("install-state");
        try
        {
            if (!File.Exists(Path))
            {
                // Not a fault: a portable copy, a dev build and an install from before this file
                // existed all legitimately have none.
                log.Debug($"no {FileName} in {Paths.DataDir} — this Halo was not recorded by the installer");
                return;
            }
            string text = File.ReadAllText(Path).Trim();
            log.Info($"{FileName}: {Collapse(text)}");
        }
        catch (Exception ex)
        {
            log.Warn($"could not read {Path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>One log record per line, so the whole record greps as a unit.</summary>
    private static string Collapse(string text)
    {
        string flat = text.Replace("\r", "").Replace('\n', ' ').Replace('\t', ' ');
        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ", StringComparison.Ordinal);
        return flat.Length > 1200 ? flat[..1200] + "…" : flat;
    }
}
