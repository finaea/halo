using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Halo.Shared;

namespace Halo.Tests;

/// <summary>
/// One xUnit collection for every test that touches <see cref="Log"/> or <see cref="SessionLog"/>.
///
/// <para><b>Why the whole collection is serialised.</b> <c>Log</c> is process-global static state by
/// design (see <c>src\Halo.Shared\AssemblyInfo.cs</c>): one destination path, one queue, one pump
/// thread, one set of counters. Two tests running at once would reset each other's destination
/// mid-assertion and read each other's records. <c>DisableParallelization</c> also keeps this
/// collection from running beside the rest of the suite, because <c>ConfigStore</c> logs
/// (<c>ConfigStore.cs:209</c>, <c>:266</c>, <c>:296</c>) and those lines would land in whichever
/// file a log test happened to be asserting on.</para>
///
/// <para>Belt and braces: every test here also tags its own records with a per-test
/// <see cref="LogTestBase.Tag"/>, so an assertion about a particular record ignores anything the
/// logger wrote for someone else. The two whole-file rules -- no bare lines, and the envelope
/// shape -- are deliberately applied to every line in the file, because they have to hold for a
/// foreign record just as much as for this test's own.</para>
/// </summary>
[CollectionDefinition(LogTestCollection.Name, DisableParallelization = true)]
public sealed class LogTestCollection
{
    public const string Name = "Halo.Log";
}

/// <summary>
/// Per-test clean slate for the process-global logger: a fresh temp directory, zeroed counters and
/// a known level, via the <c>internal</c> <see cref="Log.ResetForTests"/> seam.
/// </summary>
public abstract class LogTestBase : IDisposable
{
    /// <summary>Where the logger is parked on the way out, so the live file's handle is released
    /// before the test's own directory is deleted. Reused, never asserted on.</summary>
    private static readonly string ParkDir =
        Path.Combine(Path.GetTempPath(), "Halo.Tests.Log", "park");

    /// <summary>
    /// Every physical line the logger emits must look like this — that is the whole point of the
    /// framing rule, so it is asserted as a rule rather than by rebuilding the expected string.
    ///
    /// <para>The layout is <c>stamp SP tag SP session SP component-padded-to-12 (2 spaces | "+ ")
    /// message</c> (<c>Log.Format</c>). The component group is <c>\S+</c> followed by optional
    /// padding, then the two-character separator: <c>cont</c> captures <c>+</c> for a continuation
    /// line and a space for a first line, which is what tells a stack frame apart from a record.</para>
    /// </summary>
    public static readonly Regex Envelope = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}(?:[+-]\d{2}:\d{2}|Z))"
        + @" (?<level>DBG|INF|WRN|ERR)"
        + @" (?<session>[0-9a-f]{8})"
        + @" (?<component>\S+) *(?<cont>[ +]) (?<message>.*)$",
        RegexOptions.Compiled);

    /// <summary>This test's own log directory. Unique per test, deleted on the way out.</summary>
    protected string Dir { get; }

    /// <summary>A token unique to this test, put in every message it logs so assertions can ignore
    /// anything the logger wrote for someone else.</summary>
    protected string Tag { get; }

    protected LogTestBase(string processName, LogLevel level = LogLevel.Info)
    {
        Dir = Path.Combine(Path.GetTempPath(), "Halo.Tests.Log", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Tag = "tag" + Guid.NewGuid().ToString("N")[..8];
        Log.ResetForTests(Dir, processName, level);
    }

    public virtual void Dispose()
    {
        // Park first: the pump holds the live file open, so the directory cannot be removed while
        // the logger still points into it.
        try
        {
            Log.SetMaxFileBytesForTests(long.MaxValue);
            QuiesceThePump();
            Log.ResetForTests(ParkDir, "parked", LogLevel.Info);
        }
        catch { /* nothing to do about it; the delete below is best effort anyway */ }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            try { Directory.Delete(Dir, recursive: true); return; }
            catch { ReleaseOrphanedStreams(); Thread.Sleep(50); }
        }
    }

    /// <summary>
    /// Wait until the pump has nothing left in flight, before <see cref="Log.ResetForTests"/> zeroes
    /// the counters underneath it.
    ///
    /// <para><b>Why this is required.</b> <c>ResetForTests</c> drains the queue and zeroes
    /// <c>_persisted</c>, but it cannot reach a batch the pump has already taken. That batch then
    /// gets written into the <i>next</i> test's file and adds its record count to the freshly
    /// zeroed <c>_persisted</c> — so the next test's <c>Flush()</c> sees
    /// <c>persisted &gt;= accepted</c> and returns <c>Reached</c> before that test's own records
    /// have been written. It cost a real false failure in <c>SessionLogTests</c> on 2026-09-17:
    /// green on its own, red after the shedding tests.</para>
    ///
    /// <para>A sentinel is used rather than <c>Log.Flush</c> or a sleep: one pump reading one FIFO
    /// queue means that once the sentinel is on disk, everything queued before it is too. Logged at
    /// Error because that is the one level no test can have filtered out.</para>
    /// </summary>
    private void QuiesceThePump()
    {
        if (Log.CurrentPath.Length == 0) return; // file logging is off; nothing is in flight
        string sentinel = "pump-drained-" + Guid.NewGuid().ToString("N");
        Log.Error(sentinel);
        WaitForOnDisk(sentinel, 30_000);
    }

    /// <summary>
    /// Close a <c>FileStream</c> the logger dropped on the floor, so the file it holds can be
    /// deleted.
    ///
    /// <para><c>Log.TryOpen</c> used to assign <c>_stream</c> without disposing whatever was already
    /// there, so a second <c>Log.Init</c> in one process orphaned the previous handle and the file it
    /// pointed at stayed locked until the finalizer ran. Production calls <c>Init</c> once per
    /// process, so this was latent there and only bit a test that drives <c>Init</c> on purpose.
    /// <c>TryOpen</c> now closes the previous handle first (<c>Log.cs:434-436</c>); the tests that
    /// call <c>Init</c> still collect, as belt and braces.</para>
    /// </summary>
    protected static void ReleaseOrphanedStreams()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // ---- reading the log back ------------------------------------------------------------------

    /// <summary>
    /// Read a log file the logger may still have open.
    ///
    /// <para><c>File.ReadAllText</c> is not usable here: it opens with <c>FileShare.Read</c>, which
    /// forbids the writer's existing <c>FileAccess.Write</c> handle, so it throws
    /// <c>IOException</c> against the live file. <c>FileShare.ReadWrite</c> is what a tail does.
    /// The BOM the logger writes on a new file (<c>Log.TryOpen</c>) is consumed by
    /// <c>detectEncodingFromByteOrderMarks</c> rather than left on the first line.</para>
    /// </summary>
    protected static string ReadLogFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The file the logger is writing to right now.</summary>
    protected static string LiveText()
        => File.Exists(Log.CurrentPath) ? ReadLogFile(Log.CurrentPath) : "";

    /// <summary><c>&lt;base&gt;</c> of <c>&lt;base&gt;.log</c> / <c>&lt;base&gt;.1.log</c>.</summary>
    protected static string BasePath()
        => Log.CurrentPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            ? Log.CurrentPath[..^4]
            : Log.CurrentPath;

    /// <summary>Rolled files, oldest first, followed by the live file — i.e. write order.</summary>
    protected string[] LogFilesInWriteOrder()
    {
        string bas = BasePath();
        var rolled = new List<(int Index, string Path)>();
        foreach (string path in Directory.EnumerateFiles(Dir, "*.log"))
        {
            if (string.Equals(path, Log.CurrentPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.StartsWith(bas + ".", StringComparison.OrdinalIgnoreCase)) continue;
            string middle = path[(bas.Length + 1)..^4];
            if (int.TryParse(middle, out int n)) rolled.Add((n, path));
        }
        return rolled.OrderBy(r => r.Index).Select(r => r.Path)
            .Append(Log.CurrentPath).Where(File.Exists).ToArray();
    }

    /// <summary>Everything this test's logger has written, live file and rolled files alike.</summary>
    protected string AllText()
    {
        var sb = new StringBuilder();
        foreach (string path in LogFilesInWriteOrder())
        {
            // A roll can move or replace a file between the listing and the read; FileNotFound and
            // "in use" are both IOException, and both just mean "read the rest".
            try { sb.Append(ReadLogFile(path)); }
            catch (IOException) { }
        }
        return sb.ToString();
    }

    protected static string[] NonEmptyLines(string text)
        => text.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(line => line.Length > 0).ToArray();

    /// <summary>Lines carrying this test's tag, in file order.</summary>
    protected string[] TaggedLines(string text)
        => NonEmptyLines(text).Where(line => line.Contains(Tag, StringComparison.Ordinal)).ToArray();

    /// <summary>Poll until <paramref name="marker"/> shows up in one of this test's log files.
    /// Used where the file itself is the evidence: <see cref="Log.Flush"/> could not be trusted
    /// after a shed until the fix <see cref="LogFlushTests"/> guards.</summary>
    protected bool WaitForOnDisk(string marker, int timeoutMs = 30_000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (AllText().Contains(marker, StringComparison.Ordinal)) return true;
            if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            Thread.Sleep(10);
        }
    }

    // ---- Log.Health ---------------------------------------------------------------------------

    // These are internal rather than protected so DebugStorm, which is not a test class, can read
    // the same counters the assertions read.

    /// <summary>Pull one <c>name=value</c> out of <see cref="Log.Health"/>.</summary>
    internal static string HealthField(string name)
    {
        string health = Log.Health;
        int at = health.IndexOf(name + "=", StringComparison.Ordinal);
        Assert.True(at >= 0, $"Log.Health has no '{name}=': {health}");
        string rest = health[(at + name.Length + 1)..];
        int end = rest.IndexOf(' ');
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>Health reports drops as <c>dropped=&lt;queueFull&gt;+&lt;writeFailed&gt;</c>.</summary>
    private static string[] DroppedHalves() => HealthField("dropped").Split('+');

    /// <summary>Records lost to queue pressure — the Debug shed path and a full queue.</summary>
    internal static long DroppedQueueFull() => long.Parse(DroppedHalves()[0]);

    /// <summary>Records lost because the file could not be written.</summary>
    internal static long DroppedWriteFailed() => long.Parse(DroppedHalves()[1]);

    /// <summary>Total accounted drops, i.e. both halves of Health's <c>dropped=a+b</c>.</summary>
    internal static long DroppedTotal() => DroppedQueueFull() + DroppedWriteFailed();

    internal static long QueuedNow() => long.Parse(HealthField("queued"));

    internal static long AcceptedNow() => long.Parse(HealthField("accepted"));

    internal static long PersistedNow() => long.Parse(HealthField("persisted"));
}
