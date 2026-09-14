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

    /// <summary>
    /// Autostart is off and nothing is left behind: no tasks, no legacy Run value.
    /// <para>
    /// A supported configuration, not a fault. Halo is started by the user from its shortcut,
    /// which elevates the collector through UAC (audit finding 10). "No tasks" used to mean a Halo
    /// that could not start itself, and warning about it was fair; it is now the state every user
    /// who declined "Start with Windows" is in, and telling them their own choice needs attention
    /// is simply wrong.
    /// </para>
    /// </summary>
    public bool Off => Collector == ScheduledTaskState.Missing
                    && Widgets == ScheduledTaskState.Missing
                    && !HkcuRun;

    /// <summary>
    /// Genuinely wrong, as opposed to on (<see cref="Healthy"/>) or deliberately off
    /// (<see cref="Off"/>): half-registered, pointing at a different Halo, or a legacy HKCU Run
    /// value left behind. That last one includes the case worth naming — nothing registered and
    /// yet something still starts Halo at logon, which reads as "off" to the user and is not.
    /// </summary>
    public bool NeedsAttention => !Healthy && !Off;

    public string Summary => Healthy
        ? "Collector and Widgets tasks are healthy"
        : Off
            ? "Autostart is off — Halo starts when you run it from its shortcut"
            : $"Collector: {Label(Collector)} · Widgets: {Label(Widgets)}{(HkcuRun ? " · legacy startup entry found" : "")}";

    /// <summary>
    /// Label for the button that puts autostart right — shared by the System check page and the
    /// General page so a toggle reading "off" can never sit next to a button reading "repair".
    /// <para>
    /// The button is still offered from <see cref="Off"/> on purpose: an install whose
    /// <c>--register-autostart</c> failed lands in exactly that state, the installer tells the user
    /// to come here and fix it, and nothing distinguishes that from a deliberate decline without
    /// recording installer intent. So the affordance stays and only the verb changes.
    /// </para>
    /// </summary>
    public string ActionLabel => Off ? "Turn on autostart…  ⛨" : "Repair autostart…  ⛨";

    private static string Label(ScheduledTaskState state) => state switch
    {
        ScheduledTaskState.Present => "present",
        ScheduledTaskState.WrongPath => "wrong path",
        _ => "missing",
    };
}

public static class AutostartManager
{
    public const string MissingPayloadMessage = "Halo.Collector.exe / Halo.Widgets.exe not found next to Halo.Settings.exe — run from the installed or published folder";
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
        if (!HasTaskPayloads)
        {
            Console.Error.WriteLine(MissingPayloadMessage);
            return 1;
        }
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

    public static int Unregister(bool all = false)
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
            RemoveTaskIfOwned((dynamic)folder, CollectorTaskName, all);
            RemoveTaskIfOwned((dynamic)folder, WidgetsTaskName, all);
            bool folderIsEmpty = IsFolderEmpty((dynamic)folder);
            ReleaseCom(folder);
            folder = null;
            if (folderIsEmpty) ((dynamic)root).DeleteFolder("Halo", 0);
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

    public static bool HasTaskPayloads
        => File.Exists(ExpectedExe("Halo.Collector.exe")) && File.Exists(ExpectedExe("Halo.Widgets.exe"));

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
        definition.Settings.Priority = 5;
        // MultipleInstances is deliberately left at Task Scheduler's default, IGNORE_NEW: two
        // collectors fight over the same Local\Halo.Metrics.v2 section and the PresentMon ETW
        // session, so a second instance must never start. RestartCount covers a task that EXITS;
        // a collector wedged in native sensor or ETW code still counts as running, so
        // "schtasks /Run" against it is silently a no-op — the widgets' watchdog ends the task
        // first when the collector's pid is still alive (Halo.Widgets App.Watchdog).

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
        try
        {
            task = folder.GetTask(name);
            string configured = ReadTaskExecutablePath((dynamic)task);
            return SamePath(configured, expectedPath) ? ScheduledTaskState.Present : ScheduledTaskState.WrongPath;
        }
        catch { return ScheduledTaskState.Missing; }
        finally
        {
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
        string? normalizedFirst = NormalizeTaskPath(first);
        string? normalizedSecond = NormalizeTaskPath(second);
        return normalizedFirst is not null && normalizedSecond is not null
            && normalizedFirst.Equals(normalizedSecond, StringComparison.OrdinalIgnoreCase);
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

    private static void RemoveTaskIfOwned(dynamic folder, string name, bool all)
    {
        object? task = null;
        try { task = folder.GetTask(name); }
        catch { return; }

        try
        {
            string configuredPath = ReadTaskExecutablePath((dynamic)task);
            string? normalizedPath = NormalizeTaskPath(configuredPath);
            bool owned = normalizedPath is not null && IsUnderAppRoot(normalizedPath);
            bool stale = normalizedPath is null || !File.Exists(normalizedPath);
            if (!all && !owned && !stale)
            {
                Console.Error.WriteLine($@"left {TaskFolderPath}\{name} alone: it points at {configuredPath}");
                return;
            }

            try { ((dynamic)task).Stop(0); } catch { /* The task may not be running. */ }
            folder.DeleteTask(name, 0);
        }
        finally
        {
            ReleaseCom(task);
        }
    }

    private static string ReadTaskExecutablePath(dynamic task)
    {
        object? definition = null;
        object? actions = null;
        object? action = null;
        try
        {
            definition = task.Definition;
            actions = ((dynamic)definition).Actions;
            if (((dynamic)actions).Count < 1) return "";
            action = ((dynamic)actions).Item(1);
            return (string?)((dynamic)action).Path ?? "";
        }
        finally
        {
            ReleaseCom(action);
            ReleaseCom(actions);
            ReleaseCom(definition);
        }
    }

    private static bool IsUnderAppRoot(string normalizedPath)
    {
        string? normalizedRoot = NormalizeTaskPath(Paths.AppRoot);
        if (normalizedRoot is null) return false;
        string rootPrefix = Path.TrimEndingDirectorySeparator(normalizedRoot) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeTaskPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try { return Path.GetFullPath(expanded); }
        catch { return null; }
    }

    private static bool IsFolderEmpty(dynamic folder)
    {
        object? tasks = null;
        object? folders = null;
        try
        {
            tasks = folder.GetTasks(0);
            folders = folder.GetFolders(0);
            return ((dynamic)tasks).Count == 0 && ((dynamic)folders).Count == 0;
        }
        finally
        {
            ReleaseCom(folders);
            ReleaseCom(tasks);
        }
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
