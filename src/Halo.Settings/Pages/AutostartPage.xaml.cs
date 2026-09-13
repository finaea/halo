using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Halo.Settings.Pages;

/// <summary>
/// Interim autostart page. The HKCU Run value below is on its way out: the settings overhaul
/// replaces it with two scheduled tasks driven by Halo.Settings.exe --register-autostart, which
/// the installer and the dev script call as well. Until then it at least points at the real exe
/// instead of a hardcoded Debug build path.
/// </summary>
public partial class AutostartPage : UserControl, ISettingsPage
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "HaloWidgets";
    private const string TaskName = @"\Halo\Collector";

    private bool _loading;

    // All three exes share one folder in every layout (packaging plan § Layout contract).
    private static string WidgetsExe => Path.Combine(Halo.Shared.Paths.AppRoot, "Halo.Widgets.exe");

    private static string InstallScript => Path.Combine(Halo.Shared.Paths.AppRoot, "tools", "install-halo.ps1");

    public AutostartPage()
    {
        InitializeComponent();
    }

    public void OnEnter()
    {
        _loading = true;
        WidgetsExePathText.Text = WidgetsExe;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            string? val = key?.GetValue(RunValueName) as string;
            LoginCheck.IsChecked = !string.IsNullOrEmpty(val);
            RunStatus.Text = string.IsNullOrEmpty(val) ? "Not registered for login." : $"Registered -> {val}";
        }
        catch (Exception ex) { RunStatus.Text = "Registry read failed: " + ex.Message; }
        _loading = false;

        RefreshTaskStatus();
    }

    public void OnLeave() { }

    private void LoginCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (LoginCheck.IsChecked == true)
            {
                key!.SetValue(RunValueName, $"\"{WidgetsExe}\"");
                RunStatus.Text = File.Exists(WidgetsExe)
                    ? $"Registered -> {WidgetsExe}"
                    : $"Registered (note: exe not built yet at {WidgetsExe}).";
            }
            else
            {
                key!.DeleteValue(RunValueName, throwOnMissingValue: false);
                RunStatus.Text = "Not registered for login.";
            }
        }
        catch (Exception ex) { RunStatus.Text = "Registry write failed: " + ex.Message; }
    }

    private void RefreshTaskStatus()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) { TaskStatus.Text = "Could not query schtasks."; return; }
            p.WaitForExit(3000);
            TaskStatus.Text = p.ExitCode == 0 ? "Scheduled task is installed." : "Scheduled task is not installed.";
        }
        catch (Exception ex) { TaskStatus.Text = "Query failed: " + ex.Message; }
    }

    private void InstallTask_Click(object sender, RoutedEventArgs e)
    {
        // Elevate PowerShell (equivalent to Start-Process -Verb RunAs) to run the installer script.
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{InstallScript}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            Process.Start(psi);
            TaskStatus.Text = File.Exists(InstallScript)
                ? "Launched elevated installer. Click 'Refresh status' after it completes."
                : $"Launched elevated PowerShell, but {InstallScript} was not found (installer not created yet).";
        }
        catch (Exception ex)
        {
            // Includes the case where the user cancels the UAC prompt.
            TaskStatus.Text = "Could not launch elevated installer: " + ex.Message;
        }
    }

    private void RefreshTask_Click(object sender, RoutedEventArgs e) => RefreshTaskStatus();
}
