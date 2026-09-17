using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Halo.Settings.Services;
using Halo.Shared;

namespace Halo.Settings;

public partial class App : Application
{
    private int _diagnosticsClosed;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Diagnostics come up FIRST — before the CLI verbs are dispatched — because the verbs are
        // the whole reason this block exists. --register-autostart and --install-pawnio are the
        // steps a failed install fails in, installer\halo.iss runs them hidden with nobody
        // watching, and until now this process, the one that owns every setup action, wrote
        // nothing anywhere: the Log.Error in the handler below was silently discarded because
        // nothing had ever called Log.Init.
        //
        // One process name, "settings", for both the GUI and the verbs. The per-instance log
        // filename already keeps the Settings window, its elevated child and the installer's copy
        // of the same verb in separate files, and the component column says which is which inside
        // one file — so a second process name would buy nothing and split the evidence.
        string[] args = ElevatedVerb.Strip(e.Args, out string? logsDir);
        Diagnostics.InstallCrashHandlers();
        if (logsDir is { Length: > 0 }) Log.Init(logsDir, "settings");
        else Log.Init("settings");
        SessionLog.Begin("settings");
        Diagnostics.LogEnvironment("settings");
        if (logsDir is { Length: > 0 })
            Log.For("lifecycle").Info($"logs directory handed over by the launching process: {logsDir}");
        InstallState.LogIfPresent();

        if (CommandLineDispatcher.TryDispatch(args, out int exitCode))
        {
            // Close the log here rather than leaving it to OnExit. Shutdown() only posts a request
            // to the dispatcher, and whoever launched this verb starts reading our file the instant
            // we exit — a line still sitting in the pump's buffer would never reach them.
            CloseDiagnostics($"verb {(args.Length > 0 ? args[0] : "none")} exit {exitCode}");
            Environment.ExitCode = exitCode;
            Shutdown(exitCode);
            return;
        }

        // This machine is dual-GPU; WPF's HW path has been observed to compose a blank
        // (white) window here. A settings window doesn't need GPU rendering — force software.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                // ComponentLog.Error is synchronous (Log.Durable), so the reason is on disk before
                // the message box blocks the UI thread waiting for a click.
                Log.For("ui").Error("unhandled exception on the dispatcher", args.Exception);
                Console.Error.WriteLine(args.Exception);
                MessageBox.Show(args.Exception.ToString(), "Halo Settings error");
            }
            catch { }
            args.Handled = true;
        };

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CloseDiagnostics($"exit {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    /// <summary>Flush and close the log exactly once. The verb path closes it before asking WPF to
    /// shut down, and OnExit still runs afterwards; a second Shutdown would reopen the file just to
    /// write a duplicate farewell line.</summary>
    private void CloseDiagnostics(string reason)
    {
        if (Interlocked.Exchange(ref _diagnosticsClosed, 1) != 0) return;
        SessionLog.End(reason);
        Log.Shutdown(reason);
    }
}
