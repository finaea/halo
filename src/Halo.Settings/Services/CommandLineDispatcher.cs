using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Halo.Shared;

namespace Halo.Settings.Services;

/// <summary>
/// The setup verbs. Every one of them runs hidden and unattended at some point —
/// <c>installer\halo.iss</c> calls three of them with <c>SW_HIDE</c>, and the System check page
/// calls two through UAC — so each logs its intent, its inputs, its outcome and its exit code with
/// the meaning of that code spelled out. The <c>Console.Error</c> writes stay: they serve a person
/// running the verb from a prompt, and the log serves everybody else.
/// </summary>
public static class CommandLineDispatcher
{
    /// <summary>183, ERROR_ALREADY_EXISTS: PawnIO_setup.exe refuses to install over an existing
    /// copy, whatever its version (halo.iss, measured 2026-09-13).</summary>
    public const int PawnIoAlreadyExists = 183;

    /// <summary>3010, ERROR_SUCCESS_REBOOT_REQUIRED: installed, but the driver will not load until
    /// Windows restarts.</summary>
    public const int PawnIoRebootRequired = 3010;

    /// <summary>740, ERROR_ELEVATION_REQUIRED.</summary>
    public const int NeedsElevation = 740;

    public static bool TryDispatch(IReadOnlyList<string> args, out int exitCode)
    {
        exitCode = 0;
        if (args.Count == 0 || !args[0].StartsWith("--", StringComparison.Ordinal)) return false;
        AttachToParentConsole();
        switch (args[0].ToLowerInvariant())
        {
            case "--autostart-status":
                if (args.Count != 1) return Usage(args[0], out exitCode);
                AutostartStatus status = AutostartManager.GetStatus();
                string json = JsonSerializer.Serialize(new
                {
                    collector = State(status.Collector),
                    widgets = State(status.Widgets),
                    hkcuRun = status.HkcuRun,
                });
                Console.Out.WriteLine(json);
                // What we told the caller, in the log too. A status printed to a pipe nobody kept
                // is the same as no status at all.
                Log.For("autostart-status").Info($"reported {status.Trace} · json={json} · exit 0");
                return true;
            case "--register-autostart":
                string? user = null;
                if (args.Count == 3 && args[1].Equals("--user", StringComparison.OrdinalIgnoreCase)) user = args[2];
                else if (args.Count != 1)
                {
                    Console.Error.WriteLine("Usage: Halo.Settings.exe --register-autostart [--user <DOMAIN\\user>]");
                    Log.For(AutostartManager.RegisterComponent)
                        .Error($"bad arguments ({string.Join(' ', args)}) — exit 1");
                    exitCode = 1;
                    return true;
                }
                exitCode = AutostartManager.Register(user);
                if (exitCode == NeedsElevation) Console.Error.WriteLine("Administrator rights are required.");
                return true;
            case "--unregister-autostart":
                bool all = args.Count == 2 && args[1].Equals("--all", StringComparison.OrdinalIgnoreCase);
                if (args.Count != 1 && !all)
                {
                    Console.Error.WriteLine("Usage: Halo.Settings.exe --unregister-autostart [--all]");
                    Log.For(AutostartManager.UnregisterComponent)
                        .Error($"bad arguments ({string.Join(' ', args)}) — exit 1");
                    exitCode = 1;
                    return true;
                }
                exitCode = AutostartManager.Unregister(all);
                if (exitCode == NeedsElevation) Console.Error.WriteLine("Administrator rights are required.");
                return true;
            case "--install-pawnio":
                if (args.Count != 1) return Usage(args[0], out exitCode);
                exitCode = InstallPawnIo();
                return true;
            default:
                Console.Error.WriteLine($"Unknown Halo.Settings option: {args[0]}");
                Log.For("cli").Error($"unknown option {args[0]} — exit 1");
                exitCode = 1;
                return true;
        }
    }

    private static bool Usage(string verb, out int exitCode)
    {
        Console.Error.WriteLine($"{verb} does not accept additional arguments.");
        Log.For("cli").Error($"{verb} was given extra arguments — exit 1");
        exitCode = 1;
        return true;
    }

    private static int InstallPawnIo()
    {
        ComponentLog log = Log.For("install-pawnio");
        string installer = Path.Combine(Paths.RedistDir, "PawnIO_setup.exe");
        log.Info($"install requested: elevated={Elevation.IsElevated} payload={installer}"
            + $" exists={File.Exists(installer)}");

        if (!Elevation.IsElevated)
        {
            Console.Error.WriteLine("Administrator rights are required.");
            log.Error($"not running elevated — exit {NeedsElevation} (ERROR_ELEVATION_REQUIRED)");
            return NeedsElevation;
        }
        if (!File.Exists(installer))
        {
            Console.Error.WriteLine($"Installer payload not found: {installer}");
            log.Error($"payload missing at {installer} — exit 1. build.ps1 fetches it into "
                + "installer\\redist against a pinned SHA-256; a build without it ships no PawnIO.");
            return 1;
        }
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "-install -silent",
                UseShellExecute = false,
                WorkingDirectory = Paths.RedistDir,
            });
            if (process is null)
            {
                Console.Error.WriteLine($"Could not start {installer}");
                log.Error($"Process.Start returned no process for {installer} — exit 1");
                return 1;
            }
            process.WaitForExit();
            int code = process.ExitCode;
            // The exit code with its meaning, because the caller that most needs it — halo.iss,
            // running this hidden — keeps nothing else, and 183 and 3010 are both successes.
            string meaning = code switch
            {
                0 => "installed",
                PawnIoAlreadyExists => "183 ERROR_ALREADY_EXISTS: a PawnIO is already installed and "
                    + "its setup refuses to install over one. Nothing changed; the existing driver keeps working",
                PawnIoRebootRequired => "3010 ERROR_SUCCESS_REBOOT_REQUIRED: installed, but the driver "
                    + "does not load until Windows restarts, so CPU/fan/drive temperatures read N/A until then",
                _ => "the PawnIO installer failed; CPU temperatures, fan speeds and drive temperatures will read N/A",
            };
            if (code is 0 or PawnIoAlreadyExists or PawnIoRebootRequired) log.Info($"exit {code} — {meaning}");
            else log.Error($"exit {code} — {meaning}");
            return code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            log.Error($"could not run {installer} — exit 1", ex);
            return 1;
        }
    }

    private static string State(ScheduledTaskState state) => state switch
    {
        ScheduledTaskState.Present => "present",
        ScheduledTaskState.WrongPath => "wrong-path",
        _ => "missing",
    };

    /// <summary>
    /// Adopt the parent's console so the Console.Error writes above land somewhere when a person
    /// runs a verb from a prompt. Skipped when output is already redirected — which is exactly the
    /// case when <c>halo.iss</c> uses Inno's ExecAndCaptureOutput, and skipping keeps the pipe
    /// intact so the installer really does capture what we say.
    /// </summary>
    private static void AttachToParentConsole()
    {
        if (Console.IsOutputRedirected) return;
        if (!AttachConsole(uint.MaxValue)) return;
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);
}
