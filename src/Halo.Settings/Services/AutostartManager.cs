using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Halo.Shared;

namespace Halo.Settings.Services;

public enum ScheduledTaskState
{
    Present,
    Missing,
    WrongPath,
}

public sealed record AutostartStatus(ScheduledTaskState Collector, ScheduledTaskState Widgets, bool HkcuRun)
{
    public bool Healthy => Collector == ScheduledTaskState.Present && Widgets == ScheduledTaskState.Present && !HkcuRun;
    public bool Enabled => Collector == ScheduledTaskState.Present && Widgets == ScheduledTaskState.Present;
    public string Summary => Healthy
        ? "Collector and Widgets tasks are healthy"
        : $"Collector: {Label(Collector)} · Widgets: {Label(Widgets)}{(HkcuRun ? " · legacy startup entry found" : "")}";

    private static string Label(ScheduledTaskState state) => state switch
    {
        ScheduledTaskState.Present => "present",
        ScheduledTaskState.WrongPath => "wrong path",
        _ => "missing",
    };
}

public static class AutostartManager
{
    private const string TaskFolderPath = @"\Halo";
    private const string CollectorTaskName = "Collector";
    private const string WidgetsTaskName = "Widgets";
    private const string LegacyRunValue = "HaloWidgets";
    private const int TaskCreateOrUpdate = 6;
    private const int TaskActionExec = 0;
    private const int TaskTriggerLogon = 9;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelLeastPrivilege = 0;
    private const int TaskRunLevelHighest = 1;

    public static AutostartStatus GetStatus()
    {
        object? service = null;
        object? folder = null;
        try
        {
            service = CreateScheduleService();
            ((dynamic)service).Connect();
            try { folder = ((dynamic)service).GetFolder(TaskFolderPath); }
            catch { return new(ScheduledTaskState.Missing, ScheduledTaskState.Missing, HasLegacyRunValue()); }
            return new(
                ReadTaskState((dynamic)folder, CollectorTaskName, ExpectedExe("Halo.Collector.exe")),
                ReadTaskState((dynamic)folder, WidgetsTaskName, ExpectedExe("Halo.Widgets.exe")),
                HasLegacyRunValue());
        }
        catch
        {
            return new(ScheduledTaskState.Missing, ScheduledTaskState.Missing, HasLegacyRunValue());
        }
        finally
        {
            ReleaseCom(folder);
            ReleaseCom(service);
        }
    }

    public static int Register(string? requestedUser)
    {
        if (!Elevation.IsElevated) return 740;
        string user = string.IsNullOrWhiteSpace(requestedUser) ? InteractiveUser() : requestedUser.Trim();
        object? service = null;
        object? root = null;
        object? folder = null;
        try
        {
            service = CreateScheduleService();
            ((dynamic)service).Connect();
            root = ((dynamic)service).GetFolder(@"\");
            try { folder = ((dynamic)service).GetFolder(TaskFolderPath); }
            catch { folder = ((dynamic)root).CreateFolder("Halo"); }

            RegisterTask((dynamic)service, (dynamic)folder, CollectorTaskName,
                ExpectedExe("Halo.Collector.exe"), user, TaskRunLevelHighest);
            RegisterTask((dynamic)service, (dynamic)folder, WidgetsTaskName,
                ExpectedExe("Halo.Widgets.exe"), user, TaskRunLevelLeastPrivilege);
            DeleteLegacyRunValue(user);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Unwrap(ex).Message);
            return 1;
        }
        finally
        {
            ReleaseCom(folder);
            ReleaseCom(root);
            ReleaseCom(service);
        }
    }

    public static int Unregister()
    {
        if (!Elevation.IsElevated) return 740;
        object? service = null;
        object? root = null;
        object? folder = null;
        try
        {
            service = CreateScheduleService();
            ((dynamic)service).Connect();
            root = ((dynamic)service).GetFolder(@"\");
            try { folder = ((dynamic)service).GetFolder(TaskFolderPath); }
            catch { return 0; }
            TryDeleteTask((dynamic)folder, CollectorTaskName);
            TryDeleteTask((dynamic)folder, WidgetsTaskName);
            ReleaseCom(folder);
            folder = null;
            try { ((dynamic)root).DeleteFolder("Halo", 0); } catch { /* Leave a non-empty folder intact. */ }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Unwrap(ex).Message);
            return 1;
        }
        finally
        {
            ReleaseCom(folder);
            ReleaseCom(root);
            ReleaseCom(service);
        }
    }

    public static async Task<int?> RunElevatedAsync(bool enable)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return 1;
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = enable ? "--register-autostart" : "--unregister-autostart",
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

    private static void RegisterTask(dynamic service, dynamic folder, string name, string exe, string user, int runLevel)
    {
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description = name == CollectorTaskName
            ? "Halo hardware metrics collector"
            : "Halo desktop widgets";
        definition.Principal.UserId = user;
        definition.Principal.LogonType = TaskLogonInteractiveToken;
        definition.Principal.RunLevel = runLevel;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.RestartCount = 3;
        definition.Settings.RestartInterval = "PT1M";

        dynamic trigger = definition.Triggers.Create(TaskTriggerLogon);
        trigger.UserId = user;
        trigger.Enabled = true;
        dynamic action = definition.Actions.Create(TaskActionExec);
        action.Path = exe;
        action.WorkingDirectory = Paths.AppRoot;
        folder.RegisterTaskDefinition(name, definition, TaskCreateOrUpdate, user, null, TaskLogonInteractiveToken, null);
        ReleaseCom(action);
        ReleaseCom(trigger);
        ReleaseCom(definition);
    }

    private static ScheduledTaskState ReadTaskState(dynamic folder, string name, string expectedPath)
    {
        object? task = null;
        object? definition = null;
        object? actions = null;
        object? action = null;
        try
        {
            task = folder.GetTask(name);
            definition = ((dynamic)task).Definition;
            actions = ((dynamic)definition).Actions;
            if (((dynamic)actions).Count < 1) return ScheduledTaskState.WrongPath;
            action = ((dynamic)actions).Item(1);
            string configured = (string?)((dynamic)action).Path ?? "";
            return SamePath(configured, expectedPath) ? ScheduledTaskState.Present : ScheduledTaskState.WrongPath;
        }
        catch { return ScheduledTaskState.Missing; }
        finally
        {
            ReleaseCom(action);
            ReleaseCom(actions);
            ReleaseCom(definition);
            ReleaseCom(task);
        }
    }

    private static object CreateScheduleService()
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Windows Task Scheduler COM API is unavailable.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not connect to Windows Task Scheduler.");
    }

    private static string ExpectedExe(string name) => Path.GetFullPath(Path.Combine(Paths.AppRoot, name));

    private static bool SamePath(string first, string second)
    {
        try { return Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool HasLegacyRunValue()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(LegacyRunValue) is not null;
        }
        catch { return false; }
    }

    private static void DeleteLegacyRunValue(string user)
    {
        try
        {
            var account = new NTAccount(user);
            var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            using RegistryKey? key = Registry.Users.OpenSubKey($@"{sid.Value}\Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
        }
        catch
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
        }
    }

    private static string InteractiveUser()
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session != uint.MaxValue && TryReadWts(session, 5, out string user) && !string.IsNullOrWhiteSpace(user))
        {
            TryReadWts(session, 7, out string domain);
            return string.IsNullOrWhiteSpace(domain) ? user : $@"{domain}\{user}";
        }
        return $@"{Environment.UserDomainName}\{Environment.UserName}";
    }

    private static bool TryReadWts(uint session, int infoClass, out string value)
    {
        value = "";
        if (!WTSQuerySessionInformationW(0, session, infoClass, out nint buffer, out _)) return false;
        try { value = Marshal.PtrToStringUni(buffer) ?? ""; return true; }
        finally { WTSFreeMemory(buffer); }
    }

    private static void TryDeleteTask(dynamic folder, string name)
    {
        try { folder.DeleteTask(name, 0); } catch { }
    }

    private static Exception Unwrap(Exception ex)
        => ex is System.Reflection.TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : ex;

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytes);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);
}
