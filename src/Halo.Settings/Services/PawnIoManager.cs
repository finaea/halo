using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Halo.Shared;

namespace Halo.Settings.Services;

public static class PawnIoManager
{
    public const string MissingPayloadMessage = @"Installer payload not found (redist\PawnIO_setup.exe)";

    public static string InstallerPath => Path.Combine(Paths.RedistDir, "PawnIO_setup.exe");
    public static bool PayloadPresent => File.Exists(InstallerPath);

    public static bool IsInstalledFallback()
    {
        if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO")))
            return true;
        return HasUninstallEntry(RegistryView.Registry64) || HasUninstallEntry(RegistryView.Registry32);
    }

    public static async Task<int?> RunElevatedAsync()
    {
        if (!PayloadPresent) return 1;
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return 1;
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--install-pawnio",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Paths.AppRoot,
            });
            if (process is null) return 1;
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null;
        }
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
