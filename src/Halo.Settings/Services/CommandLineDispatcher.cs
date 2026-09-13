using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Halo.Shared;

namespace Halo.Settings.Services;

public static class CommandLineDispatcher
{
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
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    collector = State(status.Collector),
                    widgets = State(status.Widgets),
                    hkcuRun = status.HkcuRun,
                }));
                return true;
            case "--register-autostart":
                string? user = null;
                if (args.Count == 3 && args[1].Equals("--user", StringComparison.OrdinalIgnoreCase)) user = args[2];
                else if (args.Count != 1)
                {
                    Console.Error.WriteLine("Usage: Halo.Settings.exe --register-autostart [--user <DOMAIN\\user>]");
                    exitCode = 1;
                    return true;
                }
                exitCode = AutostartManager.Register(user);
                if (exitCode == 740) Console.Error.WriteLine("Administrator rights are required.");
                return true;
            case "--unregister-autostart":
                bool all = args.Count == 2 && args[1].Equals("--all", StringComparison.OrdinalIgnoreCase);
                if (args.Count != 1 && !all)
                {
                    Console.Error.WriteLine("Usage: Halo.Settings.exe --unregister-autostart [--all]");
                    exitCode = 1;
                    return true;
                }
                exitCode = AutostartManager.Unregister(all);
                if (exitCode == 740) Console.Error.WriteLine("Administrator rights are required.");
                return true;
            case "--install-pawnio":
                if (args.Count != 1) return Usage(args[0], out exitCode);
                exitCode = InstallPawnIo();
                return true;
            default:
                Console.Error.WriteLine($"Unknown Halo.Settings option: {args[0]}");
                exitCode = 1;
                return true;
        }
    }

    private static bool Usage(string verb, out int exitCode)
    {
        Console.Error.WriteLine($"{verb} does not accept additional arguments.");
        exitCode = 1;
        return true;
    }

    private static int InstallPawnIo()
    {
        if (!Elevation.IsElevated)
        {
            Console.Error.WriteLine("Administrator rights are required.");
            return 740;
        }
        string installer = Path.Combine(Paths.RedistDir, "PawnIO_setup.exe");
        if (!File.Exists(installer))
        {
            Console.Error.WriteLine($"Installer payload not found: {installer}");
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
                return 1;
            }
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string State(ScheduledTaskState state) => state switch
    {
        ScheduledTaskState.Present => "present",
        ScheduledTaskState.WrongPath => "wrong-path",
        _ => "missing",
    };

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
