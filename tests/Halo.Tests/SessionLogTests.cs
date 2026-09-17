using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Halo.Shared;

namespace Halo.Tests;

/// <summary>
/// The conclusions Halo draws about how a previous process ended. A wrong one actively misleads:
/// it either announces a crash that never happened or stays quiet about one that did.
///
/// <para><b>How the classification is observed.</b> <c>SessionLog.ReportPrevious</c> is private, but
/// it rewrites each record it classifies with the verdict in the <c>state</c> field
/// (<c>SessionLog.cs:269</c>, <c>:323-338</c>), so a crafted <c>Running</c> record plus a call to
/// <see cref="SessionLog.Begin"/> makes the verdict readable from the file. A record left untouched
/// is itself the assertion for the live-sibling case.</para>
///
/// <para><b>Isolation.</b> <c>SessionLog</c> has no path seam — it always writes
/// <c>Paths.LogsDir\sessions</c> — so a <c>portable.marker</c> in the test output folder moves
/// <c>Paths.DataDir</c> into <c>bin\...\data</c>. See the comment in <c>Halo.Tests.csproj</c>: this
/// is what stops the tests reclassifying and deleting a real installation's session records, which
/// <c>Begin</c> would otherwise do because it processes every file in the directory.</para>
/// </summary>
[Collection(LogTestCollection.Name)]
public sealed class SessionLogTests : LogTestBase
{
    private static string SessionsDir => Path.Combine(Paths.LogsDir, "sessions");

    public SessionLogTests() : base("sessionlog")
    {
        Assert.True(Paths.IsPortable,
            "portable.marker is missing from the test output, so Paths.LogsDir points at the real "
            + $"Halo data folder ({Paths.DataDir}) and these tests would mutate it. See "
            + "Halo.Tests.csproj.");
        Assert.StartsWith(Paths.AppRoot, Paths.DataDir, StringComparison.OrdinalIgnoreCase);

        Directory.CreateDirectory(SessionsDir);
        foreach (string stale in Directory.EnumerateFiles(SessionsDir))
            try { File.Delete(stale); } catch { }
    }

    /// <summary>A session that reached its own shutdown path is Clean, and says what it exited for.</summary>
    [Fact]
    public void ASessionThatEnded_IsClean()
    {
        SessionLog.Begin("clean");
        SessionLog.End("user asked to quit");

        string own = Assert.Single(Directory.GetFiles(SessionsDir, "clean-*.json"));
        Assert.Equal(nameof(SessionState.Clean), StateOf(own));
        Assert.Contains("exit: user asked to quit", File.ReadAllText(own));
    }

    /// <summary>The breadcrumb a hard kill or a native crash leaves behind: whatever phase was last
    /// written is what the next start has to go on.</summary>
    [Fact]
    public void SetPhase_IsPersistedForTheNextStartToRead()
    {
        SessionLog.Begin("phased");
        SessionLog.SetPhase("providers up");

        Assert.Equal("providers up", SessionLog.Phase);
        string own = Assert.Single(Directory.GetFiles(SessionsDir, "phased-*.json"));
        Assert.Contains("\"phase\":\"providers up\"", File.ReadAllText(own));
    }

    /// <summary>Still marked Running, pid gone, nobody claimed to have stopped it: a crash.</summary>
    [Fact]
    public void ARunningRecordWhosePidIsGone_IsUnclean()
    {
        string record = Craft("gone", pid: DeadPid());

        SessionLog.Begin("next");

        Assert.Equal(nameof(SessionState.Unclean), StateOf(record));
        Assert.True(Log.Flush(10_000).Reached, Log.Health);
        Assert.Contains("ended WITHOUT a clean exit", AllText());
    }

    /// <summary>
    /// <b>The one that stops Halo reporting a phantom crash on every launch.</b> A Running record
    /// whose pid <i>and</i> OS process start time both match a live process is a sibling that is
    /// still up. It must be left completely alone — not reclassified, not reported.
    ///
    /// <para>This process stands in for the sibling, which makes the match exact rather than
    /// approximate and needs no second process to keep alive for the duration.</para>
    /// </summary>
    [Fact]
    public void ALiveSibling_IsNeverCalledCrashed()
    {
        using Process me = Process.GetCurrentProcess();
        string record = Craft("sibling",
            pid: Environment.ProcessId,
            processStartUtc: me.StartTime.ToUniversalTime());
        string before = File.ReadAllText(record);

        SessionLog.Begin("next");

        Assert.Equal(nameof(SessionState.Running), StateOf(record));
        Assert.Equal(before, File.ReadAllText(record));
        Assert.True(Log.Flush(10_000).Reached, Log.Health);
        string log = AllText();
        Assert.DoesNotContain("ended WITHOUT a clean exit", log);
        Assert.DoesNotContain("cannot be classified", log);
    }

    /// <summary>
    /// Windows reuses pids. A Running record whose pid is alive but whose recorded OS start time
    /// does not match belongs to a process that is gone and whose number was handed to something
    /// else — so it is Gone, never Alive. Asserted through the verdict, because
    /// <c>SessionLog.Liveness</c> is private: Gone with no stop intent classifies as Unclean, while
    /// Alive would have left the record untouched at Running.
    /// </summary>
    [Fact]
    public void APidThatWasReused_IsNotMistakenForTheOriginalProcess()
    {
        using Process me = Process.GetCurrentProcess();
        string record = Craft("reused",
            pid: Environment.ProcessId, // alive right now
            processStartUtc: me.StartTime.ToUniversalTime().AddHours(-1)); // but not this process

        SessionLog.Begin("next");

        Assert.Equal(nameof(SessionState.Unclean), StateOf(record));
    }

    /// <summary>A process that recorded its own stop intent was stopped on purpose.</summary>
    [Fact]
    public void ARecordedStopIntent_IsExpectedTerminationNotACrash()
    {
        string record = Craft("stopped", pid: DeadPid(), stopIntent: "upgrade to 1.0.3");

        SessionLog.Begin("next");

        Assert.Equal(nameof(SessionState.ExpectedTermination), StateOf(record));
        Assert.True(Log.Flush(10_000).Reached, Log.Health);
        string log = AllText();
        Assert.True(log.Contains("was stopped on purpose: upgrade to 1.0.3"), $"log was:\n{log}");
        Assert.DoesNotContain("ended WITHOUT a clean exit", log);
    }

    /// <summary>
    /// The victim of <c>schtasks /End</c> or <c>Stop-Process -Force</c> gets no chance to record
    /// anything, so the killer records it instead. That intent has to count the same as the
    /// victim's own — the widgets watchdog restarting a wedged collector must not read as a crash.
    /// </summary>
    [Fact]
    public void AnExternalStopIntent_IsExpectedTerminationAndIsConsumed()
    {
        int dead = DeadPid();
        string record = Craft("killed", pid: dead, startedUtc: DateTime.UtcNow.AddMinutes(-5));

        SessionLog.RecordExternalStop(dead, "watchdog restart: collector stopped polling");
        string marker = Path.Combine(SessionsDir, $"stop-intent-{dead}.json");
        Assert.True(File.Exists(marker), "RecordExternalStop wrote nothing");

        SessionLog.Begin("next");

        Assert.Equal(nameof(SessionState.ExpectedTermination), StateOf(record));
        Assert.Contains("watchdog restart: collector stopped polling", File.ReadAllText(record));
        Assert.False(File.Exists(marker),
            "the stop intent was not consumed, so a later process reusing that pid would inherit it");
    }

    /// <summary>
    /// An intent marker that pre-dates the session it claims to explain belongs to an earlier
    /// holder of that pid, and must not be allowed to excuse this crash.
    /// </summary>
    [Fact]
    public void AStopIntentOlderThanTheSession_DoesNotExcuseTheCrash()
    {
        int dead = DeadPid();
        string record = Craft("stale", pid: dead, startedUtc: DateTime.UtcNow.AddMinutes(-5));

        // Hand-written rather than via RecordExternalStop, which always stamps "now".
        File.WriteAllText(Path.Combine(SessionsDir, $"stop-intent-{dead}.json"),
            "{\"reason\":\"an unrelated older stop\",\"byProcess\":\"widgets\",\"byPid\":1,"
            + $"\"atUtc\":\"{Iso(DateTime.UtcNow.AddHours(-2))}\"}}");

        SessionLog.Begin("next");

        Assert.Equal(nameof(SessionState.Unclean), StateOf(record));
        Assert.DoesNotContain("an unrelated older stop", File.ReadAllText(record));
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>Write a previous-session record for <c>Begin</c> to classify. The file name is
    /// deliberately unlike <c>&lt;proc&gt;-&lt;pid&gt;-&lt;stamp&gt;.json</c> so it can never
    /// collide with the record this process opens for itself, which <c>ReportPrevious</c> skips by
    /// path.</summary>
    private static string Craft(
        string name,
        int pid,
        DateTime? processStartUtc = null,
        string stopIntent = "",
        DateTime? startedUtc = null,
        string state = nameof(SessionState.Running))
    {
        string path = Path.Combine(SessionsDir, $"crafted-{name}.json");
        File.WriteAllText(path,
            "{"
            + "\"sessionId\":\"0badc0de\","
            + "\"process\":\"collector\","
            + $"\"pid\":{pid},"
            // An hour ago by default: a pid that is somehow alive again cannot match it, so the
            // default case is Gone for the right reason rather than by luck.
            + $"\"processStartUtc\":\"{Iso(processStartUtc ?? DateTime.UtcNow.AddHours(-1))}\","
            + $"\"startedUtc\":\"{Iso(startedUtc ?? DateTime.UtcNow.AddMinutes(-5))}\","
            + "\"exe\":\"C:\\\\Halo\\\\Halo.Collector.exe\","
            + "\"version\":\"1.0.2\","
            + "\"mode\":\"installed\","
            + "\"elevated\":true,"
            + $"\"state\":\"{state}\","
            + "\"phase\":\"providers\","
            + $"\"stopIntent\":\"{stopIntent}\","
            + "\"logFile\":\"collector-20260917-101500-4242.log\""
            + "}");
        return path;
    }

    private static string Iso(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);

    private static string StateOf(string path)
    {
        string json = File.ReadAllText(path);
        Match m = Regex.Match(json, "\"state\"\\s*:\\s*\"(?<state>[A-Za-z]+)\"");
        Assert.True(m.Success, $"no state field in {path}: {json}");
        return m.Groups["state"].Value;
    }

    /// <summary>A pid that is genuinely not running. Taken from a real process that has exited
    /// rather than invented, so it cannot accidentally name something live.</summary>
    private static int DeadPid()
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("could not start a throwaway process");
        p.WaitForExit();
        return p.Id;
    }
}
