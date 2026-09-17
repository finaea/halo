using System.Diagnostics;
using System.IO;
using Halo.Shared;

namespace Halo.Settings.Services;

/// <param name="ExitCode">The child's exit code, or <c>null</c> when the user dismissed the UAC
/// prompt (ERROR_CANCELLED, 1223).</param>
/// <param name="Detail">What the elevated child's own log file said, ready to put in front of a
/// user. Empty when it had nothing to add.</param>
public sealed record ElevatedOutcome(int? ExitCode, string Detail);

/// <summary>
/// Running one of our own CLI verbs elevated, and getting the <i>reason</i> back — not just the
/// exit code.
///
/// <para><b>Why the log file is the only channel.</b> Elevating means <c>Verb = "runas"</c>, which
/// requires <c>UseShellExecute = true</c>; redirecting stderr requires it to be <c>false</c>
/// (ProcessStartInfo.RedirectStandardError, .NET 10 docs). The two are mutually exclusive, so the
/// child's stderr cannot be piped home. It writes a log file instead, and we read that.</para>
///
/// <para><b>Which is why the logs directory is handed over explicitly.</b> An administrator who
/// approves the consent prompt runs as the same user and lands in the same
/// <c>%LOCALAPPDATA%\Halo\logs</c>. A <i>standard</i> user gets a credential prompt and may type a
/// different administrator's account — and then the child's
/// <c>SpecialFolder.LocalApplicationData</c> is that administrator's profile, it writes its log
/// somewhere we never look, and the read-back below silently finds nothing. So the parent passes
/// its own resolved logs directory on the command line (<see cref="LogsDirOption"/>) and the child
/// honours it in <c>App.OnStartup</c>. An elevated process can write into another user's
/// <c>AppData\Local</c> because Administrators inherit full control of the profile, so the
/// hand-off is what puts the evidence somewhere the parent can read.</para>
///
/// <para>An environment variable would have been the tidier carrier and does not work:
/// <c>ProcessStartInfo.Environment</c> may not be used with <c>UseShellExecute = true</c>, and an
/// elevated child launched through ShellExecuteEx does not inherit our environment anyway — the
/// AppInfo service builds it from the target account.</para>
/// </summary>
internal static class ElevatedVerb
{
    /// <summary>The hand-off. Consumed by <c>App.OnStartup</c> before <c>Log.Init</c>, and stripped
    /// out of the arguments so the verbs keep their strict argument counts.</summary>
    public const string LogsDirOption = "--logs-dir";

    /// <summary>Log filenames are <c>settings-&lt;yyyyMMdd-HHmmss&gt;-&lt;pid&gt;.log</c>.</summary>
    private const string ProcessName = "settings";

    private const int ErrorCancelled = 1223;
    private const int MaxDetailLines = 3;
    private const int MaxDetailChars = 240;

    /// <summary>
    /// Pull <see cref="LogsDirOption"/> out of the raw arguments, returning the rest unchanged.
    /// Runs before anything else in <c>App.OnStartup</c>, because the answer decides where
    /// <c>Log.Init</c> points.
    /// </summary>
    public static string[] Strip(IReadOnlyList<string> args, out string? logsDir)
    {
        logsDir = null;
        var rest = new List<string>(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].Equals(LogsDirOption, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                logsDir = args[++i].Trim().Trim('"');
                continue;
            }
            rest.Add(args[i]);
        }
        return [.. rest];
    }

    /// <summary>
    /// Relaunch this exe elevated with <paramref name="verbArgs"/>, wait for it, and read back what
    /// it wrote. <paramref name="component"/> is the log component both sides use, so the parent's
    /// "elevating" line and the child's records line up in a merged view.
    /// </summary>
    public static async Task<ElevatedOutcome> RunAsync(string component, string verbArgs)
    {
        ComponentLog log = Log.For(component);
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            log.Error("cannot elevate: Environment.ProcessPath is empty");
            return new(1, "Halo could not locate its own executable.");
        }

        // TrimEnd so the closing quote is never preceded by a backslash, which the command-line
        // parser would read as an escape and swallow.
        string logsDir = Path.TrimEndingDirectorySeparator(Paths.LogsDir);
        string arguments = $"{verbArgs} {LogsDirOption} \"{logsDir}\"";
        // Slack in both directions: the child's filename timestamp comes from its own clock, and
        // this only has to rule out an old file left by a recycled pid.
        DateTime notBeforeUtc = DateTime.UtcNow.AddMinutes(-1);

        log.Info($"elevating: {Path.GetFileName(exe)} {arguments}");
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Paths.AppRoot,
            });
            if (process is null)
            {
                log.Error("Process.Start returned no process for the elevated child");
                return new(1, "Windows did not start the elevated helper.");
            }

            int pid = process.Id;
            await process.WaitForExitAsync();
            int exitCode = process.ExitCode;
            string detail = ReadChildLog(logsDir, pid, notBeforeUtc);
            log.Info($"elevated child pid={pid} exited {exitCode}"
                + (detail.Length > 0 ? $" — {detail}" : " — its log added nothing"));
            return new(exitCode, detail);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            log.Info("the UAC prompt was dismissed; nothing was changed");
            return new(null, "");
        }
        catch (Exception ex)
        {
            log.Error("could not launch the elevated child", ex);
            return new(1, ex.Message);
        }
    }

    /// <summary>
    /// The read-back. Finds the file the child owned, keeps its warnings and errors, and turns them
    /// into one sentence. Every path returns something naming a file, so "exit code 1" is never the
    /// whole answer a user gets.
    /// </summary>
    private static string ReadChildLog(string logsDir, int pid, DateTime notBeforeUtc)
    {
        try
        {
            // Match the pid exactly. A glob of "settings-*-137.log" would also match pid 9137,
            // because the '*' happily absorbs the leading digit.
            string suffix = $"-{pid}.log";
            FileInfo? file = new DirectoryInfo(logsDir)
                .EnumerateFiles($"{ProcessName}-*.log")
                .Where(f => f.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                         && f.LastWriteTimeUtc >= notBeforeUtc)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (file is null) return "";

            List<string> problems = [];
            foreach (string line in ReadLines(file.FullName))
            {
                if (!TryParse(line, out string level, out string message)) continue;
                if (level is not ("WRN" or "ERR")) continue;
                if (message.Length > MaxDetailChars) message = message[..MaxDetailChars] + "…";
                problems.Add(message);
            }

            if (problems.Count == 0) return $"See {file.Name} for what it did.";
            IEnumerable<string> shown = problems.Count > MaxDetailLines
                ? problems.Skip(problems.Count - MaxDetailLines)
                : problems;
            return $"{string.Join(" · ", shown)} (see {file.Name})";
        }
        catch (Exception ex)
        {
            Log.For("elevation").Warn($"could not read the elevated child's log: {ex.GetType().Name}: {ex.Message}");
            return "";
        }
    }

    /// <summary>FileShare.ReadWrite because the child may still be letting go of its handle, and a
    /// missing reason is the one outcome this whole path exists to prevent.</summary>
    private static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    /// <summary>
    /// Split one record back into its level and its message. The envelope is
    /// <c>&lt;stamp&gt; &lt;TAG&gt; &lt;session&gt; &lt;component&gt;  &lt;message&gt;</c> with the
    /// component padded, so splitting on runs of spaces recovers the four fields and leaves the
    /// message intact.
    /// <para>A wrapped stack-trace line carries the same envelope and a <c>+</c> where the message
    /// would start. Those belong in the file, not in a status line — and the <c>+</c> has to be
    /// looked for in <b>two</b> places, which is not obvious and was wrong first time round:
    /// <c>register-autostart</c> and <c>unregister-autostart</c> are longer than the 12-character
    /// component column, so nothing pads them, the marker abuts the component with no space
    /// between, and the split hands it back on the end of the component field instead of the start
    /// of the message. Those two components are exactly the ones that log exceptions, so checking
    /// only the message let every frame of a stack trace through as its own "error" — measured
    /// 2026-09-17 against the real format.</para>
    /// </summary>
    private static bool TryParse(string line, out string level, out string message)
    {
        level = "";
        message = "";
        string[] parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return false;
        level = parts[1];
        if (parts[3].EndsWith('+')) return false;
        message = parts[4].Trim();
        return message.Length > 0 && message[0] != '+';
    }
}
