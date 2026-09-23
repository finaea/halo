using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Halo.Shared;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>What a <see cref="Log.Flush"/> actually achieved. Shutdown code needs to know:
/// "I asked for my last line to be persisted" and "it was" are different facts.</summary>
public readonly record struct FlushResult(bool Reached, long Persisted, long Accepted, long Dropped)
{
    public override string ToString() => Reached
        ? $"flushed ({Persisted} persisted, {Dropped} dropped)"
        : $"flush TIMED OUT at {Persisted}/{Accepted} persisted, {Dropped} dropped";
}

/// <summary>
/// Dependency-free async file logger. One file per process instance:
/// <c>logs\&lt;proc&gt;-&lt;yyyyMMdd-HHmmss&gt;-&lt;pid&gt;.log</c>.
///
/// <para><b>Why one file per instance, not one per day.</b> The old shared per-day file could not
/// work once more than one writer existed. Five collectors appended to a single
/// <c>collector-20260917.log</c> on 2026-09-17 with no way to tell their lines apart, and the
/// Settings GUI, its elevated <c>--register-autostart</c> child and the installer's copy of the
/// same verb all write at once. A held <c>FileStream</c> on a shared path rejects the second
/// writer, so the elevated child and the crash writer would fail exactly when their output is the
/// only evidence there is. Exclusive per-instance ownership removes the whole problem: no
/// interprocess locking, no rotation races, and attribution comes free from the filename.</para>
///
/// <para><b>Durability is not severity.</b> <see cref="Durable"/> takes a level, so a routine
/// startup breadcrumb can be written synchronously without being labelled an error. Queued and
/// synchronous records share one stream and one lock; a durable Warn/Error waits for the backlog first.</para>
///
/// <para><b>Debug can never starve the critical lane.</b> Under queue pressure Debug is shed
/// first; Warn and Error wait up to 250 ms for queue space, and Durable skips the queue. Losses are
/// counted and reported, because a logger that drops silently is worse than no logger.
/// </para>
/// </summary>
public static class Log
{
    /// <summary>Env var that pins the level regardless of config. The escape hatch for a
    /// settings.json that will not parse — which is exactly when verbose logging is needed.</summary>
    public const string LevelEnvVar = "HALO_LOG_LEVEL";

    private const int QueueCapacity = 8192;
    /// <summary>Above this depth, Debug records are shed to keep room for Warn/Error.</summary>
    private const int DebugShedDepth = QueueCapacity * 3 / 4;
    /// <summary>How long a <see cref="Durable"/> record waits for the queue to catch up before it
    /// forces itself out ahead of the backlog. Short: a crash handler is often the caller.</summary>
    private const int DurableDrainMs = 150;
    private const int RetentionDays = 7;
    private const long InstalledMaxFileBytes = 8L * 1024 * 1024;
    private const long PortableMaxFileBytes = 2L * 1024 * 1024;
    /// <summary>Total byte budget for the whole logs directory, oldest files swept first.</summary>
    private const long InstalledDirBudgetBytes = 160L * 1024 * 1024;
    private const long PortableDirBudgetBytes = 24L * 1024 * 1024;

    private static readonly BlockingCollection<string> Queue = new(QueueCapacity);
    private static readonly object WriteGate = new();

    private static string _path = "";
    private static string _basePath = "";
    private static long _maxFileBytes = InstalledMaxFileBytes;
    private static long _written;
    private static int _rolls;
    private static FileStream? _stream;
    private static Thread? _thread;

    private static long _accepted;
    private static long _persisted;
    private static long _droppedQueueFull;
    private static long _droppedWriteFailed;
    private static string _lastWriteError = "";
    private static bool _levelPinnedByEnv;

    public static bool AlsoConsole;

    /// <summary>Random per-process identity. PIDs are reused and a filename can be renamed; this
    /// is what ties a log line to a session record for the life of the evidence.</summary>
    public static string SessionId { get; } = Guid.NewGuid().ToString("N")[..8];

    public static LogLevel Level { get; private set; } = LogLevel.Info;

    /// <summary>
    /// True when <see cref="LevelEnvVar"/> set the level, which makes <see cref="SetLevel"/> a
    /// no-op. Public because a UI offering to change the level has to be able to say "your choice
    /// is not what is in effect" — a control that silently does nothing is worse than no control.
    /// </summary>
    public static bool LevelPinnedByEnv => _levelPinnedByEnv;

    /// <summary>The file this process is writing to, or "" when file logging is unavailable.</summary>
    public static string CurrentPath => _path;

    /// <summary>Logger health, for the startup snapshot and the crash path. A sink that can only
    /// report its own failure through itself is silent for the one failure that matters.</summary>
    public static string Health =>
        $"dest={(_path.Length > 0 ? Path.GetFileName(_path) : "<none>")} level={Level}"
        + $" accepted={Interlocked.Read(ref _accepted)} persisted={Interlocked.Read(ref _persisted)}"
        + $" queued={Queue.Count} rolls={_rolls}"
        + $" dropped={Interlocked.Read(ref _droppedQueueFull)}+{Interlocked.Read(ref _droppedWriteFailed)}"
        + (_lastWriteError.Length > 0 ? $" lastError={_lastWriteError}" : "");

    /// <summary>Start logging into the user's data folder (<see cref="Paths.LogsDir"/>).</summary>
    public static void Init(string processName, bool alsoConsole = false)
    {
        Init(Paths.LogsDir, processName, alsoConsole);
        if (Paths.DataDirFallbackReason is { } why)
            Warn($"data folder fallback: {why}");
    }

    public static void Init(string logsDir, string processName, bool alsoConsole = false)
    {
        ApplyEnvLevel();
        bool portable = Paths.IsPortable;
        _maxFileBytes = portable ? PortableMaxFileBytes : InstalledMaxFileBytes;

        try
        {
            Directory.CreateDirectory(logsDir);
        }
        catch (Exception ex)
        {
            // Never let logging take the process down: fall back to %TEMP% and say so on stdout,
            // which is the only channel left at this point.
            string fallback = Path.Combine(Path.GetTempPath(), "Halo", "logs");
            Console.Error.WriteLine($"[halo] logs dir {logsDir} unusable ({ex.Message}); using {fallback}");
            logsDir = fallback;
            try { Directory.CreateDirectory(logsDir); } catch { return; }
        }

        Sweep(logsDir, portable);

        // Per-instance name. The pid and start time live here so every line can stay short and
        // still be attributable, and so two instances never contend for one handle.
        _basePath = Path.Combine(logsDir,
            $"{processName}-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
        _path = _basePath + ".log";
        AlsoConsole = alsoConsole;

        if (!TryOpen())
        {
            Console.Error.WriteLine($"[halo] cannot open {_path} ({_lastWriteError}); file logging is off");
            _path = "";
            return;
        }

        _thread = new Thread(Pump) { IsBackground = true, Name = "halo-log" };
        _thread.Start();

        // The banner is durable: if the process dies immediately after start — the 2026-09-17
        // pid 26784 case — this is the only line that will exist.
        Durable(LogLevel.Info,
            $"=== {processName} start pid={Environment.ProcessId} session={SessionId}"
            + $" level={Level}{(_levelPinnedByEnv ? $" (pinned by {LevelEnvVar})" : "")}"
            + $" mode={(portable ? "portable" : "installed")} ===", "lifecycle");
    }

    /// <summary>Apply a level from config. The env var wins if it set one — a machine started with
    /// <c>HALO_LOG_LEVEL=debug</c> must stay verbose even after config loads.</summary>
    public static void SetLevel(LogLevel level)
    {
        if (_levelPinnedByEnv) return;
        if (Level == level) return;
        LogLevel old = Level;
        Level = level;
        Info($"log level {old} -> {level}");
    }

    /// <summary>Parse a config/env spelling. Unknown values leave the level alone.</summary>
    public static bool TryParseLevel(string? text, out LogLevel level)
    {
        level = LogLevel.Info;
        if (string.IsNullOrWhiteSpace(text)) return false;
        switch (text.Trim().ToLowerInvariant())
        {
            case "debug" or "dbg" or "verbose" or "trace": level = LogLevel.Debug; return true;
            case "info" or "inf" or "information": level = LogLevel.Info; return true;
            case "warn" or "wrn" or "warning": level = LogLevel.Warn; return true;
            case "error" or "err": level = LogLevel.Error; return true;
            default: return false;
        }
    }

    private static void ApplyEnvLevel()
    {
        string? raw = Environment.GetEnvironmentVariable(LevelEnvVar);
        if (TryParseLevel(raw, out LogLevel level))
        {
            Level = level;
            _levelPinnedByEnv = true;
        }
    }

    public static void Debug(string msg) => Write(LogLevel.Debug, msg, "-");
    public static void Info(string msg) => Write(LogLevel.Info, msg, "-");
    public static void Warn(string msg) => Write(LogLevel.Warn, msg, "-");
    public static void Error(string msg) => Write(LogLevel.Error, msg, "-");

    public static void Error(string msg, Exception ex) => Write(LogLevel.Error, Describe(msg, ex), "-");

    /// <summary>
    /// Write synchronously, before returning. For anything whose whole value is that it survives
    /// the next instruction: crash handlers, and breadcrumbs around native calls that can take the
    /// process down with no managed handler (LHM's freed code page, a GPU driver fault).
    /// <para>The level is the caller's to choose — a startup breadcrumb is <see cref="LogLevel.Debug"/>
    /// or <see cref="LogLevel.Info"/>, not an error. Durable records ignore the level filter's
    /// shedding but still respect it for output, except <see cref="LogLevel.Error"/> which is
    /// always written.</para>
    /// </summary>
    public static void Durable(LogLevel level, string msg, string component = "-")
    {
        if (level < Level && level != LogLevel.Error) return;

        // Let the backlog land first, bounded. Without this a synchronous record jumps ahead of
        // everything still queued, so a crash line lands in the file BEFORE the events that led to
        // it — measured 2026-09-17, and exactly backwards for the case this API exists to serve.
        // Bounded, because the record matters more than the order: if the pump is wedged, write
        // anyway and say what was left behind.
        //
        // Only Warn and Error wait. Debug/Info durable records are breadcrumbs, and some sit on hot
        // paths holding a lock — LhmProvider crumbs every hw.Update() at 5 Hz inside LHM's read
        // gate. A Debug session always has a backlog, so draining there would block each crumb for
        // up to DurableDrainMs while holding that gate, stall polling, and trip the freshness
        // watchdog: turning on verbose logging would break the thing being diagnosed. Breadcrumbs
        // are ordered against each other anyway (same lock), which is what "which native call did
        // we die in" actually needs; it is the crash record that must not precede its own causes.
        long pending = level >= LogLevel.Warn ? DrainBefore(DurableDrainMs) : 0;

        string line = Format(level, component, msg);
        if (AlsoConsole) Console.WriteLine(line);
        // After the path check, not before: accepting a record there is no file for would leave a
        // debt nothing can ever discharge, and Flush would time out forever waiting for it.
        if (_path.Length == 0) return;
        Interlocked.Increment(ref _accepted);
        lock (WriteGate)
        {
            // Same gate and same stream as the pump, so a synchronous record can never overtake
            // one the pump already holds.
            Discharge(WriteLocked(line + Environment.NewLine));
            if (pending > 0)
            {
                Interlocked.Increment(ref _accepted);
                string note = Format(LogLevel.Warn, "logger",
                    $"{pending} queued record(s) were still unwritten when the record above was "
                    + "forced out — they follow it out of order, or were lost with the process");
                Discharge(WriteLocked(note + Environment.NewLine));
            }
        }
    }

    /// <summary>Settle one accepted record: persisted, or explicitly counted as lost. Never
    /// silently neither — an accepted record that is neither makes <see cref="Flush"/> wait for
    /// something that is never coming.</summary>
    private static void Discharge(bool written)
    {
        if (written) Interlocked.Increment(ref _persisted);
        else Interlocked.Increment(ref _droppedWriteFailed);
    }

    /// <summary>Wait for the queue to catch up, up to <paramref name="timeoutMs"/>. Returns how
    /// many records were still outstanding when it gave up; 0 when it caught up.</summary>
    private static long DrainBefore(int timeoutMs)
    {
        long target = Interlocked.Read(ref _accepted);
        if (Outstanding(target) <= 0) return 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Outstanding(target) <= 0) return 0;
            Thread.Sleep(2);
        }
        return Math.Max(0, Outstanding(target));
    }

    /// <summary>
    /// Records accepted up to <paramref name="target"/> that the logger still owes the disk.
    ///
    /// <para><b>The accounting rule, because getting it wrong makes <see cref="Flush"/> lie again.</b>
    /// <c>_accepted</c> counts only records the logger took responsibility for. A record is
    /// discharged by being persisted, or by being counted in <c>_droppedWriteFailed</c> — the sink
    /// refused it after we had already accepted it.</para>
    ///
    /// <para><c>_droppedQueueFull</c> must NOT appear here. Those records were rejected *before*
    /// acceptance: the shed path returns without ever incrementing <c>_accepted</c>, and the
    /// queue-full path decrements it back off. Including them inflated the discharge side against a
    /// target they never entered, so one shed record let one accepted record go unwritten while
    /// <c>Flush</c> reported success. Measured by the test suite: <c>Flush</c> returned
    /// "flushed (6072 persisted, 722 dropped)" with 142 queued records still on the floor, and a
    /// heavier storm hid 6015. That is the original bug this whole change set exists to kill,
    /// reintroduced one layer down.</para>
    /// </summary>
    private static long Outstanding(long target) => target
        - Interlocked.Read(ref _persisted)
        - Interlocked.Read(ref _droppedWriteFailed);

    public static void Durable(LogLevel level, string msg, Exception ex, string component = "-")
        => Durable(level, Describe(msg, ex), component);

    /// <summary>A logger bound to one component, so the column is set once instead of at every
    /// call site. Providers pass their own <c>Name</c>.</summary>
    public static ComponentLog For(string component) => new(component);

    internal static void Write(LogLevel level, string msg, string component)
    {
        if (level < Level) return;
        string line = Format(level, component, msg);
        if (AlsoConsole) Console.WriteLine(line);
        if (_path.Length == 0) return;

        // Shedding policy: Debug gives way first so a debug storm cannot consume the queue in the
        // moment before a warning or a crash. Warn/Error wait (up to 250 ms) for space instead of vanishing.
        if (level == LogLevel.Debug && Queue.Count >= DebugShedDepth)
        {
            Interlocked.Increment(ref _droppedQueueFull);
            return;
        }

        Interlocked.Increment(ref _accepted);
        if (Queue.TryAdd(line, level >= LogLevel.Warn ? 250 : 0)) return;
        Interlocked.Increment(ref _droppedQueueFull);
        Interlocked.Decrement(ref _accepted);
    }

    private static string Describe(string msg, Exception ex)
    {
        var sb = new StringBuilder();
        sb.Append(msg).Append(": ").Append(ex.GetType().Name).Append(' ').Append(ex.Message);
        if (ex.StackTrace is { Length: > 0 } stack) sb.Append('\n').Append(stack);
        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            sb.Append("\n--- inner: ").Append(inner.GetType().Name).Append(' ').Append(inner.Message);
            if (inner.StackTrace is { Length: > 0 } innerStack) sb.Append('\n').Append(innerStack);
        }
        return sb.ToString();
    }

    /// <summary>
    /// One physical line per record, with every line carrying the envelope — a stack trace's
    /// continuation lines are marked <c>+</c> instead of being emitted bare, so they can never be
    /// mistaken for unrelated records when the file is grepped or merged with another process's.
    /// </summary>
    private static string Format(LogLevel level, string component, string msg)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
        string tag = level switch
        {
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warn => "WRN",
            _ => "ERR",
        };
        // The trailing space is load-bearing. `{component,-12}` pads but never truncates, so a
        // component longer than the column — `unregister-autostart` is 20 — ran straight into the
        // continuation marker with no separator, giving `unregister-autostart+ at Foo()`. Any
        // reader splitting on whitespace then recovers the marker as part of the component and the
        // stack frame as a record of its own. Found 2026-09-17 by the elevated-verb read-back,
        // which surfaced every frame of a trace as a separate error in the UI; those two verbs are
        // exactly the components that log exceptions. One guaranteed space, at any name length.
        string head = $"{stamp} {tag} {SessionId} {component,-12} ";
        if (msg.IndexOf('\n') < 0) return $"{head} {msg}";

        var sb = new StringBuilder();
        string[] parts = msg.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append(Environment.NewLine);
            sb.Append(head).Append(i == 0 ? " " : "+ ").Append(parts[i].TrimEnd());
        }
        return sb.ToString();
    }

    private static void Pump()
    {
        var sb = new StringBuilder();
        while (true)
        {
            string first;
            try { first = Queue.Take(); }
            catch (InvalidOperationException) { return; }

            sb.Clear();
            sb.Append(first).Append(Environment.NewLine);
            int batched = 1;
            while (sb.Length < 64 * 1024 && Queue.TryTake(out string? more, 20))
            {
                sb.Append(more).Append(Environment.NewLine);
                batched++;
            }

            lock (WriteGate)
            {
                if (WriteLocked(sb.ToString())) Interlocked.Add(ref _persisted, batched);
                else Interlocked.Add(ref _droppedWriteFailed, batched);
            }
        }
    }

    /// <summary>Append under <see cref="WriteGate"/>. Retries once: Defender has been observed
    /// holding a freshly written file just long enough to throw
    /// <c>UnauthorizedAccessException</c>, and dropping a batch for that is a silent loss.</summary>
    private static bool WriteLocked(string text)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (_stream is null && !TryOpen()) return false;
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                _stream!.Write(bytes, 0, bytes.Length);
                _stream.Flush();
                _written += bytes.Length;
                if (_written >= _maxFileBytes) Roll();
                return true;
            }
            catch (Exception ex)
            {
                _lastWriteError = $"{ex.GetType().Name}: {ex.Message}";
                CloseStream();
                if (attempt == 0) Thread.Sleep(20);
            }
        }
        return false;
    }

    private static bool TryOpen()
    {
        // Never leak the previous handle. Production opens once per process so this was latent,
        // but an orphaned handle keeps the old file locked with nothing able to close it.
        CloseStream();
        try
        {
            // FileShare.Read so the file can be tailed while Halo runs. No other writer is
            // expected — this path belongs to this process instance alone.
            _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _written = _stream.Length;
            if (_written == 0)
            {
                // A UTF-8 BOM, once, on a brand new file. Halo's messages are full of "—" and "·",
                // and without the BOM both Notepad and Windows PowerShell 5.1 decode the file as
                // ANSI and render them as mojibake — on a log whose whole job is to be readable by
                // a stranger who has been asked to open it and look.
                _stream.Write([0xEF, 0xBB, 0xBF], 0, 3);
                _stream.Flush();
                _written = 3;
            }
            return true;
        }
        catch (Exception ex)
        {
            _lastWriteError = $"{ex.GetType().Name}: {ex.Message}";
            _stream = null;
            return false;
        }
    }

    private static void CloseStream()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
    }

    /// <summary>
    /// Size cap rolls; it never terminates output. The old 64 MB cap suppressed file logging for
    /// the rest of the session, which killed the log at precisely the moment something was
    /// spamming it.
    /// </summary>
    private static void Roll()
    {
        CloseStream();
        _rolls++;
        string rolled = $"{_basePath}.{_rolls}.log";
        try
        {
            if (File.Exists(rolled)) File.Delete(rolled);
            File.Move(_path, rolled);
        }
        catch (Exception ex)
        {
            // Could not roll: keep writing to the same file rather than going silent.
            _lastWriteError = $"roll failed: {ex.GetType().Name}: {ex.Message}";
            _written = 0;
            TryOpen();
            return;
        }
        _written = 0;
        TryOpen();
    }

    /// <summary>Age sweep plus a total-bytes budget for the directory. Per-instance files mean
    /// more of them, so the directory — not any single file — is what has to stay bounded.</summary>
    private static void Sweep(string logsDir, bool portable)
    {
        try
        {
            var files = new DirectoryInfo(logsDir).GetFiles("*.log");
            DateTime cutoff = DateTime.UtcNow.AddDays(-(portable ? 3 : RetentionDays));
            long budget = portable ? PortableDirBudgetBytes : InstalledDirBudgetBytes;

            var live = new List<FileInfo>();
            foreach (FileInfo f in files)
            {
                if (f.LastWriteTimeUtc < cutoff) { try { f.Delete(); } catch { } }
                else live.Add(f);
            }

            long total = live.Sum(f => f.Length);
            if (total <= budget) return;
            foreach (FileInfo f in live.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= budget) break;
                long size = f.Length;
                try { f.Delete(); total -= size; } catch { }
            }
        }
        catch { /* retention is best-effort */ }
    }

    /// <summary>
    /// Wait until everything accepted before the call is persisted (or accounted as dropped), and
    /// say whether that happened.
    /// <para>The old implementation waited on <c>Queue.Count &gt; 0</c>, which the pump had
    /// already driven to zero by taking the record out — so it returned successfully while the
    /// line sat unwritten in the pump's buffer, and the background thread then died with the
    /// process. Every graceful shutdown lost its last lines, including the widgets' fatal
    /// handler.</para>
    /// </summary>
    public static FlushResult Flush(int timeoutMs = 2000)
    {
        long target = Interlocked.Read(ref _accepted);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            // One shared definition with DrainBefore. They disagreed once; that cost the whole
            // guarantee, so there is now exactly one place that knows what "settled" means.
            if (Outstanding(target) <= 0)
                return new(true, Interlocked.Read(ref _persisted), target, Dropped);
            if (sw.ElapsedMilliseconds >= timeoutMs)
                return new(false, Interlocked.Read(ref _persisted), target, Dropped);
            Thread.Sleep(5);
        }
    }

    private static long Dropped =>
        Interlocked.Read(ref _droppedQueueFull) + Interlocked.Read(ref _droppedWriteFailed);

    /// <summary>Flush, then report anything lost. Call on the way out: a shutdown that dropped
    /// records should say so in the file it did manage to write.</summary>
    public static void Shutdown(string reason)
    {
        // Drain the backlog BEFORE the shutdown record, not after. Info-level durable records skip
        // the drain by design (it would stall hot-path breadcrumbs holding a lock), so without this
        // the last line in the file jumps ahead of the work it is reporting the end of: measured
        // 2026-09-17, where `shutting down: verb --register-autostart exit 0` landed above the four
        // lines describing the registration, including `register finished — exit 0`. This is a
        // shutdown path, so waiting is free and being last is the whole point of the line.
        Flush();
        Durable(LogLevel.Info, $"shutting down: {reason}", "lifecycle");
        FlushResult result = Flush();
        if (!result.Reached || result.Dropped > 0)
            Durable(LogLevel.Warn, result.ToString(), "logger");
        lock (WriteGate) CloseStream();
    }

    // ---- test seam -------------------------------------------------------------------------
    // Log is process-global by design (140+ call sites, no DI). Tests need a clean slate without
    // the public surface growing a reset that production code could call by accident.
    internal static void ResetForTests(string logsDir, string processName, LogLevel level)
    {
        lock (WriteGate) CloseStream();
        while (Queue.TryTake(out _)) { }
        Interlocked.Exchange(ref _accepted, 0);
        Interlocked.Exchange(ref _persisted, 0);
        Interlocked.Exchange(ref _droppedQueueFull, 0);
        Interlocked.Exchange(ref _droppedWriteFailed, 0);
        _lastWriteError = "";
        _rolls = 0;
        _levelPinnedByEnv = false;
        Level = level;
        _maxFileBytes = InstalledMaxFileBytes;
        Directory.CreateDirectory(logsDir);
        _basePath = Path.Combine(logsDir, $"{processName}-{Environment.ProcessId}");
        _path = _basePath + ".log";
        if (File.Exists(_path)) File.Delete(_path);
        TryOpen();
        if (_thread is null)
        {
            _thread = new Thread(Pump) { IsBackground = true, Name = "halo-log" };
            _thread.Start();
        }
    }

    internal static void SetMaxFileBytesForTests(long bytes) => _maxFileBytes = bytes;
}

/// <summary>A <see cref="Log"/> bound to one component column.</summary>
public readonly struct ComponentLog(string component)
{
    public string Component { get; } = component;

    public void Debug(string msg) => Log.Write(LogLevel.Debug, msg, Component);
    public void Info(string msg) => Log.Write(LogLevel.Info, msg, Component);
    public void Warn(string msg) => Log.Write(LogLevel.Warn, msg, Component);
    public void Error(string msg) => Log.Write(LogLevel.Error, msg, Component);
    public void Error(string msg, Exception ex) => Log.Durable(LogLevel.Error, msg, ex, Component);

    /// <summary>Synchronous write at the caller's level — for breadcrumbs around calls that can
    /// end the process without warning.</summary>
    public void Durable(LogLevel level, string msg) => Log.Durable(level, msg, Component);
}
