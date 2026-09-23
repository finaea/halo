using Halo.Shared;
using Halo.Widgets;

// --start-collector: what the "Halo" shortcut runs. Bring the overlay up, then ask for the
// elevation the collector needs (CollectorLauncher). Plain Halo.Widgets.exe stays what it was —
// the overlay on its own, against whatever collector happens to be running.
bool startCollector = args.Contains("--start-collector", StringComparer.OrdinalIgnoreCase);

// Before anything that can throw. All three shipping exes are WinExe, so the runtime's default
// "print the unhandled exception to stderr" writes to a console that does not exist.
//
// The widgets opt in to the last-gasp dialog. The try/catch below only covers what unwinds through
// it; a throw on one of the background threads reaches AppDomain.UnhandledException instead, and
// without this the user would still be left with a process that vanished and no window to explain
// it. The collector deliberately does NOT opt in — it is a background service and must not put a
// modal dialog on an idle desktop.
ProcessDiagnostics.OnFatal = detail => ShowFatalDialog("Halo's widgets stopped unexpectedly.", detail);
ProcessDiagnostics.InstallCrashHandlers();

using var singleInstance = new Mutex(true, "Local\\Halo.Widgets.SingleInstance.v2", out bool isNew);
if (!isNew)
{
    // The shortcut was clicked while the overlay is already up. The widgets are a single instance,
    // but the collector may have died since — and clicking the shortcut again is exactly how a
    // user says "start Halo properly", so honour that half before bowing out.
    //
    // This logs unconditionally now. A duplicate launch used to exit 1 in complete silence, which
    // from the outside is the same event as a crash: the 2026-09-17 report was three shortcut
    // clicks (10:46:08, 10:49:55, 10:50:08) and "nothing happened" each time. Per-instance log
    // files mean one file per click, so the record of the click is the record of the rejection.
    Log.Init("widgets");
    SessionLog.Begin("widgets");
    string dupReason = startCollector
        ? "duplicate instance, handed off --start-collector"
        : "duplicate instance";
    Log.Durable(LogLevel.Info,
        "another Halo.Widgets instance already owns Local\\Halo.Widgets.SingleInstance.v2 — "
        + $"this one is exiting with code 1 ({(startCollector ? "after ensuring a collector" : "nothing to do")})",
        "lifecycle");
    if (startCollector)
    {
        SessionLog.SetPhase("duplicate instance: ensuring a collector");
        CollectorLauncher.EnsureRunning();
    }
    SessionLog.End(dupReason);
    Log.Shutdown(dupReason);
    return 1;
}

// Log.Init must run before any ConfigStore is constructed — ConfigStore's constructor logs, and
// App's constructor is what builds it.
Log.Init("widgets");
SessionLog.Begin("widgets");
ProcessDiagnostics.LogEnvironment("widgets");

string reason = "message loop ended";
int exitCode = 0;
try
{
    SessionLog.SetPhase("constructing App");
    using var app = new App(startCollector);
    SessionLog.SetPhase("message loop");
    app.Run();
}
catch (Exception ex)
{
    reason = $"fatal in {SessionLog.Phase}: {ex.GetType().Name}";
    // Durable, not queued: the dialog below blocks until the user clicks, and if they kill the
    // process instead of clicking, this line has to already be on disk.
    Log.Durable(LogLevel.Error, $"fatal (phase: {SessionLog.Phase})", ex, "lifecycle");
    // Clear the hook: this path reports the failure itself, and the handler must not show a second
    // dialog for the same exception if it also surfaces as unhandled during teardown.
    ProcessDiagnostics.OnFatal = null;
    ShowFatalDialog("Halo's widgets could not start.", $"{ex.GetType().Name}: {ex.Message}");
    exitCode = 2;
}

SessionLog.End(reason);
Log.Shutdown(reason);
return exitCode;

// Turn "I clicked the shortcut and nothing happened" into a screenshot someone can act on. This
// covers every managed failure and none of the pure-native ones — an access violation inside a GPU
// driver never reaches here at all, which is why the Dx breadcrumbs exist alongside this rather
// than instead of it.
static void ShowFatalDialog(string headline, string detail)
{
    try
    {
        string where = Log.CurrentPath.Length > 0 ? Log.CurrentPath : "(no log file could be opened)";
        Native.MessageBoxW(0,
            $"{headline}\n\n"
            + $"{detail}\n\n"
            + $"Halo {AppVersion.Current} · phase: {SessionLog.Phase}\n"
            + $"Log: {where}",
            "Halo — widgets failed to start",
            Native.MB_OK | Native.MB_ICONERROR | Native.MB_SETFOREGROUND | Native.MB_TOPMOST);
    }
    catch (Exception dialogEx)
    {
        // Failing to show the dialog must never replace the failure it was reporting.
        Log.Durable(LogLevel.Warn,
            $"could not show the fatal-error dialog: {dialogEx.GetType().Name}: {dialogEx.Message}",
            "lifecycle");
    }
}
