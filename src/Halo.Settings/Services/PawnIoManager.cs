using System.IO;
using Microsoft.Win32;
using Halo.Shared;

namespace Halo.Settings.Services;

public static class PawnIoManager
{
    public const string MissingPayloadMessage = @"Installer payload not found (redist\PawnIO_setup.exe)";
    public const string InstallVerb = "--install-pawnio";
    public const string Component = "install-pawnio";

    public static string InstallerPath => Path.Combine(Paths.RedistDir, "PawnIO_setup.exe");
    public static bool PayloadPresent => File.Exists(InstallerPath);

    public static bool IsInstalledFallback()
    {
        if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO")))
            return true;
        return HasUninstallEntry(RegistryView.Registry64) || HasUninstallEntry(RegistryView.Registry32);
    }

    /// <summary>
    /// Run our own <c>--install-pawnio</c> verb elevated. Same channel as autostart: the elevated
    /// child's stderr cannot be piped back through <c>runas</c>, so the reason comes out of its log
    /// file — see <see cref="ElevatedVerb"/>.
    /// </summary>
    public static Task<ElevatedOutcome> RunElevatedAsync()
    {
        if (!PayloadPresent)
        {
            Log.For(Component).Error($"{MissingPayloadMessage} — looked at {InstallerPath}");
            return Task.FromResult(new ElevatedOutcome(1, MissingPayloadMessage));
        }
        return ElevatedVerb.RunAsync(Component, InstallVerb);
    }

    private static bool HasUninstallEntry(RegistryView view)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey? uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) return false;
            foreach (string name in uninstall.GetSubKeyNames())
            {
                using RegistryKey? item = uninstall.OpenSubKey(name);
                if ((item?.GetValue("DisplayName") as string)?.Contains("PawnIO", StringComparison.OrdinalIgnoreCase) == true)
                    return true;
            }
        }
        catch { }
        return false;
    }
}
