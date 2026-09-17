using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Halo.Shared;

/// <summary>How a previous session ended, as far as the evidence supports.</summary>
public enum SessionState
{
    /// <summary>Still marked running. Either live right now, or it died without saying so.</summary>
    Running,
    /// <summary>Reached its own shutdown path.</summary>
    Clean,
    /// <summary>Stopped on purpose by something outside it — an upgrade, a watchdog restart, a
    /// <c>schtasks /End</c>. Not a crash, and must never be reported as one.</summary>
    ExpectedTermination,
    /// <summary>Gone, with no shutdown record and no stop intent. A crash or a hard kill.</summary>
    Unclean,
    /// <summary>Cannot be decided — the process may or may not still exist and Halo could not
    /// prove either way. Recorded honestly instead of guessed.</summary>
    Unknown,
}

/// <summary>
/// One durable record per process instance, so "why is this process no longer running" has an
/// answer even when nothing managed ever ran.
///
/// <para><b>Identity is pid plus OS process start time, never pid alone.</b> Windows reuses PIDs.
/// A record naming a dead pid that some unrelated process now holds would otherwise read as
/// "still running", and — worse — a fresh instance could declare a live sibling crashed. Both
/// halves have to match before any conclusion is drawn, and when the start time cannot be read
/// the answer is <see cref="SessionState.Unknown"/>, not a guess.</para>
///
/// <para>Written to <c>logs\sessions\&lt;proc&gt;-&lt;pid&gt;-&lt;startUtc&gt;.json</c>, one file per
/// instance so concurrent or overlapping instances cannot overwrite each other. Hand-rolled JSON:
/// Halo.Shared is AOT-compatible and this is not worth a serializer context.</para>
/// </summary>
public static class SessionLog
{
    private const string Dir = "sessions";
    private static readonly ComponentLog Log2 = Log.For("lifecycle");

    private static string _file = "";
    private static string _process = "";
    private static DateTime _startedUtc;
    private static DateTime _processStartUtc;
    private static string _phase = "start";
    private static string _stopIntent = "";
    private static SessionState _state = SessionState.Running;

    public static string Phase => _phase;

    /// <summary>Open this process's record and classify every previous one. Call right after
    /// <see cref="Log.Init"/>.</summary>
    public static void Begin(string processName)
    {
        _process = processName;
        _startedUtc = DateTime.UtcNow;
        _processStartUtc = OwnProcessStartUtc();
        _state = SessionState.Running;

        string dir = Path.Combine(Paths.LogsDir, Dir);
        try
        {
            Directory.CreateDirectory(dir);
            _file = Path.Combine(dir,
                $"{processName}-{Environment.ProcessId}-{_processStartUtc:yyyyMMddHHmmss}.json");
            Save();
        }
        catch (Exception ex)
        {
            // A session record we cannot write is not worth failing over, but it IS worth saying:
            // without it, the next start cannot tell a crash from a clean exit.
            Log2.Warn($"cannot write the session record ({ex.GetType().Name}: {ex.Message}) — "
                + "a crash this session will not be reportable next start");
            _file = "";
        }

        ReportPrevious(dir);
    }

    /// <summary>Record how far startup got. The last phase written is what a hard kill or a native
    /// crash leaves behind, so this is the breadcrumb that outlives the process.</summary>
    public static void SetPhase(string phase)
    {
        _phase = phase;
        Save();
    }

    /// <summary>Declare that something is about to stop this process on purpose, so the next start
    /// classifies it <see cref="SessionState.ExpectedTermination"/> instead of inventing a crash.
    /// The caller that is about to be killed sets this — an upgrade, a watchdog restart.</summary>
    public static void SetStopIntent(string reason)
    {
        _stopIntent = reason;
        Save();
    }

    /// <summary>Mark this session as having reached its own shutdown path.</summary>
    public static void End(string reason)
    {
        _state = SessionState.Clean;
        _phase = $"exit: {reason}";
        Save();
    }

    /// <summary>
    /// Declare that <b>another</b> process is about to be stopped on purpose, so its next start
    /// does not report a crash. The killer records the intent because the victim gets no chance to:
    /// <c>schtasks /End</c> and <c>Stop-Process -Force</c> give it no notice at all.
    /// <para>The widgets watchdog ending a wedged collector is the case this exists for — three
    /// such restarts on 2026-09-17 left no trace of who asked or why.</para>
    /// </summary>
    public static void RecordExternalStop(int pid, string reason)
    {
        if (pid <= 0) return;
        try
        {
            string dir = Path.Combine(Paths.LogsDir, Dir);
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.Append('{');
            Field(sb, "reason", reason); sb.Append(',');
            Field(sb, "byProcess", _process.Length > 0 ? _process : "?"); sb.Append(',');
            sb.Append("\"byPid\":").Append(Environment.ProcessId).Append(',');
            Field(sb, "atUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            sb.Append('}');
            File.WriteAllText(Path.Combine(dir, $"{StopIntentPrefix}{pid}.json"), sb.ToString());
        }
        catch { /* best effort: a missing intent only costs us a false "unclean" */ }
    }

    private const string StopIntentPrefix = "stop-intent-";

    /// <summary>An external stop intent recorded for this pid after it started, if any.</summary>
    private static string? ExternalStopFor(string dir, string pidText, string startedUtc)
    {
        if (!int.TryParse(pidText, out int pid)) return null;
        string path = Path.Combine(dir, $"{StopIntentPrefix}{pid}.json");
        Dictionary<string, string>? rec = TryRead(path);
        if (rec is null) return null;

        // The marker has to post-date the session it claims to explain, or a pid reused later
        // would inherit an unrelated intent.
        if (DateTime.TryParse(startedUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime started)
            && DateTime.TryParse(rec.GetValueOrDefault("atUtc", ""), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime at)
            && at < started)
            return null;

        try { File.Delete(path); } catch { }
        string reason = rec.GetValueOrDefault("reason", "external stop");
        string by = rec.GetValueOrDefault("byProcess", "?");
        return $"{reason} (by {by}, pid {rec.GetValueOrDefault("byPid", "?")})";
    }

    private static DateTime OwnProcessStartUtc()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow; }
    }

    private static void Save()
    {
        if (_file.Length == 0) return;
        var sb = new StringBuilder();
        sb.Append('{');
        Field(sb, "sessionId", Log.SessionId); sb.Append(',');
        Field(sb, "process", _process); sb.Append(',');
        sb.Append("\"pid\":").Append(Environment.ProcessId).Append(',');
        Field(sb, "processStartUtc", _processStartUtc.ToString("O", CultureInfo.InvariantCulture)); sb.Append(',');
        Field(sb, "startedUtc", _startedUtc.ToString("O", CultureInfo.InvariantCulture)); sb.Append(',');
        Field(sb, "exe", Environment.ProcessPath ?? ""); sb.Append(',');
        Field(sb, "version", AppVersion.Current); sb.Append(',');
        Field(sb, "mode", Paths.IsPortable ? "portable" : "installed"); sb.Append(',');
        sb.Append("\"elevated\":").Append(Elevation.IsElevated ? "true" : "false").Append(',');
        Field(sb, "state", _state.ToString()); sb.Append(',');
        Field(sb, "phase", _phase); sb.Append(',');
        Field(sb, "stopIntent", _stopIntent); sb.Append(',');
        Field(sb, "logFile", Path.GetFileName(Log.CurrentPath));
        sb.Append('}');

        try
        {
            // Write-then-move so a kill mid-write cannot leave a truncated record that reads as
            // corrupt evidence.
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            File.Move(tmp, _file, overwrite: true);
        }
        catch { /* best effort by design */ }
    }

    private static void Field(StringBuilder sb, string name, string value)
    {
        sb.Append('"').Append(name).Append("\":\"");
        foreach (char c in value)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        sb.Append('"');
    }

    /// <summary>Classify and report previous sessions, then rewrite them so the same crash is not
    /// announced on every start for a week.</summary>
    private static void ReportPrevious(string dir)
    {
        List<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.json").ToList(); }
        catch { return; }

        foreach (string path in files)
        {
            if (string.Equals(path, _file, StringComparison.OrdinalIgnoreCase)) continue;
            Dictionary<string, string>? rec = TryRead(path);
            if (rec is null) continue;
            if (!rec.TryGetValue("state", out string? state)) continue;

            // Already classified and already reported.
            if (state is not nameof(SessionState.Running)) { PruneIfOld(path); continue; }

            string proc = rec.GetValueOrDefault("process", "?");
            string pid = rec.GetValueOrDefault("pid", "0");
            string phase = rec.GetValueOrDefault("phase", "?");
            string intent = rec.GetValueOrDefault("stopIntent", "");
            string version = rec.GetValueOrDefault("version", "?");
            string logFile = rec.GetValueOrDefault("logFile", "");
            string started = rec.GetValueOrDefault("startedUtc", "");

            Liveness live = CheckLive(pid, rec.GetValueOrDefault("processStartUtc", ""));
            if (live == Liveness.Alive)
                continue; // A live sibling. Never touch it, never call it crashed.

            // It never got to record its own intent, but whoever stopped it may have.
            if (intent.Length == 0 && ExternalStopFor(dir, pid, started) is { } external)
            {
                intent = external;
                rec["stopIntent"] = external; // keep the record and the log line telling one story
            }

            SessionState verdict = live == Liveness.Indeterminate
                ? SessionState.Unknown
                : intent.Length > 0 ? SessionState.ExpectedTermination : SessionState.Unclean;

            string age = TryAge(started);
            switch (verdict)
            {
                case SessionState.ExpectedTermination:
                    Log2.Info($"previous {proc} session (pid {pid}, halo {version}) was stopped on "
                        + $"purpose: {intent}{age}");
                    break;
                case SessionState.Unknown:
                    Log2.Warn($"previous {proc} session (pid {pid}, halo {version}) cannot be "
                        + $"classified — its process could not be queried. Last phase: {phase}"
                        + $"{LogHint(logFile)}");
                    break;
                default:
                    Log2.Warn($"previous {proc} session (pid {pid}, halo {version}) ended WITHOUT a "
                        + $"clean exit{age}. Last phase reached: {phase}{LogHint(logFile)}");
                    break;
            }

            Rewrite(path, rec, verdict);
        }
    }

    private static string LogHint(string logFile)
        => logFile.Length > 0 ? $" — see {logFile}" : "";

    private static string TryAge(string startedUtc)
    {
        if (!DateTime.TryParse(startedUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime started)) return "";
        TimeSpan lived = DateTime.UtcNow - started;
        if (lived < TimeSpan.Zero) return "";
        if (lived.TotalMinutes < 1) return $" after {lived.TotalSeconds:0.#} s";
        if (lived.TotalHours < 1) return $" after {lived.TotalMinutes:0} min";
        return $" after {(int)lived.TotalHours}h{lived.Minutes:00}m";
    }

    private enum Liveness { Gone, Alive, Indeterminate }

    /// <summary>
    /// Both pid and OS start time must match for "alive". A pid that now belongs to something else
    /// is Gone; a process Halo is not allowed to query is Indeterminate, because an elevated
    /// collector cannot always be inspected by the medium-integrity widgets — and "I could not
    /// look" is not evidence of a crash.
    /// </summary>
    private static Liveness CheckLive(string pidText, string startUtcText)
    {
        if (!int.TryParse(pidText, out int pid) || pid <= 0) return Liveness.Gone;
        Process? process = null;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return Liveness.Gone; }
        catch { return Liveness.Indeterminate; }

        try
        {
            if (process.HasExited) return Liveness.Gone;
            DateTime actual = process.StartTime.ToUniversalTime();
            if (!DateTime.TryParse(startUtcText, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime recorded))
                return Liveness.Indeterminate;
            // Two seconds of slack: the record is written just after the process starts.
            return Math.Abs((actual - recorded).TotalSeconds) <= 2 ? Liveness.Alive : Liveness.Gone;
        }
        catch
        {
            return Liveness.Indeterminate;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void Rewrite(string path, Dictionary<string, string> rec, SessionState verdict)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        bool first = true;
        foreach ((string key, string value) in rec)
        {
            if (!first) sb.Append(',');
            first = false;
            if (key == "state") Field(sb, key, verdict.ToString());
            else if (key is "pid" or "elevated") sb.Append('"').Append(key).Append("\":").Append(value);
            else Field(sb, key, value);
        }
        sb.Append('}');
        try { File.WriteAllText(path, sb.ToString()); } catch { }
    }

    private static void PruneIfOld(string path)
    {
        try
        {
            if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7)) File.Delete(path);
        }
        catch { }
    }

    /// <summary>Flat string-to-string read of our own one-level JSON. Deliberately minimal — it
    /// only ever parses what <see cref="Save"/> wrote.</summary>
    private static Dictionary<string, string>? TryRead(string path)
    {
        string text;
        try { text = File.ReadAllText(path); } catch { return null; }
        if (text.Length < 2 || text[0] != '{') return null;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = 1;
        while (i < text.Length)
        {
            while (i < text.Length && (text[i] is ' ' or ',' or '\n' or '\r' or '\t')) i++;
            if (i >= text.Length || text[i] == '}') break;
            if (text[i] != '"') return result.Count > 0 ? result : null;
            string? key = ReadString(text, ref i);
            if (key is null) return result.Count > 0 ? result : null;
            while (i < text.Length && (text[i] is ' ' or ':')) i++;
            if (i >= text.Length) break;
            if (text[i] == '"')
            {
                string? value = ReadString(text, ref i);
                if (value is null) break;
                result[key] = value;
            }
            else
            {
                int start = i;
                while (i < text.Length && text[i] is not (',' or '}')) i++;
                result[key] = text[start..i].Trim();
            }
        }
        return result;
    }

    private static string? ReadString(string text, ref int i)
    {
        if (text[i] != '"') return null;
        i++;
        var sb = new StringBuilder();
        while (i < text.Length)
        {
            char c = text[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= text.Length) return null;
            char esc = text[i++];
            sb.Append(esc switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'u' when i + 4 <= text.Length => ReadHex(text, ref i),
                _ => esc,
            });
        }
        return null;
    }

    private static char ReadHex(string text, ref int i)
    {
        char value = (char)Convert.ToInt32(text.Substring(i, 4), 16);
        i += 4;
        return value;
    }
}
