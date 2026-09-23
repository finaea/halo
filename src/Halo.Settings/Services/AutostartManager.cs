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
    /// <para>
    /// Installer intent <i>is</i> recorded now — <c>installer\halo.iss</c> writes
    /// <c>install-state.json</c> and <see cref="InstallState"/> puts it in the log — but the button
    /// deliberately still does not branch on it. Being offered a working button in a state that
    /// turned out to be a deliberate decline costs nothing; hiding it from someone whose install
    /// failed is the expensive mistake.
    /// </para>
    /// </summary>
    public string ActionLabel => Off ? "Turn on autostart…  ⛨" : "Repair autostart…  ⛨";

    /// <summary>One-line form for the log, so a status read every 5 s in the UI is one grep away.</summary>
    public string Trace => $"collector={Label(Collector)} widgets={Label(Widgets)} hkcuRun={HkcuRun}"
        + $" healthy={Healthy} off={Off} needsAttention={NeedsAttention}";

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
    public const string RegisterVerb = "--register-autostart";
    public const string UnregisterVerb = "--unregister-autostart";

    /// <summary>Log components, one per verb, so a single log file separates the three callers of
    /// this class by column.</summary>
    public const string RegisterComponent = "register-autostart";
    public const string UnregisterComponent = "unregister-autostart";
    private const string StatusComponent = "autostart-status";

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
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    /// <summary>The System check page reads the status every 5 s, so a broken Task Scheduler would
    /// otherwise log the same warning twelve times a minute. Only a change is worth a line.</summary>
    private static string _lastStatusFailure = "";

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
        catch (Exception ex)
        {
            // This catch is why autostart used to be undiagnosable. A Task Scheduler that will not
            // answer at all is reported to the UI as "no tasks" — the same shape as a user who
            // declined "Start with Windows" — so without this line the status is a confident lie.
            ReportStatusFailure($"{Unwrap(ex).GetType().Name}: {Unwrap(ex).Message}");
            return new(ScheduledTaskState.Missing, ScheduledTaskState.Missing, HasLegacyRunValue());
        }
        finally
        {
            ReleaseCom(folder);
            ReleaseCom(service);
        }
    }

    private static void ReportStatusFailure(string reason)
    {
        if (reason == _lastStatusFailure) return;
        _lastStatusFailure = reason;
        Log.For(StatusComponent).Warn(
            $"could not read the \\Halo tasks ({reason}) — reporting autostart as off, which is "
            + "indistinguishable from a deliberate decline. Treat this status as unknown.");
    }

    public static int Register(string? requestedUser)
    {
        ComponentLog log = Log.For(RegisterComponent);
        string collectorExe = ExpectedExe("Halo.Collector.exe");
        string widgetsExe = ExpectedExe("Halo.Widgets.exe");

        log.Info($"register requested: user={(string.IsNullOrWhiteSpace(requestedUser) ? "<resolve from console session>" : requestedUser)}"
            + $" elevated={Elevation.IsElevated} appRoot={Paths.AppRoot}");
        log.Info($"payload: {collectorExe} exists={File.Exists(collectorExe)}");
        log.Info($"payload: {widgetsExe} exists={File.Exists(widgetsExe)}");

        if (!HasTaskPayloads)
        {
            Console.Error.WriteLine(MissingPayloadMessage);
            log.Error($"{MissingPayloadMessage} — exit 1");
            return 1;
        }
        if (!Elevation.IsElevated)
        {
            log.Error("not running elevated; Task Scheduler will not accept a RunLevel Highest task — exit 740");
            return 740;
        }

        string user;
        if (string.IsNullOrWhiteSpace(requestedUser))
        {
            (user, string source, bool fallback) = InteractiveUser();
            // The subtle one, and the reason it is logged at all: the installer omits --user on
            // purpose (halo.iss), because Inno's "username" constant under UAC is whoever's
            // credentials approved the elevation rather than whoever is at the keyboard. So the
            // console user is resolved here instead — and when autostart silently never fires at
            // logon, "which user did we register for" is the first question, and nothing used to
            // answer it.
            if (fallback)
                log.Warn($"principal fell back to {user} ({source}) — under UAC that is whoever "
                    + "approved the elevation, not necessarily the person at the keyboard, so the "
                    + "logon trigger may be registered for the wrong account");
            else
                log.Info($"principal resolved to {user} (via {source})");
        }
        else
        {
            user = requestedUser.Trim();
            log.Info($"principal taken from --user: {user}");
        }

        object? service = null;
        object? root = null;
        object? folder = null;
        try
        {
            service = CreateScheduleService();
            ((dynamic)service).Connect();
            root = ((dynamic)service).GetFolder(@"\");
            try
            {
                folder = ((dynamic)service).GetFolder(TaskFolderPath);
                log.Debug($"using the existing {TaskFolderPath} task folder");
            }
            catch
            {
                folder = ((dynamic)root).CreateFolder("Halo");
                log.Info($"created the {TaskFolderPath} task folder");
            }

            RegisterTask((dynamic)service, (dynamic)folder, CollectorTaskName,
                collectorExe, user, TaskRunLevelHighest, log);
            RegisterTask((dynamic)service, (dynamic)folder, WidgetsTaskName,
                widgetsExe, user, TaskRunLevelLeastPrivilege, log);
            log.Info(DeleteLegacyRunValue(user));
            log.Info("register finished — exit 0");
            return 0;
        }
        catch (Exception ex)
        {
            // Console.Error serves an interactive caller and the installer, which runs this hidden
            // and captures it into its setup log (halo.iss ExecHaloSettings); the log serves
            // everybody else. Both, never one.
            Console.Error.WriteLine(Unwrap(ex).Message);
            log.Error($"register failed for {user} — exit 1", Unwrap(ex));
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
        ComponentLog log = Log.For(UnregisterComponent);
        log.Info($"unregister requested: all={all} elevated={Elevation.IsElevated} appRoot={Paths.AppRoot}");
        if (!Elevation.IsElevated)
        {
            log.Error($"not running elevated; cannot delete tasks under {TaskFolderPath} — exit 740");
            return 740;
        }
        object? service = null;
        object? root = null;
        object? folder = null;
        try
        {
            service = CreateScheduleService();
            ((dynamic)service).Connect();
            root = ((dynamic)service).GetFolder(@"\");
            try { folder = ((dynamic)service).GetFolder(TaskFolderPath); }
            catch
            {
                log.Info($"there is no {TaskFolderPath} task folder — nothing to remove, exit 0");
                return 0;
            }
            RemoveTaskIfOwned((dynamic)folder, CollectorTaskName, all, log);
            RemoveTaskIfOwned((dynamic)folder, WidgetsTaskName, all, log);
            bool folderIsEmpty = IsFolderEmpty((dynamic)folder);
            ReleaseCom(folder);
            folder = null;
            if (folderIsEmpty)
            {
                ((dynamic)root).DeleteFolder("Halo", 0);
                log.Info($"deleted the now-empty {TaskFolderPath} task folder");
            }
            else
            {
                log.Info($"kept {TaskFolderPath}: it still holds tasks or folders that are not ours");
            }
            log.Info("unregister finished — exit 0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Unwrap(ex).Message);
            log.Error("unregister failed — exit 1", Unwrap(ex));
            return 1;
        }
        finally
        {
            ReleaseCom(folder);
            ReleaseCom(root);
            ReleaseCom(service);
        }
    }

    /// <summary>
    /// Run this exe's own register/unregister verb elevated. The reason comes back through the
    /// child's log file, not its stderr — see <see cref="ElevatedVerb"/> for why that is the only
    /// channel available.
    /// </summary>
    public static Task<ElevatedOutcome> RunElevatedAsync(bool enable)
        => ElevatedVerb.RunAsync(enable ? RegisterComponent : UnregisterComponent,
                                 enable ? RegisterVerb : UnregisterVerb);

    public static bool HasTaskPayloads
        => File.Exists(ExpectedExe("Halo.Collector.exe")) && File.Exists(ExpectedExe("Halo.Widgets.exe"));

    private static void RegisterTask(dynamic service, dynamic folder, string name, string exe, string user,
        int runLevel, ComponentLog log)
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
        // Logged here, next to the call, so the record cannot drift from what was actually asked for.
        log.Info($@"registered {TaskFolderPath}\{name}: runLevel="
            + $"{(runLevel == TaskRunLevelHighest ? "Highest" : "LeastPrivilege")}"
            + $" principal={user} logonTrigger={user} action={exe} workingDir={Paths.AppRoot}");
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

    /// <summary>Delete the pre-v2 HKCU Run value, and say which hive it was looked for in — the
    /// elevated verb's own HKCU is the elevating administrator's, not the user's, so the SID lookup
    /// is the path that actually matters and its failure is worth seeing.</summary>
    private static string DeleteLegacyRunValue(string user)
    {
        try
        {
            var account = new NTAccount(user);
            var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            using RegistryKey? key = Registry.Users.OpenSubKey($@"{sid.Value}\Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            bool present = key?.GetValue(LegacyRunValue) is not null;
            key?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
            return $"legacy Run value {LegacyRunValue}: "
                + (key is null ? "no Run key under" : present ? "deleted under" : "absent under")
                + $" HKU\\{sid.Value} ({user})";
        }
        catch (Exception ex)
        {
            // The fallback gets its own guard, and it matters: this method is called from inside
            // Register's try, so an exception escaping here would be caught by Register and turn a
            // registration that actually SUCCEEDED into "register failed, exit 1". A leftover Run
            // value is a tidy-up, never a reason to report the tasks as unregistered.
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                bool present = key?.GetValue(LegacyRunValue) is not null;
                key?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
                return $"legacy Run value {LegacyRunValue}: could not resolve {user} to a SID"
                    + $" ({ex.GetType().Name}: {ex.Message}); fell back to this process's own HKCU,"
                    + $" where it was {(present ? "deleted" : "absent")}";
            }
            catch (Exception fallback)
            {
                return $"legacy Run value {LegacyRunValue}: left alone — {user} would not resolve to"
                    + $" a SID ({ex.GetType().Name}: {ex.Message}) and this process's own HKCU was"
                    + $" unreadable too ({fallback.GetType().Name}: {fallback.Message})";
            }
        }
    }

    /// <summary>
    /// The account the logon trigger and the principal are set to. Resolved from the active console
    /// session rather than taken from the caller, because under UAC the caller's own identity is
    /// whoever approved the elevation.
    /// </summary>
    /// <returns>The account, how it was found, and whether that was the fallback — the fallback is
    /// exactly the wrong-account case, so the caller warns about it.</returns>
    private static (string User, string Source, bool Fallback) InteractiveUser()
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session != uint.MaxValue && TryReadWts(session, WtsUserName, out string user) && !string.IsNullOrWhiteSpace(user))
        {
            TryReadWts(session, WtsDomainName, out string domain);
            return (string.IsNullOrWhiteSpace(domain) ? user : $@"{domain}\{user}",
                $"WTSQuerySessionInformation on console session {session}", false);
        }
        return ($@"{Environment.UserDomainName}\{Environment.UserName}",
            session == uint.MaxValue
                ? "WTSGetActiveConsoleSessionId reported no attached console session"
                : $"console session {session} would not report a user name",
            true);
    }

    private static bool TryReadWts(uint session, int infoClass, out string value)
    {
        value = "";
        if (!WTSQuerySessionInformationW(0, session, infoClass, out nint buffer, out _)) return false;
        try { value = Marshal.PtrToStringUni(buffer) ?? ""; return true; }
        finally { WTSFreeMemory(buffer); }
    }

    private static void RemoveTaskIfOwned(dynamic folder, string name, bool all, ComponentLog log)
    {
        object? task = null;
        try { task = folder.GetTask(name); }
        catch
        {
            log.Info($@"{TaskFolderPath}\{name} is not registered — nothing to remove");
            return;
        }

        try
        {
            string configuredPath = ReadTaskExecutablePath((dynamic)task);
            string? normalizedPath = NormalizeTaskPath(configuredPath);
            bool owned = normalizedPath is not null && IsUnderAppRoot(normalizedPath);
            bool stale = normalizedPath is null || !File.Exists(normalizedPath);
            if (!all && !owned && !stale)
            {
                // This branch IS the safety property — an uninstall must not delete the task
                // belonging to a source build or a second install — so it has to survive into the
                // log, not just into a stderr stream the installer throws away.
                Console.Error.WriteLine($@"left {TaskFolderPath}\{name} alone: it points at {configuredPath}");
                log.Warn($@"left {TaskFolderPath}\{name} alone: its action is {configuredPath}, which is "
                    + $"outside {Paths.AppRoot} and still exists, so it belongs to another Halo. "
                    + "Pass --all to remove it anyway.");
                return;
            }

            log.Info($@"removing {TaskFolderPath}\{name}: action={configuredPath} "
                + $"reason={(all ? "--all" : owned ? "points inside this install" : "its target no longer exists")}");
            try { ((dynamic)task).Stop(0); } catch { /* The task may not be running. */ }
            folder.DeleteTask(name, 0);
            log.Info($@"removed {TaskFolderPath}\{name}");
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
