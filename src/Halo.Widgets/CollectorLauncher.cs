using System.Diagnostics;
using Halo.Metrics;
using Halo.Shared;

namespace Halo.Widgets;

/// <summary>
/// The shortcut's launch path. Halo does not start itself: the user clicks a Start-menu or desktop
/// shortcut that runs <c>Halo.Widgets.exe --start-collector</c>, the overlay comes up, and this
/// asks for the elevation the collector needs — LibreHardwareMonitor's CPU/SuperIO/storage parts
/// and both ETW pipelines are administrator-only, and without them temps, fans, drive temps, FPS
/// and latency all read N/A.
///
/// The UAC prompt is deliberate (product decision, 2026-09-14), but it belongs here rather than in the
/// collector's manifest. <c>requireAdministrator</c> would make it unconditional — including for
/// <c>--dump</c>, <c>--migrate-config</c> and the two smoketests — and would delete the "degrades
/// gracefully unelevated" property the collector is built around.
/// </summary>
internal static class CollectorLauncher
{
    private const int ErrorCancelled = 1223;   // the user clicked No on the UAC dialog

    /// <summary>Probe the shared section ourselves, then start the collector if nothing is there.
    /// For callers with no <see cref="MetricCache"/> of their own — the second widget process a
    /// shortcut click produces, which exits on the single-instance mutex but should still be able
    /// to start a collector that has since died.</summary>
    public static void EnsureRunning()
    {
        using var session = new CollectorSession(frameBufferSize: 1);
        session.Poll();
        EnsureRunning(session.Attached, session.CollectorPid);
    }

    /// <param name="attached">Whether a <c>Local\Halo.Metrics.v2</c> section is mapped.</param>
    /// <param name="collectorPid">Collector pid from the section header (0 when not attached).</param>
    public static void EnsureRunning(bool attached, int collectorPid)
    {
        if (IsRunning(attached, collectorPid))
        {
            Log.Info($"--start-collector: collector pid {collectorPid} is already running — not prompting");
            return;
        }

        // All three exes live in the same folder in every layout (packaging plan § Layout).
        string exe = Path.Combine(Paths.AppRoot, "Halo.Collector.exe");
        if (!File.Exists(exe))
        {
            Log.Warn($"--start-collector: {exe} does not exist — the panels will show stale badges");
            return;
        }

        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,      // required for the runas verb
                Verb = "runas",
                WorkingDirectory = Paths.AppRoot,
            });
            Log.Info($"--start-collector: started {exe} elevated (pid {started?.Id ?? 0})");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // Not a failure: the user said no. The overlay stays up and reads whatever an
            // unelevated collector would have given it, which is nothing until one is started.
            Log.Warn("--start-collector: the UAC prompt was declined — no collector, so the panels "
                + "will show stale badges until Halo is started again");
        }
        catch (Exception ex)
        {
            Log.Error("--start-collector", ex);
        }
    }

    /// <summary>Is there a live collector behind the section? The header carries its pid, which is
    /// what makes "already started by the autostart task, or by an earlier shortcut click" a cheap
    /// check instead of a second UAC dialog. The pid matters on its own: a session whose collector
    /// died still reads as attached for 30 s (CollectorSession.DeadAfterSeconds), and that is
    /// exactly when someone clicks the shortcut again.</summary>
    public static bool IsRunning(bool attached, int collectorPid)
    {
        if (!attached || collectorPid <= 0) return false;
        // Enumeration, not a handle: a medium-integrity widget process cannot open the elevated
        // collector, but it can still see whether the pid exists.
        try { using var p = Process.GetProcessById(collectorPid); return true; }
        catch (ArgumentException) { return false; }
        catch { return true; }   // could not tell — assume it is there rather than prompt twice
    }
}
