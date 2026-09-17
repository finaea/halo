using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Halo.Shared;

/// <summary>
/// The two things every Halo process must do on the way up: install crash handlers, and write down
/// enough about the machine that a stranger's log is readable without asking them questions.
/// <para>Named <c>ProcessDiagnostics</c> and not <c>Diagnostics</c> on purpose: a class called
/// <c>Diagnostics</c> in <c>Halo.Shared</c> is ambiguous with the <c>System.Diagnostics</c>
/// namespace in any file that uses both, and this codebase uses <c>System.Diagnostics</c>
/// everywhere. The short name forced a fully-qualified reference at every call site.</para>
/// </summary>
public static class ProcessDiagnostics
{
    private static readonly ComponentLog Log2 = Log.For("lifecycle");
    private static bool _installed;

    /// <summary>
    /// Optional last-gasp notification, run after the crash has been written and flushed. Opt-in
    /// per process, because the right answer differs: the widgets are a user-facing app with no
    /// other channel — a user whose overlay vanished sees no window, no tray icon and no error, and
    /// clicks the shortcut again because "nothing happened" and "crashed" look identical from
    /// outside — while the collector is a background service that must never put a modal dialog on
    /// an idle desktop, and Halo.Settings already has its own WPF handler.
    /// <para>Set it before <see cref="InstallCrashHandlers"/>. It runs inside the unhandled-exception
    /// handler with the runtime already tearing down, so it is invoked last and its own failure is
    /// swallowed: the log is already safe by then, and nothing here may cost us that.</para>
    /// </summary>
    public static Action<string>? OnFatal { get; set; }

    /// <summary>
    /// Route every escape hatch the runtime offers into the log, synchronously.
    ///
    /// <para>Both shipping exes are <c>WinExe</c>, so the runtime's default "print the unhandled
    /// exception to stderr" writes to a console that does not exist — which is why a process could
    /// die mid-startup and leave nothing behind at all.</para>
    ///
    /// <para><b>Unobserved task exceptions are a warning, not a crash.</b> On modern .NET the
    /// runtime raises the event and then swallows the exception; the process keeps running. Writing
    /// those as fatal would manufacture crash reports for failures nothing actually died of.
    /// (<c>TaskScheduler.UnobservedTaskException</c> remarks, .NET 10.)</para>
    /// </summary>
    public static void InstallCrashHandlers()
    {
        if (_installed) return;
        _installed = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // This fires and then the process dies. Nothing queued survives, so every write here
            // goes straight to the file under the same lock the pump uses.
            try
            {
                string phase = SessionLog.Phase;
                if (e.ExceptionObject is Exception ex)
                    Log.Durable(LogLevel.Error,
                        $"UNHANDLED EXCEPTION — terminating={e.IsTerminating}, phase={phase}", ex,
                        "lifecycle");
                else
                    Log.Durable(LogLevel.Error,
                        $"UNHANDLED non-Exception throw — terminating={e.IsTerminating}, "
                        + $"phase={phase}: {e.ExceptionObject}", "lifecycle");
                Log.Durable(LogLevel.Info, Log.Health, "logger");
                if (e.IsTerminating) SessionLog.SetPhase($"crashed in {phase}");
                Log.Flush(1000);

                // Only now, with the evidence on disk, tell the user. A dialog that throws or
                // blocks must not be able to cost us the log record.
                if (e.IsTerminating && OnFatal is { } notify)
                {
                    string what = e.ExceptionObject is Exception ex2
                        ? $"{ex2.GetType().Name}: {ex2.Message}"
                        : e.ExceptionObject?.ToString() ?? "unknown error";
                    try { notify($"{what}\n\nPhase: {phase}\nLog: {Log.CurrentPath}"); } catch { }
                }
            }
            catch { /* there is nowhere left to report a failure to report */ }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                // Observe it so the behaviour does not depend on the default, then report it as
                // what it is: a bug worth fixing that did not end the process.
                e.SetObserved();
                Log.For("lifecycle").Error("unobserved task exception (process continues)", e.Exception);
            }
            catch { }
        };
    }

    /// <summary>
    /// One block, once, at startup. Everything here has been needed to explain a real report and
    /// costs one line: an adapter reading "Microsoft Basic Render Driver" or an elevation reading
    /// False answers the whole question on its own.
    /// </summary>
    public static void LogEnvironment(string processName)
    {
        try
        {
            Log2.Info($"halo {AppVersion.Current} · {processName} · {RuntimeInformation.FrameworkDescription}");
            Log2.Info($"windows {Environment.OSVersion.Version} · {RuntimeInformation.OSArchitecture}"
                + $" · {Environment.ProcessorCount} logical cpus");
            Log2.Info($"identity: {Identity()} · elevated={Elevation.IsElevated} · integrity={IntegrityLevel()}");
            Log2.Info($"app root: {Paths.AppRoot}");
            Log2.Info($"data: {Paths.DataDir}{(Paths.IsPortable ? " (portable)" : "")}");
            Log2.Info($"logger: {Log.Health}");
        }
        catch (Exception ex)
        {
            Log2.Warn($"environment snapshot incomplete: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Identity()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
        catch { return "?"; }
    }

    /// <summary>Integrity level, not just "is admin". A task registered at Limited and one at
    /// Highest behave differently, and the difference has been the answer before.</summary>
    private static string IntegrityLevel()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            foreach (IdentityReference group in identity.Groups ?? [])
            {
                string sid = group.Value;
                switch (sid)
                {
                    case "S-1-16-4096": return "Low";
                    case "S-1-16-8192": return "Medium";
                    case "S-1-16-8448": return "MediumPlus";
                    case "S-1-16-12288": return "High";
                    case "S-1-16-16384": return "System";
                }
            }
            return "?";
        }
        catch { return "?"; }
    }
}
